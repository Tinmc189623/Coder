use std::{net::SocketAddr, time::Duration};

use axum::{
    Json, Router,
    extract::{
        ConnectInfo, FromRequest, FromRequestParts, Path, Query, Request, State, WebSocketUpgrade,
        ws::WebSocket,
    },
    http::{
        HeaderMap, HeaderName, StatusCode,
        header::{AUTHORIZATION, CONTENT_TYPE},
        request::Parts,
    },
    middleware::{Next, from_fn_with_state},
    response::{IntoResponse, Response},
    routing::{get, post},
};
use chrono::{DateTime, Utc};
use coder_core::model::{ChatRequest, ModelClient, ModelResponse};
use coder_protocol::{
    ApiErrorBody, CreateTaskRequest, DeviceRegisterRequest, DeviceRegisterResponse,
    DeviceTokenRequest, DeviceTokenResponse, DeviceView, ErrorCode, PendingPermission,
    PermissionDecisionRequest, TaskLimits, TaskView, UserId,
};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use tokio_util::sync::CancellationToken;
use tower_http::{
    compression::CompressionLayer,
    limit::RequestBodyLimitLayer,
    request_id::{MakeRequestUuid, PropagateRequestIdLayer, SetRequestIdLayer},
    timeout::TimeoutLayer,
    trace::TraceLayer,
};
use tracing::warn;
use uuid::Uuid;

use crate::{
    auth::{bearer_token, hash_secret, random_secret, sha256_hex, verify_secret},
    error::CloudError,
    state::AppState,
    store::{NewTask, PermissionRecord, TaskRecord, UserRecord},
};

/// 管理员请求头与请求 ID 头。
const REQUEST_ID_HEADER: &str = "x-request-id";
const MAX_PROMPT_CHARS: usize = 32_000;
const MAX_WORKSPACE_CHARS: usize = 4_096;

/// 已认证的普通用户。
pub struct UserPrincipal(pub UserRecord);

impl FromRequestParts<AppState> for UserPrincipal {
    type Rejection = CloudError;

    /// 通过 Bearer API key 识别用户。
    async fn from_request_parts(
        parts: &mut Parts,
        state: &AppState,
    ) -> Result<Self, Self::Rejection> {
        let token = bearer_token(&parts.headers)?;
        Ok(Self(state.authenticate_api_key(token).await?))
    }
}

/// 接受 JSON 请求体并把解析失败映射为统一错误。
pub struct ApiJson<T>(pub T);

impl<T, S> FromRequest<S> for ApiJson<T>
where
    Json<T>: FromRequest<S, Rejection = axum::extract::rejection::JsonRejection>,
    S: Send + Sync,
{
    type Rejection = CloudError;

    /// 在提取阶段转换错误响应。
    async fn from_request(request: Request, state: &S) -> Result<Self, Self::Rejection> {
        match Json::<T>::from_request(request, state).await {
            Ok(Json(value)) => Ok(Self(value)),
            Err(rejection) => Err(CloudError::Invalid(rejection.body_text())),
        }
    }
}

/// 创建用户请求。
#[derive(Debug, Deserialize)]
struct CreateUserRequest {
    email: String,
}

/// 创建用户响应；`api_key` 只返回一次。
#[derive(Debug, Serialize)]
struct CreateUserResponse {
    user_id: UserId,
    email: String,
    api_key: String,
    created_at: DateTime<Utc>,
}

/// 签发配对令牌请求。
#[derive(Debug, Deserialize)]
struct CreatePairingRequest {
    user_id: UserId,
}

/// 配对令牌响应。
#[derive(Debug, Serialize)]
struct CreatePairingResponse {
    pairing_token: String,
    expires_at: DateTime<Utc>,
}

/// 任务列表查询参数。
#[derive(Debug, Deserialize)]
struct ListTasksQuery {
    limit: Option<i64>,
}

/// 权限处理结果。
#[derive(Debug, Serialize)]
struct PermissionDecisionResponse {
    task_id: coder_protocol::TaskId,
    request_id: coder_protocol::RequestId,
    granted: bool,
    reason: Option<String>,
}

/// 当前用户信息。
#[derive(Debug, Serialize)]
struct MeResponse {
    user_id: UserId,
    email: String,
    created_at: DateTime<Utc>,
}

