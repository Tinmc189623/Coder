//! `coder admin`：创建用户与配对令牌。

use anyhow::{Result, bail};
use coder_protocol::UserId;

use crate::{AdminArgs, AdminCommand, cloud_api::CloudApi, config_util::require_admin_key};

/// 分发管理员子命令。
pub async fn execute(command: AdminCommand) -> Result<u8> {
    match command {
        AdminCommand::CreateUser { admin, email } => create_user(admin, email).await,
        AdminCommand::CreatePairing { admin, user_id } => create_pairing(admin, user_id).await,
    }
}

/// 创建用户并输出一次性 API key。
async fn create_user(admin: AdminArgs, email: String) -> Result<u8> {
    let email = email.trim().to_owned();
    if email.is_empty() {
        bail!("--email 不能为空");
    }
    let api = admin_api(&admin)?;
    let created = api.create_user(&email).await?;
    if admin.json {
        println!("{}", serde_json::to_string_pretty(&created)?);
    } else {
        println!("用户已创建");
        println!("user_id: {}", created.user_id);
        println!("email: {}", created.email);
        println!("api_key: {}", created.api_key);
        eprintln!("API key 只显示这一次，请立即保存到安全位置。");
    }
    Ok(0)
}

/// 为用户签发一次性配对令牌。
async fn create_pairing(admin: AdminArgs, user_id: String) -> Result<u8> {
    let user_id: UserId = user_id
        .parse()
        .map_err(|err| anyhow::anyhow!("无效的用户 ID: {err}"))?;
    let api = admin_api(&admin)?;
    let created = api.create_pairing_token(user_id).await?;
    if admin.json {
        println!("{}", serde_json::to_string_pretty(&created)?);
    } else {
        println!("配对令牌: {}", created.pairing_token);
        println!("过期时间: {}", created.expires_at);
        eprintln!("配对令牌只能使用一次；通过 CODER_PAIRING_TOKEN 传给设备。");
    }
    Ok(0)
}

/// 构造管理员 API 客户端。
fn admin_api(admin: &AdminArgs) -> Result<CloudApi> {
    let server_url = admin
        .server_url
        .as_deref()
        .ok_or_else(|| anyhow::anyhow!("缺少云端地址；使用 --server-url 或 CODER_CLOUD_URL"))?;
    CloudApi::admin(server_url, require_admin_key(admin.admin_key.clone())?)
}
