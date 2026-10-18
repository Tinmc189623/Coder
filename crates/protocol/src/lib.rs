//! Coder 云端、本地 Agent 与 CLI 共用的版本化协议定义。
//!
//! 该 crate 只描述消息与领域枚举，不包含网络或存储实现，便于三端独立演进。

use std::str::FromStr;

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use thiserror::Error;

pub mod ids;

pub use ids::{DeviceId, RequestId, SessionId, TaskId, UserId};

/// 当前协议主版本。客户端与服务端必须主版本一致。
pub const PROTOCOL_VERSION: u32 = 1;
/// 服务端仍支持的最低协议版本。
pub const MIN_PROTOCOL_VERSION: u32 = 1;

/// 判断客户端协议版本是否可以与服务端通信。
pub fn is_protocol_compatible(client_version: u32) -> bool {
    client_version >= MIN_PROTOCOL_VERSION && client_version <= PROTOCOL_VERSION
}

/// 协议层错误。
#[derive(Debug, Error)]
pub enum ProtocolError {
    #[error(
        "unsupported protocol version {0}; server supports {MIN_PROTOCOL_VERSION}..={PROTOCOL_VERSION}"
    )]
    UnsupportedVersion(u32),
    #[error("invalid task status: {0}")]
    InvalidTaskStatus(String),
    #[error("message cannot be encoded: {0}")]
    Encode(String),
}

/// 任务生命周期状态。
///
/// `Unknown` 表示执行结果无法确认，绝不能当作成功处理；只有管理员显式重试
/// 才会产生新的 attempt。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum TaskStatus {
    Pending,
    Assigned,
    Running,
    Succeeded,
    Failed,
    Canceled,
    Unknown,
}

impl TaskStatus {
    /// 是否为终态。
    pub fn is_terminal(self) -> bool {
        matches!(
            self,
            Self::Succeeded | Self::Failed | Self::Canceled | Self::Unknown
        )
    }

    /// 数据库/日志中使用的稳定字符串。
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Pending => "pending",
            Self::Assigned => "assigned",
            Self::Running => "running",
            Self::Succeeded => "succeeded",
            Self::Failed => "failed",
            Self::Canceled => "canceled",
            Self::Unknown => "unknown",
        }
    }
}

impl std::fmt::Display for TaskStatus {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.as_str())
    }
}

impl FromStr for TaskStatus {
    type Err = ProtocolError;

    fn from_str(s: &str) -> Result<Self, Self::Err> {
        match s {
            "pending" => Ok(Self::Pending),
            "assigned" => Ok(Self::Assigned),
            "running" => Ok(Self::Running),
            "succeeded" => Ok(Self::Succeeded),
            "failed" => Ok(Self::Failed),
            "canceled" => Ok(Self::Canceled),
            "unknown" => Ok(Self::Unknown),
            other => Err(ProtocolError::InvalidTaskStatus(other.to_owned())),
        }
    }
}

/// 工具操作的风险等级，用于本地与云端授权判断。
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum RiskLevel {
    Low,
    Medium,
    High,
}

impl RiskLevel {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Low => "low",
            Self::Medium => "medium",
            Self::High => "high",
        }
    }
}

impl std::fmt::Display for RiskLevel {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.as_str())
    }
}

/// 代理循环中的结构化进度事件。
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "event", rename_all = "snake_case")]
pub enum ProgressEvent {
    SessionStarted {
        workspace: String,
        model: String,
    },
    PlanUpdated {
        items: Vec<PlanItem>,
    },
    ToolStarted {
        call_id: String,
        tool: String,
        summary: String,
    },
    ToolFinished {
        call_id: String,
        tool: String,
        success: bool,
        summary: String,
    },
    FileChanged {
        path: String,
        change: FileChangeKind,
    },
    CommandOutput {
        command: String,
        exit_code: Option<i32>,
        stdout_tail: String,
        stderr_tail: String,
    },
    AssistantText {
        text: String,
    },
    Log {
        level: String,
        message: String,
    },
}

/// 计划项状态。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum PlanItemStatus {
    Pending,
    InProgress,
    Completed,
}