/// 组装完整路由。
pub fn router(state: AppState) -> Router {
    let body_limit = state.config.request_body_limit_bytes;
    let timeout = Duration::from_secs(state.config.request_timeout_secs);
    let api = Router::new()
        .route("/api/v1/health", get(health))
        .route("/api/v1/ready", get(ready))
        .route("/api/v1/metrics", get(metrics))
        .route("/api/v1/admin/users", post(admin_create_user))
        .route("/api/v1/admin/pairing-tokens", post(admin_create_pairing))
        .route("/api/v1/devices/register", post(register_device))
        .route("/api/v1/devices/token", post(device_token))
        .route("/api/v1/devices", get(list_devices))
        .route("/api/v1/me", get(me))
        .route("/api/v1/tasks", get(list_tasks).post(create_task))
        .route("/api/v1/tasks/{task_id}", get(get_task))
        .route("/api/v1/tasks/{task_id}/cancel", post(cancel_task))
        .route("/api/v1/tasks/{task_id}/retry", post(retry_task))
        .route("/api/v1/tasks/{task_id}/permissions", get(list_permissions))
        .route(
            "/api/v1/tasks/{task_id}/permissions/{request_id}",
            post(decide_permission),
        )
        .route("/api/v1/model/chat", post(model_chat))
        .layer(RequestBodyLimitLayer::new(body_limit))
        .layer(TimeoutLayer::with_status_code(
            StatusCode::REQUEST_TIMEOUT,
            timeout,
        ))
        .layer(CompressionLayer::new())
        .layer(from_fn_with_state(state.clone(), rate_limit))
        .layer(PropagateRequestIdLayer::new(HeaderName::from_static(
            REQUEST_ID_HEADER,
        )))
        .layer(SetRequestIdLayer::new(
            HeaderName::from_static(REQUEST_ID_HEADER),
            MakeRequestUuid,
        ))
        .layer(TraceLayer::new_for_http());

    Router::new()
        .route("/api/v1/agent/ws", get(agent_ws))
        .merge(api)
        .fallback(not_found)
        .with_state(state)
}

/// 健康检查。
async fn health(State(state): State<AppState>) -> Json<Value> {
    let uptime = Utc::now()
        .signed_duration_since(state.started_at)
        .num_seconds()
        .max(0);
    Json(json!({
        "status": "ok",
        "version": env!("CARGO_PKG_VERSION"),
        "uptime_secs": uptime,
    }))
}

/// 就绪检查，验证存储连接。
async fn ready(State(state): State<AppState>) -> Result<Json<Value>, CloudError> {
    if state.ready().await {
        Ok(Json(json!({"status": "ready"})))
    } else {
        Err(CloudError::Unavailable("database is not reachable".into()))
    }
}

/// Prometheus 指标导出。
async fn metrics(State(state): State<AppState>) -> Result<Response, CloudError> {
    let Some(handle) = &state.metrics else {
        return Err(CloudError::Unavailable(
            "metrics exporter is not enabled".into(),
        ));
    };
    Ok((
        [(CONTENT_TYPE, "text/plain; version=0.0.4; charset=utf-8")],
        handle.render(),
    )
        .into_response())
}

/// 未知路由统一返回 JSON。
async fn not_found() -> impl IntoResponse {
    (
        StatusCode::NOT_FOUND,
        Json(ApiErrorBody::new(
            ErrorCode::TaskNotFound,
            "route not found",
            None,
        )),
    )
}

/// 管理员创建用户并返回一次性 API key。
async fn admin_create_user(
    State(state): State<AppState>,
    headers: HeaderMap,
    ApiJson(request): ApiJson<CreateUserRequest>,
) -> Result<(StatusCode, Json<CreateUserResponse>), CloudError> {
    state.authenticate_admin(&headers)?;
    let email = request.email.trim().to_owned();
    if email.len() < 3
        || email.len() > 254
        || !email.contains('@')
        || email.chars().any(char::is_control)
    {
        return Err(CloudError::Invalid("invalid email address".into()));
    }
    let api_key = format!("ck_{}", random_secret(32));
    let hash_input = api_key.clone();
    let api_key_hash = tokio::task::spawn_blocking(move || hash_secret(&hash_input))
        .await
        .map_err(|err| CloudError::Internal(format!("hash worker failed: {err}")))??;
    let api_key_lookup = sha256_hex(&api_key);
    let user = state
        .store
        .create_user(&email, &api_key_hash, &api_key_lookup)
        .await?;
    Ok((
        StatusCode::CREATED,
        Json(CreateUserResponse {
            user_id: user.id,
            email: user.email,
            api_key,
            created_at: user.created_at,
        }),
    ))
}

