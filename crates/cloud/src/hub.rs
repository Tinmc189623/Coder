use std::{
    collections::{HashMap, HashSet},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    time::Duration,
};

use axum::extract::ws::{Message, WebSocket};
use chrono::Utc;
use coder_protocol::{
    ClientMessage, InFlightTask, LocalTaskState, PROTOCOL_VERSION, RequestId, ServerMessage,
    SessionId, TaskId, TaskRef, TaskStatus, is_protocol_compatible,
};
use dashmap::DashMap;
use futures::{SinkExt, StreamExt};
use tokio::{
    sync::{Mutex, mpsc},
    time::{Instant, timeout},
};
use tracing::{debug, info, warn};

use crate::{
    auth::DevicePrincipal,
    config::CloudConfig,
    error::CloudError,
    store::{CloudStore, PermissionRecord, StoreError, TaskRecord, TaskResultData},
};

/// 单个设备的 WebSocket 连接。
struct Connection {
    session_id: SessionId,
    sender: mpsc::UnboundedSender<ServerMessage>,
    ready: Arc<AtomicBool>,
    dispatch: Arc<Mutex<()>>,
}

/// 欢迎消息中的任务恢复计划。
struct WelcomePlan {
    tasks: Vec<coder_protocol::TaskAssignment>,
    canceled: Vec<TaskRef>,
}

/// 云端设备连接中心：会话建立、任务下发、取消、权限转发与租约清理。
pub struct Hub {
    store: Arc<dyn CloudStore>,
    config: Arc<CloudConfig>,
    connections: DashMap<coder_protocol::DeviceId, Connection>,
}

/// Hello 消息中云端关心的字段。
struct HelloInfo {
    protocol_version: u32,
    in_flight: Vec<InFlightTask>,
    device_name: String,
    platform: String,
    agent_version: String,
}

impl Hub {
    /// 创建连接中心。
    pub fn new(store: Arc<dyn CloudStore>, config: Arc<CloudConfig>) -> Self {
        Self {
            store,
            config,
            connections: DashMap::new(),
        }
    }

    /// 设备是否已建立并完成欢迎握手。
    pub fn is_online(&self, device_id: coder_protocol::DeviceId) -> bool {
        self.connections
            .get(&device_id)
            .is_some_and(|connection| connection.ready.load(Ordering::SeqCst))
    }

    /// 当前在线的设备集合。
    pub fn online_devices(&self) -> HashSet<coder_protocol::DeviceId> {
        self.connections
            .iter()
            .filter(|entry| entry.ready.load(Ordering::SeqCst))
            .map(|entry| *entry.key())
            .collect()
    }

    /// 向设备发送消息；未连接时返回 `false`。
    pub fn send(&self, device_id: coder_protocol::DeviceId, message: ServerMessage) -> bool {
        self.connections
            .get(&device_id)
            .map(|connection| connection.sender.send(message).is_ok())
            .unwrap_or(false)
    }

    /// 下发一个待执行任务；设备离线时保留 pending 状态，等待重连。
    pub async fn dispatch_task(&self, task: &TaskRecord) {
        if task.status != TaskStatus::Pending {
            return;
        }
        let Some(connection) = self.connections.get(&task.device_id) else {
            return;
        };
        if !connection.ready.load(Ordering::SeqCst) {
            return;
        }
        let sender = connection.sender.clone();
        let ready = connection.ready.clone();
        let lock = connection.dispatch.clone();
        drop(connection);
        let _guard = lock.lock().await;
        if !ready.load(Ordering::SeqCst) {
            return;
        }
        // 锁内重新读取，避免创建路由与重连泵送重复下发同一 attempt。
        match self.store.get_task(task.id).await {
            Ok(Some(current)) if current.status == TaskStatus::Pending => {}
            Ok(_) => return,
            Err(err) => {
                warn!(task_id = %task.id, error = %err, "failed to re-read task before dispatch");
                return;
            }
        }
        let lease = Utc::now() + chrono::Duration::seconds(self.config.task_lease_secs as i64);
        match self.store.assign_task(task.id, task.attempt, lease).await {
            Ok(assigned) => {
                let assignment = assigned.to_assignment(assigned.lease_expires_at.unwrap_or(lease));
                if sender
                    .send(ServerMessage::TaskAssign { assignment })
                    .is_err()
                {
                    // 无法证明任务已送达；宁可标记 unknown，也不重放可能产生副作用的执行。
                    if let Err(err) = self
                        .store
                        .mark_unknown(
                            assigned.id,
                            assigned.attempt,
                            "task assignment could not be delivered to the agent",
                        )
                        .await
                    {
                        warn!(task_id = %assigned.id, error = %err, "failed to mark undelivered task unknown");
                    }
                } else {
                    metrics::counter!("coder_cloud_tasks_assigned_total").increment(1);
                }
            }
            Err(StoreError::Conflict(_)) => {
                debug!(task_id = %task.id, "task is no longer pending; dispatch skipped");
            }
            Err(err) => warn!(task_id = %task.id, error = %err, "failed to assign task"),
        }
    }

