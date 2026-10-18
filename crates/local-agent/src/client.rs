use std::{
    collections::HashMap,
    sync::{Arc, Mutex as StdMutex},
    time::{Duration, Instant},
};

use async_trait::async_trait;
use chrono::Utc;
use coder_core::{
    config::ModelCapabilities,
    model::{AccessTokenProvider, ModelClient, ModelError, OpenAiModel, ProxyModel},
    store::SqliteStore,
};
use coder_protocol::{
    AgentCapabilities, ClientMessage, ErrorCode, InFlightTask, LocalTaskState, PROTOCOL_VERSION,
    ServerMessage, SessionId, TaskAssignment, TaskId, TaskRef, TaskStatus, is_protocol_compatible,
};
use futures::{SinkExt, StreamExt};
use rand::Rng;
use tokio::{
    sync::{Mutex, mpsc},
    time::MissedTickBehavior,
};
use tokio_tungstenite::{
    connect_async,
    tungstenite::{
        Message,
        client::IntoClientRequest,
        http::{HeaderValue, header::AUTHORIZATION},
    },
};
use tokio_util::sync::CancellationToken;
use tracing::{debug, error, info, warn};
use url::Url;

use crate::{
    config::{LocalAgentConfig, ModelMode, ReconnectConfig},
    credentials::{DeviceCredentials, fetch_access_token, normalize_server_url, platform_name},
    error::AgentError,
    ledger::{StoredTaskResult, TaskLedger},
    runner::{
        TaskExecutor, pending_approvals, rejected_message, resolve_permission, result_message,
    },
};

/// 本地 Agent 支持的工具名，随 Hello 上报给云端。
const TOOL_NAMES: &[&str] = &[
    "read_file",
    "write_file",
    "edit_file",
    "list_files",
    "search",
    "shell",
    "run_project_checks",
    "git",
    "update_plan",
    "check_permissions",
];

/// 缓存中的短期访问令牌。
struct CachedToken {
    value: String,
    expires_at: Instant,
}

/// 设备令牌管理器，实现模型代理与 WebSocket 共用的认证刷新。
pub struct TokenManager {
    http: reqwest::Client,
    credentials: Arc<DeviceCredentials>,
    cached: Mutex<Option<CachedToken>>,
}

impl TokenManager {
    /// 创建设备令牌管理器。
    pub fn new(credentials: Arc<DeviceCredentials>) -> Result<Self, AgentError> {
        let http = reqwest::Client::builder()
            .timeout(Duration::from_secs(30))
            .user_agent(concat!("coder-local-agent/", env!("CARGO_PKG_VERSION")))
            .build()?;
        Ok(Self {
            http,
            credentials,
            cached: Mutex::new(None),
        })
    }
}

#[async_trait]
impl AccessTokenProvider for TokenManager {
    /// 返回缓存令牌，过期或要求强制刷新时向云端换取新令牌。
    async fn access_token(&self, force_refresh: bool) -> Result<String, ModelError> {
        let mut cached = self.cached.lock().await;
        if !force_refresh
            && let Some(token) = cached.as_ref()
            && token.expires_at > Instant::now()
        {
            return Ok(token.value.clone());
        }
        let response = fetch_access_token(&self.http, &self.credentials)
            .await
            .map_err(|err| ModelError::Http(err.to_string()))?;
        if !response.token_type.eq_ignore_ascii_case("bearer") {
            return Err(ModelError::NotConfigured(format!(
                "unexpected token type `{}`",
                response.token_type
            )));
        }
        let ttl = response.expires_in_secs.saturating_sub(30).max(5);
        *cached = Some(CachedToken {
            value: response.access_token.clone(),
            expires_at: Instant::now() + Duration::from_secs(ttl),
        });
        Ok(response.access_token)
    }
}

/// 会话标识与对应的消息发送端。
type SessionSender = (uuid::Uuid, mpsc::UnboundedSender<ClientMessage>);

