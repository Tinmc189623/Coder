//! `coder agent`：设备注册与云端托管运行。

use anyhow::{Result, bail};
use coder_core::Secret;
use coder_local_agent::{
    LocalAgentConfig,
    client::AgentClient,
    config::ModelMode,
    credentials::{default_credentials_path, load_credentials, register_device, store_credentials},
};
use tokio_util::sync::CancellationToken;

use crate::{
    AgentRegisterArgs, AgentRunArgs,
    config_util::{resolve_config_path, validate_cloud_url},
};

/// 使用配对令牌注册设备。
pub async fn register(args: AgentRegisterArgs) -> Result<u8> {
    let server_url =
        validate_cloud_url(args.server_url.as_deref().ok_or_else(|| {
            anyhow::anyhow!("缺少云端地址；使用 --server-url 或 CODER_CLOUD_URL")
        })?)?;
    let pairing_token = args
        .pairing_token
        .as_deref()
        .map(str::trim)
        .filter(|token| !token.is_empty())
        .ok_or_else(|| {
            anyhow::anyhow!("缺少配对令牌；使用 --pairing-token 或 CODER_PAIRING_TOKEN")
        })?;
    let name = args
        .name
        .as_deref()
        .map(str::trim)
        .filter(|name| !name.is_empty())
        .map(str::to_owned)
        .unwrap_or_else(default_device_name);
    let credentials_path = match args.credentials_path {
        Some(path) => path,
        None => default_credentials_path().map_err(|err| anyhow::anyhow!(err.to_string()))?,
    };

    let credentials = register_device(&server_url, pairing_token, &name, &credentials_path).await?;
    println!("设备注册成功");
    println!("device_id: {}", credentials.device_id);
    println!("user_id: {}", credentials.user_id);
    println!("凭据: {}", credentials_path.display());
    eprintln!("配对令牌已被消费，请勿再次使用。");
    Ok(0)
}

/// 运行本地 Agent，持续接收云端任务。
pub async fn run(mut args: AgentRunArgs) -> Result<u8> {
    let config_path = resolve_config_path(args.config.take(), "agent.toml")?;
    let mut config = LocalAgentConfig::load_unvalidated(config_path.as_deref())?;

    if let Some(server_url) = args.server_url.take() {
        config.server_url = server_url;
    }
    if let Some(name) = args.device_name.take() {
        config.device_name = name;
    }
    if let Some(token) = args.pairing_token.take() {
        config.pairing_token = Secret(token);
    }
    if let Some(path) = args.credentials_path.take() {
        config.credentials_path = path;
    }
    if !args.workspace_roots.is_empty() {
        config.workspace_roots = args.workspace_roots;
    }
    if let Some(mode) = args.model_mode {
        config.model_mode = mode.into();
    }
    if args.allow_insecure_ws {
        config.allow_insecure_ws = true;
    }
    config.validate()?;
    let server_url = validate_cloud_url(&config.server_url)?;
    config.server_url = server_url;

    let credentials = if config.credentials_path.exists() {
        load_credentials(&config.credentials_path)?
    } else {
        if config.pairing_token.is_empty() {
            bail!(
                "设备尚未注册，且未提供配对令牌；先运行 coder agent register，或设置 CODER_PAIRING_TOKEN"
            );
        }
        eprintln!("未找到设备凭据，使用配对令牌进行首次注册。");
        let credentials = register_device(
            &config.server_url,
            config.pairing_token.expose(),
            &config.device_name,
            &config.credentials_path,
        )
        .await?;
        // 凭据文件不可写时立即失败，避免每次重启都重新配对。
        store_credentials(&config.credentials_path, &credentials)?;
        eprintln!("配对令牌已消费，请从配置或环境变量中移除。");
        credentials
    };

    println!("云端: {}", config.server_url);
    println!("设备: {} ({})", config.device_name, credentials.device_id);
    println!(
        "模型模式: {}",
        match config.model_mode {
            ModelMode::Direct => "direct",
            ModelMode::Cloud => "cloud",
        }
    );
    println!(
        "授权工作区: {}",
        config
            .workspace_roots
            .iter()
            .map(|root| root.display().to_string())
            .collect::<Vec<_>>()
            .join(", ")
    );

    let client = AgentClient::new(config, credentials).await?;
    let shutdown = CancellationToken::new();
    let mut interrupted = false;
    let running = client.run(shutdown.clone());
    tokio::pin!(running);
    loop {
        tokio::select! {
            result = &mut running => {
                result?;
                break;
            }
            _ = tokio::signal::ctrl_c(), if !interrupted => {
                interrupted = true;
                eprintln!();
                eprintln!("收到 Ctrl+C，正在停止任务并断开连接。");
                shutdown.cancel();
            }
        }
    }
    Ok(0)
}

/// 读取主机名作为默认设备名称。
fn default_device_name() -> String {
    std::env::var("COMPUTERNAME")
        .or_else(|_| std::env::var("HOSTNAME"))
        .ok()
        .map(|value| value.trim().to_owned())
        .filter(|value| !value.is_empty())
        .unwrap_or_else(|| "local-agent".into())
}