/// 计划项。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PlanItem {
    pub title: String,
    pub status: PlanItemStatus,
}

/// 文件变更类型。
#[derive(Clone, Copy, Debug, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum FileChangeKind {
    Created,
    Modified,
    Deleted,
}

/// 本地 Agent 断线前记录的未完成任务状态。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum LocalTaskState {
    Started,
    Completed,
    Failed,
    Canceled,
    Unknown,
}

/// Agent 能力声明，便于云端按能力分配任务。
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct AgentCapabilities {
    pub tools: Vec<String>,
    pub workspace_roots: Vec<String>,
    pub max_parallel_tasks: u32,
}

/// 会话恢复时上报的未完成任务。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct InFlightTask {
    pub task_id: TaskId,
    pub attempt: u32,
    pub state: LocalTaskState,
}

/// 本地 Agent 发往云端的消息。
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum ClientMessage {
    Hello {
        protocol_version: u32,
        agent_version: String,
        device_name: String,
        platform: String,
        capabilities: AgentCapabilities,
        in_flight: Vec<InFlightTask>,
    },
    Heartbeat {
        ts: DateTime<Utc>,
    },
    TaskStarted {
        task_id: TaskId,
        attempt: u32,
        started_at: DateTime<Utc>,
    },
    TaskProgress {
        task_id: TaskId,
        attempt: u32,
        seq: u64,
        event: ProgressEvent,
    },
    TaskResult {
        task_id: TaskId,
        attempt: u32,
        status: TaskStatus,
        summary: String,
        error: Option<String>,
        duration_ms: u64,
        files_changed: Vec<String>,
        session_id: Option<SessionId>,
    },
    TaskRejected {
        task_id: TaskId,
        attempt: u32,
        reason: String,
    },
    TaskInterrupted {
        task_id: TaskId,
        attempt: u32,
        reason: String,
    },
    PermissionRequest {
        task_id: TaskId,
        attempt: u32,
        request_id: RequestId,
        tool: String,
        action: String,
        details: String,
        risk: RiskLevel,
    },
    CancelAck {
        task_id: TaskId,
        attempt: u32,
    },
    Goodbye {
        reason: String,
    },
}

/// 云端发往本地 Agent 的消息。
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum ServerMessage {
    Welcome {
        protocol_version: u32,
        server_version: String,
        session_id: SessionId,
        server_time: DateTime<Utc>,
        tasks: Vec<TaskAssignment>,
        canceled: Vec<TaskRef>,
    },
    HeartbeatAck {
        ts: DateTime<Utc>,
    },
    TaskAssign {
        assignment: TaskAssignment,
    },
    TaskCancel {
        task_id: TaskId,
        attempt: u32,
    },
    /// 云端已幂等接收某次尝试的终态结果；本地账本可停止重发。
    TaskResultAck {
        task_id: TaskId,
        attempt: u32,
    },
    PermissionDecision {
        task_id: TaskId,
        request_id: RequestId,
        granted: bool,
        reason: Option<String>,
    },
    Error {
        code: ErrorCode,
        message: String,
        request_id: Option<RequestId>,
    },
    ServerShutdown {
        reason: String,
        retry_after_secs: u64,
    },
}

/// 任务引用。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct TaskRef {
    pub task_id: TaskId,
    pub attempt: u32,
}

/// 云端分配给本地 Agent 的任务。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct TaskAssignment {
    pub task_id: TaskId,
    pub attempt: u32,
    pub prompt: String,
    pub workspace: String,
    pub limits: TaskLimits,
    pub lease_expires_at: DateTime<Utc>,
    pub created_at: DateTime<Utc>,
}

/// 单任务资源限制。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct TaskLimits {
    pub max_iterations: u32,
    pub max_duration_secs: u64,
    pub max_tool_calls: u32,
    pub token_budget: u64,
}

impl Default for TaskLimits {
    fn default() -> Self {
        Self {
            max_iterations: 30,
            max_duration_secs: 1_800,
            max_tool_calls: 100,
            token_budget: 200_000,
        }
    }
}

