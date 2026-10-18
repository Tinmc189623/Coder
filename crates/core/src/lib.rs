//! Coder 核心：工作区安全边界、工具系统、模型适配与 Agent 执行循环。

pub mod agent;
pub mod config;
pub mod error;
pub mod events;
pub mod model;
pub mod permissions;
pub mod store;
pub mod tools;
pub mod workspace;

pub use agent::{AgentRunner, ApprovalDecision, ApprovalHandler, ApprovalRequest};
pub use config::{RunnerConfig, Secret};
pub use error::{CoreError, RunOutcome};
pub use events::AgentEvent;
pub use workspace::Workspace;
