use std::{collections::HashMap, path::PathBuf, sync::Arc, time::Duration};

use async_trait::async_trait;
use coder_core::{
    AgentEvent, AgentRunner, ApprovalDecision, ApprovalHandler, ApprovalRequest, CoreError,
    RunOutcome, RunnerConfig, Workspace,
};
use coder_protocol::{
    ClientMessage, PlanItem, ProgressEvent, RequestId, SessionId, TaskAssignment, TaskId,
    TaskLimits, TaskStatus,
};
use tokio::sync::{Mutex, mpsc, oneshot};
use tokio_util::sync::CancellationToken;
use tracing::{debug, error, warn};

use crate::{
    config::LocalAgentConfig,
    error::AgentError,
    ledger::{StoredTaskResult, TaskLedger},
};

/// 等待云端批复的权限请求。
pub struct PendingApproval {
    pub task_id: TaskId,
    pub attempt: u32,
    sender: oneshot::Sender<bool>,
}

/// 权限请求表，由 WebSocket 读写任务共享。
pub type PendingApprovals = Arc<Mutex<HashMap<RequestId, PendingApproval>>>;

/// 创建空的权限请求表。
pub fn pending_approvals() -> PendingApprovals {
    Arc::new(Mutex::new(HashMap::new()))
}

/// 把云端的权限决定路由给对应的高风险工具调用。
///
/// 返回 `false` 表示请求不存在或任务不匹配，调用方应忽略该决定。
pub async fn resolve_permission(
    pending: &PendingApprovals,
    task_id: TaskId,
    request_id: RequestId,
    granted: bool,
) -> bool {
    let mut map = pending.lock().await;
    match map.get(&request_id) {
        Some(entry) if entry.task_id == task_id => {
            if let Some(entry) = map.remove(&request_id) {
                let _ = entry.sender.send(granted);
            }
            true
        }
        _ => false,
    }
}

/// 删除某任务遗留的权限请求，任务结束时调用。
pub async fn clear_permissions_for_task(pending: &PendingApprovals, task_id: TaskId) {
    let mut map = pending.lock().await;
    map.retain(|_, entry| entry.task_id != task_id);
}

/// 把高风险操作审批转发到云端。
struct RemoteApproval {
    task_id: TaskId,
    attempt: u32,
    outbound: mpsc::UnboundedSender<ClientMessage>,
    pending: PendingApprovals,
    timeout: Duration,
}

#[async_trait]
impl ApprovalHandler for RemoteApproval {
    /// 发送权限请求并等待云端决定；超时、断线或取消一律拒绝。
    async fn request(
        &self,
        request: ApprovalRequest,
        cancel: CancellationToken,
    ) -> ApprovalDecision {
        if cancel.is_cancelled() {
            return ApprovalDecision::Deny;
        }
        let request_id = RequestId::new();
        let (tx, rx) = oneshot::channel();
        {
            let mut map = self.pending.lock().await;
            map.insert(
                request_id,
                PendingApproval {
                    task_id: self.task_id,
                    attempt: self.attempt,
                    sender: tx,
                },
            );
        }
        let message = ClientMessage::PermissionRequest {
            task_id: self.task_id,
            attempt: self.attempt,
            request_id,
            tool: request.tool.clone(),
            action: request.action,
            details: request.details,
            risk: request.risk,
        };
        if self.outbound.send(message).is_err() {
            self.pending.lock().await.remove(&request_id);
            return ApprovalDecision::Deny;
        }
        let decision = tokio::select! {
            _ = cancel.cancelled() => None,
            result = rx => result.ok(),
            _ = tokio::time::sleep(self.timeout) => None,
        };
        self.pending.lock().await.remove(&request_id);
        if decision == Some(true) {
            ApprovalDecision::Allow
        } else {
            ApprovalDecision::Deny
        }
    }
}

/// 本地任务执行器。
///
/// 每次尝试都重新创建工作区和执行器；模型、会话存储与账本在任务之间共享。
pub struct TaskExecutor {
    config: Arc<LocalAgentConfig>,
    model: Arc<dyn coder_core::model::ModelClient>,
    ledger: Arc<TaskLedger>,
    store: Arc<coder_core::store::SqliteStore>,
    authorized_roots: Vec<PathBuf>,
    pending: PendingApprovals,
    approval_timeout: Duration,
}

impl TaskExecutor {
    /// 构造任务执行器并规范化授权工作区根目录。
    pub fn new(
        config: Arc<LocalAgentConfig>,
        model: Arc<dyn coder_core::model::ModelClient>,
        ledger: Arc<TaskLedger>,
        store: Arc<coder_core::store::SqliteStore>,
        pending: PendingApprovals,
    ) -> Result<Self, AgentError> {
        let mut authorized_roots = Vec::with_capacity(config.workspace_roots.len());
        for root in &config.workspace_roots {
            let canonical = dunce::canonicalize(root).map_err(|err| {
                AgentError::Config(format!(
                    "workspace root {} cannot be resolved: {err}",
                    root.display()
                ))
            })?;
            if !canonical.is_dir() {
                return Err(AgentError::Config(format!(
                    "workspace root {} is not a directory",
                    canonical.display()
                )));
            }
            authorized_roots.push(canonical);
        }
        Ok(Self {
            approval_timeout: Duration::from_secs(config.approval_timeout_secs),
            config,
            model,
            ledger,
            store,
            authorized_roots,
            pending,
        })
    }