/// 当前 WebSocket 会话的发送端，任务结果总是走最新会话。
#[derive(Clone, Default)]
struct SessionSink {
    inner: Arc<StdMutex<Option<SessionSender>>>,
}

impl SessionSink {
    /// 注册当前会话的发送端。
    fn set(&self, session_id: uuid::Uuid, sender: mpsc::UnboundedSender<ClientMessage>) {
        let mut guard = self.lock();
        *guard = Some((session_id, sender));
    }

    /// 仅在发送端仍属于指定会话时清除，避免误删新会话。
    fn clear(&self, session_id: uuid::Uuid) {
        let mut guard = self.lock();
        if guard
            .as_ref()
            .is_some_and(|(current, _)| *current == session_id)
        {
            *guard = None;
        }
    }

    /// 发送一条消息；无活动会话时返回 `false`。
    fn send(&self, message: ClientMessage) -> bool {
        let guard = self.lock();
        guard
            .as_ref()
            .map(|(_, sender)| sender.send(message).is_ok())
            .unwrap_or(false)
    }

    /// 取出原始发送端，用于任务进度流。
    fn sender(&self) -> Option<mpsc::UnboundedSender<ClientMessage>> {
        self.lock().as_ref().map(|(_, sender)| sender.clone())
    }

    /// 获取互斥锁，忽略中毒状态。
    fn lock(
        &self,
    ) -> std::sync::MutexGuard<'_, Option<(uuid::Uuid, mpsc::UnboundedSender<ClientMessage>)>> {
        self.inner
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    }
}

/// 一个正在本地执行的任务。
struct ActiveTask {
    cancel: CancellationToken,
    started_at: chrono::DateTime<Utc>,
}

/// 会话结束原因。
enum SessionEnd {
    Shutdown,
    AuthFailed(String),
    ServerShutdown {
        reason: String,
        retry_after_secs: u64,
    },
    Disconnected(String),
}

/// 一次连接的结果。
struct SessionOutcome {
    welcomed: bool,
    end: SessionEnd,
}

/// 服务端欢迎消息中与本客户端相关的字段。
struct WelcomeData {
    protocol_version: u32,
    session_id: SessionId,
    tasks: Vec<TaskAssignment>,
    canceled: Vec<TaskRef>,
}

/// 云端连接的本地 Agent。
pub struct AgentClient {
    config: Arc<LocalAgentConfig>,
    tokens: Arc<TokenManager>,
    ledger: Arc<TaskLedger>,
    executor: Arc<TaskExecutor>,
    sink: Arc<SessionSink>,
    active: Arc<Mutex<HashMap<(TaskId, u32), ActiveTask>>>,
}

impl AgentClient {
    /// 组装客户端：打开账本与会话存储、恢复孤儿任务、构造模型。
    pub async fn new(
        config: LocalAgentConfig,
        credentials: DeviceCredentials,
    ) -> Result<Self, AgentError> {
        config.validate()?;
        let expected = normalize_server_url(&config.server_url)?;
        let stored = normalize_server_url(&credentials.server_url)?;
        if expected != stored {
            return Err(AgentError::Config(format!(
                "configured server_url {expected} does not match credentials server_url {stored}"
            )));
        }
        let config = Arc::new(config);
        let credentials = Arc::new(credentials);

        let ledger = Arc::new(TaskLedger::connect(&config.storage_path).await?);
        let orphans = ledger.recover_orphans().await?;
        if orphans > 0 {
            warn!(
                count = orphans,
                "tasks left running by a previous process were marked unknown; they will not be replayed"
            );
        }
        let store = Arc::new(SqliteStore::connect(&config.runner.storage.resolved_path()?).await?);
        let tokens = Arc::new(TokenManager::new(credentials.clone())?);
        let model: Arc<dyn ModelClient> = match config.model_mode {
            ModelMode::Direct => Arc::new(OpenAiModel::new(config.runner.model.clone())?),
            ModelMode::Cloud => Arc::new(ProxyModel::new(
                &config.server_url,
                config.model_name().to_owned(),
                ModelCapabilities {
                    tools: true,
                    streaming: false,
                },
                tokens.clone(),
                config.runner.model.request_timeout_secs,
                config.runner.model.max_retries,
            )?),
        };
        let pending = pending_approvals();
        let executor = Arc::new(TaskExecutor::new(
            config.clone(),
            model,
            ledger.clone(),
            store,
            pending,
        )?);
        Ok(Self {
            config,
            tokens,
            ledger,
            executor,
            sink: Arc::new(SessionSink::default()),
            active: Arc::new(Mutex::new(HashMap::new())),
        })
    }