    /// 通知设备取消某个任务尝试。
    pub fn cancel_task(&self, device_id: coder_protocol::DeviceId, task_id: TaskId, attempt: u32) {
        self.send(device_id, ServerMessage::TaskCancel { task_id, attempt });
    }

    /// 把权限决定转发给正在等待的本地 Agent。
    pub async fn send_permission_decision(
        &self,
        task_id: TaskId,
        request_id: RequestId,
        granted: bool,
        reason: Option<String>,
    ) -> Result<(), CloudError> {
        let task = self
            .store
            .get_task(task_id)
            .await?
            .ok_or_else(|| CloudError::NotFound(format!("task {task_id}")))?;
        self.send(
            task.device_id,
            ServerMessage::PermissionDecision {
                task_id,
                request_id,
                granted,
                reason,
            },
        );
        Ok(())
    }

    /// 向所有设备广播服务端关闭通知。
    pub fn broadcast_shutdown(&self, reason: &str, retry_after_secs: u64) {
        for connection in &self.connections {
            let _ = connection.sender.send(ServerMessage::ServerShutdown {
                reason: reason.to_owned(),
                retry_after_secs,
            });
        }
    }

    /// 扫描过期租约，把无法确认结果的任务标记为 unknown。
    pub async fn reap_once(&self) -> Result<usize, StoreError> {
        let expired = self.store.expire_leases(Utc::now()).await?;
        if !expired.is_empty() {
            metrics::counter!("coder_cloud_tasks_unknown_total", "reason" => "lease_expired")
                .increment(expired.len() as u64);
            warn!(
                count = expired.len(),
                "expired task leases were marked unknown"
            );
        }
        Ok(expired.len())
    }

