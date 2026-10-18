//! `coder config`：生成与查看脱敏配置。

use anyhow::{Context, Result, bail};
use coder_core::{
    RunnerConfig,
    config::{default_config_dir, default_data_dir},
};
use coder_local_agent::LocalAgentConfig;
use serde_json::{Value, json};

use crate::{
    ConfigCommand, ConfigInitArgs, ConfigShowArgs,
    config_util::{resolve_config_path, toml_string},
};

/// 分发配置子命令。
pub async fn execute(command: ConfigCommand) -> Result<u8> {
    match command {
        ConfigCommand::Init(args) => init(args),
        ConfigCommand::Show(args) => show(args),
    }
}

/// 生成一份可直接编辑的本地执行配置。
fn init(mut args: ConfigInitArgs) -> Result<u8> {
    let path = match args.path.take() {
        Some(path) => path,
        None => default_config_dir()?.join("config.toml"),
    };
    if path.exists() && !args.force {
        bail!("配置文件已存在: {}；使用 --force 覆盖", path.display());
    }
    let workspace = std::env::current_dir().context("无法获取当前目录")?;
    let data_dir = default_data_dir()?;
    let sqlite_path = data_dir.join("coder.db");
    let content = format!(
        r#"# Coder 本地执行配置。
# 模型 API key 不要写入此文件，使用 CODER_MODEL__API_KEY 环境变量提供。

[workspace]
root = {root}
writable = true
protect_git_dir = true

[storage]
sqlite_path = {sqlite}

[permissions]
mode = "workspace_write"
auto_approve_high_risk = false

[agent]
max_iterations = 30
max_duration_secs = 1800
token_budget = 200000
tool_call_budget = 100

[model]
# 设置 base_url 与 model 并配置 API key 后才能执行真实任务。
max_output_tokens = 8192
context_window = 128000
request_timeout_secs = 120
stream = true
"#,
        root = toml_string(&workspace.display().to_string()),
        sqlite = toml_string(&sqlite_path.display().to_string()),
    );
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)
            .with_context(|| format!("无法创建配置目录 {}", parent.display()))?;
    }
    std::fs::write(&path, content)
        .with_context(|| format!("无法写入配置文件 {}", path.display()))?;
    println!("已生成配置: {}", path.display());
    eprintln!(
        "下一步: 设置 CODER_MODEL__BASE_URL、CODER_MODEL__MODEL、CODER_MODEL__API_KEY，然后运行 coder doctor"
    );
    Ok(0)
}

/// 打印脱敏后的配置。
fn show(mut args: ConfigShowArgs) -> Result<u8> {
    let path = resolve_config_path(
        args.config.take(),
        if args.agent {
            "agent.toml"
        } else {
            "config.toml"
        },
    )?;
    let source = path
        .as_ref()
        .map(|path| path.display().to_string())
        .unwrap_or_else(|| "defaults + environment".to_owned());

    let value = if args.agent {
        let config = LocalAgentConfig::load_unvalidated(path.as_deref())?;
        json!({
            "source": source,
            "server_url": config.server_url,
            "device_name": config.device_name,
            "credentials_path": config.credentials_path,
            "pairing_token": redacted_secret(&config.pairing_token),
            "workspace_roots": config.workspace_roots,
            "storage_path": config.storage_path,
            "model_mode": config.model_mode,
            "heartbeat_secs": config.heartbeat_secs,
            "approval_timeout_secs": config.approval_timeout_secs,
            "max_parallel_tasks": config.max_parallel_tasks,
            "allow_insecure_ws": config.allow_insecure_ws,
            "reconnect": {
                "initial_delay_ms": config.reconnect.initial_delay_ms,
                "max_delay_secs": config.reconnect.max_delay_secs,
                "jitter_ms": config.reconnect.jitter_ms,
                "max_attempts": config.reconnect.max_attempts,
            },
            "runner": runner_view(&config.runner),
        })
    } else {
        let config = RunnerConfig::load_unvalidated(path.as_deref())?;
        json!({
            "source": source,
            "runner": runner_view(&config),
        })
    };
    println!("{}", serde_json::to_string_pretty(&value)?);
    Ok(0)
}

/// 构造隐藏所有密钥的配置视图。
fn runner_view(config: &RunnerConfig) -> Value {
    let extra_headers: serde_json::Map<String, Value> = config
        .model
        .extra_headers
        .keys()
        .map(|name| (name.clone(), Value::String("<redacted>".into())))
        .collect();
    json!({
        "workspace": {
            "root": &config.workspace.root,
            "allowed_roots": &config.workspace.allowed_roots,
            "writable": config.workspace.writable,
            "protect_git_dir": config.workspace.protect_git_dir,
            "max_read_bytes": config.workspace.max_read_bytes,
            "max_write_bytes": config.workspace.max_write_bytes,
        },
        "model": {
            "base_url": &config.model.base_url,
            "model": &config.model.model,
            "api_key": redacted_secret(&config.model.api_key),
            "temperature": (f64::from(config.model.temperature) * 1000.0).round() / 1000.0,
            "max_output_tokens": config.model.max_output_tokens,
            "context_window": config.model.context_window,
            "request_timeout_secs": config.model.request_timeout_secs,
            "max_retries": config.model.max_retries,
            "stream": config.model.stream,
            "allow_insecure_http": config.model.allow_insecure_http,
            "capabilities": {
                "tools": config.model.capabilities.tools,
                "streaming": config.model.capabilities.streaming,
            },
            "extra_headers": extra_headers,
        },
        "agent": {
            "max_iterations": config.agent.max_iterations,
            "max_duration_secs": config.agent.max_duration_secs,
            "token_budget": config.agent.token_budget,
            "tool_call_budget": config.agent.tool_call_budget,
            "max_tool_output_chars": config.agent.max_tool_output_chars,
            "context_keep_messages": config.agent.context_keep_messages,
        },
        "permissions": {
            "mode": config.permissions.mode,
            "auto_approve_max_risk": config.permissions.auto_approve_max_risk,
            "auto_approve_high_risk": config.permissions.auto_approve_high_risk,
            "extra_env_allowlist": &config.permissions.extra_env_allowlist,
            "command_timeout_secs": config.permissions.command_timeout_secs,
            "max_command_timeout_secs": config.permissions.max_command_timeout_secs,
            "max_command_output_bytes": config.permissions.max_command_output_bytes,
        },
        "storage": {
            "sqlite_path": &config.storage.sqlite_path,
        },
    })
}

/// 空密钥显示为空串，非空密钥统一脱敏。
fn redacted_secret(secret: &coder_core::Secret) -> &'static str {
    if secret.is_empty() { "" } else { "<redacted>" }
}
