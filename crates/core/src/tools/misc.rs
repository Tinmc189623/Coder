use async_trait::async_trait;
use coder_protocol::PlanItem;
use serde_json::{Value, json};

use super::{Tool, ToolContext, ToolError, ToolOutput, required_str};
use crate::{events::AgentEvent, model::ToolSpec};

/// 更新结构化执行计划。
pub struct UpdatePlanTool;

#[async_trait]
impl Tool for UpdatePlanTool {
    fn spec(&self) -> ToolSpec {
        ToolSpec {
            name: "update_plan".into(),
            description: "Replace the task plan with the current list of items and statuses. Use it to keep multi-step work explicit; at most one item should be in_progress.".into(),
            parameters: json!({
                "type": "object",
                "properties": {
                    "items": {
                        "type": "array",
                        "maxItems": 50,
                        "items": {
                            "type": "object",
                            "properties": {
                                "title": {"type": "string", "minLength": 1, "maxLength": 200},
                                "status": {"type": "string", "enum": ["pending", "in_progress", "completed"]}
                            },
                            "required": ["title", "status"],
                            "additionalProperties": false
                        }
                    }
                },
                "required": ["items"],
                "additionalProperties": false
            }),
        }
    }

    async fn execute(&self, ctx: &ToolContext, args: Value) -> Result<ToolOutput, ToolError> {
        let raw_items = args
            .get("items")
            .and_then(Value::as_array)
            .ok_or_else(|| ToolError::InvalidArguments("missing array field `items`".into()))?;
        if raw_items.len() > 50 {
            return Err(ToolError::InvalidArguments(
                "plan may contain at most 50 items".into(),
            ));
        }
        let mut items = Vec::with_capacity(raw_items.len());
        for item in raw_items {
            let title = required_str(item, "title")?;
            if title.trim().is_empty() || title.chars().count() > 200 {
                return Err(ToolError::InvalidArguments(
                    "plan item title must be 1..=200 characters".into(),
                ));
            }
            let status = match required_str(item, "status")?.as_str() {
                "pending" => coder_protocol::PlanItemStatus::Pending,
                "in_progress" => coder_protocol::PlanItemStatus::InProgress,
                "completed" => coder_protocol::PlanItemStatus::Completed,
                other => {
                    return Err(ToolError::InvalidArguments(format!(
                        "invalid plan item status `{other}`"
                    )));
                }
            };
            items.push(PlanItem { title, status });
        }
        let in_progress = items
            .iter()
            .filter(|item| item.status == coder_protocol::PlanItemStatus::InProgress)
            .count();
        if in_progress > 1 {
            return Err(ToolError::InvalidArguments(
                "at most one plan item may be in_progress".into(),
            ));
        }
        let completed = items
            .iter()
            .filter(|item| item.status == coder_protocol::PlanItemStatus::Completed)
            .count();
        *ctx.plan.lock().await = items.clone();
        ctx.emit(AgentEvent::PlanUpdated {
            items: items.clone(),
        });
        let rendered = items
            .iter()
            .map(|item| {
                let marker = match item.status {
                    coder_protocol::PlanItemStatus::Pending => "[ ]",
                    coder_protocol::PlanItemStatus::InProgress => "[>]",
                    coder_protocol::PlanItemStatus::Completed => "[x]",
                };
                format!("{marker} {}", item.title)
            })
            .collect::<Vec<_>>()
            .join("\n");
        Ok(ToolOutput::text(
            format!(
                "plan updated ({completed}/{} completed)\n{rendered}",
                items.len()
            ),
            format!("plan updated: {completed}/{} completed", items.len()),
        ))
    }
}

/// 在不执行操作的情况下查询权限策略判定结果。
pub struct CheckPermissionsTool;

#[async_trait]
impl Tool for CheckPermissionsTool {
    fn spec(&self) -> ToolSpec {
        ToolSpec {
            name: "check_permissions".into(),
            description: "Check whether an operation would be allowed before attempting it. Read-only; does not grant permissions or execute anything.".into(),
            parameters: json!({
                "type": "object",
                "properties": {
                    "operation": {"type": "string", "enum": ["read", "write", "execute"]},
                    "path": {"type": "string"},
                    "command": {"type": "string"}
                },
                "required": ["operation"],
                "additionalProperties": false
            }),
        }
    }

    async fn execute(&self, ctx: &ToolContext, args: Value) -> Result<ToolOutput, ToolError> {
        let operation = required_str(&args, "operation")?;
        let (check, detail) = match operation.as_str() {
            "read" => {
                let path = required_str(&args, "path")?;
                let resolved = ctx.workspace.resolve_existing(&path)?;
                (
                    ctx.policy.check_read(),
                    format!("path: {}", ctx.workspace.relative(&resolved)),
                )
            }
            "write" => {
                let path = required_str(&args, "path")?;
                let resolved = ctx.workspace.resolve_for_write(&path)?;
                (
                    ctx.policy.check_write(),
                    format!("path: {}", ctx.workspace.relative(&resolved)),
                )
            }
            "execute" => {
                let command = required_str(&args, "command")?;
                (
                    ctx.policy.check_execute(&command),
                    format!("command: {command}"),
                )
            }
            other => {
                return Err(ToolError::InvalidArguments(format!(
                    "unsupported operation `{other}`"
                )));
            }
        };
        let content = format!(
            "allowed: {}\nrisk: {}\nrequires_approval: {}\nreason: {}\n{detail}",
            check.allowed, check.risk, check.requires_approval, check.reason
        );
        Ok(ToolOutput::text(
            content,
            format!("permission check: {} allowed={}", operation, check.allowed),
        ))
    }
}