    /// 访问任务共享的权限请求表。
    pub fn pending_approvals(&self) -> PendingApprovals {
        self.pending.clone()
    }

    /// 执行一次任务尝试，把终态写入账本后返回。
    ///
    /// 该函数不会把失败转成“成功”：任何无法确认的情况都会返回 Failed，
    /// 只有模型正常结束且没有工具错误时才会得到 Succeeded。
    pub async fn execute_task(
        &self,
        assignment: &TaskAssignment,
        outbound: Option<mpsc::UnboundedSender<ClientMessage>>,
        cancel: CancellationToken,
    ) -> StoredTaskResult {
        let task_id = assignment.task_id;
        let attempt = assignment.attempt;
        let result = self
            .run_attempt(assignment, outbound, cancel)
            .await
            .unwrap_or_else(|err| StoredTaskResult {
                status: TaskStatus::Failed,
                summary: format!("local execution failed: {err}"),
                error: Some(err.to_string()),
                duration_ms: 0,
                files_changed: Vec::new(),
                session_id: None,
            });
        if let Err(err) = self.ledger.finish_task(task_id, attempt, &result).await {
            error!(%task_id, attempt, error = %err, "failed to persist task result in local ledger");
        }
        clear_permissions_for_task(&self.pending, task_id).await;
        result
    }

    /// 真正构造 AgentRunner 并执行。
    async fn run_attempt(
        &self,
        assignment: &TaskAssignment,
        outbound: Option<mpsc::UnboundedSender<ClientMessage>>,
        cancel: CancellationToken,
    ) -> Result<StoredTaskResult, AgentError> {
        let workspace = self.prepare_workspace(&assignment.workspace)?;
        let runner_config = self.runner_config(&assignment.limits)?;
        let approval = Arc::new(RemoteApproval {
            task_id: assignment.task_id,
            attempt: assignment.attempt,
            outbound: session_sender(&outbound),
            pending: self.pending.clone(),
            timeout: self.approval_timeout,
        });
        let runner = AgentRunner::new(
            runner_config,
            workspace,
            self.model.clone(),
            Some(self.store.clone()),
            approval,
        );
        let session_id = SessionId::new();
        let mut events = runner.subscribe();
        let run = runner.run(&assignment.prompt, session_id, cancel.clone());
        tokio::pin!(run);

        let mut seq = 0u64;
        let mut events_open = true;
        let outcome: Result<RunOutcome, CoreError> = loop {
            tokio::select! {
                result = &mut run => break result,
                event = events.recv(), if events_open => match event {
                    Ok(event) => forward_event(
                        outbound.as_ref(),
                        assignment.task_id,
                        assignment.attempt,
                        &mut seq,
                        event,
                    ),
                    Err(tokio::sync::broadcast::error::RecvError::Lagged(skipped)) => {
                        debug!(skipped, "progress receiver lagged; some progress events were dropped");
                    }
                    Err(tokio::sync::broadcast::error::RecvError::Closed) => {
                        events_open = false;
                    }
                }
            }
        };
        while let Ok(event) = events.try_recv() {
            forward_event(
                outbound.as_ref(),
                assignment.task_id,
                assignment.attempt,
                &mut seq,
                event,
            );
        }

        let outcome = match outcome {
            Ok(outcome) => outcome,
            Err(err) => {
                if cancel.is_cancelled() {
                    return Ok(StoredTaskResult {
                        status: TaskStatus::Canceled,
                        summary: "task canceled during execution".into(),
                        error: Some(err.to_string()),
                        duration_ms: 0,
                        files_changed: Vec::new(),
                        session_id: Some(session_id),
                    });
                }
                return Err(AgentError::Core(err));
            }
        };
        Ok(StoredTaskResult {
            status: outcome.status,
            summary: outcome.summary,
            error: outcome.error,
            duration_ms: outcome.duration_ms,
            files_changed: outcome.files_changed,
            session_id: Some(session_id),
        })
    }

    /// 校验任务工作区位于授权根目录内，并创建受限工作区。
    fn prepare_workspace(&self, requested: &str) -> Result<Arc<Workspace>, AgentError> {
        let canonical = dunce::canonicalize(requested).map_err(|err| {
            AgentError::Config(format!(
                "task workspace {requested} cannot be resolved: {err}"
            ))
        })?;
        if !canonical.is_dir() {
            return Err(AgentError::Config(format!(
                "task workspace {} is not a directory",
                canonical.display()
            )));
        }
        if !self
            .authorized_roots
            .iter()
            .any(|root| canonical.starts_with(root))
        {
            return Err(AgentError::Config(format!(
                "task workspace {} is outside the agent's authorized roots",
                canonical.display()
            )));
        }
        let mut workspace_config = self.config.runner.workspace.clone();
        workspace_config.root = canonical.clone();
        workspace_config.allowed_roots = vec![canonical];
        Ok(Workspace::new(workspace_config)?)
    }

