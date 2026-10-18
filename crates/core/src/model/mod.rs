//! 模型适配层。核心循环只依赖 `ModelClient`，不绑定任何单一提供商。

use serde::{Deserialize, Serialize};
use thiserror::Error;
use tokio::sync::mpsc;
use tokio_util::sync::CancellationToken;

use crate::config::ModelCapabilities;

pub mod openai;
pub mod proxy;

pub use openai::OpenAiModel;
pub use proxy::{AccessTokenProvider, ProxyModel};

/// 模型调用错误。
#[derive(Debug, Error)]
pub enum ModelError {
    #[error("model endpoint is not configured: {0}")]
    NotConfigured(String),
    #[error("model HTTP error: {0}")]
    Http(String),
    #[error("model endpoint returned HTTP {status}: {body}")]
    Status { status: u16, body: String },
    #[error("cannot decode model response: {0}")]
    Decode(String),
    #[error("model request was canceled")]
    Canceled,
    #[error("model does not support required capability: {0}")]
    Unsupported(String),
}

/// 对话角色。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Role {
    System,
    User,
    Assistant,
    Tool,
}

/// 模型返回的工具调用。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ToolCall {
    pub id: String,
    pub name: String,
    pub arguments: String,
}

/// 统一对话消息。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ChatMessage {
    pub role: Role,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub content: Option<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub tool_calls: Vec<ToolCall>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub tool_call_id: Option<String>,
}

impl ChatMessage {
    /// 系统消息。
    pub fn system(content: impl Into<String>) -> Self {
        Self {
            role: Role::System,
            content: Some(content.into()),
            tool_calls: Vec::new(),
            tool_call_id: None,
        }
    }

    /// 用户消息。
    pub fn user(content: impl Into<String>) -> Self {
        Self {
            role: Role::User,
            content: Some(content.into()),
            tool_calls: Vec::new(),
            tool_call_id: None,
        }
    }

    /// 助手消息，可同时携带文本和工具调用。
    pub fn assistant(content: Option<String>, tool_calls: Vec<ToolCall>) -> Self {
        Self {
            role: Role::Assistant,
            content,
            tool_calls,
            tool_call_id: None,
        }
    }

    /// 工具执行结果消息。
    pub fn tool_result(call_id: impl Into<String>, content: impl Into<String>) -> Self {
        Self {
            role: Role::Tool,
            content: Some(content.into()),
            tool_calls: Vec::new(),
            tool_call_id: Some(call_id.into()),
        }
    }

    /// 估算该消息占用的 token 数（粗略字符近似，用于上下文预算）。
    pub fn estimated_tokens(&self) -> u64 {
        let content = self.content.as_deref().unwrap_or_default();
        let calls: usize = self
            .tool_calls
            .iter()
            .map(|call| call.name.len() + call.arguments.len() + call.id.len())
            .sum();
        ((content.len() + calls) as u64 / 4).max(1)
    }
}

/// 工具 JSON Schema 描述。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ToolSpec {
    pub name: String,
    pub description: String,
    pub parameters: serde_json::Value,
}

/// 一次模型请求。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ChatRequest {
    pub messages: Vec<ChatMessage>,
    #[serde(default)]
    pub tools: Vec<ToolSpec>,
    pub temperature: f32,
    pub max_output_tokens: u32,
    pub stream: bool,
}

/// Token 用量。
#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize)]
pub struct TokenUsage {
    pub input_tokens: u64,
    pub output_tokens: u64,
}

/// 模型响应。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ModelResponse {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub content: Option<String>,
    #[serde(default)]
    pub tool_calls: Vec<ToolCall>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub finish_reason: Option<String>,
    #[serde(default)]
    pub usage: TokenUsage,
}

impl ModelResponse {
    /// 是否为工具调用回合。
    pub fn has_tool_calls(&self) -> bool {
        !self.tool_calls.is_empty()
    }
}

/// 模型适配器统一接口。
#[async_trait::async_trait]
pub trait ModelClient: Send + Sync {
    /// 模型名称。
    fn model_name(&self) -> &str;

    /// 模型能力声明。
    fn capabilities(&self) -> ModelCapabilities;

    /// 发起一次完整的对话请求；`delta_tx` 用于流式文本增量。
    async fn complete(
        &self,
        request: ChatRequest,
        cancel: CancellationToken,
        delta_tx: Option<mpsc::UnboundedSender<String>>,
    ) -> Result<ModelResponse, ModelError>;
}
