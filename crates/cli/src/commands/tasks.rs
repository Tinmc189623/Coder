//! `coder tasks`：通过云端 API 创建、查看与管理任务。

use anyhow::{Result, bail};
use coder_protocol::{DeviceId, TaskId, TaskView};

use crate::{CloudArgs, TasksCommand, cloud_api::CloudApi, config_util::require_api_key};

/// 分发任务子命令。
pub async fn execute(command: TasksCommand) -> Result<u8> {
    match command {
        TasksCommand::List { cloud, limit } => list(cloud, limit).await,
        TasksCommand::Create {
            cloud,
            device_id,
            prompt,
            workspace,
        } => create(cloud, device_id, prompt, workspace).await,
        TasksCommand::Show { cloud, task_id } => show(cloud, task_id).await,
        TasksCommand::Cancel { cloud, task_id } => cancel(cloud, task_id).await,
        TasksCommand::Retry { cloud, task_id } => retry(cloud, task_id).await,
        TasksCommand::Permissions { cloud, task_id } => permissions(cloud, task_id).await,
        TasksCommand::Decide {
            cloud,
            task_id,
            request_id,
            grant,
            deny: _,
            reason,
        } => decide(cloud, task_id, request_id, grant, reason).await,
    }
}

/// 列出任务。
async fn list(cloud: CloudArgs, limit: u32) -> Result<u8> {
    let api = api(&cloud)?;
    let tasks = api.list_tasks(limit).await?;
    if cloud.json {
        println!("{}", serde_json::to_string_pretty(&tasks)?);
        return Ok(0);
    }
    if tasks.is_empty() {
        println!("没有任务。");
        return Ok(0);
    }
    println!(
        "{:<36} {:<10} {:>3}  {:<36} PROMPT",
        "TASK", "STATUS", "TRY", "DEVICE"
    );
    for task in tasks {
        println!(
            "{:<36} {:<10} {:>3}  {:<36} {}",
            task.task_id,
            task.status.as_str(),
            task.attempt,
            task.device_id,
            truncate(&task.prompt.replace('\n', " "), 60)
        );
    }
    Ok(0)
}

/// 创建并下发任务。
async fn create(
    cloud: CloudArgs,
    device_id: String,
    prompt: String,
    workspace: String,
) -> Result<u8> {
    let device_id: DeviceId = device_id
        .parse()
        .map_err(|err| anyhow::anyhow!("无效的设备 ID: {err}"))?;
    if prompt.trim().is_empty() {
        bail!("--prompt 不能为空");
    }
    if !std::path::Path::new(&workspace).is_absolute() {
        bail!("--workspace 必须是 Agent 所在机器上的绝对路径");
    }
    let api = api(&cloud)?;
    let task = api.create_task(device_id, prompt, workspace).await?;
    if cloud.json {
        println!("{}", serde_json::to_string_pretty(&task)?);
    } else {
        println!("任务已创建: {}", task.task_id);
        print_task(&task);
    }
    Ok(0)
}

/// 查看任务详情。
async fn show(cloud: CloudArgs, task_id: String) -> Result<u8> {
    let task_id = parse_task_id(&task_id)?;
    let api = api(&cloud)?;
    let task = api.get_task(task_id).await?;
    if cloud.json {
        println!("{}", serde_json::to_string_pretty(&task)?);
    } else {
        print_task(&task);
    }
    Ok(0)
}

/// 请求取消任务。
async fn cancel(cloud: CloudArgs, task_id: String) -> Result<u8> {
    let task_id = parse_task_id(&task_id)?;
    let api = api(&cloud)?;
    let task = api.cancel_task(task_id).await?;
    if cloud.json {
        println!("{}", serde_json::to_string_pretty(&task)?);
    } else {
        println!("取消请求已提交，当前状态: {}", task.status.as_str());
    }
    Ok(0)
}

