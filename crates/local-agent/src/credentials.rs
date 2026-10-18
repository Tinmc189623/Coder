use std::{
    fmt, fs,
    io::Write,
    path::{Path, PathBuf},
};

use coder_protocol::{
    DeviceId, DeviceRegisterRequest, DeviceRegisterResponse, DeviceTokenRequest,
    DeviceTokenResponse, UserId,
};
use reqwest::Client;
use serde::{Deserialize, Serialize};
use tempfile::NamedTempFile;
use url::Url;

use crate::error::AgentError;

/// DPAPI 加密文件头，避免把密文误当 JSON 解析。
#[cfg(windows)]
const DPAPI_MAGIC: &[u8] = b"CODER-DPAPI-1\n";

/// 设备凭据。设备密钥只在注册时下发一次，之后用于换取短期访问令牌。
#[derive(Clone, Serialize, Deserialize)]
pub struct DeviceCredentials {
    pub server_url: String,
    pub device_id: DeviceId,
    pub user_id: UserId,
    pub device_secret: String,
}

impl fmt::Debug for DeviceCredentials {
    /// 隐藏设备密钥。
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("DeviceCredentials")
            .field("server_url", &self.server_url)
            .field("device_id", &self.device_id)
            .field("user_id", &self.user_id)
            .field("device_secret", &"<redacted>")
            .finish()
    }
}

/// 规范化云端根地址，去掉末尾斜杠。
pub fn normalize_server_url(server_url: &str) -> Result<String, AgentError> {
    let mut url = Url::parse(server_url)
        .map_err(|err| AgentError::Config(format!("invalid server URL: {err}")))?;
    if url.query().is_some() || url.fragment().is_some() {
        return Err(AgentError::Config(
            "server URL must not contain query or fragment".into(),
        ));
    }
    let trimmed = url.path().trim_end_matches('/').to_owned();
    url.set_path(if trimmed.is_empty() { "/" } else { &trimmed });
    Ok(url.to_string().trim_end_matches('/').to_owned())
}

/// 读取设备凭据，Windows 上自动解密 DPAPI 文件。
pub fn load_credentials(path: &Path) -> Result<DeviceCredentials, AgentError> {
    let bytes = fs::read(path).map_err(|err| {
        AgentError::Credentials(format!("cannot read credentials {}: {err}", path.display()))
    })?;
    let plaintext = decode_credentials_bytes(&bytes)?;
    serde_json::from_slice(&plaintext)
        .map_err(|err| AgentError::Credentials(format!("invalid credentials JSON: {err}")))
}

/// 原子写入设备凭据；Unix 使用 0600，Windows 使用当前用户 DPAPI。
pub fn store_credentials(path: &Path, credentials: &DeviceCredentials) -> Result<(), AgentError> {
    let plaintext = serde_json::to_vec_pretty(credentials)?;
    let bytes = encode_credentials_bytes(&plaintext)?;
    let parent = path.parent().ok_or_else(|| {
        AgentError::Credentials("credentials path has no parent directory".into())
    })?;
    fs::create_dir_all(parent)?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        fs::set_permissions(parent, fs::Permissions::from_mode(0o700))?;
    }
    let mut temp = NamedTempFile::new_in(parent)?;
    temp.write_all(&bytes)?;
    temp.as_file().sync_all()?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        temp.as_file()
            .set_permissions(fs::Permissions::from_mode(0o600))?;
    }
    temp.persist(path).map_err(|err| {
        AgentError::Credentials(format!("cannot persist credentials: {}", err.error))
    })?;
    Ok(())
}

