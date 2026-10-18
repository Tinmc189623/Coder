use std::path::PathBuf;

use coder_core::{
    CoreError, RunnerConfig, Secret,
    config::{ModelConfig, default_data_dir},
};
use figment::{
    Figment,
    providers::{Env, Format, Serialized, Toml},
};
use serde::{Deserialize, Serialize};
use url::Url;

/// 模型推理所在位置。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ModelMode {
    /// 本地 Agent 直接调用配置的模型端点。
    #[default]
    Direct,
    /// 通过已认证的云端代理调用模型。
    Cloud,
}

/// 重连退避配置。
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default)]
pub struct ReconnectConfig {
    pub initial_delay_ms: u64,
    pub max_delay_secs: u64,
    pub jitter_ms: u64,
    /// 0 表示无限重连。
    pub max_attempts: u32,
}

impl Default for ReconnectConfig {
    fn default() -> Self {
        Self {
            initial_delay_ms: 500,
            max_delay_secs: 30,
            jitter_ms: 750,
            max_attempts: 0,
        }
    }
}

/// 本地 Agent 配置。
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct LocalAgentConfig {
    /// 云端服务根地址，例如 `https://coder.example.com`。
    pub server_url: String,
    pub device_name: String,
    pub credentials_path: PathBuf,
    /// 首次注册用的配对令牌；成功注册后应清空。
    pub pairing_token: Secret,
    /// 允许执行任务的本地工作区根目录。
    pub workspace_roots: Vec<PathBuf>,
    /// 任务账本 SQLite 路径。
    pub storage_path: PathBuf,
    pub model_mode: ModelMode,
    pub reconnect: ReconnectConfig,
    pub heartbeat_secs: u64,
    /// 等待云端权限确认的最大时长。
    pub approval_timeout_secs: u64,
    pub max_parallel_tasks: u32,
    /// 允许对回环地址使用明文 ws://。
    pub allow_insecure_ws: bool,
    pub runner: RunnerConfig,
}

impl LocalAgentConfig {
    /// 从 TOML 与环境变量加载配置。
    pub fn load(path: Option<&std::path::Path>) -> Result<Self, CoreError> {
        let config = Self::load_unvalidated(path)?;
        config.validate()?;
        Ok(config)
    }

    /// 加载配置但不执行校验，供 CLI 先应用命令行覆盖再统一校验。
    pub fn load_unvalidated(path: Option<&std::path::Path>) -> Result<Self, CoreError> {
        let mut defaults = Self::default();
        defaults.apply_default_paths()?;
        let mut figment = Figment::from(Serialized::defaults(defaults));
        if let Some(path) = path {
            figment = figment.merge(Toml::file(path));
        }
        let mut config: Self = figment
            .merge(Env::prefixed("CODER_").split("__"))
            .extract()
            .map_err(|err| CoreError::Config(err.to_string()))?;
        config.apply_default_paths()?;
        Ok(config)
    }

    /// 填充平台默认路径。
    fn apply_default_paths(&mut self) -> Result<(), CoreError> {
        let data_dir = default_data_dir()?;
        if self.credentials_path.as_os_str().is_empty() {
            self.credentials_path = data_dir.join("device.json");
        }
        if self.storage_path.as_os_str().is_empty() {
            self.storage_path = data_dir.join("agent.db");
        }
        if self.workspace_roots.is_empty() {
            self.workspace_roots.push(std::env::current_dir()?);
        }
        if self.device_name.trim().is_empty() {
            self.device_name = "local-agent".into();
        }
        if self.heartbeat_secs == 0 {
            self.heartbeat_secs = 20;
        }
        if self.approval_timeout_secs == 0 {
            self.approval_timeout_secs = 120;
        }
        if self.max_parallel_tasks == 0 {
            self.max_parallel_tasks = 1;
        }
        if self.model_mode == ModelMode::Cloud && self.runner.model.model.is_empty() {
            self.runner.model.model = "cloud-proxy".into();
        }
        Ok(())
    }

    /// 校验配置的安全边界。
    pub fn validate(&self) -> Result<(), CoreError> {
        if self.server_url.trim().is_empty() {
            return Err(CoreError::Config(
                "server_url is required for the local agent".into(),
            ));
        }
        let url = Url::parse(&self.server_url)
            .map_err(|err| CoreError::Config(format!("invalid server_url: {err}")))?;
        let loopback = url.host_str().map(is_loopback_host).unwrap_or(false);
        match url.scheme() {
            "https" => {}
            "http" if self.allow_insecure_ws && loopback => {}
            "http" => {
                return Err(CoreError::Config(
                    "plain http server_url is only allowed for loopback with allow_insecure_ws=true"
                        .into(),
                ));
            }
            other => {
                return Err(CoreError::Config(format!(
                    "unsupported server_url scheme `{other}`; use https"
                )));
            }
        }
        if self.heartbeat_secs < 5 || self.heartbeat_secs > 300 {
            return Err(CoreError::Config(
                "heartbeat_secs must be between 5 and 300".into(),
            ));
        }
        if self.approval_timeout_secs < 10 || self.approval_timeout_secs > 3_600 {
            return Err(CoreError::Config(
                "approval_timeout_secs must be between 10 and 3600".into(),
            ));
        }
        if self.max_parallel_tasks > 8 {
            return Err(CoreError::Config(
                "max_parallel_tasks must not exceed 8".into(),
            ));
        }
        if self.model_mode == ModelMode::Direct {
            self.runner.model.validate()?;
        }
        if self.workspace_roots.is_empty() {
            return Err(CoreError::Config(
                "at least one workspace root is required".into(),
            ));
        }
        Ok(())
    }

    /// 选择模型名用于会话记录。
    pub fn model_name(&self) -> &str {
        if self.model_mode == ModelMode::Cloud {
            "cloud-proxy"
        } else {
            &self.runner.model.model
        }
    }

    /// 直接模式下的模型配置。
    pub fn direct_model_config(&self) -> &ModelConfig {
        &self.runner.model
    }
}

/// 判断主机名是否为本机回环地址。
fn is_loopback_host(host: &str) -> bool {
    host.eq_ignore_ascii_case("localhost")
        || host == "127.0.0.1"
        || host == "::1"
        || host == "[::1]"
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_insecure_remote_http() {
        let mut config = LocalAgentConfig {
            server_url: "http://coder.example.com".into(),
            allow_insecure_ws: true,
            model_mode: ModelMode::Cloud,
            ..LocalAgentConfig::default()
        };
        config.apply_default_paths().unwrap();
        config.workspace_roots = vec![std::env::current_dir().unwrap()];
        assert!(config.validate().is_err());
    }

    #[test]
    fn allows_insecure_loopback_for_tests() {
        let mut config = LocalAgentConfig {
            server_url: "http://127.0.0.1:8080".into(),
            allow_insecure_ws: true,
            model_mode: ModelMode::Cloud,
            ..LocalAgentConfig::default()
        };
        config.apply_default_paths().unwrap();
        config.workspace_roots = vec![std::env::current_dir().unwrap()];
        assert!(config.validate().is_ok());
    }
}
