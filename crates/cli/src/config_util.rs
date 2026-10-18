//! 配置文件定位、云端地址校验与脱敏辅助函数。

use std::path::{Path, PathBuf};

use anyhow::{Context, Result, bail};
use coder_core::config::default_config_dir;
use coder_local_agent::credentials::normalize_server_url;

/// 定位配置文件。
///
/// 显式路径优先；否则在平台配置目录中查找 `file_name`，不存在时返回 `None`，
/// 让配置层仅使用默认值和环境变量。
pub fn resolve_config_path(explicit: Option<PathBuf>, file_name: &str) -> Result<Option<PathBuf>> {
    if let Some(path) = explicit {
        return Ok(Some(path));
    }
    let candidate = default_config_dir()?.join(file_name);
    Ok(candidate.exists().then_some(candidate))
}

/// 解析并校验云端根地址，确保 API key 不会通过明文链路传输。
///
/// HTTPS 始终允许；HTTP 仅允许回环主机，用于本机开发。包含内嵌凭据的 URL
/// 会被拒绝，避免密钥出现在日志或进程参数中。
pub fn validate_cloud_url(raw: &str) -> Result<String> {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        bail!("云端地址为空；使用 --server-url 或 CODER_CLOUD_URL 指定");
    }
    let normalized = normalize_server_url(trimmed)
        .map_err(|err| anyhow::anyhow!(err.to_string()))
        .with_context(|| format!("无效的云端地址 `{trimmed}`"))?;
    let url = reqwest::Url::parse(&normalized)
        .with_context(|| format!("无效的云端地址 `{normalized}`"))?;
    if !url.username().is_empty() || url.password().is_some() {
        bail!("云端地址不得包含用户名或密码");
    }
    match url.scheme() {
        "https" => {}
        "http" if url.host_str().is_some_and(is_loopback_host) => {}
        "http" => bail!("远端云端地址必须使用 https；API key 不能通过明文 HTTP 发送"),
        other => bail!("不支持的云端协议 `{other}`，请使用 https"),
    }
    Ok(normalized)
}

/// 从命令行或环境变量取得必需的 API key。
pub fn require_api_key(value: Option<String>) -> Result<String> {
    match value {
        Some(key) if !key.trim().is_empty() => Ok(key.trim().to_owned()),
        _ => bail!("缺少 API key；使用 --api-key 或 CODER_API_KEY 提供"),
    }
}

/// 从命令行或环境变量取得必需的管理员密钥。
pub fn require_admin_key(value: Option<String>) -> Result<String> {
    match value {
        Some(key) if !key.trim().is_empty() => Ok(key.trim().to_owned()),
        _ => bail!("缺少管理员密钥；使用 --admin-key 或 CODER_ADMIN_KEY 提供"),
    }
}

/// 将任意字符串编码为 TOML 基本字符串。
pub fn toml_string(value: &str) -> String {
    let escaped = value.replace('\\', "\\\\").replace('"', "\\\"");
    format!("\"{escaped}\"")
}

/// 判断主机名是否为本机回环地址。
fn is_loopback_host(host: &str) -> bool {
    host.eq_ignore_ascii_case("localhost")
        || host == "127.0.0.1"
        || host == "::1"
        || host == "[::1]"
}

/// 确认父目录存在并可写：创建目录后写入并删除探针文件。
pub fn probe_writable_directory(dir: &Path) -> Result<()> {
    std::fs::create_dir_all(dir).with_context(|| format!("无法创建目录 {}", dir.display()))?;
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|value| value.as_nanos())
        .unwrap_or_default();
    let probe = dir.join(format!(".coder-write-probe-{}-{nanos}", std::process::id()));
    std::fs::write(&probe, b"ok").with_context(|| format!("目录不可写: {}", dir.display()))?;
    let _ = std::fs::remove_file(&probe);
    Ok(())
}