/// 使用一次性配对令牌注册设备并保存凭据。
///
/// 配对令牌只能成功使用一次；服务端不保留明文。
pub async fn register_device(
    server_url: &str,
    pairing_token: &str,
    name: &str,
    credentials_path: &Path,
) -> Result<DeviceCredentials, AgentError> {
    if pairing_token.trim().is_empty() {
        return Err(AgentError::Credentials("pairing token is empty".into()));
    }
    let base = normalize_server_url(server_url)?;
    let client = Client::builder()
        .timeout(std::time::Duration::from_secs(30))
        .user_agent(concat!("coder-local-agent/", env!("CARGO_PKG_VERSION")))
        .build()?;
    let request = DeviceRegisterRequest {
        pairing_token: pairing_token.to_owned(),
        name: name.to_owned(),
        platform: platform_name().to_owned(),
        agent_version: env!("CARGO_PKG_VERSION").to_owned(),
    };
    let response = client
        .post(format!("{base}/api/v1/devices/register"))
        .json(&request)
        .send()
        .await?;
    if !response.status().is_success() {
        let status = response.status();
        let body = response.text().await.unwrap_or_default();
        return Err(AgentError::Server(format!(
            "device registration failed ({status}): {}",
            truncate(&body, 400)
        )));
    }
    let registered: DeviceRegisterResponse = response.json().await?;
    let credentials = DeviceCredentials {
        server_url: base,
        device_id: registered.device_id,
        user_id: registered.user_id,
        device_secret: registered.device_secret,
    };
    store_credentials(credentials_path, &credentials)?;
    Ok(credentials)
}

/// 使用设备密钥换取短期访问令牌。
pub async fn fetch_access_token(
    client: &Client,
    credentials: &DeviceCredentials,
) -> Result<DeviceTokenResponse, AgentError> {
    let response = client
        .post(format!("{}/api/v1/devices/token", credentials.server_url))
        .json(&DeviceTokenRequest {
            device_id: credentials.device_id,
            device_secret: credentials.device_secret.clone(),
        })
        .send()
        .await?;
    if !response.status().is_success() {
        let status = response.status();
        let body = response.text().await.unwrap_or_default();
        return Err(AgentError::Auth(format!(
            "token exchange failed ({status}): {}",
            truncate(&body, 400)
        )));
    }
    response
        .json::<DeviceTokenResponse>()
        .await
        .map_err(AgentError::from)
}

/// 当前平台标识，供云端展示与筛选。
pub fn platform_name() -> &'static str {
    if cfg!(target_os = "windows") {
        "windows"
    } else if cfg!(target_os = "macos") {
        "macos"
    } else if cfg!(target_os = "linux") {
        "linux"
    } else {
        std::env::consts::OS
    }
}

/// 将文件字节还原为 JSON 明文。
fn decode_credentials_bytes(bytes: &[u8]) -> Result<Vec<u8>, AgentError> {
    #[cfg(windows)]
    {
        let ciphertext = bytes.strip_prefix(DPAPI_MAGIC).ok_or_else(|| {
            AgentError::Credentials(
                "credentials file is not DPAPI-protected; delete it and re-register".into(),
            )
        })?;
        dpapi_unprotect(ciphertext)
    }
    #[cfg(not(windows))]
    {
        if bytes.starts_with(b"CODER-DPAPI-1\n") {
            return Err(AgentError::Credentials(
                "DPAPI credentials cannot be read on this platform".into(),
            ));
        }
        Ok(bytes.to_vec())
    }
}

/// 将 JSON 明文编码为待写入文件的内容。
fn encode_credentials_bytes(plaintext: &[u8]) -> Result<Vec<u8>, AgentError> {
    #[cfg(windows)]
    {
        let ciphertext = dpapi_protect(plaintext)?;
        let mut output = Vec::with_capacity(DPAPI_MAGIC.len() + ciphertext.len());
        output.extend_from_slice(DPAPI_MAGIC);
        output.extend_from_slice(&ciphertext);
        Ok(output)
    }
    #[cfg(not(windows))]
    {
        Ok(plaintext.to_vec())
    }
}

