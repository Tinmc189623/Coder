//! 交互式高风险操作审批。

use std::io::Write;

use async_trait::async_trait;
use coder_core::{ApprovalDecision, ApprovalHandler, ApprovalRequest};
use tokio::{
    io::{AsyncBufReadExt, BufReader, Stdin},
    sync::Mutex,
};
use tokio_util::sync::CancellationToken;

/// 在终端中询问用户是否允许高风险操作。
///
/// 输入 `y` 或 `yes`（不区分大小写）才批准；EOF、取消或其它输入一律拒绝。
pub struct InteractiveApproval {
    input: Mutex<BufReader<Stdin>>,
}

impl InteractiveApproval {
    /// 创建绑定标准输入的审批器。
    pub fn new() -> Self {
        Self {
            input: Mutex::new(BufReader::new(tokio::io::stdin())),
        }
    }
}

#[async_trait]
impl ApprovalHandler for InteractiveApproval {
    /// 展示工具、风险与参数摘要，等待用户确认。
    async fn request(
        &self,
        request: ApprovalRequest,
        cancel: CancellationToken,
    ) -> ApprovalDecision {
        eprintln!();
        eprintln!("需要审批: {}", request.tool);
        eprintln!("风险等级: {:?}", request.risk);
        eprintln!("操作摘要: {}", sanitize(&request.action));
        if !request.details.trim().is_empty() {
            eprintln!("参数:");
            for line in sanitize(&request.details).lines() {
                eprintln!("  {line}");
            }
        }
        eprint!("允许执行? [y/N] ");
        let _ = std::io::stderr().flush();

        let mut input = self.input.lock().await;
        let mut line = String::new();
        let read = tokio::select! {
            _ = cancel.cancelled() => return ApprovalDecision::Deny,
            result = input.read_line(&mut line) => result,
        };
        match read {
            Ok(count) if count > 0 => {
                let answer = line.trim().to_ascii_lowercase();
                if answer == "y" || answer == "yes" {
                    ApprovalDecision::Allow
                } else {
                    ApprovalDecision::Deny
                }
            }
            Ok(_) => {
                eprintln!("标准输入已关闭，按拒绝处理。");
                ApprovalDecision::Deny
            }
            Err(err) => {
                eprintln!("读取审批输入失败: {err}");
                ApprovalDecision::Deny
            }
        }
    }
}

/// 清除终端控制字符，避免工具参数注入转义序列。
fn sanitize(text: &str) -> String {
    text.chars()
        .filter(|ch| *ch == '\n' || *ch == '\t' || !ch.is_control())
        .collect()
}
