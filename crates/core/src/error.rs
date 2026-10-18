use coder_protocol::TaskStatus;
use thiserror::Error;

/// 核心执行引擎的统一错误类型。
#[derive(Debug, Error)]
pub enum CoreError {
    #[error("configuration error: {0}")]
    Config(String),
    #[error("workspace error: {0}")]
    Workspace(String),
    #[error("permission denied: {0}")]
    PermissionDenied(String),
    #[error("tool `{tool}` failed: {message}")]
    Tool { tool: String, message: String },
    #[error("model error: {0}")]
    Model(String),
    #[error("storage error: {0}")]
    Storage(#[from] sqlx::Error),
    #[error("database migration error: {0}")]
    Migration(String),
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("task canceled")]
    Canceled,
    #[error("budget exceeded: {0}")]
    BudgetExceeded(String),
}

/// Agent 执行完成后的状态与摘要。
#[derive(Clone, Debug)]
pub struct RunOutcome {
    pub status: TaskStatus,
    pub summary: String,
    pub error: Option<String>,
    pub iterations: u32,
    pub tool_calls: u32,
    pub input_tokens: u64,
    pub output_tokens: u64,
    pub files_changed: Vec<String>,
    pub duration_ms: u64,
}