/// 管理员签发一次性配对令牌。
async fn admin_create_pairing(
    State(state): State<AppState>,
    headers: HeaderMap,
    ApiJson(request): ApiJson<CreatePairingRequest>,
) -> Result<(StatusCode, Json<CreatePairingResponse>), CloudError> {
    state.authenticate_admin(&headers)?;
    state
        .store
        .get_user(request.user_id)
        .await?
        .ok_or_else(|| CloudError::NotFound("user".into()))?;
    let token = random_secret(32);
    let token_hash = sha256_hex(&token);
    let expires_at = Utc::now() + chrono::Duration::seconds(state.config.pairing_ttl_secs as i64);
    state
        .store
        .create_pairing_token(request.user_id, &token_hash, expires_at)
        .await?;
    Ok((
        StatusCode::CREATED,
        Json(CreatePairingResponse {
            pairing_token: token,
            expires_at,
        }),
    ))
}

/// 使用配对令牌注册设备。
async fn register_device(
    State(state): State<AppState>,
    ApiJson(request): ApiJson<DeviceRegisterRequest>,
) -> Result<(StatusCode, Json<DeviceRegisterResponse>), CloudError> {
    let pairing_token = request.pairing_token.trim();
    if pairing_token.len() < 16 || pairing_token.len() > 512 {
        return Err(CloudError::Unauthorized(
            "pairing token is invalid or expired".into(),
        ));
    }
    let name = request.name.trim();
    if name.is_empty() || name.len() > 64 || name.chars().any(char::is_control) {
        return Err(CloudError::Invalid("invalid device name".into()));
    }
    if request.platform.len() > 32 || request.agent_version.len() > 32 {
        return Err(CloudError::Invalid(
            "platform or agent_version is too long".into(),
        ));
    }
    let token_hash = sha256_hex(pairing_token);
    let user_id = state
        .store
        .consume_pairing_token(&token_hash, Utc::now())
        .await?
        .ok_or_else(|| CloudError::Unauthorized("pairing token is invalid or expired".into()))?;
    let device_secret = format!("ck_dev_{}", random_secret(32));
    let hash_input = device_secret.clone();
    let secret_hash = tokio::task::spawn_blocking(move || hash_secret(&hash_input))
        .await
        .map_err(|err| CloudError::Internal(format!("hash worker failed: {err}")))??;
    let device = state
        .store
        .create_device(
            user_id,
            name,
            &request.platform,
            &request.agent_version,
            &secret_hash,
        )
        .await?;
    metrics::counter!("coder_cloud_devices_registered_total").increment(1);
    Ok((
        StatusCode::CREATED,
        Json(DeviceRegisterResponse {
            device_id: device.id,
            device_secret,
            user_id,
        }),
    ))
}

/// 使用设备密钥换取短期访问令牌。
async fn device_token(
    State(state): State<AppState>,
    ApiJson(request): ApiJson<DeviceTokenRequest>,
) -> Result<Json<DeviceTokenResponse>, CloudError> {
    if request.device_secret.len() > 512 {
        return Err(CloudError::Unauthorized(
            "invalid device credentials".into(),
        ));
    }
    let device = state
        .store
        .get_device(request.device_id)
        .await?
        .ok_or_else(|| CloudError::Unauthorized("invalid device credentials".into()))?;
    if device.revoked {
        return Err(CloudError::Unauthorized("device has been revoked".into()));
    }
    let secret_hash = device.secret_hash.clone();
    let candidate = request.device_secret;
    let valid = tokio::task::spawn_blocking(move || verify_secret(&secret_hash, &candidate))
        .await
        .map_err(|err| CloudError::Internal(format!("hash worker failed: {err}")))?;
    if !valid {
        return Err(CloudError::Unauthorized(
            "invalid device credentials".into(),
        ));
    }
    let now = Utc::now();
    if let Err(err) = state.store.touch_device(device.id, now).await {
        warn!(error = %err, "failed to update device last_seen_at");
    }
    let (access_token, expires_in_secs) =
        state.auth.issue_device_token(device.id, device.user_id)?;
    Ok(Json(DeviceTokenResponse {
        access_token,
        token_type: "Bearer".into(),
        expires_in_secs,
    }))
}

