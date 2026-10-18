//! Coder 命令行入口：本地任务执行、Agent 连接、云端管理与诊断。

mod approval;
mod cloud_api;
mod commands;
mod config_util;
mod render;

use std::path::PathBuf;

use clap::{ArgGroup, Args, Parser, Subcommand, ValueEnum};
use coder_local_agent::config::ModelMode;

/// Coder：可连接云端调度的自主编程智能体。
#[derive(Debug, Parser)]
#[command(
    name = "coder",
    version,
    about = "Cross-platform autonomous coding agent",
    propagate_version = true
)]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

/// 顶层命令。
#[derive(Debug, Subcommand)]
enum Command {
    /// 在当前工作区直接执行一次任务。
    Run(RunArgs),
    /// 注册设备或运行连接云端的本地 Agent。
    Agent {
        #[command(subcommand)]
        command: AgentCommand,
    },
    /// 启动 Coder Cloud 服务。
    Serve(ServeArgs),
    /// 本地配置的生成与查看。
    Config {
        #[command(subcommand)]
        command: ConfigCommand,
    },
    /// 检查配置、工作区、数据库与云端连通性。
    Doctor(DoctorArgs),
    /// 查看本地 SQLite 中保存的会话。
    Sessions {
        #[command(subcommand)]
        command: SessionsCommand,
    },
    /// 通过云端 API 管理任务。
    Tasks {
        #[command(subcommand)]
        command: TasksCommand,
    },
    /// 通过云端 API 查看设备。
    Devices {
        #[command(subcommand)]
        command: DevicesCommand,
    },
    /// 使用管理员密钥创建用户与配对令牌。
    Admin {
        #[command(subcommand)]
        command: AdminCommand,
    },
    /// 打印版本信息。
    Version,
}

/// 直接执行任务的参数。
#[derive(Debug, Args)]
struct RunArgs {
    /// 任务描述；包含多个词时可直接书写。
    #[arg(required = true, num_args = 1.., trailing_var_arg = true)]
    prompt: Vec<String>,
    /// 工作区根目录，覆盖配置文件中的值。
    #[arg(short = 'C', long)]
    workspace: Option<PathBuf>,
    /// TOML 配置文件；默认读取平台配置目录下的 config.toml。
    #[arg(long)]
    config: Option<PathBuf>,
    /// 只读模式：禁止写入和命令执行。
    #[arg(long)]
    read_only: bool,
    /// 明确允许自动批准高风险操作。
    #[arg(long)]
    yes: bool,
    /// 覆盖模型名称。
    #[arg(long)]
    model: Option<String>,
    /// 覆盖模型 API 根地址。
    #[arg(long)]
    base_url: Option<String>,
    /// 覆盖模型 API 密钥；建议改用 CODER_MODEL__API_KEY 环境变量。
    #[arg(long, hide_env_values = true)]
    api_key: Option<String>,
    /// 覆盖最大模型迭代次数。
    #[arg(long)]
    max_iterations: Option<u32>,
    /// 覆盖任务最长执行秒数。
    #[arg(long)]
    max_duration_secs: Option<u64>,
    /// 以 JSON Lines 输出结构化事件。
    #[arg(long)]
    json: bool,
    /// 显示迭代与 token 统计事件。
    #[arg(long)]
    verbose: bool,
}

/// Agent 子命令。
#[derive(Debug, Subcommand)]
enum AgentCommand {
    /// 使用一次性配对令牌注册设备并保存加密凭据。
    Register(AgentRegisterArgs),
    /// 运行本地 Agent，持续接受云端下发的任务。
    Run(AgentRunArgs),
}

/// 设备注册参数。
#[derive(Debug, Args)]
struct AgentRegisterArgs {
    /// 云端根地址，例如 https://coder.example.com。
    #[arg(long, env = "CODER_CLOUD_URL", hide_env_values = true)]
    server_url: Option<String>,
    /// 管理员签发的一次性配对令牌。
    #[arg(long, env = "CODER_PAIRING_TOKEN", hide_env_values = true)]
    pairing_token: Option<String>,
    /// 设备显示名称。
    #[arg(long)]
    name: Option<String>,
    /// 凭据保存路径。
    #[arg(long)]
    credentials_path: Option<PathBuf>,
}