/// 重试终态任务。
async fn retry(cloud: CloudArgs, task_id: String) -> Result<u8> {
    let task_id = parse_task_id(&task_id)?;
    let api = api(&cloud)?;
    let task = api.retry_task(task_id).await?;
    if cloud.json {
        println!("{}", serde_json::to_string_pretty(&task)?);
    } else {
        println!(
            "任务已重试: {} (attempt {}, 状态 {})",
            task.task_id,
            task.attempt,
            task.status.as_str()
        );
    }
    Ok(0)
}

/// 列出待处理权限请求。
async fn permissions(cloud: CloudArgs, task_id: String) -> Result<u8> {
    let task_id = parse_task_id(&task_id)?;
    let api = api(&cloud)?;
    let pending = api.list_permissions(task_id).await?;
    if cloud.json {
        println!("{}", serde_json::to_string_pretty(&pending)?);
        return Ok(0);
    }
    if pending.is_empty() {
        println!("没有待处理的权限请求。");
        return Ok(0);
    }
    println!(
        "{:<36} {:<16} {:<8} {:<25} ACTION",
        "REQUEST", "TOOL", "RISK", "CREATED"
    );
    for permission in pending {
        println!(
            "{:<36} {:<16} {:<8} {:<25} {}",
            permission.request_id,
            truncate(&permission.tool, 16),
            permission.risk.as_str(),
            permission.created_at.to_rfc3339(),
            truncate(&permission.action.replace('\n', " "), 80)
        );
    }
    Ok(0)
}

/// 批准或拒绝权限请求。
async fn decide(
    cloud: CloudArgs,
    task_id: String,
    request_id: String,
    grant: bool,
    reason: Option<String>,
) -> Result<u8> {
    let task_id = parse_task_id(&task_id)?;
    let request_id: coder_protocol::RequestId = request_id
        .parse()
        .map_err(|err| anyhow::anyhow!("无效的权限请求 ID: {err}"))?;
    let api = api(&cloud)?;
    let decision = api
        .decide_permission(task_id, request_id, grant, reason)
        .await?;
    if cloud.json {
        println!("{}", serde_json::to_string_pretty(&decision)?);
    } else {
        println!(
            "权限请求 {} 已{}",
            decision.request_id,
            if decision.granted { "批准" } else { "拒绝" }
        );
        if let Some(reason) = decision.reason {
            println!("原因: {reason}");
        }
    }
    Ok(0)
}

/// 构造用户 API 客户端。
fn api(cloud: &CloudArgs) -> Result<CloudApi> {
    let server_url = cloud
        .server_url
        .as_deref()
        .ok_or_else(|| anyhow::anyhow!("缺少云端地址；使用 --server-url 或 CODER_CLOUD_URL"))?;
    CloudApi::user(server_url, require_api_key(cloud.api_key.clone())?)
}

/// 解析任务 ID。
fn parse_task_id(value: &str) -> Result<TaskId> {
    value
        .parse()
        .map_err(|err| anyhow::anyhow!("无效的任务 ID `{value}`: {err}"))
}

/// 打印任务详情。
fn print_task(task: &TaskView) {
    println!("任务: {}", task.task_id);
    println!("状态: {}", task.status.as_str());
    println!("尝试: {}", task.attempt);
    println!("设备: {}", task.device_id);
    println!("工作区: {}", task.workspace);
    println!("创建: {}", task.created_at.to_rfc3339());
    println!("更新: {}", task.updated_at.to_rfc3339());
    if let Some(lease) = task.lease_expires_at {
        println!("租约到期: {}", lease.to_rfc3339());
    }
    println!("提示词: {}", task.prompt);
    if let Some(summary) = &task.summary {
        println!("结果: {summary}");
    }
    if let Some(error) = &task.error {
        println!("错误: {error}");
    }
    if !task.files_changed.is_empty() {
        println!("变更文件: {}", task.files_changed.join(", "));
    }
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