    /// 处理一条已认证的设备 WebSocket 连接。
    pub async fn run_agent(self: Arc<Self>, principal: DevicePrincipal, socket: WebSocket) {
        let (mut sink, mut stream) = socket.split();
        let hello = match read_hello(&mut stream).await {
            Ok(hello) => hello,
            Err(err) => {
                warn!(device_id = %principal.device_id, error = %err, "agent handshake failed");
                send_direct(
                    &mut sink,
                    &ServerMessage::Error {
                        code: coder_protocol::ErrorCode::InvalidRequest,
                        message: err.to_string(),
                        request_id: None,
                    },
                )
                .await;
                let _ = sink.close().await;
                return;
            }
        };
        if !is_protocol_compatible(hello.protocol_version) {
            send_direct(
                &mut sink,
                &ServerMessage::Error {
                    code: coder_protocol::ErrorCode::ProtocolVersionUnsupported,
                    message: format!(
                        "protocol {} is not supported; server supports {PROTOCOL_VERSION}",
                        hello.protocol_version
                    ),
                    request_id: None,
                },
            )
            .await;
            let _ = sink.close().await;
            return;
        }

        info!(
            device_id = %principal.device_id,
            name = %hello.device_name,
            platform = %hello.platform,
            agent_version = %hello.agent_version,
            in_flight = hello.in_flight.len(),
            "agent connected"
        );

        let session_id = SessionId::new();
        let (outbound, inbound) = mpsc::unbounded_channel::<ServerMessage>();
        let ready = Arc::new(AtomicBool::new(false));
        let dispatch = Arc::new(Mutex::new(()));
        let previous = self.connections.insert(
            principal.device_id,
            Connection {
                session_id,
                sender: outbound.clone(),
                ready: ready.clone(),
                dispatch,
            },
        );
        if let Some(previous) = previous {
            let _ = previous.sender.send(ServerMessage::ServerShutdown {
                reason: "a newer agent session replaced this connection".into(),
                retry_after_secs: 2,
            });
        }
        update_connection_gauge(self.connections.len());

        let now = Utc::now();
        if let Err(err) = self.store.touch_device(principal.device_id, now).await {
            warn!(error = %err, "failed to update device last_seen_at");
        }

        let mut writer = tokio::spawn(write_loop(sink, inbound));
        let plan = self
            .build_welcome(principal.device_id, &hello.in_flight)
            .await;
        let welcome = ServerMessage::Welcome {
            protocol_version: PROTOCOL_VERSION,
            server_version: env!("CARGO_PKG_VERSION").to_owned(),
            session_id,
            server_time: Utc::now(),
            tasks: plan.tasks,
            canceled: plan.canceled,
        };
        if outbound.send(welcome).is_err() {
            warn!(device_id = %principal.device_id, "cannot deliver welcome message");
            self.unregister(principal.device_id, session_id);
            drop(outbound);
            let _ = timeout(Duration::from_secs(2), &mut writer).await;
            return;
        }
        ready.store(true, Ordering::SeqCst);
        self.pump_pending(principal.device_id).await;

        loop {
            tokio::select! {
                _ = &mut writer => break,
                frame = stream.next() => {
                    let Some(frame) = frame else { break };
                    let flow = match frame {
                        Ok(Message::Text(text)) => {
                            match serde_json::from_str::<ClientMessage>(text.as_str()) {
                                Ok(message) => {
                                    self.handle_message(principal, &outbound, message).await
                                }
                                Err(err) => {
                                    warn!(device_id = %principal.device_id, error = %err, "invalid client message");
                                    let _ = outbound.send(ServerMessage::Error {
                                        code: coder_protocol::ErrorCode::InvalidRequest,
                                        message: format!("invalid message: {err}"),
                                        request_id: None,
                                    });
                                    Flow::Continue
                                }
                            }
                        }
                        Ok(Message::Close(_)) => break,
                        Ok(Message::Ping(_) | Message::Pong(_) | Message::Binary(_)) => Flow::Continue,
                        Err(err) => {
                            debug!(device_id = %principal.device_id, error = %err, "websocket read failed");
                            break;
                        }
                    };
                    if flow == Flow::Stop {
                        break;
                    }
                }
            }
        }

        self.unregister(principal.device_id, session_id);
        drop(outbound);
        if !writer.is_finished() && timeout(Duration::from_secs(2), &mut writer).await.is_err() {
            writer.abort();
        }
        info!(device_id = %principal.device_id, %session_id, "agent disconnected");
    }

