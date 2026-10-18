use std::{
    path::{Path, PathBuf},
    process::Stdio,
    sync::Arc,
    time::{Duration, Instant},
};

use async_trait::async_trait;
use coder_protocol::RiskLevel;
use serde_json::{Value, json};
use tokio::{
    io::{AsyncRead, AsyncReadExt},
    process::Command,
};

use super::{Tool, ToolContext, ToolError, ToolOutput, optional_str, optional_u64, required_str};
use crate::{
    events::AgentEvent,
    model::ToolSpec,
    permissions::{PermissionPolicy, classify_command_risk},
};

/// 工作区命令执行工具。
pub struct ShellTool {
    policy: Arc<PermissionPolicy>,
}

impl ShellTool {
    /// 构造命令执行工具。
    pub fn new(policy: Arc<PermissionPolicy>) -> Self {
        Self { policy }
    }
}

#[async_trait]
impl Tool for ShellTool {
    fn spec(&self) -> ToolSpec {
        ToolSpec {
            name: "shell".into(),
            description: "Run a shell command inside the workspace and return its real stdout, stderr and exit code. Commands use cmd.exe on Windows and /bin/sh elsewhere, with a restricted environment and a timeout. Destructive commands require approval. Prefer read_file/search/edit_file for file work.".into(),
            parameters: json!({
                "type": "object",
                "properties": {
                    "command": {"type": "string"},
                    "cwd": {"type": "string", "description": "Working directory inside the workspace; defaults to the workspace root."},
                    "timeout_secs": {"type": "integer", "minimum": 1}
                },
                "required": ["command"],
                "additionalProperties": false
            }),
        }
    }

    fn risk(&self, args: &Value) -> RiskLevel {
        args.get("command")
            .and_then(Value::as_str)
            .map(classify_command_risk)
            .unwrap_or(RiskLevel::High)
    }

    async fn execute(&self, ctx: &ToolContext, args: Value) -> Result<ToolOutput, ToolError> {
        let command = required_str(&args, "command")?;
        if command.trim().is_empty() {
            return Err(ToolError::InvalidArguments(
                "command must not be empty".into(),
            ));
        }
        let permission = ctx.policy.check_execute(&command);
        if !permission.allowed {
            return Err(ToolError::PermissionDenied(permission.reason));
        }
        let cwd = resolve_cwd(ctx, optional_str(&args, "cwd"))?;
        let timeout_secs = optional_u64(&args, "timeout_secs")
            .unwrap_or(self.policy.command_timeout_secs())
            .clamp(1, self.policy.max_command_timeout_secs());
        let result = run_command(ctx, &command, &cwd, timeout_secs, self.policy.as_ref()).await?;

        let display = format_command(&command, &cwd, &result);
        Ok(ToolOutput::text(
            ctx.clamp_output(display),
            result.summary(),
        ))
    }
}

/// 项目构建、测试与静态检查工具。
pub struct RunTestsTool {
    policy: Arc<PermissionPolicy>,
}

impl RunTestsTool {
    /// 构造项目检查工具。
    pub fn new(policy: Arc<PermissionPolicy>) -> Self {
        Self { policy }
    }
}

#[async_trait]
impl Tool for RunTestsTool {
    fn spec(&self) -> ToolSpec {
        ToolSpec {
            name: "run_project_checks".into(),
            description: "Run the project's real build, test or lint command and return actual output. Detects Cargo, npm, Go and pytest projects. Pass `command` to override detection. Use this to verify changes instead of assuming they work.".into(),
            parameters: json!({
                "type": "object",
                "properties": {
                    "kind": {"type": "string", "enum": ["tests", "build", "lint"], "default": "tests"},
                    "command": {"type": "string", "description": "Explicit command overriding auto-detection."},
                    "timeout_secs": {"type": "integer", "minimum": 1}
                },
                "additionalProperties": false
            }),
        }
    }

    fn risk(&self, _args: &Value) -> RiskLevel {
        RiskLevel::Medium
    }