    /// 运行重连循环；收到 `shutdown` 后取消本地任务并退出。
    pub async fn run(&self, shutdown: CancellationToken) -> Result<(), AgentError> {
        let mut attempt: u32 = 0;
        let mut force_refresh = false;
        loop {
            if shutdown.is_cancelled() {
                return Ok(());
            }
            let token = match self.tokens.access_token(force_refresh).await {
                Ok(token) => token,
                Err(err) => {
                    warn!(error = %err, "failed to obtain device access token");
                    attempt = attempt.saturating_add(1);
                    if self.attempts_exhausted(attempt) {
                        return Err(AgentError::Auth(err.to_string()));
                    }
                    if !sleep_backoff(&self.config.reconnect, attempt, &shutdown).await {
                        return Ok(());
                    }
                    continue;
                }
            };

            let outcome = self.run_session(&token, &shutdown).await;
            let outcome = match outcome {
                Ok(outcome) => outcome,
                Err(err) => {
                    warn!(error = %err, "agent connection ended");
                    SessionOutcome {
                        welcomed: false,
                        end: SessionEnd::Disconnected(err.to_string()),
                    }
                }
            };
            if outcome.welcomed {
                attempt = 0;
            }
            match outcome.end {
                SessionEnd::Shutdown => return Ok(()),
                SessionEnd::AuthFailed(reason) => {
                    warn!(%reason, "cloud rejected agent authentication");
                    force_refresh = true;
                }
                SessionEnd::ServerShutdown {
                    reason,
                    retry_after_secs,
                } => {
                    info!(%reason, retry_after_secs, "cloud requested a reconnect");
                    force_refresh = false;
                    attempt = attempt.saturating_add(1);
                    if self.attempts_exhausted(attempt) {
                        return Err(AgentError::ConnectionClosed(reason));
                    }
                    let delay = Duration::from_secs(retry_after_secs.clamp(1, 600));
                    if !sleep_until_or_shutdown(delay, &shutdown).await {
                        return Ok(());
                    }
                    continue;
                }
                SessionEnd::Disconnected(reason) => {
                    debug!(%reason, "websocket disconnected");
                    force_refresh = false;
                }
            }

            attempt = attempt.saturating_add(1);
            if self.attempts_exhausted(attempt) {
                return Err(AgentError::ConnectionClosed(format!(
                    "exhausted {} reconnect attempts",
                    self.config.reconnect.max_attempts
                )));
            }
            if !sleep_backoff(&self.config.reconnect, attempt, &shutdown).await {
                return Ok(());
            }
        }
    }

    /// 是否达到配置的重连上限；0 表示无限重试。
    fn attempts_exhausted(&self, attempt: u32) -> bool {
        self.config.reconnect.max_attempts > 0 && attempt >= self.config.reconnect.max_attempts
    }

