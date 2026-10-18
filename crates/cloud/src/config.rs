use std::{
    net::SocketAddr,
    path::{Path, PathBuf},
};

use coder_core::{Secret, config::ModelConfig};
use figment::{
    Figment,
    providers::{Env, Format, Serialized, Toml},
};
use serde::{Deserialize, Serialize};

use crate::error::CloudError;

/// 云端服务配置。
///
/// 所有密钥均通过配置或 `CODER_CLOUD_` 环境变量注入，代码中不保存默认凭据。
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default)]
pub struct CloudConfig {
    /// 监听地址，默认仅绑定回环地址。
    pub bind: String,
    /// 对外展示的服务地址，用于日志与文档。
    pub public_url: String,
    /// `postgres://...` 或本地开发用的 `memory://`。
    pub database_url: String,
    pub jwt_secret: Secret,
    pub admin_api_key: Secret,
    /// 设备短期令牌有效期。
    pub token_ttl_secs: u64,
    /// 一次性配对令牌有效期。
    pub pairing_ttl_secs: u64,
    /// 任务租约有效期；过期但无终态结果的任务会转为 unknown。
    pub task_lease_secs: u64,
    /// 租约扫描间隔。
    pub lease_sweep_secs: u64,
    pub request_body_limit_bytes: usize,
    pub request_timeout_secs: u64,
    pub rate_limit_per_minute: u32,
    pub max_ws_message_bytes: usize,
    pub shutdown_grace_secs: u64,
    /// 允许在非回环地址上使用明文 HTTP；仅应在反向代理终止 TLS 时显式开启。
    pub allow_insecure_http: bool,
    /// 是否信任反向代理写入的 `X-Forwarded-For`。
    ///
    /// 仅在服务端口只对可信代理开放时启用，否则客户端可以伪造来源绕过限流。
    pub trust_proxy_headers: bool,
    pub tls_cert_path: PathBuf,
    pub tls_key_path: PathBuf,
    /// 云端模型代理使用的 OpenAI 兼容端点；留空则关闭代理。
    pub model: ModelConfig,
}

impl Default for CloudConfig {
    fn default() -> Self {
        Self {
            bind: "127.0.0.1:8080".into(),
            public_url: String::new(),
            database_url: "memory://".into(),
            jwt_secret: Secret::default(),
            admin_api_key: Secret::default(),
            token_ttl_secs: 900,
            pairing_ttl_secs: 600,
            task_lease_secs: 120,
            lease_sweep_secs: 15,
            request_body_limit_bytes: 2_097_152,
            request_timeout_secs: 300,
            rate_limit_per_minute: 120,
            max_ws_message_bytes: 1_048_576,
            shutdown_grace_secs: 20,
            allow_insecure_http: false,
            trust_proxy_headers: false,
            tls_cert_path: PathBuf::new(),
            tls_key_path: PathBuf::new(),
            model: ModelConfig::default(),
        }
    }
}

impl CloudConfig {
    /// 从可选 TOML 文件与环境变量加载并校验配置。
    pub fn load(path: Option<&Path>) -> Result<Self, CloudError> {
        let mut figment = Figment::from(Serialized::defaults(Self::default()));
        if let Some(path) = path {
            figment = figment.merge(Toml::file(path));
        }
        let config: Self = figment
            .merge(Env::prefixed("CODER_CLOUD_").split("__"))
            .extract()
            .map_err(|err| CloudError::Config(err.to_string()))?;
        config.validate()?;
        Ok(config)
    }