/// 设备 WebSocket 升级入口。
async fn agent_ws(
    State(state): State<AppState>,
    headers: HeaderMap,
    ws: WebSocketUpgrade,
) -> Result<Response, CloudError> {
    let principal = state.authenticate_device(&headers).await?;
    let hub = state.hub.clone();
    let max_message = state.config.max_ws_message_bytes;
    Ok(ws
        .max_message_size(max_message)
        .max_frame_size(max_message)
        .on_upgrade(move |socket: WebSocket| async move {
            hub.run_agent(principal, socket).await;
        }))
}

/// 当前用户信息。
async fn me(
    State(state): State<AppState>,
    principal: UserPrincipal,
) -> Result<Json<MeResponse>, CloudError> {
    let user = state
        .store
        .get_user(principal.0.id)
        .await?
        .ok_or_else(|| CloudError::Unauthorized("user no longer exists".into()))?;
    Ok(Json(MeResponse {
        user_id: user.id,
        email: user.email,
        created_at: user.created_at,
    }))
}

/// 创建设备任务。
async fn create_task(
    State(state): State<AppState>,
    principal: UserPrincipal,
    ApiJson(request): ApiJson<CreateTaskRequest>,
) -> Result<(StatusCode, Json<TaskView>), CloudError> {
    let prompt = request.prompt.trim();
    if prompt.is_empty() || prompt.chars().count() > MAX_PROMPT_CHARS {
        return Err(CloudError::Invalid(format!(
            "prompt must contain between 1 and {MAX_PROMPT_CHARS} characters"
        )));
    }
    let workspace = request.workspace.trim();
    if workspace.is_empty()
        || workspace.len() > MAX_WORKSPACE_CHARS
        || workspace.contains('\0')
        || workspace.chars().any(char::is_control)
    {
        return Err(CloudError::Invalid("invalid workspace path".into()));
    }
    let device = state
        .store
        .get_device(request.device_id)
        .await?
        .ok_or_else(|| CloudError::NotFound("device".into()))?;
    if device.user_id != principal.0.id {
        return Err(CloudError::Forbidden(
            "device does not belong to the current user".into(),
        ));
    }
    let record = state
        .store
        .create_task(NewTask {
            device_id: device.id,
            user_id: principal.0.id,
            prompt: prompt.to_owned(),
            workspace: workspace.to_owned(),
            limits: clamp_limits(request.limits),
        })
        .await?;
    state.hub.dispatch_task(&record).await;
    metrics::counter!("coder_cloud_tasks_created_total").increment(1);
    Ok((StatusCode::ACCEPTED, Json(record.to_view())))
}

/// 列出当前用户的任务。
async fn list_tasks(
    State(state): State<AppState>,
    principal: UserPrincipal,
    Query(query): Query<ListTasksQuery>,
) -> Result<Json<Vec<TaskView>>, CloudError> {
    let limit = query.limit.unwrap_or(50).clamp(1, 200);
    let tasks = state.store.list_user_tasks(principal.0.id, limit).await?;
    Ok(Json(tasks.iter().map(TaskRecord::to_view).collect()))
}

/// 查询单个任务。
async fn get_task(
    State(state): State<AppState>,
    principal: UserPrincipal,
    Path(task_id): Path<Uuid>,
) -> Result<Json<TaskView>, CloudError> {
    let task = owned_task(&state, principal.0.id, task_id.into()).await?;
    Ok(Json(task.to_view()))
}

/// 请求取消任务。
async fn cancel_task(
    State(state): State<AppState>,
    principal: UserPrincipal,
    Path(task_id): Path<Uuid>,
) -> Result<Json<TaskView>, CloudError> {
    let task_id = coder_protocol::TaskId::from(task_id);
    owned_task(&state, principal.0.id, task_id).await?;
    let task = state.store.request_cancel(task_id).await?;
    if !task.status.is_terminal() {
        state.hub.cancel_task(task.device_id, task.id, task.attempt);
    }
    Ok(Json(task.to_view()))
}

