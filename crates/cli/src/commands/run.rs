//! `coder run`：在本地工作区直接执行一次智能体任务。

use std::{sync::Arc, time::Duration};

use anyhow::{Context, Result, bail};
use coder_core::{
    AgentRunner, RunnerConfig, Workspace, config::PermissionMode, model::OpenAiModel,
    store::SqliteStore,
};
use coder_protocol::{SessionId, TaskStatus};
use tokio_util::sync::CancellationToken;

use crate::{
    RunArgs, approval::InteractiveApproval, config_util::resolve_config_path, render::EventRenderer,
};

/// 执行一次任务并返回进程退出码。
///
/// 退出码：0 成功，1 失败，2 结果未知，130 被取消。
pub async fn execute(mut args: RunArgs) -> Result<u8> {
    let config_path = resolve_config_path(args.config.take(), "config.toml")?;
    let mut config = RunnerConfig::load_unvalidated(config_path.as_deref())?;

    if let Some(root) = args.workspace.take() {
        config.workspace.root = root;
        config.workspace.allowed_roots.clear();
    }
    if let Some(model) = args.model.take() {
        config.model.model = model;
    }
    if let Some(base_url) = args.base_url.take() {
        config.model.base_url = base_url;
    }
    if let Some(api_key) = args.api_key.take() {
        config.model.api_key = api_key.into();
    }
    if let Some(iterations) = args.max_iterations {
        config.agent.max_iterations = iterations;
    }
    if let Some(duration) = args.max_duration_secs {
        config.agent.max_duration_secs = duration;
    }
    if args.read_only {
        config.permissions.mode = PermissionMode::ReadOnly;
        config.workspace.writable = false;
    }
    if args.yes {
        config.permissions.auto_approve_high_risk = true;
    }
    config.validate()?;
    config.model.validate()?;

    let prompt = args.prompt.join(" ");
    if prompt.trim().is_empty() {
        bail!("任务描述不能为空");
    }

    let workspace = Workspace::new(config.workspace.clone())?;
    let sqlite_path = config.storage.resolved_path()?;
    let store = Arc::new(
        SqliteStore::connect(&sqlite_path)
            .await
            .with_context(|| format!("无法打开会话数据库 {}", sqlite_path.display()))?,
    );
    // 超过任务最长执行时间的 running 会话不可能仍存活，标记为 unknown。
    let orphan_age = Duration::from_secs(config.agent.max_duration_secs.saturating_add(60));
    if let Err(err) = store.mark_orphaned_sessions(orphan_age).await {
        tracing::warn!(error = %err, "无法清理孤儿会话");
    }

    let model_name = config.model.model.clone();
    let model = Arc::new(OpenAiModel::new(config.model.clone())?);
    let runner = AgentRunner::new(
        config,
        workspace.clone(),
        model,
        Some(store),
        Arc::new(InteractiveApproval::new()),
    );

    println!("模型: {model_name}");
    println!("工作区: {}", workspace.root().display());
    println!("会话库: {}", sqlite_path.display());
    if args.yes {
        eprintln!("注意: 已通过 --yes 自动批准高风险操作。");
    }

    let session_id = SessionId::new();
    let cancel = CancellationToken::new();
    let mut events = runner.subscribe();
    let renderer = EventRenderer::new(args.json, args.verbose);
    let mut events_open = true;
    let mut interrupted = false;

    let run = runner.run(&prompt, session_id, cancel.clone());
    tokio::pin!(run);
    let outcome = loop {
        tokio::select! {
            result = &mut run => break result?,
            event = events.recv(), if events_open => match event {
                Ok(event) => renderer.render(&event),
                Err(tokio::sync::broadcast::error::RecvError::Lagged(skipped)) => {
                    eprintln!("警告: 丢弃了 {skipped} 个进度事件");
                }
                Err(tokio::sync::broadcast::error::RecvError::Closed) => {
                    events_open = false;
                }
            },
            _ = tokio::signal::ctrl_c(), if !interrupted => {
                interrupted = true;
                eprintln!();
                eprintln!("收到 Ctrl+C，正在取消任务；等待清理完成后退出。");
                cancel.cancel();
            }
        }
    };
    while let Ok(event) = events.try_recv() {
        renderer.render(&event);
    }

    if args.json {
        let summary = serde_json::json!({
            "session_id": session_id,
            "status": outcome.status.as_str(),
            "summary": outcome.summary,
            "error": outcome.error,
            "iterations": outcome.iterations,
            "tool_calls": outcome.tool_calls,
            "input_tokens": outcome.input_tokens,
            "output_tokens": outcome.output_tokens,
            "files_changed": outcome.files_changed,
            "duration_ms": outcome.duration_ms,
        });
        println!("{}", serde_json::to_string_pretty(&summary)?);
    } else {
        println!(
            "统计: 迭代 {} | 工具 {} | token {} 入 / {} 出 | 耗时 {} ms",
            outcome.iterations,
            outcome.tool_calls,
            outcome.input_tokens,
            outcome.output_tokens,
            outcome.duration_ms
        );
        if !outcome.files_changed.is_empty() {
            println!("变更文件: {}", outcome.files_changed.join(", "));
        }
        if let Some(error) = &outcome.error {
            eprintln!("错误: {error}");
        }
    }

    Ok(exit_code(outcome.status))
}

/// 将终态映射为进程退出码。
fn exit_code(status: TaskStatus) -> u8 {
    match status {
        TaskStatus::Succeeded => 0,
        TaskStatus::Canceled => 130,
        TaskStatus::Unknown => 2,
        _ => 1,
    }
}
