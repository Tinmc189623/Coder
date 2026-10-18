//! `coder sessions`：查看本地 SQLite 会话记录。

use anyhow::{Context, Result, bail};
use coder_core::{
    RunnerConfig,
    model::Role,
    store::{SessionDetail, SqliteStore},
};
use coder_protocol::SessionId;
use serde_json::json;

use crate::{
    SessionsCommand, SessionsListArgs, SessionsShowArgs, config_util::resolve_config_path,
};

/// 分发会话子命令。
pub async fn execute(command: SessionsCommand) -> Result<u8> {
    match command {
        SessionsCommand::List(args) => list(args).await,
        SessionsCommand::Show(args) => show(args).await,
    }
}

/// 列出最近的会话。
async fn list(mut args: SessionsListArgs) -> Result<u8> {
    let store = open_store(args.config.take()).await?;
    let sessions = store.list_sessions(args.limit).await?;
    if args.json {
        let values: Vec<_> = sessions.iter().map(summary_json).collect::<Vec<_>>();
        println!("{}", serde_json::to_string_pretty(&values)?);
        return Ok(0);
    }
    if sessions.is_empty() {
        println!("没有本地会话记录。");
        return Ok(0);
    }
    println!("{:<36} {:<10} {:<25} TASK", "SESSION", "STATUS", "UPDATED");
    for session in sessions {
        println!(
            "{:<36} {:<10} {:<25} {}",
            session.session_id,
            session.status.as_str(),
            session.updated_at.to_rfc3339(),
            truncate(&session.task.replace('\n', " "), 60)
        );
    }
    Ok(0)
}

/// 显示单个会话及消息记录。
async fn show(mut args: SessionsShowArgs) -> Result<u8> {
    let session_id: SessionId = args
        .session_id
        .parse()
        .with_context(|| format!("无效的会话 ID `{}`", args.session_id))?;
    let store = open_store(args.config.take()).await?;
    let Some(detail) = store.session_detail(session_id).await? else {
        bail!("会话不存在: {session_id}");
    };
    if args.json {
        let value = json!({
            "session": summary_json(&detail.summary),
            "messages": serde_json::to_value(&detail.messages)?,
        });
        println!("{}", serde_json::to_string_pretty(&value)?);
        return Ok(0);
    }
    print_detail(&detail);
    Ok(0)
}

/// 打开会话数据库。
async fn open_store(path: Option<std::path::PathBuf>) -> Result<SqliteStore> {
    let config_path = resolve_config_path(path, "config.toml")?;
    let config = RunnerConfig::load_unvalidated(config_path.as_deref())?;
    let sqlite_path = config.storage.resolved_path()?;
    SqliteStore::connect(&sqlite_path)
        .await
        .with_context(|| format!("无法打开会话数据库 {}", sqlite_path.display()))
}

/// 构造会话摘要 JSON。
fn summary_json(session: &coder_core::store::SessionSummary) -> serde_json::Value {
    json!({
        "session_id": session.session_id,
        "task": &session.task,
        "workspace": &session.workspace,
        "model": &session.model,
        "status": session.status.as_str(),
        "summary": &session.summary,
        "iterations": session.iterations,
        "input_tokens": session.input_tokens,
        "output_tokens": session.output_tokens,
        "created_at": session.created_at.to_rfc3339(),
        "updated_at": session.updated_at.to_rfc3339(),
    })
}

/// 以人类可读形式打印会话。
fn print_detail(detail: &SessionDetail) {
    let summary = &detail.summary;
    println!("会话: {}", summary.session_id);
    println!("状态: {}", summary.status.as_str());
    println!("模型: {}", summary.model);
    println!("工作区: {}", summary.workspace);
    println!("创建: {}", summary.created_at.to_rfc3339());
    println!("更新: {}", summary.updated_at.to_rfc3339());
    println!(
        "用量: 迭代 {} | token {} 入 / {} 出",
        summary.iterations, summary.input_tokens, summary.output_tokens
    );
    println!("任务: {}", summary.task);
    if let Some(text) = &summary.summary {
        println!("结果: {text}");
    }
    println!();
    for message in &detail.messages {
        if message.role == Role::System {
            continue;
        }
        let role = match message.role {
            Role::System => "system",
            Role::User => "user",
            Role::Assistant => "assistant",
            Role::Tool => "tool",
        };
        if let Some(content) = &message.content
            && !content.trim().is_empty()
        {
            println!("--- {role} ---");
            println!("{content}");
        }
        for call in &message.tool_calls {
            println!("--- {role} tool call ---");
            println!("{}({})", call.name, call.arguments);
        }
        if let Some(call_id) = &message.tool_call_id {
            println!("  (tool_call_id: {call_id})");
        }
    }
}

/// 按字符数截断单行文本。
fn truncate(text: &str, max: usize) -> String {
    if text.chars().count() <= max {
        return text.to_owned();
    }
    let mut truncated: String = text.chars().take(max).collect();
    truncated.push('…');
    truncated
}
