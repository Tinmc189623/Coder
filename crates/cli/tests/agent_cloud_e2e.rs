//! 真实本地 Agent 连接进程内云端服务的端到端测试。
//!
//! 覆盖设备注册、令牌交换、WebSocket 下发、真实文件写入、状态回传与终态落账，
//! 模型由本地 OpenAI 兼容 SSE 服务提供，不访问外部网络。

use std::{
    net::SocketAddr,
    path::Path,
    sync::{
        Arc,
        atomic::{AtomicUsize, Ordering},
    },
    time::{Duration, Instant},
};

use axum::{
    Json, Router, extract::State, http::header::CONTENT_TYPE, response::IntoResponse, routing::post,
};
use coder_cloud::{
    auth::AuthService, config::CloudConfig, hub::Hub, routes, state::AppState, store::MemoryStore,
};
use coder_core::{
    RunnerConfig, Secret,
    config::{ModelConfig, StorageConfig},
};
use coder_local_agent::{
    client::AgentClient,
    config::{LocalAgentConfig, ModelMode, ReconnectConfig},
    credentials::DeviceCredentials,
};
use coder_protocol::{TaskStatus, TaskView};
use serde_json::{Value, json};
use tokio::net::TcpListener;
use tokio_util::sync::CancellationToken;

const ADMIN_KEY: &str = "test-admin-key-0123456789";

/// 模拟模型服务的共享状态。
#[derive(Clone)]
struct ModelState {
    calls: Arc<AtomicUsize>,
}

/// 已注册设备的凭据与用户 API key。
struct ProvisionedDevice {
    user_id: String,
    device_id: String,
    device_secret: String,
    api_key: String,
}

/// 启动内存存储的云端服务，返回监听地址。
async fn start_cloud() -> SocketAddr {
    let config = CloudConfig {
        jwt_secret: Secret("j".repeat(32)),
        admin_api_key: Secret(ADMIN_KEY.into()),
        rate_limit_per_minute: 10_000,
        ..CloudConfig::default()
    };
    config.validate().expect("cloud config");
    let config = Arc::new(config);
    let store = Arc::new(MemoryStore::new());
    let auth = Arc::new(AuthService::new(
        config.jwt_secret.expose(),
        config.token_ttl_secs,
    ));
    let hub = Arc::new(Hub::new(store.clone(), config.clone()));
    let state = AppState::new(config, store, auth, hub, None, None);
    let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind cloud");
    let addr = listener.local_addr().expect("cloud address");
    tokio::spawn(async move {
        axum::serve(
            listener,
            routes::router(state).into_make_service_with_connect_info::<SocketAddr>(),
        )
        .await
        .expect("cloud server");
    });
    addr
}

/// 启动本地 OpenAI 兼容 SSE 服务，首次调用写入文件，之后返回最终文本。
async fn start_model_server() -> (String, ModelState) {
    let state = ModelState {
        calls: Arc::new(AtomicUsize::new(0)),
    };
    let app = Router::new()
        .route("/v1/chat/completions", post(completions))
        .with_state(state.clone());
    let listener = TcpListener::bind("127.0.0.1:0")
        .await
        .expect("bind model server");
    let addr = listener.local_addr().expect("model address");
    tokio::spawn(async move {
        axum::serve(listener, app).await.expect("model server");
    });
    (format!("http://{addr}/v1"), state)
}

/// 按调用顺序返回工具调用与最终回答。
async fn completions(
    State(state): State<ModelState>,
    Json(_body): Json<Value>,
) -> impl IntoResponse {
    let index = state.calls.fetch_add(1, Ordering::SeqCst);
    let payload = match index {
        0 => tool_call_event(
            "call_write",
            "write_file",
            json!({"path": "agent-cloud.txt", "content": "written by local agent\n"}),
        ),
        _ => text_event("任务完成：已通过云端下发并由本地 Agent 写入文件。"),
    };
    ([(CONTENT_TYPE, "text/event-stream")], payload)
}

