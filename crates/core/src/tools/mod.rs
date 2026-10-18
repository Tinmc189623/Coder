//! 工具系统：每个工具声明 JSON Schema、风险等级并执行真实操作。

use std::{collections::HashMap, path::Path, sync::Arc};

use async_trait::async_trait;
use coder_protocol::{FileChangeKind, PlanItem, RiskLevel};
use serde_json::Value;
use thiserror::Error;
use tokio::sync::{Mutex, broadcast};
use tokio_util::sync::CancellationToken;

use crate::{
    events::AgentEvent, model::ToolSpec, permissions::PermissionPolicy, workspace::Workspace,
};

pub mod filesystem;
pub mod git;
pub mod misc;
pub mod search;
pub mod shell;

pub use filesystem::{EditFileTool, ListFilesTool, ReadFileTool, WriteFileTool};
pub use git::GitTool;
pub use misc::{CheckPermissionsTool, UpdatePlanTool};
pub use search::SearchTool;
pub use shell::{RunTestsTool, ShellTool};

/// 工具错误。工具错误会作为文本反馈给模型，而不是直接终止 Agent。
#[derive(Debug, Error)]
pub enum ToolError {
    #[error("invalid arguments: {0}")]
    InvalidArguments(String),
    #[error("permission denied: {0}")]
    PermissionDenied(String),
    #[error("not found: {0}")]
    NotFound(String),
    #[error("io error: {0}")]
    Io(String),
    #[error("execution error: {0}")]
    Execution(String),
    #[error("tool was canceled")]
    Canceled,
}

impl From<std::io::Error> for ToolError {
    fn from(err: std::io::Error) -> Self {
        Self::Io(err.to_string())
    }
}

impl From<crate::error::CoreError> for ToolError {
    fn from(err: crate::error::CoreError) -> Self {
        match err {
            crate::error::CoreError::PermissionDenied(message) => Self::PermissionDenied(message),
            crate::error::CoreError::Workspace(message) => Self::InvalidArguments(message),
            crate::error::CoreError::Canceled => Self::Canceled,
            other => Self::Io(other.to_string()),
        }
    }
}

/// 一次文件变更记录。
#[derive(Clone, Debug)]
pub struct FileChange {
    pub path: String,
    pub kind: FileChangeKind,
}

/// 工具执行结果。
#[derive(Clone, Debug)]
pub struct ToolOutput {
    /// 返回给模型的文本。
    pub content: String,
    /// 用于事件与日志的一行摘要。
    pub summary: String,
    /// 本次操作改变的文件。
    pub changed_files: Vec<FileChange>,
}

impl ToolOutput {
    /// 构造纯文本结果。
    pub fn text(content: impl Into<String>, summary: impl Into<String>) -> Self {
        Self {
            content: content.into(),
            summary: summary.into(),
            changed_files: Vec::new(),
        }
    }
}

/// 工具执行上下文。
pub struct ToolContext {
    pub workspace: Arc<Workspace>,
    pub policy: Arc<PermissionPolicy>,
    pub cancel: CancellationToken,
    pub events: broadcast::Sender<AgentEvent>,
    pub plan: Arc<Mutex<Vec<PlanItem>>>,
    /// 送入模型前的最大字符数。
    pub max_output_chars: usize,
}

impl ToolContext {
    /// 发布事件；没有订阅者时静默忽略。
    pub fn emit(&self, event: AgentEvent) {
        let _ = self.events.send(event);
    }

    /// 截断工具输出，避免单次结果撑爆上下文。
    pub fn clamp_output(&self, text: String) -> String {
        if text.chars().count() <= self.max_output_chars {
            return text;
        }
        let truncated: String = text.chars().take(self.max_output_chars).collect();
        format!(
            "{truncated}\n\n[output truncated: {} characters omitted]",
            text.chars().count() - self.max_output_chars
        )
    }
}

/// 所有工具实现的统一接口。
#[async_trait]
pub trait Tool: Send + Sync {
    /// 工具定义。
    fn spec(&self) -> ToolSpec;

    /// 根据参数计算风险等级。
    fn risk(&self, _args: &Value) -> RiskLevel {
        RiskLevel::Low
    }

    /// 执行工具。
    async fn execute(&self, ctx: &ToolContext, args: Value) -> Result<ToolOutput, ToolError>;
}

/// 工具注册表。
pub struct ToolRegistry {
    tools: HashMap<String, Arc<dyn Tool>>,
    specs: Vec<ToolSpec>,
}

impl ToolRegistry {
    /// 构造内置工具集合。
    pub fn builtin(policy: Arc<PermissionPolicy>) -> Self {
        let mut registry = Self {
            tools: HashMap::new(),
            specs: Vec::new(),
        };
        registry.register(Arc::new(ReadFileTool));
        registry.register(Arc::new(WriteFileTool));
        registry.register(Arc::new(EditFileTool));
        registry.register(Arc::new(ListFilesTool));
        registry.register(Arc::new(SearchTool));
        registry.register(Arc::new(ShellTool::new(policy.clone())));
        registry.register(Arc::new(RunTestsTool::new(policy)));
        registry.register(Arc::new(GitTool));
        registry.register(Arc::new(UpdatePlanTool));
        registry.register(Arc::new(CheckPermissionsTool));
        registry
    }

    /// 注册单个工具。
    pub fn register(&mut self, tool: Arc<dyn Tool>) {
        let spec = tool.spec();
        self.specs.push(spec.clone());
        self.tools.insert(spec.name, tool);
    }

    /// 返回全部工具描述。
    pub fn specs(&self) -> &[ToolSpec] {
        &self.specs
    }

    /// 按名称查找工具。
    pub fn get(&self, name: &str) -> Option<Arc<dyn Tool>> {
        self.tools.get(name).cloned()
    }
}

/// 从参数中读取必填字符串。
pub(crate) fn required_str(args: &Value, key: &str) -> Result<String, ToolError> {
    args.get(key)
        .and_then(Value::as_str)
        .map(ToOwned::to_owned)
        .ok_or_else(|| ToolError::InvalidArguments(format!("missing string field `{key}`")))
}

/// 从参数中读取可选字符串。
pub(crate) fn optional_str(args: &Value, key: &str) -> Option<String> {
    args.get(key).and_then(Value::as_str).map(ToOwned::to_owned)
}

/// 从参数中读取可选无符号整数。
pub(crate) fn optional_u64(args: &Value, key: &str) -> Option<u64> {
    args.get(key).and_then(Value::as_u64)
}

/// 从参数中读取可选布尔值。
pub(crate) fn optional_bool(args: &Value, key: &str, default: bool) -> bool {
    args.get(key).and_then(Value::as_bool).unwrap_or(default)
}

/// 根据路径生成用于事件的相对路径。
pub(crate) fn display_path(workspace: &Workspace, path: &Path) -> String {
    workspace.relative(path)
}

/// 将 `FileChange` 转换为事件。
pub(crate) fn emit_file_change(ctx: &ToolContext, path: &Path, kind: FileChangeKind) {
    ctx.emit(AgentEvent::FileChanged {
        path: display_path(&ctx.workspace, path),
        change: kind,
    });
}