    async fn execute(&self, ctx: &ToolContext, args: Value) -> Result<ToolOutput, ToolError> {
        let kind = optional_str(&args, "kind").unwrap_or_else(|| "tests".into());
        let command = if let Some(command) = optional_str(&args, "command") {
            command
        } else {
            detect_command(ctx.workspace.root(), &kind)?
        };
        let permission = ctx.policy.check_execute(&command);
        if !permission.allowed {
            return Err(ToolError::PermissionDenied(permission.reason));
        }
        let timeout_secs = optional_u64(&args, "timeout_secs")
            .unwrap_or(self.policy.command_timeout_secs())
            .clamp(1, self.policy.max_command_timeout_secs());
        let cwd = ctx.workspace.root().to_path_buf();
        let result = run_command(ctx, &command, &cwd, timeout_secs, self.policy.as_ref()).await?;
        Ok(ToolOutput::text(
            ctx.clamp_output(format_command(&command, &cwd, &result)),
            result.summary(),
        ))
    }
}

/// 命令执行结果。
struct CommandResult {
    exit_code: Option<i32>,
    stdout: String,
    stderr: String,
    timed_out: bool,
    canceled: bool,
    truncated: bool,
    duration: Duration,
}

impl CommandResult {
    /// 生成一行摘要。
    fn summary(&self) -> String {
        if self.canceled {
            return "command canceled".into();
        }
        if self.timed_out {
            return format!(
                "command timed out after {:.1}s",
                self.duration.as_secs_f64()
            );
        }
        match self.exit_code {
            Some(code) => format!("command exited {code} in {}ms", self.duration.as_millis()),
            None => format!(
                "command terminated by signal in {}ms",
                self.duration.as_millis()
            ),
        }
    }
}

/// 执行真实子进程，支持超时、取消与进程树终止。
async fn run_command(
    ctx: &ToolContext,
    command: &str,
    cwd: &Path,
    timeout_secs: u64,
    policy: &PermissionPolicy,
) -> Result<CommandResult, ToolError> {
    if ctx.cancel.is_cancelled() {
        return Err(ToolError::Canceled);
    }
    let mut cmd = build_shell_command(command);
    cmd.current_dir(cwd)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true);
    apply_environment(&mut cmd, policy);

    let started = Instant::now();
    let mut child = cmd
        .spawn()
        .map_err(|err| ToolError::Execution(format!("cannot start command `{command}`: {err}")))?;
    let pid = child.id();
    let stdout = child
        .stdout
        .take()
        .ok_or_else(|| ToolError::Execution("stdout pipe was not captured".into()))?;
    let stderr = child
        .stderr
        .take()
        .ok_or_else(|| ToolError::Execution("stderr pipe was not captured".into()))?;
    let cap = policy.max_command_output_bytes();
    let stdout_task = tokio::spawn(read_capped(stdout, cap));
    let stderr_task = tokio::spawn(read_capped(stderr, cap));

    let mut timed_out = false;
    let mut canceled = false;
    let status = tokio::select! {
        result = child.wait() => Some(result.map_err(|err| ToolError::Execution(err.to_string()))?),
        _ = tokio::time::sleep(Duration::from_secs(timeout_secs)) => {
            timed_out = true;
            None
        }
        _ = ctx.cancel.cancelled() => {
            canceled = true;
            None
        }
    };

    if status.is_none() {
        kill_process_tree(pid, &mut child).await;
        let _ = child.wait().await;
    }

    let (stdout, stdout_truncated) = stdout_task
        .await
        .map_err(|err| ToolError::Execution(format!("stdout reader failed: {err}")))?
        .map_err(|err| ToolError::Execution(format!("stdout read failed: {err}")))?;
    let (stderr, stderr_truncated) = stderr_task
        .await
        .map_err(|err| ToolError::Execution(format!("stderr reader failed: {err}")))?
        .map_err(|err| ToolError::Execution(format!("stderr read failed: {err}")))?;

    let exit_code = status.and_then(|status| status.code());
    let duration = started.elapsed();
    let result = CommandResult {
        exit_code,
        stdout,
        stderr,
        timed_out,
        canceled,
        truncated: stdout_truncated || stderr_truncated,
        duration,
    };
    ctx.emit(AgentEvent::CommandOutput {
        command: command.to_owned(),
        exit_code: if result.timed_out || result.canceled {
            None
        } else {
            result.exit_code
        },
        stdout_tail: tail(&result.stdout, 2_000),
        stderr_tail: tail(&result.stderr, 2_000),
    });
    if canceled {
        return Err(ToolError::Canceled);
    }
    Ok(result)
}