/// 本地 Agent 运行参数。
#[derive(Debug, Args)]
struct AgentRunArgs {
    /// TOML 配置文件；默认读取平台配置目录下的 agent.toml。
    #[arg(long)]
    config: Option<PathBuf>,
    /// 覆盖云端根地址。
    #[arg(long, env = "CODER_CLOUD_URL", hide_env_values = true)]
    server_url: Option<String>,
    /// 覆盖设备名称。
    #[arg(long)]
    device_name: Option<String>,
    /// 首次注册使用的一次性配对令牌。
    #[arg(long, env = "CODER_PAIRING_TOKEN", hide_env_values = true)]
    pairing_token: Option<String>,
    /// 覆盖凭据路径。
    #[arg(long)]
    credentials_path: Option<PathBuf>,
    /// 可执行任务的授权工作区根目录，可重复指定。
    #[arg(long = "workspace")]
    workspace_roots: Vec<PathBuf>,
    /// 模型推理模式。
    #[arg(long, value_enum)]
    model_mode: Option<ModelModeArg>,
    /// 仅对回环地址允许明文 ws://，用于本机联调。
    #[arg(long)]
    allow_insecure_ws: bool,
}

/// 模型推理模式的命令行表示。
#[derive(Clone, Copy, Debug, ValueEnum)]
enum ModelModeArg {
    Direct,
    Cloud,
}

impl From<ModelModeArg> for ModelMode {
    /// 映射到本地 Agent 的配置枚举。
    fn from(value: ModelModeArg) -> Self {
        match value {
            ModelModeArg::Direct => ModelMode::Direct,
            ModelModeArg::Cloud => ModelMode::Cloud,
        }
    }
}

/// 云端服务启动参数。
#[derive(Debug, Args)]
struct ServeArgs {
    /// 云端 TOML 配置文件。
    #[arg(long)]
    config: Option<PathBuf>,
}

/// 配置子命令。
#[derive(Debug, Subcommand)]
enum ConfigCommand {
    /// 生成一份本地执行配置。
    Init(ConfigInitArgs),
    /// 打印脱敏后的有效配置。
    Show(ConfigShowArgs),
}

/// 配置生成参数。
#[derive(Debug, Args)]
struct ConfigInitArgs {
    /// 写入路径；默认为平台配置目录下的 config.toml。
    #[arg(long)]
    path: Option<PathBuf>,
    /// 覆盖已存在的文件。
    #[arg(long)]
    force: bool,
}

/// 配置查看参数。
#[derive(Debug, Args)]
struct ConfigShowArgs {
    /// TOML 配置文件。
    #[arg(long)]
    config: Option<PathBuf>,
    /// 查看本地 Agent 配置而非直接执行配置。
    #[arg(long)]
    agent: bool,
}

/// 诊断参数。
#[derive(Debug, Args)]
struct DoctorArgs {
    /// TOML 配置文件。
    #[arg(long)]
    config: Option<PathBuf>,
    /// 以本地 Agent 配置进行诊断。
    #[arg(long)]
    agent: bool,
    /// 额外请求云端健康检查。
    #[arg(long)]
    online: bool,
}

/// 会话子命令。
#[derive(Debug, Subcommand)]
enum SessionsCommand {
    /// 列出最近的会话。
    List(SessionsListArgs),
    /// 查看单个会话与完整消息记录。
    Show(SessionsShowArgs),
}

/// 会话列表参数。
#[derive(Debug, Args)]
struct SessionsListArgs {
    /// 返回条数上限。
    #[arg(long, default_value_t = 20)]
    limit: u32,
    /// TOML 配置文件。
    #[arg(long)]
    config: Option<PathBuf>,
    /// JSON 输出。
    #[arg(long)]
    json: bool,
}

/// 会话详情参数。
#[derive(Debug, Args)]
struct SessionsShowArgs {
    /// 会话 ID。
    session_id: String,
    /// TOML 配置文件。
    #[arg(long)]
    config: Option<PathBuf>,
    /// JSON 输出。
    #[arg(long)]
    json: bool,
}

/// 云端 API 认证参数。
#[derive(Debug, Args)]
struct CloudArgs {
    /// 云端根地址。
    #[arg(long, env = "CODER_CLOUD_URL", hide_env_values = true)]
    server_url: Option<String>,
    /// 用户 API key。
    #[arg(long, env = "CODER_API_KEY", hide_env_values = true)]
    api_key: Option<String>,
    /// JSON 输出。
    #[arg(long)]
    json: bool,
}

/// 管理员 API 认证参数。
#[derive(Debug, Args)]
struct AdminArgs {
    /// 云端根地址。
    #[arg(long, env = "CODER_CLOUD_URL", hide_env_values = true)]
    server_url: Option<String>,
    /// 管理员密钥。
    #[arg(long, env = "CODER_ADMIN_KEY", hide_env_values = true)]
    admin_key: Option<String>,
    /// JSON 输出。
    #[arg(long)]
    json: bool,
}