/// 统一错误码。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ErrorCode {
    Unauthorized,
    Forbidden,
    InvalidRequest,
    ProtocolVersionUnsupported,
    TaskNotFound,
    TaskConflict,
    PermissionDenied,
    RateLimited,
    Internal,
    Unavailable,
}

impl ErrorCode {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Unauthorized => "unauthorized",
            Self::Forbidden => "forbidden",
            Self::InvalidRequest => "invalid_request",
            Self::ProtocolVersionUnsupported => "protocol_version_unsupported",
            Self::TaskNotFound => "task_not_found",
            Self::TaskConflict => "task_conflict",
            Self::PermissionDenied => "permission_denied",
            Self::RateLimited => "rate_limited",
            Self::Internal => "internal",
            Self::Unavailable => "unavailable",
        }
    }

    /// 对应的 HTTP 状态码，供云端统一错误响应复用。
    pub fn http_status(self) -> u16 {
        match self {
            Self::Unauthorized => 401,
            Self::Forbidden | Self::PermissionDenied => 403,
            Self::InvalidRequest | Self::ProtocolVersionUnsupported => 400,
            Self::TaskNotFound => 404,
            Self::TaskConflict => 409,
            Self::RateLimited => 429,
            Self::Internal => 500,
            Self::Unavailable => 503,
        }
    }
}

/// HTTP 统一错误响应体。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ApiErrorBody {
    pub error: ApiError,
}

/// HTTP 错误详情。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ApiError {
    pub code: String,
    pub message: String,
    pub request_id: Option<String>,
}

impl ApiErrorBody {
    /// 构造统一错误响应。
    pub fn new(code: ErrorCode, message: impl Into<String>, request_id: Option<String>) -> Self {
        Self {
            error: ApiError {
                code: code.as_str().to_owned(),
                message: message.into(),
                request_id,
            },
        }
    }
}

/// 设备注册请求。配对令牌由管理员签发且只能使用一次。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct DeviceRegisterRequest {
    pub pairing_token: String,
    pub name: String,
    pub platform: String,
    pub agent_version: String,
}

/// 设备注册结果，`device_secret` 仅返回一次。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct DeviceRegisterResponse {
    pub device_id: DeviceId,
    pub device_secret: String,
    pub user_id: UserId,
}

/// 设备换取短期访问令牌的请求。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct DeviceTokenRequest {
    pub device_id: DeviceId,
    pub device_secret: String,
}

/// 短期访问令牌响应。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct DeviceTokenResponse {
    pub access_token: String,
    pub token_type: String,
    pub expires_in_secs: u64,
}

/// 创建设备任务请求。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct CreateTaskRequest {
    pub device_id: DeviceId,
    pub prompt: String,
    pub workspace: String,
    #[serde(default)]
    pub limits: Option<TaskLimits>,
}

/// 任务详情视图。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct TaskView {
    pub task_id: TaskId,
    pub device_id: DeviceId,
    pub user_id: UserId,
    pub prompt: String,
    pub workspace: String,
    pub status: TaskStatus,
    pub attempt: u32,
    pub summary: Option<String>,
    pub error: Option<String>,
    pub files_changed: Vec<String>,
    pub created_at: DateTime<Utc>,
    pub updated_at: DateTime<Utc>,
    pub lease_expires_at: Option<DateTime<Utc>>,
    pub limits: TaskLimits,
}

/// 云端权限确认请求。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PermissionDecisionRequest {
    pub granted: bool,
    pub reason: Option<String>,
}

/// 待处理权限确认。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PendingPermission {
    pub task_id: TaskId,
    pub request_id: RequestId,
    pub tool: String,
    pub action: String,
    pub details: String,
    pub risk: RiskLevel,
    pub created_at: DateTime<Utc>,
}

/// 设备视图。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct DeviceView {
    pub device_id: DeviceId,
    pub user_id: UserId,
    pub name: String,
    pub platform: String,
    pub agent_version: String,
    pub online: bool,
    pub last_seen_at: Option<DateTime<Utc>>,
    pub created_at: DateTime<Utc>,
}