    /// 处理单条客户端消息，返回是否继续会话。
    async fn handle_message(
        &self,
        principal: DevicePrincipal,
        outbound: &mpsc::UnboundedSender<ServerMessage>,
        message: ClientMessage,
    ) -> Flow {
        match message {
            ClientMessage::Hello { .. } => {
                let _ = outbound.send(ServerMessage::Error {
                    code: coder_protocol::ErrorCode::InvalidRequest,
                    message: "duplicate hello message".into(),
                    request_id: None,
                });
                Flow::Stop
            }
            ClientMessage::Heartbeat { .. } => {
                let now = Utc::now();
                if let Err(err) = self.store.touch_device(principal.device_id, now).await {
                    warn!(error = %err, "heartbeat: failed to update device");
                }
                let lease = now + chrono::Duration::seconds(self.config.task_lease_secs as i64);
                if let Err(err) = self.store.extend_leases(principal.device_id, lease).await {
                    warn!(error = %err, "heartbeat: failed to extend leases");
                }
                let _ = outbound.send(ServerMessage::HeartbeatAck { ts: now });
                Flow::Continue
            }
            ClientMessage::TaskStarted {
                task_id,
                attempt,
                started_at,
            } => {
                let lease =
                    Utc::now() + chrono::Duration::seconds(self.config.task_lease_secs as i64);
                match self.store.start_task(task_id, attempt, lease).await {
                    Ok(_) => {
                        debug!(%task_id, attempt, %started_at, "task started");
                    }
                    Err(StoreError::Conflict(_) | StoreError::NotFound(_)) => {
                        // 云端认为该尝试已结束或不存在，通知本地停止以避免重复副作用。
                        self.cancel_task(principal.device_id, task_id, attempt);
                    }
                    Err(err) => {
                        warn!(%task_id, attempt, error = %err, "failed to mark task started")
                    }
                }
                Flow::Continue
            }
            ClientMessage::TaskProgress {
                task_id,
                attempt,
                seq,
                event,
            } => {
                debug!(%task_id, attempt, seq, event = ?event, "task progress");
                metrics::counter!("coder_cloud_task_progress_total").increment(1);
                Flow::Continue
            }
            ClientMessage::TaskResult {
                task_id,
                attempt,
                status,
                summary,
                error,
                session_id,
                files_changed,
                ..
            } => {
                if !status.is_terminal() {
                    let _ = outbound.send(ServerMessage::Error {
                        code: coder_protocol::ErrorCode::InvalidRequest,
                        message: "task result must carry a terminal status".into(),
                        request_id: None,
                    });
                    return Flow::Continue;
                }
                let result = TaskResultData {
                    status,
                    summary,
                    error,
                    files_changed,
                    session_id,
                };
                match self.store.record_result(task_id, attempt, result).await {
                    Ok(record) => {
                        metrics::counter!(
                            "coder_cloud_task_results_total",
                            "status" => record.status.as_str()
                        )
                        .increment(1);
                        let _ = outbound.send(ServerMessage::TaskResultAck { task_id, attempt });
                    }
                    Err(StoreError::Conflict(message)) => {
                        // 旧 attempt 或已终态的结果无需重发；确认后本地账本停止重试。
                        debug!(%task_id, attempt, %message, "stale task result acknowledged");
                        let _ = outbound.send(ServerMessage::TaskResultAck { task_id, attempt });
                    }
                    Err(StoreError::NotFound(_)) => {
                        debug!(%task_id, attempt, "result for unknown task acknowledged");
                        let _ = outbound.send(ServerMessage::TaskResultAck { task_id, attempt });
                    }
                    Err(err) => {
                        warn!(%task_id, attempt, error = %err, "failed to record task result");
                        let _ = outbound.send(ServerMessage::Error {
                            code: coder_protocol::ErrorCode::Internal,
                            message: "failed to persist task result".into(),
                            request_id: None,
                        });
                    }
                }
                Flow::Continue
            }
            ClientMessage::TaskRejected {
                task_id,
                attempt,
                reason,
            } => {
                match self.store.reject_task(task_id, attempt, &reason).await {
                    Ok(_) => {
                        metrics::counter!("coder_cloud_task_results_total", "status" => "failed")
                            .increment(1);
                    }
                    Err(StoreError::Conflict(_) | StoreError::NotFound(_)) => {
                        debug!(%task_id, attempt, "stale task rejection ignored");
                    }
                    Err(err) => {
                        warn!(%task_id, attempt, error = %err, "failed to record task rejection")
                    }
                }
                Flow::Continue
            }
            ClientMessage::TaskInterrupted {
                task_id,
                attempt,
                reason,
            } => {
                match self.store.mark_unknown(task_id, attempt, &reason).await {
                    Ok(_) => {
                        metrics::counter!("coder_cloud_tasks_unknown_total", "reason" => "agent_interrupted")
                            .increment(1);
                    }
                    Err(StoreError::Conflict(_) | StoreError::NotFound(_)) => {
                        debug!(%task_id, attempt, "stale interruption ignored");
                    }
                    Err(err) => {
                        warn!(%task_id, attempt, error = %err, "failed to record interruption")
                    }
                }
                Flow::Continue
            }
            ClientMessage::PermissionRequest {
                task_id,
                attempt,
                request_id,
                tool,
                action,
                details,
                risk,
            } => {
                let task = match self.store.get_task(task_id).await {
                    Ok(Some(task)) => task,
                    Ok(None) => {
                        let _ = outbound.send(ServerMessage::Error {
                            code: coder_protocol::ErrorCode::TaskNotFound,
                            message: format!("task {task_id} is not known"),
                            request_id: Some(request_id),
                        });
                        return Flow::Continue;
                    }
                    Err(err) => {
                        warn!(%task_id, error = %err, "failed to load task for permission request");
                        return Flow::Continue;
                    }
                };
                if task.device_id != principal.device_id
                    || task.user_id != principal.user_id
                    || task.attempt != attempt
                {
                    let _ = outbound.send(ServerMessage::Error {
                        code: coder_protocol::ErrorCode::Forbidden,
                        message: "permission request does not match the assigned task attempt"
                            .into(),
                        request_id: Some(request_id),
                    });
                    return Flow::Continue;
                }
                if task.status.is_terminal() {
                    let _ = outbound.send(ServerMessage::PermissionDecision {
                        task_id,
                        request_id,
                        granted: false,
                        reason: Some("task is already finished".into()),
                    });
                    return Flow::Continue;
                }
                let record = PermissionRecord {
                    request_id,
                    task_id,
                    user_id: principal.user_id,
                    tool: truncate(&tool, 256),
                    action: truncate(&action, 512),
                    details: truncate(&details, 8_192),
                    risk,
                    granted: None,
                    reason: None,
                    created_at: Utc::now(),
                    resolved_at: None,
                };
                match self.store.create_permission(record).await {
                    Ok(()) => {
                        metrics::counter!("coder_cloud_permission_requests_total").increment(1);
                    }
                    Err(StoreError::Conflict(_)) => {
                        debug!(%request_id, "duplicate permission request ignored");
                    }
                    Err(err) => {
                        warn!(%request_id, error = %err, "failed to store permission request")
                    }
                }
                Flow::Continue
            }
            ClientMessage::CancelAck { task_id, attempt } => {
                debug!(%task_id, attempt, "agent acknowledged cancellation");
                Flow::Continue
            }
            ClientMessage::Goodbye { reason } => {
                debug!(device_id = %principal.device_id, %reason, "agent said goodbye");
                Flow::Stop
            }
        }
    }