/// 构造平台 shell 命令。
fn build_shell_command(command: &str) -> Command {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        let shell = std::env::var("ComSpec").unwrap_or_else(|_| "cmd.exe".to_owned());
        let mut cmd = Command::new(shell);
        // cmd 与实际子进程默认使用 OEM 代码页；切换到 UTF-8 后输出才能被正确解码。
        cmd.arg("/C").arg(format!("chcp 65001 >NUL & {command}"));
        cmd.as_std_mut().creation_flags(CREATE_NO_WINDOW);
        cmd
    }
    #[cfg(not(windows))]
    {
        let mut cmd = Command::new("/bin/sh");
        cmd.arg("-c").arg(command);
        #[cfg(unix)]
        {
            use std::os::unix::process::CommandExt;
            cmd.as_std_mut().process_group(0);
        }
        cmd
    }
}

/// 应用最小环境变量白名单。
fn apply_environment(cmd: &mut Command, policy: &PermissionPolicy) {
    const WINDOWS_ENV: &[&str] = &[
        "PATH",
        "PATHEXT",
        "SystemRoot",
        "windir",
        "ComSpec",
        "TEMP",
        "TMP",
        "USERPROFILE",
        "APPDATA",
        "LOCALAPPDATA",
        "ProgramData",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "HOMEDRIVE",
        "HOMEPATH",
        "USERNAME",
        "NUMBER_OF_PROCESSORS",
        "PROCESSOR_ARCHITECTURE",
        "OS",
    ];
    const UNIX_ENV: &[&str] = &[
        "PATH", "HOME", "USER", "LOGNAME", "SHELL", "LANG", "LC_ALL", "LC_CTYPE", "TERM", "TMPDIR",
        "TZ",
    ];
    cmd.env_clear();
    let keys = if cfg!(windows) { WINDOWS_ENV } else { UNIX_ENV };
    for key in keys {
        if let Ok(value) = std::env::var(key) {
            cmd.env(key, value);
        }
    }
    for key in policy.extra_env_allowlist() {
        if let Ok(value) = std::env::var(key) {
            cmd.env(key, value);
        }
    }
}

/// 读取子进程输出，超过上限后继续排空管道但丢弃多余内容。
async fn read_capped<R: AsyncRead + Unpin>(
    mut reader: R,
    capacity: usize,
) -> std::io::Result<(String, bool)> {
    let mut buffer = [0u8; 8_192];
    let mut captured = Vec::new();
    let mut truncated = false;
    loop {
        let read = reader.read(&mut buffer).await?;
        if read == 0 {
            break;
        }
        if captured.len() < capacity {
            let remaining = capacity - captured.len();
            let take = remaining.min(read);
            captured.extend_from_slice(&buffer[..take]);
            if take < read {
                truncated = true;
            }
        } else {
            truncated = true;
        }
    }
    Ok((String::from_utf8_lossy(&captured).into_owned(), truncated))
}

/// 终止子进程及其进程树。
async fn kill_process_tree(pid: Option<u32>, child: &mut tokio::process::Child) {
    #[cfg(unix)]
    if let Some(pid) = pid {
        // 子进程以独立进程组启动，向负 PID 发送 SIGKILL 覆盖整个进程组。
        unsafe {
            libc::kill(-(pid as i32), libc::SIGKILL);
            libc::kill(pid as i32, libc::SIGKILL);
        }
    }
    #[cfg(windows)]
    if let Some(pid) = pid {
        let mut taskkill = Command::new("taskkill");
        taskkill
            .args(["/PID", &pid.to_string(), "/T", "/F"])
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .kill_on_drop(true);
        if let Ok(mut killer) = taskkill.spawn() {
            let _ = tokio::time::timeout(Duration::from_secs(5), killer.wait()).await;
        }
    }
    let _ = child.start_kill();
}

