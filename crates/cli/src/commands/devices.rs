//! `coder devices`：查看当前用户注册的设备。

use anyhow::Result;

use crate::{CloudArgs, DevicesCommand, cloud_api::CloudApi, config_util::require_api_key};

/// 分发设备子命令。
pub async fn execute(command: DevicesCommand) -> Result<u8> {
    match command {
        DevicesCommand::List { cloud } => list(cloud).await,
    }
}

/// 列出设备。
async fn list(cloud: CloudArgs) -> Result<u8> {
    let server_url = cloud
        .server_url
        .as_deref()
        .ok_or_else(|| anyhow::anyhow!("缺少云端地址；使用 --server-url 或 CODER_CLOUD_URL"))?;
    let api = CloudApi::user(server_url, require_api_key(cloud.api_key.clone())?)?;
    let devices = api.list_devices().await?;
    if cloud.json {
        println!("{}", serde_json::to_string_pretty(&devices)?);
        return Ok(0);
    }
    if devices.is_empty() {
        println!("没有已注册设备。");
        return Ok(0);
    }
    println!(
        "{:<36} {:<10} {:<20} {:<8} LAST SEEN",
        "DEVICE", "PLATFORM", "NAME", "ONLINE"
    );
    for device in devices {
        println!(
            "{:<36} {:<10} {:<20} {:<8} {}",
            device.device_id,
            device.platform,
            truncate(&device.name, 20),
            if device.online { "yes" } else { "no" },
            device
                .last_seen_at
                .map(|value| value.to_rfc3339())
                .unwrap_or_else(|| "never".into())
        );
    }
    Ok(0)
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