    /// 建立一次 WebSocket 会话并处理消息，直到断开或取消。
    async fn run_session(
        &self,
        token: &str,
        shutdown: &CancellationToken,
    ) -> Result<SessionOutcome, AgentError> {
        let ws_url = websocket_url(&self.config.server_url)?;
        let mut request = ws_url
            .as_str()
            .into_client_request()
            .map_err(AgentError::WebSocket)?;
        let mut auth = HeaderValue::from_str(&format!("Bearer {token}"))
            .map_err(|err| AgentError::Config(format!("invalid access token: {err}")))?;
        auth.set_sensitive(true);
        request.headers_mut().insert(AUTHORIZATION, auth);

        let (socket, _response) = connect_async(request).await?;
        info!(url = %ws_url, "connected to Coder Cloud");
        let (write, mut read) = socket.split();
        let (tx, rx) = mpsc::unbounded_channel::<ClientMessage>();
        let mut writer = tokio::spawn(ws_writer(write, rx));

        let in_flight = self.ledger.in_flight().await.unwrap_or_else(|err| {
            error!(error = %err, "failed to read in-flight tasks from ledger");
            Vec::<InFlightTask>::new()
        });
        let capabilities = AgentCapabilities {
            tools: TOOL_NAMES.iter().map(|name| (*name).to_owned()).collect(),
            workspace_roots: self
                .config
                .workspace_roots
                .iter()
                .map(|root| root.display().to_string())
                .collect(),
            max_parallel_tasks: self.config.max_parallel_tasks,
        };
        let hello = ClientMessage::Hello {
            protocol_version: PROTOCOL_VERSION,
            agent_version: env!("CARGO_PKG_VERSION").to_owned(),
            device_name: self.config.device_name.clone(),
            platform: platform_name().to_owned(),
            capabilities,
            in_flight,
        };
        if tx.send(hello).is_err() {
            writer.abort();
            return Err(AgentError::ConnectionClosed(
                "websocket writer is not available".into(),
            ));
        }

        let welcome = match tokio::time::timeout(
            Duration::from_secs(30),
            wait_for_welcome(&mut read),
        )
        .await
        {
            Ok(Ok(welcome)) => welcome,
            Ok(Err(err)) => {
                writer.abort();
                return Err(err);
            }
            Err(_) => {
                writer.abort();
                return Err(AgentError::ConnectionClosed(
                    "timed out waiting for server welcome".into(),
                ));
            }
        };
        if !is_protocol_compatible(welcome.protocol_version) {
            let _ = tx.send(ClientMessage::Goodbye {
                reason: format!("unsupported protocol version {}", welcome.protocol_version),
            });
            writer.abort();
            return Err(AgentError::ProtocolViolation(format!(
                "server protocol {} is not compatible with client {}",
                welcome.protocol_version, PROTOCOL_VERSION
            )));
        }

        let session_id = welcome.session_id;
        self.sink.set(session_id.as_uuid(), tx.clone());
        info!(
            %session_id,
            tasks = welcome.tasks.len(),
            canceled = welcome.canceled.len(),
            "agent session established"
        );

        for canceled in &welcome.canceled {
            self.cancel_task(canceled.task_id, canceled.attempt).await;
            self.sink.send(ClientMessage::CancelAck {
                task_id: canceled.task_id,
                attempt: canceled.attempt,
            });
        }
        match self.ledger.unreported_results().await {
            Ok(results) => {
                for pending in results {
                    debug!(
                        task_id = %pending.task_id,
                        attempt = pending.attempt,
                        status = %pending.result.status,
                        "resending unacknowledged task result"
                    );
                    self.sink.send(result_message(
                        pending.task_id,
                        pending.attempt,
                        &pending.result,
                    ));
                }
            }
            Err(err) => error!(error = %err, "failed to load unreported task results"),
        }
        for assignment in welcome.tasks {
            self.dispatch(assignment).await;
        }

        let heartbeat_period = Duration::from_secs(self.config.heartbeat_secs);
        let mut heartbeat = tokio::time::interval_at(
            tokio::time::Instant::now() + heartbeat_period,
            heartbeat_period,
        );
        heartbeat.set_missed_tick_behavior(MissedTickBehavior::Delay);

        let end = loop {
            tokio::select! {
                _ = shutdown.cancelled() => {
                    self.sink.send(ClientMessage::Goodbye {
                        reason: "agent shutting down".into(),
                    });
                    self.cancel_all().await;
                    tokio::time::sleep(Duration::from_millis(150)).await;
                    break SessionEnd::Shutdown;
                }
                result = &mut writer => {
                    let reason = match result {
                        Ok(Ok(())) => "websocket writer stopped".to_owned(),
                        Ok(Err(err)) => err.to_string(),
                        Err(err) => format!("websocket writer task failed: {err}"),
                    };
                    break SessionEnd::Disconnected(reason);
                }
                _ = heartbeat.tick() => {
                    if !self.sink.send(ClientMessage::Heartbeat { ts: Utc::now() }) {
                        break SessionEnd::Disconnected("cannot send heartbeat".into());
                    }
                }
                incoming = read.next() => {
                    let Some(incoming) = incoming else {
                        break SessionEnd::Disconnected("websocket closed by server".into());
                    };
                    let message = match incoming {
                        Ok(Message::Text(text)) => {
                            match serde_json::from_str::<ServerMessage>(text.as_str()) {
                                Ok(message) => message,
                                Err(err) => {
                                    break SessionEnd::Disconnected(format!(
                                        "invalid server message: {err}"
                                    ));
                                }
                            }
                        }
                        Ok(Message::Close(frame)) => {
                            break SessionEnd::Disconnected(format!(
                                "server closed the websocket: {frame:?}"
                            ));
                        }
                        Ok(Message::Ping(_) | Message::Pong(_) | Message::Binary(_) | Message::Frame(_)) => continue,
                        Err(err) => break SessionEnd::Disconnected(err.to_string()),
                    };
                    match message {
                        ServerMessage::Welcome { .. } => {
                            break SessionEnd::Disconnected(
                                "server sent a duplicate welcome message".into(),
                            );
                        }
                        ServerMessage::HeartbeatAck { .. } => {}
                        ServerMessage::TaskAssign { assignment } => {
                            self.dispatch(assignment).await;
                        }
                        ServerMessage::TaskCancel { task_id, attempt } => {
                            self.cancel_task(task_id, attempt).await;
                            self.sink.send(ClientMessage::CancelAck { task_id, attempt });
                        }
                        ServerMessage::TaskResultAck { task_id, attempt } => {
                            if let Err(err) = self.ledger.mark_reported(task_id, attempt).await {
                                error!(%task_id, attempt, error = %err, "failed to mark result reported");
                            }
                        }
                        ServerMessage::PermissionDecision {
                            task_id,
                            request_id,
                            granted,
                            reason,
                        } => {
                            let found = resolve_permission(
                                &self.executor.pending_approvals(),
                                task_id,
                                request_id,
                                granted,
                            )
                            .await;
                            if !found {
                                debug!(
                                    %task_id,
                                    %request_id,
                                    reason = reason.as_deref().unwrap_or(""),
                                    "permission decision did not match a pending request"
                                );
                            }
                        }
                        ServerMessage::Error { code, message, .. } => {
                            if matches!(code, ErrorCode::Unauthorized | ErrorCode::Forbidden) {
                                break SessionEnd::AuthFailed(message);
                            }
                            warn!(code = code.as_str(), %message, "cloud reported an error");
                        }
                        ServerMessage::ServerShutdown {
                            reason,
                            retry_after_secs,
                        } => {
                            break SessionEnd::ServerShutdown {
                                reason,
                                retry_after_secs,
                            };
                        }
                    }
                }
            }
        };

        self.sink.clear(session_id.as_uuid());
        writer.abort();
        let _ = writer.await;
        Ok(SessionOutcome {
            welcomed: true,
            end,
        })
    }