/// 构造包含单个工具调用的 SSE 响应。
fn tool_call_event(id: &str, name: &str, arguments: Value) -> String {
    let arguments = serde_json::to_string(&arguments).expect("encode arguments");
    let event = json!({
        "choices": [{
            "delta": {
                "tool_calls": [{
                    "index": 0,
                    "id": id,
                    "type": "function",
                    "function": {"name": name, "arguments": arguments}
                }]
            },
            "finish_reason": "tool_calls"
        }]
    });
    format!("data: {event}\n\ndata: [DONE]\n\n")
}

/// 构造最终文本 SSE 响应。
fn text_event(text: &str) -> String {
    let event = json!({
        "choices": [{
            "delta": {"content": text},
            "finish_reason": "stop"
        }],
        "usage": {"prompt_tokens": 64, "completion_tokens": 12}
    });
    format!("data: {event}\n\ndata: [DONE]\n\n")
}

/// 通过管理员接口创建用户、签发配对令牌并注册设备。
async fn provision(http: &reqwest::Client, addr: SocketAddr) -> ProvisionedDevice {
    let base = format!("http://{addr}");
    let (status, user) = post_json(
        http,
        &format!("{base}/api/v1/admin/users"),
        Some(("x-admin-key", ADMIN_KEY)),
        json!({"email": format!("agent-e2e-{}@example.com", uuid::Uuid::new_v4())}),
    )
    .await;
    assert_eq!(status, reqwest::StatusCode::CREATED, "{user}");
    let user_id = string_field(&user, "user_id");
    let api_key = string_field(&user, "api_key");

    let (status, pairing) = post_json(
        http,
        &format!("{base}/api/v1/admin/pairing-tokens"),
        Some(("x-admin-key", ADMIN_KEY)),
        json!({"user_id": user_id}),
    )
    .await;
    assert_eq!(status, reqwest::StatusCode::CREATED, "{pairing}");
    let pairing_token = string_field(&pairing, "pairing_token");

    let (status, body) = post_json(
        http,
        &format!("{base}/api/v1/devices/register"),
        None,
        json!({
            "pairing_token": pairing_token,
            "name": "agent-e2e",
            "platform": std::env::consts::OS,
            "agent_version": env!("CARGO_PKG_VERSION"),
        }),
    )
    .await;
    assert_eq!(status, reqwest::StatusCode::CREATED, "{body}");

    ProvisionedDevice {
        user_id,
        device_id: string_field(&body, "device_id"),
        device_secret: string_field(&body, "device_secret"),
        api_key,
    }
}

/// 发送 JSON POST 并解析响应。
async fn post_json(
    http: &reqwest::Client,
    url: &str,
    header: Option<(&str, &str)>,
    body: Value,
) -> (reqwest::StatusCode, Value) {
    let mut request = http.post(url).json(&body);
    if let Some((name, value)) = header {
        request = request.header(name, value);
    }
    let response = request.send().await.expect("POST request");
    let status = response.status();
    let text = response.text().await.unwrap_or_default();
    (
        status,
        serde_json::from_str(&text).unwrap_or_else(|_| json!({"raw": text})),
    )
}

/// 读取 JSON 字符串字段。
fn string_field(body: &Value, name: &str) -> String {
    body.get(name)
        .and_then(Value::as_str)
        .unwrap_or_else(|| panic!("missing string field `{name}` in {body}"))
        .to_owned()
}

/// 构造指向测试云端的本地 Agent 配置。
fn agent_config(
    addr: SocketAddr,
    workspace: &Path,
    data: &Path,
    model_url: &str,
) -> LocalAgentConfig {
    let runner = RunnerConfig {
        model: ModelConfig {
            base_url: model_url.to_owned(),
            api_key: Secret("test-model-key".into()),
            model: "mock-model".into(),
            allow_insecure_http: true,
            max_retries: 0,
            ..ModelConfig::default()
        },
        storage: StorageConfig {
            sqlite_path: data.join("sessions.db"),
        },
        ..RunnerConfig::default()
    };
    LocalAgentConfig {
        server_url: format!("http://{addr}"),
        device_name: "agent-e2e".into(),
        credentials_path: data.join("device.json"),
        pairing_token: Secret::default(),
        workspace_roots: vec![workspace.to_path_buf()],
        storage_path: data.join("agent.db"),
        model_mode: ModelMode::Direct,
        reconnect: ReconnectConfig::default(),
        heartbeat_secs: 5,
        approval_timeout_secs: 30,
        max_parallel_tasks: 1,
        allow_insecure_ws: true,
        runner,
    }
}

