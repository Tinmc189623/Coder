//! Coder Cloud HTTP API 客户端。

use std::time::Duration;

use anyhow::{Context, Result, bail};
use coder_protocol::{
    ApiErrorBody, CreateTaskRequest, DeviceId, DeviceView, PendingPermission,
    PermissionDecisionRequest, TaskId, TaskView, UserId,
};
use reqwest::{Client, Method};
use serde::{Deserialize, Serialize, de::DeserializeOwned};

use crate::config_util::validate_cloud_url;

/// 创建用户接口的一次性响应。
#[derive(Debug, Deserialize, Serialize)]
pub struct CreatedUser {
    pub user_id: UserId,
    pub email: String,
    pub api_key: String,
}

/// 配对令牌接口的响应。
#[derive(Debug, Deserialize, Serialize)]
pub struct CreatedPairing {
    pub pairing_token: String,
    pub expires_at: String,
}

/// 权限决定接口的响应。
#[derive(Debug, Deserialize, Serialize)]
pub struct PermissionDecision {
    pub task_id: TaskId,
    pub request_id: coder_protocol::RequestId,
    pub granted: bool,
    pub reason: Option<String>,
}

/// 已认证的云端 API 客户端。
pub struct CloudApi {
    http: Client,
    base: String,
    api_key: Option<String>,
    admin_key: Option<String>,
}

impl CloudApi {
    /// 构造用户 API 客户端。
    pub fn user(server_url: &str, api_key: String) -> Result<Self> {
        Self::build(server_url, Some(api_key), None)
    }

    /// 构造管理员 API 客户端。
    pub fn admin(server_url: &str, admin_key: String) -> Result<Self> {
        Self::build(server_url, None, Some(admin_key))
    }

    /// 校验地址并创建 HTTP 客户端。
    fn build(server_url: &str, api_key: Option<String>, admin_key: Option<String>) -> Result<Self> {
        let base = validate_cloud_url(server_url)?;
        let http = Client::builder()
            .timeout(Duration::from_secs(30))
            .user_agent(concat!("coder-cli/", env!("CARGO_PKG_VERSION")))
            .build()
            .context("无法创建 HTTP 客户端")?;
        Ok(Self {
            http,
            base,
            api_key,
            admin_key,
        })
    }

    /// 列出当前用户的任务。
    pub async fn list_tasks(&self, limit: u32) -> Result<Vec<TaskView>> {
        let limit = limit.clamp(1, 200);
        self.send(Method::GET, &format!("/api/v1/tasks?limit={limit}"), None)
            .await
    }

    /// 创建任务并等待云端分配给设备。
    pub async fn create_task(
        &self,
        device_id: DeviceId,
        prompt: String,
        workspace: String,
    ) -> Result<TaskView> {
        let request = CreateTaskRequest {
            device_id,
            prompt,
            workspace,
            limits: None,
        };
        self.send(
            Method::POST,
            "/api/v1/tasks",
            Some(serde_json::to_value(request)?),
        )
        .await
    }

    /// 查询单个任务。
    pub async fn get_task(&self, task_id: TaskId) -> Result<TaskView> {
        self.send(Method::GET, &format!("/api/v1/tasks/{task_id}"), None)
            .await
    }

    /// 请求取消任务。
    pub async fn cancel_task(&self, task_id: TaskId) -> Result<TaskView> {
        self.send(
            Method::POST,
            &format!("/api/v1/tasks/{task_id}/cancel"),
            None,
        )
        .await
    }

    /// 以新的 attempt 重试任务。
    pub async fn retry_task(&self, task_id: TaskId) -> Result<TaskView> {
        self.send(
            Method::POST,
            &format!("/api/v1/tasks/{task_id}/retry"),
            None,
        )
        .await
    }

    /// 列出任务下待处理的权限请求。
    pub async fn list_permissions(&self, task_id: TaskId) -> Result<Vec<PendingPermission>> {
        self.send(
            Method::GET,
            &format!("/api/v1/tasks/{task_id}/permissions"),
            None,
        )
        .await
    }

    /// 批准或拒绝权限请求。
    pub async fn decide_permission(
        &self,
        task_id: TaskId,
        request_id: coder_protocol::RequestId,
        granted: bool,
        reason: Option<String>,
    ) -> Result<PermissionDecision> {
        let body = PermissionDecisionRequest { granted, reason };
        self.send(
            Method::POST,
            &format!("/api/v1/tasks/{task_id}/permissions/{request_id}"),
            Some(serde_json::to_value(body)?),
        )
        .await
    }

    /// 列出当前用户的设备。
    pub async fn list_devices(&self) -> Result<Vec<DeviceView>> {
        self.send(Method::GET, "/api/v1/devices", None).await
    }

    /// 创建用户并返回一次性 API key。
    pub async fn create_user(&self, email: &str) -> Result<CreatedUser> {
        self.send(
            Method::POST,
            "/api/v1/admin/users",
            Some(serde_json::json!({ "email": email })),
        )
        .await
    }

    /// 为用户签发一次性配对令牌。
    pub async fn create_pairing_token(&self, user_id: UserId) -> Result<CreatedPairing> {
        self.send(
            Method::POST,
            "/api/v1/admin/pairing-tokens",
            Some(serde_json::json!({ "user_id": user_id })),
        )
        .await
    }

    /// 发送请求并把非 2xx 响应转换为可读错误。
    async fn send<T: DeserializeOwned>(
        &self,
        method: Method,
        path: &str,
        body: Option<serde_json::Value>,
    ) -> Result<T> {
        let url = format!("{}{}", self.base, path);
        let mut request = self.http.request(method, &url);
        if let Some(key) = &self.api_key {
            request = request.bearer_auth(key);
        }
        if let Some(key) = &self.admin_key {
            request = request.header("x-admin-key", key);
        }
        if let Some(body) = body {
            request = request.json(&body);
        }
        let response = request
            .send()
            .await
            .with_context(|| format!("无法连接云端 {url}"))?;
        let status = response.status();
        if !status.is_success() {
            let text = response.text().await.unwrap_or_default();
            if let Ok(body) = serde_json::from_str::<ApiErrorBody>(&text) {
                bail!(
                    "云端返回 HTTP {} ({}): {}",
                    status.as_u16(),
                    body.error.code,
                    body.error.message
                );
            }
            bail!(
                "云端返回 HTTP {}: {}",
                status.as_u16(),
                truncate(&text, 500)
            );
        }
        response
            .json::<T>()
            .await
            .with_context(|| format!("无法解析云端响应 {url}"))
    }
}

/// 按字符数截断错误正文。
fn truncate(text: &str, max: usize) -> String {
    if text.chars().count() <= max {
        return text.to_owned();
    }
    let mut truncated: String = text.chars().take(max).collect();
    truncated.push('…');
    truncated
}
