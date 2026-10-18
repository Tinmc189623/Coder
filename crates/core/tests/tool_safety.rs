//! 验证文件工具与命令工具的路径边界、真实输出、超时和输出上限。

use std::{path::Path, sync::Arc, time::Duration};

use coder_core::{
    config::{PermissionConfig, PermissionMode, WorkspaceConfig},
    permissions::PermissionPolicy,
    tools::{EditFileTool, ReadFileTool, ShellTool, Tool, ToolContext, ToolError, WriteFileTool},
    workspace::Workspace,
};
use coder_protocol::PlanItem;
use serde_json::json;
use tokio::sync::{Mutex, broadcast};
use tokio_util::sync::CancellationToken;

/// 构造一个可写的工作区与工具上下文。
fn context(root: &Path) -> ToolContext {
    context_with(root, PermissionConfig::default(), true)
}

/// 构造指定权限与可写状态的工作区上下文。
fn context_with(root: &Path, permissions: PermissionConfig, writable: bool) -> ToolContext {
    let workspace = Workspace::new(WorkspaceConfig {
        root: root.to_path_buf(),
        writable,
        ..WorkspaceConfig::default()
    })
    .expect("workspace");
    let (events, _) = broadcast::channel(64);
    ToolContext {
        workspace,
        policy: Arc::new(PermissionPolicy::new(permissions)),
        cancel: CancellationToken::new(),
        events,
        plan: Arc::new(Mutex::new(Vec::<PlanItem>::new())),
        max_output_chars: 1_000_000,
    }
}

#[tokio::test]
async fn file_tools_write_edit_and_read_real_content() {
    let dir = tempfile::tempdir().expect("workspace");
    let ctx = context(dir.path());

    let written = WriteFileTool
        .execute(
            &ctx,
            json!({"path": "src/nested/greeting.txt", "content": "hello\nworld\n"}),
        )
        .await
        .expect("write file");
    assert_eq!(written.changed_files.len(), 1);
    assert_eq!(
        std::fs::read_to_string(dir.path().join("src/nested/greeting.txt")).expect("real file"),
        "hello\nworld\n"
    );

    let edited = EditFileTool
        .execute(
            &ctx,
            json!({"path": "src/nested/greeting.txt", "old_string": "world", "new_string": "coder"}),
        )
        .await
        .expect("edit file");
    assert!(edited.content.contains("-world"));
    assert!(edited.content.contains("+coder"));
    assert_eq!(
        std::fs::read_to_string(dir.path().join("src/nested/greeting.txt")).expect("edited file"),
        "hello\ncoder\n"
    );

    let read = ReadFileTool
        .execute(&ctx, json!({"path": "src/nested/greeting.txt"}))
        .await
        .expect("read file");
    assert_eq!(read.content, "hello\ncoder\n");
}

#[tokio::test]
async fn rejects_parent_traversal_and_outside_absolute_paths() {
    let base = tempfile::tempdir().expect("base");
    let root = base.path().join("workspace");
    std::fs::create_dir(&root).expect("workspace dir");
    std::fs::write(base.path().join("secret.txt"), "outside-secret").expect("outside file");
    let ctx = context(&root);

    let read_error = ReadFileTool
        .execute(&ctx, json!({"path": "../secret.txt"}))
        .await
        .expect_err("parent traversal must fail");
    assert!(matches!(read_error, ToolError::PermissionDenied(_)));

    let absolute_error = ReadFileTool
        .execute(
            &ctx,
            json!({"path": base.path().join("secret.txt").display().to_string()}),
        )
        .await
        .expect_err("outside absolute path must fail");
    assert!(matches!(absolute_error, ToolError::PermissionDenied(_)));

    let write_error = WriteFileTool
        .execute(&ctx, json!({"path": "../escape.txt", "content": "escaped"}))
        .await
        .expect_err("escaping write must fail");
    assert!(matches!(write_error, ToolError::PermissionDenied(_)));
    assert!(!base.path().join("escape.txt").exists());
}

#[tokio::test]
async fn blocks_git_metadata_reads_and_writes() {
    let dir = tempfile::tempdir().expect("workspace");
    std::fs::create_dir_all(dir.path().join(".git")).expect("git dir");
    std::fs::write(dir.path().join(".git/config"), "[core]\n").expect("git config");
    let ctx = context(dir.path());

    let read_error = ReadFileTool
        .execute(&ctx, json!({"path": ".git/config"}))
        .await
        .expect_err("git metadata read must fail");
    assert!(matches!(read_error, ToolError::PermissionDenied(_)));

    let write_error = WriteFileTool
        .execute(
            &ctx,
            json!({"path": ".git/hooks/pre-commit", "content": "echo"}),
        )
        .await
        .expect_err("git metadata write must fail");
    assert!(matches!(write_error, ToolError::PermissionDenied(_)));
}