/// 解析并校验工作目录。
fn resolve_cwd(ctx: &ToolContext, cwd: Option<String>) -> Result<PathBuf, ToolError> {
    let path = if let Some(cwd) = cwd {
        ctx.workspace.resolve_existing(&cwd)?
    } else {
        ctx.workspace.root().to_path_buf()
    };
    if !path.is_dir() {
        return Err(ToolError::InvalidArguments(format!(
            "working directory {} is not a directory",
            path.display()
        )));
    }
    if ctx.workspace.is_git_internal(&path) {
        return Err(ToolError::PermissionDenied(
            "running commands inside git metadata is blocked".into(),
        ));
    }
    Ok(path)
}

/// 根据项目类型检测构建/测试/检查命令。
fn detect_command(root: &Path, kind: &str) -> Result<String, ToolError> {
    if root.join("Cargo.toml").is_file() {
        return Ok(match kind {
            "tests" => "cargo test --workspace --all-targets".into(),
            "build" => "cargo build --workspace --all-targets".into(),
            "lint" => "cargo clippy --workspace --all-targets -- -D warnings".into(),
            other => {
                return Err(ToolError::InvalidArguments(format!(
                    "unsupported check kind `{other}`"
                )));
            }
        });
    }
    if root.join("package.json").is_file() {
        let package = std::fs::read_to_string(root.join("package.json"))?;
        let parsed: Value = serde_json::from_str(&package).map_err(|err| {
            ToolError::Execution(format!("package.json is not valid JSON: {err}"))
        })?;
        let script = match kind {
            "tests" => "test",
            "build" => "build",
            "lint" => "lint",
            other => {
                return Err(ToolError::InvalidArguments(format!(
                    "unsupported check kind `{other}`"
                )));
            }
        };
        let has_script = parsed
            .get("scripts")
            .and_then(|scripts| scripts.get(script))
            .is_some();
        if !has_script {
            return Err(ToolError::Execution(format!(
                "package.json has no `{script}` script; pass an explicit command"
            )));
        }
        return Ok(format!("npm run {script}"));
    }
    if root.join("go.mod").is_file() {
        return Ok(match kind {
            "tests" => "go test ./...".into(),
            "build" => "go build ./...".into(),
            "lint" => "go vet ./...".into(),
            other => {
                return Err(ToolError::InvalidArguments(format!(
                    "unsupported check kind `{other}`"
                )));
            }
        });
    }
    if root.join("pyproject.toml").is_file() || root.join("setup.py").is_file() {
        if kind == "tests" {
            return Ok("python -m pytest -q".into());
        }
        return Err(ToolError::Execution(format!(
            "no {kind} command is defined for this Python project; pass an explicit command"
        )));
    }
    Err(ToolError::Execution(
        "cannot detect a supported project; pass an explicit `command`".into(),
    ))
}

/// 格式化命令结果供模型阅读。
fn format_command(command: &str, cwd: &Path, result: &CommandResult) -> String {
    let mut text = format!(
        "command: {command}\ncwd: {}\nexit_code: {}\ntimed_out: {}\nduration_ms: {}\n",
        cwd.display(),
        result
            .exit_code
            .map(|code| code.to_string())
            .unwrap_or_else(|| "signal".into()),
        result.timed_out,
        result.duration.as_millis()
    );
    text.push_str("\n--- stdout ---\n");
    text.push_str(if result.stdout.is_empty() {
        "(empty)"
    } else {
        &result.stdout
    });
    text.push_str("\n--- stderr ---\n");
    text.push_str(if result.stderr.is_empty() {
        "(empty)"
    } else {
        &result.stderr
    });
    if result.truncated {
        text.push_str("\n[output truncated at configured limit]");
    }
    if result.timed_out {
        text.push_str("\n[command exceeded its timeout and the process tree was terminated]");
    }
    text
}

/// 返回文本末尾若干字节（按字符边界截断）。
fn tail(text: &str, max: usize) -> String {
    if text.len() <= max {
        return text.to_owned();
    }
    let mut start = text.len() - max;
    while !text.is_char_boundary(start) {
        start += 1;
    }
    format!("…{}", &text[start..])
}