    /// 依据本地账本状态构造欢迎消息中的恢复与取消列表。
    async fn build_welcome(
        &self,
        device_id: coder_protocol::DeviceId,
        in_flight: &[InFlightTask],
    ) -> WelcomePlan {
        let active = match self.store.list_device_active_tasks(device_id).await {
            Ok(tasks) => tasks,
            Err(err) => {
                warn!(error = %err, "failed to load active tasks for welcome");
                return WelcomePlan {
                    tasks: Vec::new(),
                    canceled: Vec::new(),
                };
            }
        };
        let local: HashMap<(TaskId, u32), LocalTaskState> = in_flight
            .iter()
            .map(|item| ((item.task_id, item.attempt), item.state))
            .collect();
        let lease_until =
            Utc::now() + chrono::Duration::seconds(self.config.task_lease_secs as i64);
        let mut canceled: HashSet<(TaskId, u32)> = HashSet::new();
        let mut assignments = Vec::new();

        for item in in_flight {
            match self.store.get_task(item.task_id).await {
                Ok(Some(task)) => {
                    if task.attempt != item.attempt || task.status.is_terminal() {
                        canceled.insert((item.task_id, item.attempt));
                    }
                }
                Ok(None) => {
                    canceled.insert((item.task_id, item.attempt));
                }
                Err(err) => {
                    warn!(task_id = %item.task_id, error = %err, "failed to inspect in-flight task")
                }
            }
        }

        for task in active {
            let key = (task.id, task.attempt);
            if task.cancel_requested {
                canceled.insert(key);
                continue;
            }
            if task.status == TaskStatus::Pending {
                if local.get(&key) == Some(&LocalTaskState::Started) {
                    // 云端没有下发记录，本地却已开始执行：结果无法确认，先转 unknown，
                    // 避免欢迎握手后的 pending 泵送把同一次尝试再次下发。
                    let reason = "agent reports this attempt was started locally while the cloud had no assignment; the outcome cannot be confirmed";
                    if let Err(err) = self.store.mark_unknown(task.id, task.attempt, reason).await {
                        warn!(task_id = %task.id, error = %err, "failed to mark locally-started task unknown");
                    }
                    canceled.insert(key);
                }
                continue;
            }
            match local.get(&key).copied() {
                Some(LocalTaskState::Started) => {
                    match self
                        .store
                        .assign_task(task.id, task.attempt, lease_until)
                        .await
                    {
                        Ok(assigned) => {
                            let lease = assigned.lease_expires_at.unwrap_or(lease_until);
                            assignments.push(assigned.to_assignment(lease));
                        }
                        Err(StoreError::Conflict(_) | StoreError::NotFound(_)) => {
                            canceled.insert(key);
                        }
                        Err(err) => {
                            warn!(task_id = %task.id, error = %err, "failed to refresh task lease");
                            canceled.insert(key);
                        }
                    }
                }
                Some(state) => {
                    let reason = format!(
                        "agent reports local state {state:?} but the cloud has no terminal result; the attempt will not be replayed"
                    );
                    if let Err(err) = self
                        .store
                        .mark_unknown(task.id, task.attempt, &reason)
                        .await
                    {
                        warn!(task_id = %task.id, error = %err, "failed to mark inconsistent task unknown");
                    }
                }
                None => {
                    let reason = "agent has no in-flight record for this attempt; the outcome cannot be confirmed and it will not be replayed";
                    if let Err(err) = self.store.mark_unknown(task.id, task.attempt, reason).await {
                        warn!(task_id = %task.id, error = %err, "failed to mark unconfirmed task unknown");
                    }
                }
            }
        }

        WelcomePlan {
            tasks: assignments,
            canceled: canceled
                .into_iter()
                .map(|(task_id, attempt)| TaskRef { task_id, attempt })
                .collect(),
        }
    }

