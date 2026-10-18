use coder_core::{CoreError, model::ModelError};
use coder_protocol::{ProtocolError, TaskId};
use thiserror::Error;

/// 本地 Agent 的统一错误类型。
#[derive(Debug, Error)]
pub enum AgentError {
    #[error(transparent)]
    Core(#[from] CoreError),
    #[error(transparent)]
    Protocol(#[from] ProtocolError),
    #[error(transparent)]
    Model(#[from] ModelError),
    #[error(transparent)]
    Sqlx(#[from] sqlx::Error),
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error(transparent)]
    Http(#[from] reqwest::Error),
    #[error(transparent)]
    Json(#[from] serde_json::Error),
    #[error(transparent)]
    WebSocket(#[from] tokio_tungstenite::tungstenite::Error),
    #[error("configuration error: {0}")]
    Config(String),
    #[error("credential error: {0}")]
    Credentials(String),
    #[error("authentication failed: {0}")]
    Auth(String),
    #[error("server rejected the request: {0}")]
    Server(String),
    #[error("protocol violation: {0}")]
    ProtocolViolation(String),
    #[error("connection closed: {0}")]
    ConnectionClosed(String),
    #[error("task {0} was rejected: {1}")]
    TaskRejected(TaskId, String),
    #[error("operation canceled")]
    Canceled,
    #[error("agent worker stopped")]
    Stopped,
}
