//! `coder doctor`：检查配置、工作区、数据库与云端连通性。

use std::time::Duration;

use anyhow::{Context, Result, bail};
use coder_core::{RunnerConfig, Workspace, config::default_config_dir, store::SqliteStore};
use coder_local_agent::{
    LocalAgentConfig, TaskLedger, config::ModelMode, credentials::load_credentials,
};

use crate::{
    DoctorArgs,
    config_util::{probe_writable_directory, resolve_config_path, validate_cloud_url},
};

/// 执行诊断并返回是否全部通过。
pub async fn execute(mut args: DoctorArgs) -> Result<bool> {
    let file_name = if args.agent {
        "agent.toml"
    } else {
        "config.toml"
    };
    let path = resolve_config_path(args.config.take(), file_name)?;
    println!(
        "配置来源: {}",
        path.as_ref()
            .map(|path| path.display().to_string())
            .unwrap_or_else(|| "默认值 + 环境变量".to_owned())
    );
    let mut ok = true;
    report(
        &mut ok,
        "配置目录",
        default_config_dir()
            .map_err(anyhow::Error::from)
            .and_then(|dir| {
                probe_writable_directory(&dir)?;
                Ok(dir.display().to_string())
            }),
    );
    if let Some(parent) = path.as_deref().and_then(std::path::Path::parent) {
        report(
            &mut ok,
            "配置文件目录",
            probe_writable_directory(parent).map(|_| parent.display().to_string()),
        );
    }
    if args.agent {
        doctor_agent(path, args.online, &mut ok).await?;
    } else {
        doctor_runner(path, &mut ok).await?;
    }
    println!();
    if ok {
        println!("诊断通过。");
    } else {
        println!("诊断发现问题，请按上面的失败项修复。");
    }
    Ok(ok)
}

/// 检查直接执行模式。
async fn doctor_runner(path: Option<std::path::PathBuf>, ok: &mut bool) -> Result<()> {
    let Some(config) = load_runner(path, ok)? else {
        return Ok(());
    };
    report(
        ok,
        "配置校验",
        config
            .validate()
            .map(|_| "通过".to_owned())
            .map_err(Into::into),
    );
    report(
        ok,
        "模型配置",
        config
            .model
            .validate()
            .map(|_| format!("模型 `{}` 配置完整", config.model.model))
            .map_err(Into::into),
    );
    report(
        ok,
        "工作区",
        Workspace::new(config.workspace.clone())
            .map(|workspace| {
                format!(
                    "{} (可写: {})",
                    workspace.root().display(),
                    config.workspace.writable
                )
            })
            .map_err(Into::into),
    );
    let sqlite_path = config.storage.resolved_path()?;
    report(
        ok,
        "会话数据库",
        SqliteStore::connect(&sqlite_path)
            .await
            .map(|_| sqlite_path.display().to_string())
            .map_err(Into::into),
    );
    Ok(())
}

