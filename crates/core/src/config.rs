use std::{
    collections::BTreeMap,
    fmt,
    path::{Path, PathBuf},
};

use figment::{
    Figment,
    providers::{Env, Format, Serialized, Toml},
};
use serde::{Deserialize, Serialize};

use crate::error::CoreError;

/// 敏感字符串包装，避免在日志和 `Debug` 输出中泄漏。
#[derive(Clone, Default, Serialize, Deserialize)]
#[serde(transparent)]
pub struct Secret(pub String);

impl Secret {
    /// 返回明文引用，仅应在构造认证头时调用。
    pub fn expose(&self) -> &str {
        &self.0
    }

    /// 是否为空。
    pub fn is_empty(&self) -> bool {
        self.0.is_empty()
    }
}

impl fmt::Debug for Secret {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        if self.0.is_empty() {
            f.write_str("Secret(\"\")")
        } else {
            f.write_str("Secret(\"<redacted>\")")
        }
    }
}

impl From<String> for Secret {
    fn from(value: String) -> Self {
        Self(value)
    }
}

/// 工作区配置。
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default)]
pub struct WorkspaceConfig {
    /// 会话根目录；相对路径按当前工作目录解析。
    pub root: PathBuf,
    /// 允许访问的根目录集合，默认为 `root`。
    pub allowed_roots: Vec<PathBuf>,
    /// 是否允许写入（只读模式下所有写操作被拒绝）。
    pub writable: bool,
    /// 是否禁止写入 `.git` 内部文件。
    pub protect_git_dir: bool,
    /// 单次读取的最大字节数。
    pub max_read_bytes: usize,
    /// 单次写入的最大字节数。
    pub max_write_bytes: usize,
}

impl Default for WorkspaceConfig {
    fn default() -> Self {
        Self {
            root: PathBuf::from("."),
            allowed_roots: Vec::new(),
            writable: true,
            protect_git_dir: true,
            max_read_bytes: 1_048_576,
            max_write_bytes: 4_194_304,
        }
    }
}

/// 模型能力声明。服务端不会假设所有模型都支持工具调用或流式输出。
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default)]
pub struct ModelCapabilities {
    pub tools: bool,
    pub streaming: bool,
}

impl Default for ModelCapabilities {
    fn default() -> Self {
        Self {
            tools: true,
            streaming: true,
        }
    }
}

/// 通用 OpenAI 兼容模型配置。
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default)]
pub struct ModelConfig {
    /// 模型 API 根地址，例如 `https://api.example.com/v1`。
    pub base_url: String,
    /// API 密钥；云端代理模式下可以为空。
    pub api_key: Secret,
    /// 模型名称。
    pub model: String,
    pub temperature: f32,
    pub max_output_tokens: u32,
    /// 模型上下文窗口，用于本地上下文裁剪。
    pub context_window: u64,
    pub request_timeout_secs: u64,
    pub max_retries: u32,
    pub stream: bool,
    /// 是否允许非 HTTPS 端点（仅建议本机回环地址使用）。
    pub allow_insecure_http: bool,
    pub capabilities: ModelCapabilities,
    /// 附加请求头，用于网关鉴权或路由。
    pub extra_headers: BTreeMap<String, String>,
}

impl Default for ModelConfig {
    fn default() -> Self {
        Self {
            base_url: String::new(),
            api_key: Secret::default(),
            model: String::new(),
            temperature: 0.2,
            max_output_tokens: 8_192,
            context_window: 128_000,
            request_timeout_secs: 120,
            max_retries: 3,
            stream: true,
            allow_insecure_http: false,
            capabilities: ModelCapabilities::default(),
            extra_headers: BTreeMap::new(),
        }
    }
}

impl ModelConfig {
    /// 校验模型配置是否足以发起真实请求。
    pub fn validate(&self) -> Result<(), CoreError> {
        if self.model.trim().is_empty() {
            return Err(CoreError::Config(
                "model.model is empty; configure an OpenAI-compatible model endpoint".into(),
            ));
        }
        if self.base_url.trim().is_empty() {
            return Err(CoreError::Config("model.base_url is empty".into()));
        }
        if self.api_key.is_empty() {
            return Err(CoreError::Config("model.api_key is empty".into()));
        }
        if !self.allow_insecure_http && !self.base_url.starts_with("https://") {
            return Err(CoreError::Config(
                "model.base_url must use https; set model.allow_insecure_http=true only for a trusted loopback endpoint"
                    .into(),
            ));
        }
        if self.request_timeout_secs == 0 {
            return Err(CoreError::Config(
                "model.request_timeout_secs must be greater than zero".into(),
            ));
        }
        if self.max_output_tokens == 0 {
            return Err(CoreError::Config(
                "model.max_output_tokens must be greater than zero".into(),
            ));
        }
        Ok(())
    }
}

/// Agent 迭代与资源限制。
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default)]
pub struct AgentLimits {
    pub max_iterations: u32,
    pub max_duration_secs: u64,
    pub token_budget: u64,
    pub tool_call_budget: u32,
    /// 单条工具输出送入模型前的最大字符数。
    pub max_tool_output_chars: usize,
    /// 保留在上下文中的最近消息条数。
    pub context_keep_messages: usize,
}