/// 轮询设备列表直到目标设备上线。
async fn wait_until_online(
    http: &reqwest::Client,
    addr: SocketAddr,
    device_id: &str,
    api_key: &str,
) {
    let deadline = Instant::now() + Duration::from_secs(10);
    loop {
        let response = http
            .get(format!("http://{addr}/api/v1/devices"))
            .header("authorization", format!("Bearer {api_key}"))
            .send()
            .await
            .expect("device list");
        let devices: Value = response.json().await.expect("device list json");
        let online = devices.as_array().into_iter().flatten().any(|view| {
            view.get("device_id").and_then(Value::as_str) == Some(device_id)
                && view.get("online").and_then(Value::as_bool) == Some(true)
        });
        if online {
            return;
        }
        assert!(Instant::now() < deadline, "agent did not connect to cloud");
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn real_agent_registers_executes_cloud_task_and_reports_success() {
    let addr = start_cloud().await;
    let (model_url, model_state) = start_model_server().await;
    let http = reqwest::Client::new();
    let device = provision(&http, addr).await;

    let workspace = tempfile::tempdir().expect("workspace");
    let data = tempfile::tempdir().expect("agent data");
    let config = agent_config(addr, workspace.path(), data.path(), &model_url);
    let credentials = DeviceCredentials {
        server_url: config.server_url.clone(),
        device_id: device.device_id.parse().expect("device id"),
        user_id: device.user_id.parse().expect("user id"),
        device_secret: device.device_secret.clone(),
    };
    let agent = AgentClient::new(config, credentials)
        .await
        .expect("create agent client");
    let shutdown = CancellationToken::new();
    let agent_shutdown = shutdown.clone();
    let agent_task = tokio::spawn(async move { agent.run(agent_shutdown).await });

    wait_until_online(&http, addr, &device.device_id, &device.api_key).await;

    let (status, body) = post_json(
        &http,
        &format!("http://{addr}/api/v1/tasks"),
        Some((
            "authorization",
            format!("Bearer {}", device.api_key).as_str(),
        )),
        json!({
            "device_id": device.device_id,
            "prompt": "写入文件并报告完成",
            "workspace": workspace.path().display().to_string(),
        }),
    )
    .await;
    assert_eq!(status, reqwest::StatusCode::ACCEPTED, "{body}");
    let task_id = string_field(&body, "task_id");

    let deadline = Instant::now() + Duration::from_secs(30);
    let task = loop {
        let response = http
            .get(format!("http://{addr}/api/v1/tasks/{task_id}"))
            .header("authorization", format!("Bearer {}", device.api_key))
            .send()
            .await
            .expect("task request");
        let body: Value = response.json().await.expect("task json");
        let task: TaskView = serde_json::from_value(body).expect("task view");
        if task.status.is_terminal() {
            break task;
        }
        assert!(
            Instant::now() < deadline,
            "task {task_id} did not finish, last status {}",
            task.status.as_str()
        );
        tokio::time::sleep(Duration::from_millis(50)).await;
    };

    assert_eq!(
        task.status,
        TaskStatus::Succeeded,
        "task failed: {:?}",
        task.error
    );
    assert!(
        task.summary
            .as_deref()
            .is_some_and(|summary| summary.contains("任务完成")),
        "unexpected summary: {:?}",
        task.summary
    );
    assert_eq!(
        std::fs::read_to_string(workspace.path().join("agent-cloud.txt"))
            .expect("agent must write the file"),
        "written by local agent\n"
    );
    assert!(model_state.calls.load(Ordering::SeqCst) >= 2);

    shutdown.cancel();
    tokio::time::timeout(Duration::from_secs(5), agent_task)
        .await
        .expect("agent should stop promptly")
        .expect("agent task")
        .expect("agent run");
}
