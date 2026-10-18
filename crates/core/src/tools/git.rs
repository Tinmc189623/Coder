use std::{path::Path, process::Stdio, time::Duration};

use async_trait::async_trait;
use serde_json::{Value, json};
use tokio::process::Command;

use super::{
    Tool, ToolContext, ToolError, ToolOutput, optional_bool, optional_str, optional_u64,
    required_str,
};
use crate::{events::AgentEvent, model::ToolSpec};

/// 只读 Git 查询工具。写操作（commit/push/reset）刻意不暴露给模型。
pub struct GitTool;

#[async_trait]
impl Tool for GitTool {
    fn spec(&self) -> ToolSpec {
        ToolSpec {
            name: "git".into(),
            description: "Read-only Git inspection inside the workspace: status, diff, log and show. Does not expose commit, push, reset or checkout. Use it to understand the repository state before editing.".into(),
            parameters: json!({
                "type": "object",
                "properties": {
                    "action": {"type": "string", "enum": ["status", "diff", "log", "show"]},
                    "path": {"type": "string", "description": "Optional path filter for diff."},
                    "staged": {"type": "boolean", "default": false, "description": "For diff: show staged changes."},
                    "limit": {"type": "integer", "minimum": 1, "maximum": 200, "description": "For log: number of commits."},
                    "revision": {"type": "string", "description": "For show: revision, defaults to HEAD."}
                },
                "required": ["action"],
                "additionalProperties": false
            }),
        }
    }

    async fn execute(&self, ctx: &ToolContext, args: Value) -> Result<ToolOutput, ToolError> {
        let action = required_str(&args, "action")?;
        let mut argv: Vec<String> = vec!["--no-pager".into()];
        match action.as_str() {
            "status" => {
                argv.extend(["status".into(), "--porcelain=v1".into(), "--branch".into()]);
            }
            "diff" => {
                argv.push("diff".into());
                argv.push("--no-color".into());
                if optional_bool(&args, "staged", false) {
                    argv.push("--cached".into());
                }
                if let Some(path) = optional_str(&args, "path") {
                    let resolved = ctx.workspace.resolve_existing(&path)?;
                    argv.push("--".into());
                    argv.push(ctx.workspace.relative(&resolved));
                }
            }
            "log" => {
                let limit = optional_u64(&args, "limit").unwrap_or(20).clamp(1, 200);
                argv.extend([
                    "log".into(),
                    "--oneline".into(),
                    "--decorate".into(),
                    "-n".into(),
                    limit.to_string(),
                ]);
            }
            "show" => {
                let revision = optional_str(&args, "revision").unwrap_or_else(|| "HEAD".into());
                validate_revision(&revision)?;
                argv.extend([
                    "show".into(),
                    "--stat".into(),
                    "--no-color".into(),
                    revision,
                ]);
            }
            other => {
                return Err(ToolError::InvalidArguments(format!(
                    "unsupported git action `{other}`"
                )));
            }
        }

        let output = run_git(ctx.workspace.root(), &argv, &ctx.cancel).await?;
        let exit_code = output.status.code();
        let stdout = String::from_utf8_lossy(&output.stdout).into_owned();
        let stderr = String::from_utf8_lossy(&output.stderr).into_owned();
        ctx.emit(AgentEvent::CommandOutput {
            command: format!("git {}", argv.join(" ")),
            exit_code,
            stdout_tail: tail(&stdout, 2_000),
            stderr_tail: tail(&stderr, 2_000),
        });

        let mut content = format!(
            "git {action} (exit {})\n",
            exit_code.map_or("signal".into(), |code| code.to_string())
        );
        if !stdout.trim().is_empty() {
            content.push_str(&stdout);
        } else {
            content.push_str("(no output)");
        }
        if !stderr.trim().is_empty() {
            content.push_str("\n--- stderr ---\n");
            content.push_str(&stderr);
        }
        Ok(ToolOutput::text(
            ctx.clamp_output(content),
            format!(
                "git {action} exited {}",
                exit_code.map_or("signal".into(), |code| code.to_string())
            ),
        ))
    }
}

/// 运行 git 命令并捕获输出，取消或超时后终止进程。
async fn run_git(
    cwd: &Path,
    argv: &[String],
    cancel: &tokio_util::sync::CancellationToken,
) -> Result<std::process::Output, ToolError> {
    let mut command = Command::new("git");
    command
        .args(argv)
        .current_dir(cwd)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true)
        .env_clear()
        .env("GIT_TERMINAL_PROMPT", "0")
        .env("GIT_PAGER", "cat")
        .env("PAGER", "cat");
    for key in [
        "PATH",
        "PATHEXT",
        "SystemRoot",
        "ComSpec",
        "HOME",
        "USERPROFILE",
        "TEMP",
        "TMP",
        "LANG",
    ] {
        if let Ok(value) = std::env::var(key) {
            command.env(key, value);
        }
    }
    let future = command.output();
    let result = tokio::select! {
        _ = cancel.cancelled() => return Err(ToolError::Canceled),
        result = tokio::time::timeout(Duration::from_secs(30), future) => result,
    };
    match result {
        Ok(Ok(output)) => Ok(output),
        Ok(Err(err)) => Err(ToolError::Execution(format!(
            "cannot execute git (is it installed?): {err}"
        ))),
        Err(_) => Err(ToolError::Execution(
            "git command timed out after 30 seconds".into(),
        )),
    }
}

/// 拒绝可能被解释为命令行选项的 revision。
fn validate_revision(revision: &str) -> Result<(), ToolError> {
    if revision.is_empty() || revision.starts_with('-') {
        return Err(ToolError::InvalidArguments(
            "revision must be non-empty and must not start with '-'".into(),
        ));
    }
    if !revision
        .chars()
        .all(|ch| ch.is_ascii_alphanumeric() || "._/-~^@{}".contains(ch))
    {
        return Err(ToolError::InvalidArguments(
            "revision contains unsupported characters".into(),
        ));
    }
    Ok(())
}

/// 返回文本末尾内容。
fn tail(text: &str, max: usize) -> String {
    if text.len() <= max {
        return text.to_owned();
    }
    let mut start = text.len() - max;
    while !text.is_char_boundary(start) {
        start += 1;
    }
    format!("…{}", &text[start..])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_option_like_revision() {
        assert!(validate_revision("--output=/tmp/x").is_err());
        assert!(validate_revision("HEAD~1").is_ok());
    }
}