/// 使用当前 Windows 用户的 DPAPI 密钥加密数据。
#[cfg(windows)]
fn dpapi_protect(plaintext: &[u8]) -> Result<Vec<u8>, AgentError> {
    use windows_sys::Win32::Security::Cryptography::{
        CRYPT_INTEGER_BLOB, CRYPTPROTECT_UI_FORBIDDEN, CryptProtectData,
    };

    let input = CRYPT_INTEGER_BLOB {
        cbData: plaintext.len() as u32,
        pbData: plaintext.as_ptr() as *mut u8,
    };
    let mut output = CRYPT_INTEGER_BLOB {
        cbData: 0,
        pbData: std::ptr::null_mut(),
    };
    let ok = unsafe {
        CryptProtectData(
            &input,
            std::ptr::null(),
            std::ptr::null(),
            std::ptr::null(),
            std::ptr::null(),
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output,
        )
    };
    if ok == 0 {
        return Err(AgentError::Credentials(format!(
            "CryptProtectData failed: {}",
            std::io::Error::last_os_error()
        )));
    }
    let result =
        unsafe { std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec() };
    unsafe {
        windows_sys::Win32::Foundation::LocalFree(output.pbData as *mut core::ffi::c_void);
    }
    Ok(result)
}

/// 使用当前 Windows 用户的 DPAPI 密钥解密数据。
#[cfg(windows)]
fn dpapi_unprotect(ciphertext: &[u8]) -> Result<Vec<u8>, AgentError> {
    use windows_sys::Win32::Security::Cryptography::{
        CRYPT_INTEGER_BLOB, CRYPTPROTECT_UI_FORBIDDEN, CryptUnprotectData,
    };

    let input = CRYPT_INTEGER_BLOB {
        cbData: ciphertext.len() as u32,
        pbData: ciphertext.as_ptr() as *mut u8,
    };
    let mut output = CRYPT_INTEGER_BLOB {
        cbData: 0,
        pbData: std::ptr::null_mut(),
    };
    let ok = unsafe {
        CryptUnprotectData(
            &input,
            std::ptr::null_mut(),
            std::ptr::null(),
            std::ptr::null(),
            std::ptr::null(),
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output,
        )
    };
    if ok == 0 {
        return Err(AgentError::Credentials(format!(
            "CryptUnprotectData failed: {}",
            std::io::Error::last_os_error()
        )));
    }
    let result =
        unsafe { std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec() };
    unsafe {
        windows_sys::Win32::Foundation::LocalFree(output.pbData as *mut core::ffi::c_void);
    }
    Ok(result)
}

/// 截断错误响应，避免把超长 HTML 写进日志。
fn truncate(value: &str, limit: usize) -> String {
    let mut chars = value.chars();
    let truncated: String = chars.by_ref().take(limit).collect();
    if chars.next().is_some() {
        format!("{truncated}…")
    } else {
        truncated
    }
}

/// 凭据文件的默认路径。
pub fn default_credentials_path() -> Result<PathBuf, AgentError> {
    Ok(coder_core::config::default_data_dir()?.join("device.json"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trips_credentials_atomically() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("device.json");
        let credentials = DeviceCredentials {
            server_url: "https://coder.example.com".into(),
            device_id: DeviceId::new(),
            user_id: UserId::new(),
            device_secret: "super-secret".into(),
        };
        store_credentials(&path, &credentials).unwrap();
        let loaded = load_credentials(&path).unwrap();
        assert_eq!(loaded.server_url, credentials.server_url);
        assert_eq!(loaded.device_id, credentials.device_id);
        assert_eq!(loaded.device_secret, credentials.device_secret);
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            let mode = fs::metadata(&path).unwrap().permissions().mode();
            assert_eq!(mode & 0o777, 0o600);
        }
    }

    #[test]
    fn normalizes_trailing_slash() {
        assert_eq!(
            normalize_server_url("https://example.com/").unwrap(),
            "https://example.com"
        );
    }
}
