//! 把核心执行事件渲染到终端。

use coder_core::AgentEvent;
use coder_protocol::{FileChangeKind, PlanItemStatus};

/// 事件渲染器。
pub struct EventRenderer {
    json: bool,
    verbose: bool,
}

impl EventRenderer {
    /// 创建渲染器；`json` 输出 JSON Lines，`verbose` 展示迭代与 token 事件。
    pub fn new(json: bool, verbose: bool) -> Self {
        Self { json, verbose }
    }

    /// 渲染单个事件。
    pub fn render(&self, event: &AgentEvent) {
        if self.json {
            match serde_json::to_string(event) {
                Ok(line) => println!("{line}"),
                Err(err) => eprintln!("无法序列化事件: {err}"),
            }
            return;
        }
        match event {
            AgentEvent::SessionStarted {
                session_id,
                workspace,
                model,
            } => {
                println!("会话: {session_id}");
                println!("模型: {model}");
                println!("工作区: {workspace}");
                println!();
            }
            AgentEvent::Iteration { index, max } => {
                if self.verbose {
                    println!("— 模型迭代 {index}/{max}");
                }
            }
            AgentEvent::AssistantText { text } => {
                println!("{text}");
                println!();
            }
            AgentEvent::PlanUpdated { items } => {
                println!("执行计划:");
                for item in items {
                    println!("  {} {}", plan_marker(item.status), item.title);
                }
                println!();
            }
            AgentEvent::ToolStarted { tool, summary, .. } => {
                println!("→ {summary} [{tool}]");
            }
            AgentEvent::ToolFinished {
                tool,
                success,
                summary,
                ..
            } => {
                let mark = if *success { "✓" } else { "✗" };
                println!("  {mark} {tool}: {summary}");
            }
            AgentEvent::FileChanged { path, change } => {
                println!("  文件{}: {path}", file_change_label(*change));
            }
            AgentEvent::CommandOutput {
                command,
                exit_code,
                stdout_tail,
                stderr_tail,
            } => {
                let code = exit_code.map_or_else(|| "未获取".to_owned(), |code| code.to_string());
                println!("  命令: {command} (退出码 {code})");
                if !stdout_tail.trim().is_empty() {
                    println!("  stdout:");
                    for line in tail_lines(stdout_tail) {
                        println!("    {line}");
                    }
                }
                if !stderr_tail.trim().is_empty() {
                    println!("  stderr:");
                    for line in tail_lines(stderr_tail) {
                        println!("    {line}");
                    }
                }
            }
            AgentEvent::TokenUsage {
                input_tokens,
                output_tokens,
            } => {
                if self.verbose {
                    println!("  token: 输入 {input_tokens} / 输出 {output_tokens}");
                }
            }
            AgentEvent::Finished { status, summary } => {
                println!();
                println!("执行结束: {} — {summary}", status.as_str());
            }
            AgentEvent::Log { level, message } => {
                let visible = self.verbose
                    || level.eq_ignore_ascii_case("warn")
                    || level.eq_ignore_ascii_case("error");
                if visible {
                    println!("[{level}] {message}");
                }
            }
        }
    }
}

/// 计划状态标记。
fn plan_marker(status: PlanItemStatus) -> &'static str {
    match status {
        PlanItemStatus::Pending => "[ ]",
        PlanItemStatus::InProgress => "[~]",
        PlanItemStatus::Completed => "[x]",
    }
}

/// 文件变更中文标签。
fn file_change_label(change: FileChangeKind) -> &'static str {
    match change {
        FileChangeKind::Created => "新增",
        FileChangeKind::Modified => "修改",
        FileChangeKind::Deleted => "删除",
    }
}

/// 限制命令输出尾部行数与字符数，避免刷屏。
fn tail_lines(text: &str) -> Vec<String> {
    let mut lines: Vec<String> = text
        .lines()
        .rev()
        .take(20)
        .map(|line| truncate(line, 500))
        .collect();
    lines.reverse();
    lines
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