    /// 校验监听、密钥、TLS 与租约参数。
    pub fn validate(&self) -> Result<(), CloudError> {
        let addr = self.socket_addr()?;
        if self.jwt_secret.expose().len() < 32 {
            return Err(CloudError::Config(
                "jwt_secret must contain at least 32 characters".into(),
            ));
        }
        if self.admin_api_key.expose().len() < 24 {
            return Err(CloudError::Config(
                "admin_api_key must contain at least 24 characters".into(),
            ));
        }
        let database = self.database_url.trim();
        if database != "memory://"
            && !database.starts_with("postgres://")
            && !database.starts_with("postgresql://")
        {
            return Err(CloudError::Config(
                "database_url must be postgres://, postgresql:// or memory://".into(),
            ));
        }
        let tls_enabled =
            !self.tls_cert_path.as_os_str().is_empty() || !self.tls_key_path.as_os_str().is_empty();
        if tls_enabled
            && (self.tls_cert_path.as_os_str().is_empty()
                || self.tls_key_path.as_os_str().is_empty())
        {
            return Err(CloudError::Config(
                "tls_cert_path and tls_key_path must be configured together".into(),
            ));
        }
        if !tls_enabled && !addr.ip().is_loopback() && !self.allow_insecure_http {
            return Err(CloudError::Config(
                "refusing plaintext HTTP on a non-loopback bind; terminate TLS in front of the service or set allow_insecure_http=true"
                    .into(),
            ));
        }
        if !(60..=86_400).contains(&self.token_ttl_secs) {
            return Err(CloudError::Config(
                "token_ttl_secs must be between 60 and 86400".into(),
            ));
        }
        if !(60..=86_400).contains(&self.pairing_ttl_secs) {
            return Err(CloudError::Config(
                "pairing_ttl_secs must be between 60 and 86400".into(),
            ));
        }
        if !(30..=3_600).contains(&self.task_lease_secs) {
            return Err(CloudError::Config(
                "task_lease_secs must be between 30 and 3600".into(),
            ));
        }
        if !(5..=300).contains(&self.lease_sweep_secs) {
            return Err(CloudError::Config(
                "lease_sweep_secs must be between 5 and 300".into(),
            ));
        }
        if !(1_024..=16_777_216).contains(&self.request_body_limit_bytes) {
            return Err(CloudError::Config(
                "request_body_limit_bytes must be between 1024 and 16777216".into(),
            ));
        }
        if !(10..=3_600).contains(&self.request_timeout_secs) {
            return Err(CloudError::Config(
                "request_timeout_secs must be between 10 and 3600".into(),
            ));
        }
        if !(1..=100_000).contains(&self.rate_limit_per_minute) {
            return Err(CloudError::Config(
                "rate_limit_per_minute must be between 1 and 100000".into(),
            ));
        }
        if !(1_024..=16_777_216).contains(&self.max_ws_message_bytes) {
            return Err(CloudError::Config(
                "max_ws_message_bytes must be between 1024 and 16777216".into(),
            ));
        }
        if !(1..=300).contains(&self.shutdown_grace_secs) {
            return Err(CloudError::Config(
                "shutdown_grace_secs must be between 1 and 300".into(),
            ));
        }
        if self.model_configured() {
            self.model
                .validate()
                .map_err(|err| CloudError::Config(err.to_string()))?;
        }
        Ok(())
    }

    /// 解析监听地址。
    pub fn socket_addr(&self) -> Result<SocketAddr, CloudError> {
        self.bind.parse().map_err(|err| {
            CloudError::Config(format!("invalid bind address `{}`: {err}", self.bind))
        })
    }

    /// 是否配置了云端模型代理。
    pub fn model_configured(&self) -> bool {
        !self.model.base_url.trim().is_empty()
            || !self.model.model.trim().is_empty()
            || !self.model.api_key.is_empty()
    }

    /// 是否启用 TLS。
    pub fn tls_enabled(&self) -> bool {
        !self.tls_cert_path.as_os_str().is_empty()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 构造可用于校验的最小配置。
    fn base() -> CloudConfig {
        CloudConfig {
            jwt_secret: Secret("j".repeat(32)),
            admin_api_key: Secret("a".repeat(24)),
            ..CloudConfig::default()
        }
    }

    #[test]
    fn rejects_plaintext_public_bind() {
        let mut config = base();
        config.bind = "0.0.0.0:8080".into();
        assert!(config.validate().is_err());
    }

    #[test]
    fn allows_loopback_default() {
        assert!(base().validate().is_ok());
    }

    #[test]
    fn rejects_partial_tls() {
        let mut config = base();
        config.tls_cert_path = PathBuf::from("cert.pem");
        assert!(config.validate().is_err());
    }
}