impl Default for AgentLimits {
    fn default() -> Self {
        Self {
            max_iterations: 30,
            max_duration_secs: 1_800,
            token_budget: 200_000,
            tool_call_budget: 100,
            max_tool_output_chars: 40_000,
            context_keep_messages: 24,
        }
    }
}

/// 权限模式。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum PermissionMode {
    /// 仅允许读取。
    ReadOnly,
    /// 允许在授权工作区内读写与执行命令。
    WorkspaceWrite,
    /// 明确选择的高权限模式，仍记录审计事件。
    DangerFullAccess,
}

/// 权限策略配置。
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default)]
pub struct PermissionConfig {
    pub mode: PermissionMode,
    /// 不高于该风险等级的操作自动放行。
    pub auto_approve_max_risk: coder_protocol::RiskLevel,
    /// 高风险操作是否自动放行；默认关闭。
    pub auto_approve_high_risk: bool,
    /// 额外允许继承给子进程的环境变量。
    pub extra_env_allowlist: Vec<String>,
    /// 命令执行默认超时。
    pub command_timeout_secs: u64,
    /// 命令执行最大超时。
    pub max_command_timeout_secs: u64,
    /// 单次命令 stdout/stderr 捕获上限。
    pub max_command_output_bytes: usize,
}

impl Default for PermissionConfig {
    fn default() -> Self {
        Self {
            mode: PermissionMode::WorkspaceWrite,
            auto_approve_max_risk: coder_protocol::RiskLevel::Medium,
            auto_approve_high_risk: false,
            extra_env_allowlist: Vec::new(),
            command_timeout_secs: 120,
            max_command_timeout_secs: 600,
            max_command_output_bytes: 1_048_576,
        }
    }
}

/// 本地会话存储配置。
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default)]
pub struct StorageConfig {
    /// SQLite 文件路径；为空时使用平台数据目录。
    pub sqlite_path: PathBuf,
}

impl Default for StorageConfig {
    fn default() -> Self {
        Self {
            sqlite_path: PathBuf::new(),
        }
    }
}

impl StorageConfig {
    /// 解析最终的 SQLite 路径并创建父目录。
    pub fn resolved_path(&self) -> Result<PathBuf, CoreError> {
        let path = if self.sqlite_path.as_os_str().is_empty() {
            default_data_dir()?.join("coder.db")
        } else {
            self.sqlite_path.clone()
        };
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        Ok(path)
    }
}

/// 返回 Coder 的平台数据目录。
pub fn default_data_dir() -> Result<PathBuf, CoreError> {
    let base = dirs::data_local_dir()
        .or_else(dirs::data_dir)
        .ok_or_else(|| CoreError::Config("cannot determine platform data directory".into()))?;
    Ok(base.join("coder"))
}

/// 返回 Coder 的平台配置目录。
pub fn default_config_dir() -> Result<PathBuf, CoreError> {
    let base = dirs::config_dir()
        .ok_or_else(|| CoreError::Config("cannot determine platform config directory".into()))?;
    Ok(base.join("coder"))
}

/// 本地直接执行模式的完整配置。
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct RunnerConfig {
    pub workspace: WorkspaceConfig,
    pub model: ModelConfig,
    pub agent: AgentLimits,
    pub permissions: PermissionConfig,
    pub storage: StorageConfig,
}

impl RunnerConfig {
    /// 从可选 TOML 文件和环境变量加载配置。
    ///
    /// 环境变量使用 `CODER_` 前缀，嵌套字段用双下划线分隔，例如
    /// `CODER_MODEL__API_KEY`。
    pub fn load(path: Option<&Path>) -> Result<Self, CoreError> {
        let config = Self::load_unvalidated(path)?;
        config.validate()?;
        Ok(config)
    }

    /// 加载配置但不执行校验，供 CLI 先应用命令行覆盖再统一校验。
    pub fn load_unvalidated(path: Option<&Path>) -> Result<Self, CoreError> {
        let mut figment = Figment::from(Serialized::defaults(Self::default()));
        if let Some(path) = path {
            figment = figment.merge(Toml::file(path));
        }
        figment
            .merge(Env::prefixed("CODER_").split("__"))
            .extract()
            .map_err(|err| CoreError::Config(err.to_string()))
    }

    /// 执行配置校验并规范化工作区路径。
    pub fn validate(&self) -> Result<(), CoreError> {
        if self.agent.max_iterations == 0 {
            return Err(CoreError::Config(
                "agent.max_iterations must be greater than zero".into(),
            ));
        }
        if self.agent.max_duration_secs == 0 {
            return Err(CoreError::Config(
                "agent.max_duration_secs must be greater than zero".into(),
            ));
        }
        if !self.workspace.writable && self.permissions.mode != PermissionMode::ReadOnly {
            return Err(CoreError::Config(
                "workspace.writable=false requires permissions.mode=read_only".into(),
            ));
        }
        Ok(())
    }
}
