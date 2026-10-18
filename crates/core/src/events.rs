use coder_protocol::{FileChangeKind, PlanItem, TaskStatus};
use serde::Serialize;
use uuid::Uuid;

/// Agent 执行期间对外发布的结构化事件。
///
/// CLI 直接渲染这些事件；本地 Agent 将其转换为协议进度消息发送到云端。
#[derive(Clone, Debug, Serialize)]
#[serde(tag = "event", rename_all = "snake_case")]
pub enum AgentEvent {
    SessionStarted {
        session_id: Uuid,
        workspace: String,
        model: String,
    },
    Iteration {
        index: u32,
        max: u32,
    },
    AssistantText {
        text: String,
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
    TokenUsage {
        input_tokens: u64,
        output_tokens: u64,
    },
    Finished {
        status: TaskStatus,
        summary: String,
    },
    Log {
        level: String,
        message: String,
    },
}