#[tokio::test]
async fn directory_link_cannot_escape_the_workspace() {
    let base = tempfile::tempdir().expect("base");
    let root = base.path().join("workspace");
    let outside = base.path().join("outside");
    std::fs::create_dir(&root).expect("workspace dir");
    std::fs::create_dir(&outside).expect("outside dir");
    std::fs::write(outside.join("secret.txt"), "outside-secret").expect("secret");
    if !link_directory(&outside, &root.join("linked")) {
        eprintln!("skipping directory link escape test: the platform refused to create a link");
        return;
    }
    let ctx = context(&root);

    let read_error = ReadFileTool
        .execute(&ctx, json!({"path": "linked/secret.txt"}))
        .await
        .expect_err("link escape read must fail");
    assert!(matches!(read_error, ToolError::PermissionDenied(_)));

    let write_error = WriteFileTool
        .execute(
            &ctx,
            json!({"path": "linked/created.txt", "content": "escaped"}),
        )
        .await
        .expect_err("link escape write must fail");
    assert!(matches!(write_error, ToolError::PermissionDenied(_)));
    assert!(!outside.join("created.txt").exists());
}

/// 创建目录链接；Windows 使用无需管理员权限的 junction。
fn link_directory(target: &Path, link: &Path) -> bool {
    #[cfg(windows)]
    {
        std::process::Command::new("cmd")
            .args(["/C", "mklink", "/J"])
            .arg(link)
            .arg(target)
            .output()
            .map(|output| output.status.success())
            .unwrap_or(false)
    }
    #[cfg(unix)]
    {
        std::os::unix::fs::symlink(target, link).is_ok()
    }
}

#[tokio::test]
async fn read_only_workspace_blocks_writes_and_commands() {
    let dir = tempfile::tempdir().expect("workspace");
    let permissions = PermissionConfig {
        mode: PermissionMode::ReadOnly,
        ..PermissionConfig::default()
    };
    let ctx = context_with(dir.path(), permissions, false);

    let write_error = WriteFileTool
        .execute(&ctx, json!({"path": "blocked.txt", "content": "no"}))
        .await
        .expect_err("read-only write must fail");
    assert!(matches!(write_error, ToolError::PermissionDenied(_)));
    assert!(!dir.path().join("blocked.txt").exists());

    let shell = ShellTool::new(ctx.policy.clone());
    let shell_error = shell
        .execute(&ctx, json!({"command": "echo should-not-run"}))
        .await
        .expect_err("read-only command must fail");
    assert!(matches!(shell_error, ToolError::PermissionDenied(_)));
}

#[tokio::test]
async fn shell_returns_real_output_and_exit_code() {
    let dir = tempfile::tempdir().expect("workspace");
    let ctx = context(dir.path());
    let shell = ShellTool::new(ctx.policy.clone());

    let output = shell
        .execute(&ctx, json!({"command": "echo coder-shell-marker"}))
        .await
        .expect("command runs");
    assert!(
        output.content.contains("coder-shell-marker"),
        "{}",
        output.content
    );
    assert!(
        output.content.contains("exit_code: 0"),
        "{}",
        output.content
    );
    assert!(output.summary.contains("exited 0"), "{}", output.summary);

    let missing = shell
        .execute(
            &ctx,
            json!({"command": "coder-command-that-does-not-exist-xyz"}),
        )
        .await
        .expect("missing command still returns a process result");
    assert!(
        !missing.content.contains("exit_code: 0"),
        "{}",
        missing.content
    );
    assert!(
        !missing.content.contains("(empty)\n--- stderr ---\n(empty)"),
        "a missing command must report real stderr output: {}",
        missing.content
    );
}

#[tokio::test]
async fn shell_timeout_terminates_the_process_tree() {
    let dir = tempfile::tempdir().expect("workspace");
    let ctx = context(dir.path());
    let shell = ShellTool::new(ctx.policy.clone());
    let command = if cfg!(windows) {
        "ping 127.0.0.1 -n 10 > nul"
    } else {
        "sleep 10"
    };

    let started = std::time::Instant::now();
    let output = shell
        .execute(&ctx, json!({"command": command, "timeout_secs": 1}))
        .await
        .expect("timed command returns a result");
    assert!(started.elapsed() < Duration::from_secs(8));
    assert!(
        output.content.contains("timed_out: true"),
        "{}",
        output.content
    );
    assert!(
        output.content.contains("process tree was terminated"),
        "{}",
        output.content
    );
    assert!(output.summary.contains("timed out"), "{}", output.summary);
}

#[tokio::test]
async fn shell_output_is_capped_at_the_configured_limit() {
    let dir = tempfile::tempdir().expect("workspace");
    let permissions = PermissionConfig {
        max_command_output_bytes: 128,
        ..PermissionConfig::default()
    };
    let ctx = context_with(dir.path(), permissions, true);
    let shell = ShellTool::new(ctx.policy.clone());
    let command = if cfg!(windows) {
        "for /L %i in (1,1,2000) do @echo 0123456789"
    } else {
        "i=0; while [ $i -lt 2000 ]; do echo 0123456789; i=$((i+1)); done"
    };

    let output = shell
        .execute(&ctx, json!({"command": command}))
        .await
        .expect("command runs");
    assert!(
        output.content.contains("exit_code: 0"),
        "{}",
        output.content
    );
    assert!(
        output
            .content
            .contains("output truncated at configured limit"),
        "{}",
        output.content
    );
    assert!(
        output.content.matches("0123456789").count() < 100,
        "captured output must respect the byte cap"
    );
}