    /// 欢迎握手后补发在注册窗口期间创建的 pending 任务。
    async fn pump_pending(&self, device_id: coder_protocol::DeviceId) {
        let tasks = match self.store.list_device_active_tasks(device_id).await {
            Ok(tasks) => tasks,
            Err(err) => {
                warn!(error = %err, "failed to load pending tasks");
                return;
            }
        };
        for task in tasks
            .into_iter()
            .filter(|task| task.status == TaskStatus::Pending)
        {
            self.dispatch_task(&task).await;
        }
    }

    /// 仅在新会话仍是当前连接时移除登记。
    fn unregister(&self, device_id: coder_protocol::DeviceId, session_id: SessionId) {
        self.connections.remove_if(&device_id, |_, connection| {
            connection.session_id == session_id
        });
        update_connection_gauge(self.connections.len());
    }
}

/// 会话处理流程。
#[derive(Clone, Copy, PartialEq, Eq)]
enum Flow {
    Continue,
    Stop,
}

/// 等待并校验 Hello 消息。
async fn read_hello<S>(stream: &mut S) -> Result<HelloInfo, CloudError>
where
    S: futures::Stream<Item = Result<Message, axum::Error>> + Unpin,
{
    let deadline = Instant::now() + Duration::from_secs(30);
    loop {
        let remaining = deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            return Err(CloudError::Invalid(
                "timed out waiting for agent hello".into(),
            ));
        }
        let frame = timeout(remaining, stream.next())
            .await
            .map_err(|_| CloudError::Invalid("timed out waiting for agent hello".into()))?
            .ok_or_else(|| CloudError::Invalid("connection closed before hello".into()))?
            .map_err(|err| CloudError::Invalid(format!("websocket error: {err}")))?;
        match frame {
            Message::Text(text) => match serde_json::from_str::<ClientMessage>(text.as_str()) {
                Ok(ClientMessage::Hello {
                    protocol_version,
                    agent_version,
                    device_name,
                    platform,
                    in_flight,
                    ..
                }) => {
                    return Ok(HelloInfo {
                        protocol_version,
                        in_flight,
                        device_name,
                        platform,
                        agent_version,
                    });
                }
                Ok(_) => {
                    return Err(CloudError::Invalid("first message must be hello".into()));
                }
                Err(err) => {
                    return Err(CloudError::Invalid(format!("invalid hello message: {err}")));
                }
            },
            Message::Close(_) => {
                return Err(CloudError::Invalid("connection closed before hello".into()));
            }
            Message::Ping(_) | Message::Pong(_) | Message::Binary(_) => continue,
        }
    }
}

/// 直接向尚未进入写入循环的 socket 发送消息。
async fn send_direct<S>(sink: &mut S, message: &ServerMessage)
where
    S: futures::Sink<Message> + Unpin,
    S::Error: std::fmt::Debug,
{
    if let Ok(json) = serde_json::to_string(message) {
        let _ = sink.send(Message::Text(json.into())).await;
    }
}

/// WebSocket 写入循环。
async fn write_loop(
    mut sink: futures::stream::SplitSink<WebSocket, Message>,
    mut inbound: mpsc::UnboundedReceiver<ServerMessage>,
) {
    while let Some(message) = inbound.recv().await {
        let text = match serde_json::to_string(&message) {
            Ok(text) => text,
            Err(err) => {
                warn!(error = %err, "cannot encode server message");
                continue;
            }
        };
        if sink.send(Message::Text(text.into())).await.is_err() {
            break;
        }
    }
    let _ = sink.close().await;
}

/// 更新连接数指标。
fn update_connection_gauge(count: usize) {
    metrics::gauge!("coder_cloud_ws_connections").set(count as f64);
}

/// 截断过长的设备上报文本。
fn truncate(value: &str, limit: usize) -> String {
    if value.len() <= limit {
        return value.to_owned();
    }
    let mut end = limit;
    while !value.is_char_boundary(end) {
        end -= 1;
    }
    format!("{}…", &value[..end])
}