/// 任务子命令。
#[derive(Debug, Subcommand)]
enum TasksCommand {
    /// 列出当前用户的任务。
    List {
        #[command(flatten)]
        cloud: CloudArgs,
        /// 返回条数上限。
        #[arg(long, default_value_t = 50)]
        limit: u32,
    },
    /// 创建并下发任务。
    Create {
        #[command(flatten)]
        cloud: CloudArgs,
        /// 目标设备 ID。
        #[arg(long)]
        device_id: String,
        /// 任务描述。
        #[arg(long)]
        prompt: String,
        /// Agent 机器上的绝对工作区路径。
        #[arg(long)]
        workspace: String,
    },
    /// 查看任务详情。
    Show {
        #[command(flatten)]
        cloud: CloudArgs,
        /// 任务 ID。
        task_id: String,
    },
    /// 请求取消任务。
    Cancel {
        #[command(flatten)]
        cloud: CloudArgs,
        /// 任务 ID。
        task_id: String,
    },
    /// 以新的 attempt 重试终态任务。
    Retry {
        #[command(flatten)]
        cloud: CloudArgs,
        /// 任务 ID。
        task_id: String,
    },
    /// 列出待处理的高风险权限请求。
    Permissions {
        #[command(flatten)]
        cloud: CloudArgs,
        /// 任务 ID。
        task_id: String,
    },
    /// 批准或拒绝权限请求。
    #[command(group(
        ArgGroup::new("decision")
            .required(true)
            .args(["grant", "deny"])
    ))]
    Decide {
        #[command(flatten)]
        cloud: CloudArgs,
        /// 任务 ID。
        task_id: String,
        /// 权限请求 ID。
        request_id: String,
        /// 批准。
        #[arg(long)]
        grant: bool,
        /// 拒绝。
        #[arg(long)]
        deny: bool,
        /// 拒绝原因。
        #[arg(long)]
        reason: Option<String>,
    },
}

/// 设备子命令。
#[derive(Debug, Subcommand)]
enum DevicesCommand {
    /// 列出当前用户的设备。
    List {
        #[command(flatten)]
        cloud: CloudArgs,
    },
}

/// 管理员子命令。
#[derive(Debug, Subcommand)]
enum AdminCommand {
    /// 创建用户并返回一次性 API key。
    CreateUser {
        #[command(flatten)]
        admin: AdminArgs,
        /// 用户邮箱。
        #[arg(long)]
        email: String,
    },
    /// 为用户签发一次性配对令牌。
    CreatePairing {
        #[command(flatten)]
        admin: AdminArgs,
        /// 用户 ID。
        #[arg(long)]
        user_id: String,
    },
}

#[tokio::main]
async fn main() -> std::process::ExitCode {
    init_tracing();
    let cli = Cli::parse();
    match dispatch(cli.command).await {
        Ok(code) => std::process::ExitCode::from(code),
        Err(err) => {
            eprintln!("错误: {err:#}");
            std::process::ExitCode::from(1)
        }
    }
}

/// 分发子命令并统一映射退出码。
async fn dispatch(command: Command) -> anyhow::Result<u8> {
    match command {
        Command::Run(args) => commands::run::execute(args).await,
        Command::Agent { command } => match command {
            AgentCommand::Register(args) => commands::agent::register(args).await,
            AgentCommand::Run(args) => commands::agent::run(args).await,
        },
        Command::Serve(args) => {
            coder_cloud::run(args.config).await?;
            Ok(0)
        }
        Command::Config { command } => commands::config_cmd::execute(command).await,
        Command::Doctor(args) => {
            let healthy = commands::doctor::execute(args).await?;
            Ok(if healthy { 0 } else { 1 })
        }
        Command::Sessions { command } => commands::sessions::execute(command).await,
        Command::Tasks { command } => commands::tasks::execute(command).await,
        Command::Devices { command } => commands::devices::execute(command).await,
        Command::Admin { command } => commands::admin::execute(command).await,
        Command::Version => {
            println!("coder {}", env!("CARGO_PKG_VERSION"));
            println!("protocol {}", coder_protocol::PROTOCOL_VERSION);
            println!("platform {}", std::env::consts::OS);
            Ok(0)
        }
    }
}

/// 初始化日志；默认只输出警告，避免干扰命令行结果。
fn init_tracing() {
    let filter = tracing_subscriber::EnvFilter::try_from_default_env()
        .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("warn"));
    let _ = tracing_subscriber::fmt()
        .with_env_filter(filter)
        .with_writer(std::io::stderr)
        .with_target(false)
        .try_init();
}