/// 以新 attempt 重试终态任务。
async fn retry_task(
    State(state): State<AppState>,
    principal: UserPrincipal,
    Path(task_id): Path<Uuid>,
) -> Result<(StatusCode, Json<TaskView>), CloudError> {
    let task_id = coder_protocol::TaskId::from(task_id);
    owned_task(&state, principal.0.id, task_id).await?;
    let task = state.store.retry_task(task_id).await?;
    state.hub.dispatch_task(&task).await;
    Ok((StatusCode::ACCEPTED, Json(task.to_view())))
}

/// 列出任务下待处理的权限请求。
async fn list_permissions(
    State(state): State<AppState>,
    principal: UserPrincipal,
    Path(task_id): Path<Uuid>,
) -> Result<Json<Vec<PendingPermission>>, CloudError> {
    let task_id = coder_protocol::TaskId::from(task_id);
    owned_task(&state, principal.0.id, task_id).await?;
    let permissions = state.store.list_pending_permissions(task_id).await?;
    Ok(Json(
        permissions
            .iter()
            .map(PermissionRecord::to_pending_view)
            .collect(),
    ))
}

/// 批准或拒绝权限请求。
async fn decide_permission(
    State(state): State<AppState>,
    principal: UserPrincipal,
    Path((task_id, request_id)): Path<(Uuid, Uuid)>,
    ApiJson(request): ApiJson<PermissionDecisionRequest>,
) -> Result<Json<PermissionDecisionResponse>, CloudError> {
    let task_id = coder_protocol::TaskId::from(task_id);
    let request_id = coder_protocol::RequestId::from(request_id);
    let task = owned_task(&state, principal.0.id, task_id).await?;
    let permission = state
        .store
        .get_permission(request_id)
        .await?
        .ok_or_else(|| CloudError::NotFound("permission request".into()))?;
    if permission.task_id != task.id || permission.user_id != principal.0.id {
        return Err(CloudError::NotFound("permission request".into()));
    }
    let reason = request
        .reason
        .as_deref()
        .map(|value| value.chars().take(1_024).collect::<String>());
    if permission.granted.is_none() {
        let updated = state
            .store
            .resolve_permission(request_id, request.granted, reason.as_deref())
            .await?
            .ok_or_else(|| CloudError::NotFound("permission request".into()))?;
        state
            .hub
            .send_permission_decision(
                task.id,
                request_id,
                updated.granted.unwrap_or(false),
                updated.reason.clone(),
            )
            .await?;
    }
    let current = state
        .store
        .get_permission(request_id)
        .await?
        .ok_or_else(|| CloudError::NotFound("permission request".into()))?;
    Ok(Json(PermissionDecisionResponse {
        task_id,
        request_id,
        granted: current.granted.unwrap_or(false),
        reason: current.reason,
    }))
}

/// 列出当前用户的设备及在线状态。
async fn list_devices(
    State(state): State<AppState>,
    principal: UserPrincipal,
) -> Result<Json<Vec<DeviceView>>, CloudError> {
    let devices = state.store.list_devices(principal.0.id).await?;
    Ok(Json(
        devices
            .iter()
            .map(|device| device.to_view(state.hub.is_online(device.id)))
            .collect(),
    ))
}

/// 云端模型代理，仅接受已认证设备的结构化对话请求。
async fn model_chat(
    State(state): State<AppState>,
    headers: HeaderMap,
    ApiJson(mut request): ApiJson<ChatRequest>,
) -> Result<Json<ModelResponse>, CloudError> {
    state.authenticate_device(&headers).await?;
    let Some(model) = &state.model else {
        return Err(CloudError::Unavailable(
            "cloud model proxy is not configured".into(),
        ));
    };
    validate_model_request(&mut request, state.config.model.max_output_tokens)?;
    metrics::counter!("coder_cloud_model_requests_total").increment(1);
    let response = model
        .complete(request, CancellationToken::new(), None)
        .await?;
    Ok(Json(response))
}