    /// 处理一条任务分配，必要时拒绝重复或超额的尝试。
    async fn dispatch(&self, assignment: TaskAssignment) {
        let task_id = assignment.task_id;
        let attempt = assignment.attempt;
        let key = (task_id, attempt);
        {
            let active = self.active.lock().await;
            if let Some(task) = active.get(&key) {
                // 重连后服务端会重发仍在执行的任务；只补发一次开始消息。
                self.sink.send(ClientMessage::TaskStarted {
                    task_id,
                    attempt,
                    started_at: task.started_at,
                });
                return;
            }
            if active.len() >= self.config.max_parallel_tasks as usize {
                self.sink.send(rejected_message(
                    task_id,
                    attempt,
                    "agent reached max_parallel_tasks",
                ));
                return;
            }
        }

        match self.ledger.begin_task(task_id, attempt).await {
            Ok(None) => {}
            Ok(Some(LocalTaskState::Started)) => {
                // 有开始记录但进程内没有对应任务，说明无法确认执行结果。
                let result = StoredTaskResult {
                    status: TaskStatus::Unknown,
                    summary: "attempt was claimed locally but no live task exists".into(),
                    error: Some(
                        "the agent cannot confirm whether this attempt executed; it will not be replayed"
                            .into(),
                    ),
                    duration_ms: 0,
                    files_changed: Vec::new(),
                    session_id: None,
                };
                if let Err(err) = self.ledger.finish_task(task_id, attempt, &result).await {
                    error!(%task_id, attempt, error = %err, "failed to persist unknown task state");
                }
                self.sink.send(result_message(task_id, attempt, &result));
                return;
            }
            Ok(Some(_)) => {
                debug!(%task_id, attempt, "duplicate assignment for a finished attempt ignored");
                return;
            }
            Err(err) => {
                error!(%task_id, attempt, error = %err, "task ledger unavailable");
                self.sink.send(rejected_message(
                    task_id,
                    attempt,
                    "local task ledger is unavailable",
                ));
                return;
            }
        }

        let started_at = Utc::now();
        let cancel = CancellationToken::new();
        self.active.lock().await.insert(
            key,
            ActiveTask {
                cancel: cancel.clone(),
                started_at,
            },
        );
        self.sink.send(ClientMessage::TaskStarted {
            task_id,
            attempt,
            started_at,
        });

        let progress = self.sink.sender();
        let executor = self.executor.clone();
        let sink = self.sink.clone();
        let active = self.active.clone();
        tokio::spawn(async move {
            let result = executor.execute_task(&assignment, progress, cancel).await;
            sink.send(result_message(task_id, attempt, &result));
            active.lock().await.remove(&key);
        });
    }