    /// 合并云端限制到本地执行器配置，并限制在本地允许的上限内。
    fn runner_config(&self, limits: &TaskLimits) -> Result<RunnerConfig, AgentError> {
        let mut config = self.config.runner.clone();
        config.agent.max_iterations = limits.max_iterations.clamp(1, 200);
        config.agent.max_duration_secs = limits.max_duration_secs.clamp(1, 7_200);
        config.agent.tool_call_budget = limits.max_tool_calls.clamp(1, 1_000);
        config.agent.token_budget = limits.token_budget.clamp(1, 2_000_000);
        if config.model.max_output_tokens == 0 {
            return Err(AgentError::Config(
                "model.max_output_tokens must be greater than zero".into(),
            ));
        }
        Ok(config)
    }
}

/// 把核心事件转换成协议进度事件。
fn forward_event(
    outbound: Option<&mpsc::UnboundedSender<ClientMessage>>,
    task_id: TaskId,
    attempt: u32,
    seq: &mut u64,
    event: AgentEvent,
) {
    let Some(outbound) = outbound else {
        return;
    };
    let Some(event) = progress_event(event) else {
        return;
    };
    *seq = seq.saturating_add(1);
    if outbound
        .send(ClientMessage::TaskProgress {
            task_id,
            attempt,
            seq: *seq,
            event,
        })
        .is_err()
    {
        debug!(%task_id, attempt, "progress channel closed");
    }
}

/// 权限请求必须走当前会话；没有会话时直接拒绝。
fn session_sender(
    outbound: &Option<mpsc::UnboundedSender<ClientMessage>>,
) -> mpsc::UnboundedSender<ClientMessage> {
    match outbound {
        Some(sender) => sender.clone(),
        None => {
            let (sender, _receiver) = mpsc::unbounded_channel();
            sender
        }
    }
}

/// 映射核心事件到协议事件；没有对应语义的事件记录为日志。
fn progress_event(event: AgentEvent) -> Option<ProgressEvent> {
    Some(match event {
        AgentEvent::SessionStarted {
            workspace, model, ..
        } => ProgressEvent::SessionStarted { workspace, model },
        AgentEvent::Iteration { index, max } => ProgressEvent::Log {
            level: "info".into(),
            message: format!("model iteration {index}/{max}"),
        },
        AgentEvent::AssistantText { text } => ProgressEvent::AssistantText { text },
        AgentEvent::PlanUpdated { items } => ProgressEvent::PlanUpdated {
            items: items
                .into_iter()
                .map(|item| PlanItem {
                    title: item.title,
                    status: item.status,
                })
                .collect(),
        },
        AgentEvent::ToolStarted {
            call_id,
            tool,
            summary,
        } => ProgressEvent::ToolStarted {
            call_id,
            tool,
            summary,
        },
        AgentEvent::ToolFinished {
            call_id,
            tool,
            success,
            summary,
        } => ProgressEvent::ToolFinished {
            call_id,
            tool,
            success,
            summary,
        },
        AgentEvent::FileChanged { path, change } => ProgressEvent::FileChanged { path, change },
        AgentEvent::CommandOutput {
            command,
            exit_code,
            stdout_tail,
            stderr_tail,
        } => ProgressEvent::CommandOutput {
            command,
            exit_code,
            stdout_tail,
            stderr_tail,
        },
        AgentEvent::TokenUsage {
            input_tokens,
            output_tokens,
        } => ProgressEvent::Log {
            level: "debug".into(),
            message: format!("tokens: input={input_tokens} output={output_tokens}"),
        },
        AgentEvent::Finished { status, summary } => ProgressEvent::Log {
            level: "info".into(),
            message: format!("agent finished with status {status}: {summary}"),
        },
        AgentEvent::Log { level, message } => ProgressEvent::Log { level, message },
    })
}

/// 记录任务开始并检查重复投递。
///
/// `Ok(true)` 表示可以执行，`Ok(false)` 表示该尝试已存在且不能重跑。
pub async fn claim_attempt(
    ledger: &TaskLedger,
    task_id: TaskId,
    attempt: u32,
) -> Result<bool, AgentError> {
    match ledger.begin_task(task_id, attempt).await? {
        None => Ok(true),
        Some(state) => {
            warn!(%task_id, attempt, ?state, "duplicate task delivery refused");
            Ok(false)
        }
    }
}

/// 构造任务被拒绝时发送给云端的消息。
pub fn rejected_message(task_id: TaskId, attempt: u32, reason: impl Into<String>) -> ClientMessage {
    ClientMessage::TaskRejected {
        task_id,
        attempt,
        reason: reason.into(),
    }
}

/// 构造任务结果消息。
pub fn result_message(task_id: TaskId, attempt: u32, result: &StoredTaskResult) -> ClientMessage {
    ClientMessage::TaskResult {
        task_id,
        attempt,
        status: result.status,
        summary: result.summary.clone(),
        error: result.error.clone(),
        duration_ms: result.duration_ms,
        files_changed: result.files_changed.clone(),
        session_id: result.session_id,
    }
}