/// 检查本地 Agent 模式。
async fn doctor_agent(path: Option<std::path::PathBuf>, online: bool, ok: &mut bool) -> Result<()> {
    let config = match LocalAgentConfig::load_unvalidated(path.as_deref()) {
        Ok(config) => config,
        Err(err) => {
            report(ok, "配置加载", Err(err.into()));
            return Ok(());
        }
    };
    report(
        ok,
        "配置校验",
        config
            .validate()
            .map(|_| "通过".to_owned())
            .map_err(Into::into),
    );
    let server_url = report_value(ok, "云端地址", validate_cloud_url(&config.server_url));
    for root in &config.workspace_roots {
        report(
            ok,
            "工作区",
            dunce::canonicalize(root)
                .map_err(|err| anyhow::anyhow!("{}: {err}", root.display()))
                .and_then(|canonical| {
                    if canonical.is_dir() {
                        Ok(canonical.display().to_string())
                    } else {
                        bail!("{} 不是目录", canonical.display())
                    }
                }),
        );
    }
    report(
        ok,
        "设备凭据",
        if config.credentials_path.exists() {
            load_credentials(&config.credentials_path)
                .map(|credentials| {
                    format!(
                        "device {} / user {}",
                        credentials.device_id, credentials.user_id
                    )
                })
                .map_err(Into::into)
        } else if config.pairing_token.is_empty() {
            Err(anyhow::anyhow!(
                "凭据不存在且未配置配对令牌；先运行 coder agent register"
            ))
        } else {
            Ok("未注册，首次运行将使用配对令牌注册".to_owned())
        },
    );
    report(
        ok,
        "任务账本",
        TaskLedger::connect(&config.storage_path)
            .await
            .map(|_| config.storage_path.display().to_string())
            .map_err(Into::into),
    );
    let session_db = config.runner.storage.resolved_path()?;
    report(
        ok,
        "会话数据库",
        SqliteStore::connect(&session_db)
            .await
            .map(|_| session_db.display().to_string())
            .map_err(Into::into),
    );
    if config.model_mode == ModelMode::Direct {
        report(
            ok,
            "模型配置",
            config
                .runner
                .model
                .validate()
                .map(|_| format!("模型 `{}` 配置完整", config.runner.model.model))
                .map_err(Into::into),
        );
    } else {
        println!("[ok] 模型模式: cloud（由云端代理）");
    }
    if online {
        if let Some(server_url) = server_url {
            report(ok, "云端健康检查", check_health(&server_url).await);
        } else {
            report(
                ok,
                "云端健康检查",
                Err(anyhow::anyhow!("云端地址无效，跳过")),
            );
        }
    }
    Ok(())
}

/// 加载直接执行配置；失败时记录并返回 `None`。
fn load_runner(path: Option<std::path::PathBuf>, ok: &mut bool) -> Result<Option<RunnerConfig>> {
    match RunnerConfig::load_unvalidated(path.as_deref()) {
        Ok(config) => {
            println!("[ok] 配置加载: 通过");
            Ok(Some(config))
        }
        Err(err) => {
            report(ok, "配置加载", Err(err.into()));
            Ok(None)
        }
    }
}

/// 请求云端健康接口。
async fn check_health(server_url: &str) -> Result<String> {
    let http = reqwest::Client::builder()
        .timeout(Duration::from_secs(10))
        .user_agent(concat!("coder-cli/", env!("CARGO_PKG_VERSION")))
        .build()
        .context("无法创建 HTTP 客户端")?;
    let response = http
        .get(format!("{server_url}/api/v1/health"))
        .send()
        .await
        .with_context(|| format!("无法连接 {server_url}"))?;
    let status = response.status();
    let body = response.text().await.unwrap_or_default();
    if !status.is_success() {
        bail!(
            "健康检查返回 HTTP {}: {}",
            status.as_u16(),
            truncate(&body, 200)
        );
    }
    Ok(format!("HTTP {} {}", status.as_u16(), truncate(&body, 200)))
}

/// 记录一项检查结果。
fn report(ok: &mut bool, label: &str, result: Result<String>) {
    match result {
        Ok(message) => println!("[ok] {label}: {message}"),
        Err(err) => {
            *ok = false;
            println!("[fail] {label}: {err:#}");
        }
    }
}

/// 记录检查结果并返回成功值，供后续步骤复用。
fn report_value<T>(ok: &mut bool, label: &str, result: Result<T>) -> Option<T> {
    match result {
        Ok(value) => {
            println!("[ok] {label}: 通过");
            Some(value)
        }
        Err(err) => {
            *ok = false;
            println!("[fail] {label}: {err:#}");
            None
        }
    }
}

/// 按字符数截断诊断输出。
fn truncate(text: &str, max: usize) -> String {
    if text.chars().count() <= max {
        return text.to_owned();
    }
    let mut truncated: String = text.chars().take(max).collect();
    truncated.push('…');
    truncated
}