    /// 请求取消某个尝试，并等待任务自行结束。
    async fn cancel_task(&self, task_id: TaskId, attempt: u32) {
        let active = self.active.lock().await;
        if let Some(task) = active.get(&(task_id, attempt)) {
            task.cancel.cancel();
        }
    }

    /// 取消所有本地任务，用于进程退出。
    async fn cancel_all(&self) {
        let active = self.active.lock().await;
        for task in active.values() {
            task.cancel.cancel();
        }
    }
}

/// 等待服务端欢迎消息，忽略其他非错误消息。
async fn wait_for_welcome<S>(
    read: &mut futures::stream::SplitStream<tokio_tungstenite::WebSocketStream<S>>,
) -> Result<WelcomeData, AgentError>
where
    S: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin,
{
    loop {
        match read.next().await {
            Some(Ok(Message::Text(text))) => {
                match serde_json::from_str::<ServerMessage>(text.as_str()) {
                    Ok(ServerMessage::Welcome {
                        protocol_version,
                        session_id,
                        tasks,
                        canceled,
                        ..
                    }) => {
                        return Ok(WelcomeData {
                            protocol_version,
                            session_id,
                            tasks,
                            canceled,
                        });
                    }
                    Ok(ServerMessage::Error { code, message, .. }) => {
                        return Err(AgentError::Server(format!(
                            "server rejected the agent ({code:?}): {message}"
                        )));
                    }
                    Ok(_) => {}
                    Err(err) => {
                        return Err(AgentError::ProtocolViolation(format!(
                            "invalid server message before welcome: {err}"
                        )));
                    }
                }
            }
            Some(Ok(Message::Close(frame))) => {
                return Err(AgentError::ConnectionClosed(format!(
                    "server closed the connection before welcome: {frame:?}"
                )));
            }
            Some(Ok(_)) => {}
            Some(Err(err)) => return Err(AgentError::WebSocket(err)),
            None => {
                return Err(AgentError::ConnectionClosed(
                    "connection ended before welcome".into(),
                ));
            }
        }
    }
}