/// 校验并收敛模型请求的规模。
fn validate_model_request(
    request: &mut ChatRequest,
    max_output_tokens: u32,
) -> Result<(), CloudError> {
    if request.messages.is_empty() || request.messages.len() > 256 {
        return Err(CloudError::Invalid(
            "messages must contain between 1 and 256 entries".into(),
        ));
    }
    let mut characters = 0usize;
    for message in &request.messages {
        characters += message.content.as_deref().map(str::len).unwrap_or(0);
        for call in &message.tool_calls {
            characters += call.name.len() + call.arguments.len() + call.id.len();
        }
    }
    if characters > 1_000_000 {
        return Err(CloudError::Invalid(
            "model request exceeds the 1,000,000 character budget".into(),
        ));
    }
    if request.tools.len() > 128 {
        return Err(CloudError::Invalid(
            "at most 128 tools may be declared".into(),
        ));
    }
    if let Some(tool) = request.tools.iter().find(|tool| {
        serde_json::to_string(&tool.parameters)
            .map(|s| s.len() > 65_536)
            .unwrap_or(true)
    }) {
        return Err(CloudError::Invalid(format!(
            "tool schema `{}` is too large",
            tool.name
        )));
    }
    if !request.temperature.is_finite() || !(0.0..=2.0).contains(&request.temperature) {
        request.temperature = 0.2;
    }
    request.max_output_tokens = request.max_output_tokens.clamp(1, max_output_tokens.max(1));
    request.stream = false;
    Ok(())
}

/// 读取并校验任务归属。
async fn owned_task(
    state: &AppState,
    user_id: UserId,
    task_id: coder_protocol::TaskId,
) -> Result<TaskRecord, CloudError> {
    let task = state
        .store
        .get_task(task_id)
        .await?
        .ok_or_else(|| CloudError::NotFound(format!("task {task_id}")))?;
    if task.user_id != user_id {
        return Err(CloudError::NotFound(format!("task {task_id}")));
    }
    Ok(task)
}

/// 收敛客户端提交的资源限制。
fn clamp_limits(limits: Option<TaskLimits>) -> TaskLimits {
    let limits = limits.unwrap_or_default();
    TaskLimits {
        max_iterations: limits.max_iterations.clamp(1, 200),
        max_duration_secs: limits.max_duration_secs.clamp(1, 7_200),
        max_tool_calls: limits.max_tool_calls.clamp(1, 1_000),
        token_budget: limits.token_budget.clamp(1, 2_000_000),
    }
}

/// 按调用方限速；优先使用 API key，其次使用可信代理来源或真实连接地址。
async fn rate_limit(State(state): State<AppState>, request: Request, next: Next) -> Response {
    let key = request
        .headers()
        .get(AUTHORIZATION)
        .and_then(|value| value.to_str().ok())
        .map(sha256_hex)
        .or_else(|| {
            state
                .config
                .trust_proxy_headers
                .then(|| forwarded_client_ip(request.headers()))
                .flatten()
                .map(|ip| format!("addr:{ip}"))
        })
        .or_else(|| {
            request
                .extensions()
                .get::<ConnectInfo<SocketAddr>>()
                .map(|info| format!("addr:{}", info.0.ip()))
        })
        .unwrap_or_else(|| "anonymous".to_owned());
    if !state.limiter.allow(&key) {
        return CloudError::RateLimited.into_response();
    }
    next.run(request).await
}

/// 解析反向代理写入的客户端地址；无效值回退到连接地址。
fn forwarded_client_ip(headers: &HeaderMap) -> Option<std::net::IpAddr> {
    headers
        .get("x-forwarded-for")?
        .to_str()
        .ok()?
        .split(',')
        .next()?
        .trim()
        .parse()
        .ok()
}

#[cfg(test)]
mod tests {
    use super::*;
    use axum::http::{HeaderValue, header::HeaderName};

    /// 取 X-Forwarded-For 的第一个合法地址，忽略代理追加的后续地址。
    #[test]
    fn forwarded_ip_uses_first_entry() {
        let mut headers = HeaderMap::new();
        headers.insert(
            HeaderName::from_static("x-forwarded-for"),
            HeaderValue::from_static("203.0.113.7, 10.0.0.2"),
        );
        assert_eq!(
            forwarded_client_ip(&headers).map(|ip| ip.to_string()),
            Some("203.0.113.7".into())
        );
    }

    /// 非 IP 或空来源不得生成新的限流桶。
    #[test]
    fn forwarded_ip_rejects_invalid_values() {
        let mut headers = HeaderMap::new();
        headers.insert(
            HeaderName::from_static("x-forwarded-for"),
            HeaderValue::from_static("not-an-ip"),
        );
        assert!(forwarded_client_ip(&headers).is_none());
        headers.remove("x-forwarded-for");
        assert!(forwarded_client_ip(&headers).is_none());
    }
}