/// 把协议消息写入 WebSocket；通道关闭时优雅结束。
async fn ws_writer<S>(
    mut write: futures::stream::SplitSink<tokio_tungstenite::WebSocketStream<S>, Message>,
    mut rx: mpsc::UnboundedReceiver<ClientMessage>,
) -> Result<(), AgentError>
where
    S: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin,
{
    while let Some(message) = rx.recv().await {
        let json = serde_json::to_string(&message)?;
        write.send(Message::Text(json.into())).await?;
    }
    let _ = write.close().await;
    Ok(())
}

/// 由服务器地址构造 WebSocket 端点，保留反向代理路径前缀。
fn websocket_url(server_url: &str) -> Result<Url, AgentError> {
    let base = server_url.trim_end_matches('/');
    let mut url = Url::parse(&format!("{base}/api/v1/agent/ws"))
        .map_err(|err| AgentError::Config(format!("invalid server URL: {err}")))?;
    let scheme = if url.scheme() == "https" { "wss" } else { "ws" };
    url.set_scheme(scheme)
        .map_err(|_| AgentError::Config("cannot derive websocket scheme".into()))?;
    Ok(url)
}

/// 计算指数退避时长（不含抖动）。
fn backoff_delay_ms(config: &ReconnectConfig, attempt: u32) -> u64 {
    let exponent = attempt.saturating_sub(1).min(20);
    let base = config
        .initial_delay_ms
        .saturating_mul(1u64.checked_shl(exponent).unwrap_or(u64::MAX));
    base.min(config.max_delay_secs.saturating_mul(1_000))
}

/// 指数退避加抖动；关闭时返回 `false`。
async fn sleep_backoff(
    config: &ReconnectConfig,
    attempt: u32,
    shutdown: &CancellationToken,
) -> bool {
    let jitter = if config.jitter_ms > 0 {
        rand::rng().random_range(0..=config.jitter_ms)
    } else {
        0
    };
    sleep_until_or_shutdown(
        Duration::from_millis(backoff_delay_ms(config, attempt).saturating_add(jitter)),
        shutdown,
    )
    .await
}

/// 等待指定时长，可在关闭时提前返回。
async fn sleep_until_or_shutdown(delay: Duration, shutdown: &CancellationToken) -> bool {
    tokio::select! {
        _ = shutdown.cancelled() => false,
        _ = tokio::time::sleep(delay) => true,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn builds_websocket_url_from_https() {
        let url = websocket_url("https://coder.example.com").unwrap();
        assert_eq!(url.as_str(), "wss://coder.example.com/api/v1/agent/ws");
    }

    #[test]
    fn preserves_reverse_proxy_prefix() {
        let url = websocket_url("https://example.com/coder").unwrap();
        assert_eq!(url.as_str(), "wss://example.com/coder/api/v1/agent/ws");
    }

    #[test]
    fn backoff_is_capped() {
        let config = ReconnectConfig {
            initial_delay_ms: 100,
            max_delay_secs: 1,
            jitter_ms: 0,
            max_attempts: 0,
        };
        assert_eq!(backoff_delay_ms(&config, 4), 800);
        assert_eq!(backoff_delay_ms(&config, 5), 1_000);
        assert_eq!(backoff_delay_ms(&config, 20), 1_000);
    }
}
