//! 云端 HTTP 与 WebSocket 端到端测试：认证、任务状态机、权限转发与断线恢复。

use std::{net::SocketAddr, sync::Arc, time::Duration};

use axum::http::StatusCode;
use coder_cloud::{
    auth::AuthService, config::CloudConfig, hub::Hub, routes, state::AppState, store::MemoryStore,
};
use coder_core::Secret;
use coder_protocol::{
    ClientMessage, InFlightTask, LocalTaskState, PendingPermission, RequestId, RiskLevel,
    ServerMessage, SessionId, TaskAssignment, TaskId, TaskStatus, TaskView,
};
use futures::{SinkExt, StreamExt};
use serde_json::{Value, json};
use tokio::{
    net::{TcpListener, TcpStream},
    time::{sleep, timeout},
};
use tokio_tungstenite::{
    MaybeTlsStream, WebSocketStream, connect_async,
    tungstenite::{Message, client::IntoClientRequest},
};

const ADMIN_KEY: &str = "test-admin-key-0123456789";
type TestSocket = WebSocketStream<MaybeTlsStream<TcpStream>>;

/// 已启动的测试云端。
struct TestCloud {
    addr: SocketAddr,
    http: reqwest::Client,
}

/// 完成注册、配对与令牌交换的测试设备。
struct TestDevice {
    user_id: String,
    api_key: String,
    device_id: String,
    access_token: String,
}

/// 使用内存存储启动一个真实的 Axum HTTP/WebSocket 服务。
async fn start_cloud(rate_limit_per_minute: u32) -> TestCloud {
    let config = CloudConfig {
        jwt_secret: Secret("j".repeat(32)),
        admin_api_key: Secret(ADMIN_KEY.into()),
        rate_limit_per_minute,
        ..CloudConfig::default()
    };
    config.validate().expect("test cloud config");
    let config = Arc::new(config);
    let store = Arc::new(MemoryStore::new());
    let auth = Arc::new(AuthService::new(
        config.jwt_secret.expose(),
        config.token_ttl_secs,
    ));
    let hub = Arc::new(Hub::new(store.clone(), config.clone()));
    let state = AppState::new(config, store, auth, hub, None, None);
    let listener = TcpListener::bind("127.0.0.1:0")
        .await
        .expect("bind cloud test server");
    let addr = listener.local_addr().expect("cloud test address");
    let app = routes::router(state);
    tokio::spawn(async move {
        axum::serve(
            listener,
            app.into_make_service_with_connect_info::<SocketAddr>(),
        )
        .await
        .expect("cloud test server");
    });
    TestCloud {
        addr,
        http: reqwest::Client::new(),
    }
}

impl TestCloud {
    /// 拼接云端 URL。
    fn url(&self, path: &str) -> String {
        format!("http://{}{path}", self.addr)
    }

    /// 发送 GET 请求并解析 JSON 响应。
    async fn get(&self, path: &str, header: Option<(&str, String)>) -> (StatusCode, Value) {
        let mut request = self.http.get(self.url(path));
        if let Some((name, value)) = header {
            request = request.header(name, value);
        }
        parse_response(request.send().await.expect("http GET")).await
    }

    /// 发送 POST 请求并解析 JSON 响应。
    async fn post(
        &self,
        path: &str,
        header: Option<(&str, String)>,
        body: Option<Value>,
    ) -> (StatusCode, Value) {
        let mut request = self.http.post(self.url(path));
        if let Some((name, value)) = header {
            request = request.header(name, value);
        }
        if let Some(body) = body {
            request = request.json(&body);
        }
        parse_response(request.send().await.expect("http POST")).await
    }

    /// 以管理员身份创建用户并完成设备注册。
    async fn provision(&self, email: &str) -> TestDevice {
        let (status, body) = self
            .post(
                "/api/v1/admin/users",
                Some(("x-admin-key", ADMIN_KEY.into())),
                Some(json!({"email": email})),
            )
            .await;
        assert_eq!(status, StatusCode::CREATED, "{body}");
        let user_id = string_field(&body, "user_id");
        let api_key = string_field(&body, "api_key");

        let (status, body) = self
            .post(
                "/api/v1/admin/pairing-tokens",
                Some(("x-admin-key", ADMIN_KEY.into())),
                Some(json!({"user_id": user_id})),
            )
            .await;
        assert_eq!(status, StatusCode::CREATED, "{body}");
        let pairing_token = string_field(&body, "pairing_token");

        let (status, body) = self
            .post(
                "/api/v1/devices/register",
                None,
                Some(json!({
                    "pairing_token": pairing_token,
                    "name": "test-agent",
                    "platform": "windows",
                    "agent_version": "0.1.0",
                })),
            )
            .await;
        assert_eq!(status, StatusCode::CREATED, "{body}");
        let device_id = string_field(&body, "device_id");
        let device_secret = string_field(&body, "device_secret");

        let (status, body) = self
            .post(
                "/api/v1/devices/token",
                None,
                Some(json!({"device_id": device_id, "device_secret": device_secret})),
            )
            .await;
        assert_eq!(status, StatusCode::OK, "{body}");
        let access_token = string_field(&body, "access_token");

        TestDevice {
            user_id,
            api_key,
            device_id,
            access_token,
        }
    }

    /// 以用户身份创建任务。
    async fn create_task(&self, device: &TestDevice, prompt: &str) -> TaskView {
        let (status, body) = self
            .post(
                "/api/v1/tasks",
                Some(("authorization", format!("Bearer {}", device.api_key))),
                Some(json!({
                    "device_id": device.device_id,
                    "prompt": prompt,
                    "workspace": std::env::temp_dir().display().to_string(),
                })),
            )
            .await;
        assert_eq!(status, StatusCode::ACCEPTED, "{body}");
        serde_json::from_value(body).expect("task view")
    }

    /// 读取任务详情。
    async fn task(&self, device: &TestDevice, task_id: TaskId) -> TaskView {
        let (status, body) = self
            .get(
                &format!("/api/v1/tasks/{task_id}"),
                Some(("authorization", format!("Bearer {}", device.api_key))),
            )
            .await;
        assert_eq!(status, StatusCode::OK, "{body}");
        serde_json::from_value(body).expect("task view")
    }
}

/// 解析 HTTP 响应；非 JSON 正文保留在 `raw` 字段中。
async fn parse_response(response: reqwest::Response) -> (StatusCode, Value) {
    let status = response.status();
    let text = response.text().await.unwrap_or_default();
    let body = serde_json::from_str(&text).unwrap_or_else(|_| json!({"raw": text}));
    (status, body)
}

/// 读取 JSON 字符串字段。
fn string_field(body: &Value, name: &str) -> String {
    body.get(name)
        .and_then(Value::as_str)
        .unwrap_or_else(|| panic!("missing string field `{name}` in {body}"))
        .to_owned()
}

/// 建立带设备令牌的 WebSocket 连接。
async fn connect_agent(addr: SocketAddr, token: &str) -> TestSocket {
    let mut request = format!("ws://{addr}/api/v1/agent/ws")
        .into_client_request()
        .expect("websocket request");
    request.headers_mut().insert(
        "authorization",
        format!("Bearer {token}")
            .parse()
            .expect("authorization header"),
    );
    let (socket, response) = connect_async(request).await.expect("websocket connect");
    assert_eq!(response.status().as_u16(), 101);
    socket
}

/// 发送一条客户端消息。
async fn send_message(socket: &mut TestSocket, message: &ClientMessage) {
    let text = serde_json::to_string(message).expect("encode client message");
    socket
        .send(Message::Text(text.into()))
        .await
        .expect("send websocket message");
}

/// 等待下一条服务端消息。
async fn receive_message(socket: &mut TestSocket) -> ServerMessage {
    loop {
        let frame = timeout(Duration::from_secs(10), socket.next())
            .await
            .expect("timed out waiting for a server message")
            .expect("websocket closed unexpectedly")
            .expect("websocket read error");
        match frame {
            Message::Text(text) => {
                return serde_json::from_str(text.as_str()).expect("decode server message");
            }
            Message::Ping(_) | Message::Pong(_) | Message::Binary(_) | Message::Frame(_) => {}
            Message::Close(frame) => panic!("server closed websocket: {frame:?}"),
        }
    }
}

/// 构造 Hello 消息。
fn hello(in_flight: Vec<InFlightTask>) -> ClientMessage {
    ClientMessage::Hello {
        protocol_version: coder_protocol::PROTOCOL_VERSION,
        agent_version: "0.1.0".into(),
        device_name: "test-agent".into(),
        platform: "windows".into(),
        capabilities: coder_protocol::AgentCapabilities {
            tools: vec!["read_file".into()],
            workspace_roots: vec![std::env::temp_dir().display().to_string()],
            max_parallel_tasks: 1,
        },
        in_flight,
    }
}

/// 断言并解构欢迎消息。
fn expect_welcome(
    message: ServerMessage,
    expected_tasks: usize,
    expected_canceled: usize,
) -> (Vec<TaskAssignment>, Vec<coder_protocol::TaskRef>) {
    match message {
        ServerMessage::Welcome {
            tasks, canceled, ..
        } => {
            assert_eq!(
                tasks.len(),
                expected_tasks,
                "unexpected welcome tasks: {tasks:?}"
            );
            assert_eq!(
                canceled.len(),
                expected_canceled,
                "unexpected canceled attempts: {canceled:?}"
            );
            (tasks, canceled)
        }
        other => panic!("expected welcome, got {other:?}"),
    }
}

/// 断言并解构任务下发消息。
fn expect_assignment(message: ServerMessage) -> TaskAssignment {
    match message {
        ServerMessage::TaskAssign { assignment } => assignment,
        other => panic!("expected task assignment, got {other:?}"),
    }
}

/// 断言并解构取消消息。
fn expect_cancel(message: ServerMessage) -> (TaskId, u32) {
    match message {
        ServerMessage::TaskCancel { task_id, attempt } => (task_id, attempt),
        other => panic!("expected task cancel, got {other:?}"),
    }
}

/// 断言并解构结果确认消息。
fn expect_result_ack(message: ServerMessage, task_id: TaskId, attempt: u32) {
    match message {
        ServerMessage::TaskResultAck {
            task_id: acked,
            attempt: acked_attempt,
        } => {
            assert_eq!(acked, task_id);
            assert_eq!(acked_attempt, attempt);
        }
        other => panic!("expected task result ack, got {other:?}"),
    }
}

/// 构造终态任务结果。
fn result_message(
    task_id: TaskId,
    attempt: u32,
    status: TaskStatus,
    summary: &str,
) -> ClientMessage {
    ClientMessage::TaskResult {
        task_id,
        attempt,
        status,
        summary: summary.into(),
        error: None,
        duration_ms: 42,
        files_changed: vec!["src/main.rs".into()],
        session_id: Some(SessionId::new()),
    }
}

/// 轮询权限列表直到请求出现。
async fn wait_for_permissions(
    cloud: &TestCloud,
    device: &TestDevice,
    task_id: TaskId,
) -> Vec<PendingPermission> {
    for _ in 0..100 {
        let (status, body) = cloud
            .get(
                &format!("/api/v1/tasks/{task_id}/permissions"),
                Some(("authorization", format!("Bearer {}", device.api_key))),
            )
            .await;
        assert_eq!(status, StatusCode::OK, "{body}");
        let permissions: Vec<PendingPermission> =
            serde_json::from_value(body).expect("permission list");
        if !permissions.is_empty() {
            return permissions;
        }
        sleep(Duration::from_millis(20)).await;
    }
    panic!("permission request was not persisted");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn http_api_enforces_auth_and_manages_tasks() {
    let cloud = start_cloud(120).await;

    let (status, body) = cloud.get("/api/v1/health", None).await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["status"], "ok");
    let (status, body) = cloud.get("/api/v1/ready", None).await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["status"], "ready");

    let (status, body) = cloud.get("/api/v1/me", None).await;
    assert_eq!(status, StatusCode::UNAUTHORIZED);
    assert_eq!(body["error"]["code"], "unauthorized");

    let (status, _) = cloud
        .post(
            "/api/v1/admin/users",
            Some(("x-admin-key", "wrong-admin-key".into())),
            Some(json!({"email": "intruder@example.com"})),
        )
        .await;
    assert_eq!(status, StatusCode::UNAUTHORIZED);

    let device = cloud.provision("owner@example.com").await;
    let (status, body) = cloud
        .get(
            "/api/v1/me",
            Some(("authorization", format!("Bearer {}", device.api_key))),
        )
        .await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["email"], "owner@example.com");
    assert_eq!(body["user_id"], device.user_id);

    let (status, body) = cloud
        .post(
            "/api/v1/devices/register",
            None,
            Some(json!({
                "pairing_token": "this-token-was-never-issued",
                "name": "bad-agent",
                "platform": "windows",
                "agent_version": "0.1.0",
            })),
        )
        .await;
    assert_eq!(status, StatusCode::UNAUTHORIZED, "{body}");

    let task = cloud.create_task(&device, "inspect the repository").await;
    assert_eq!(task.status, TaskStatus::Pending);
    assert_eq!(task.attempt, 0);

    let (status, body) = cloud
        .get(
            "/api/v1/tasks?limit=10",
            Some(("authorization", format!("Bearer {}", device.api_key))),
        )
        .await;
    assert_eq!(status, StatusCode::OK);
    let tasks: Vec<TaskView> = serde_json::from_value(body).expect("task list");
    assert_eq!(tasks.len(), 1);
    assert_eq!(tasks[0].task_id, task.task_id);

    let (status, body) = cloud
        .get(
            "/api/v1/devices",
            Some(("authorization", format!("Bearer {}", device.api_key))),
        )
        .await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body[0]["device_id"], device.device_id);
    assert_eq!(body[0]["online"], false);

    let (status, body) = cloud
        .post(
            &format!("/api/v1/tasks/{}/cancel", task.task_id),
            Some(("authorization", format!("Bearer {}", device.api_key))),
            None,
        )
        .await;
    assert_eq!(status, StatusCode::OK, "{body}");
    assert_eq!(body["status"], "canceled");

    let (status, body) = cloud
        .post(
            &format!("/api/v1/tasks/{}/retry", task.task_id),
            Some(("authorization", format!("Bearer {}", device.api_key))),
            None,
        )
        .await;
    assert_eq!(status, StatusCode::ACCEPTED, "{body}");
    assert_eq!(body["status"], "pending");
    assert_eq!(body["attempt"], 1);

    let other = cloud.provision("other@example.com").await;
    let (status, body) = cloud
        .get(
            &format!("/api/v1/tasks/{}", task.task_id),
            Some(("authorization", format!("Bearer {}", other.api_key))),
        )
        .await;
    assert_eq!(status, StatusCode::NOT_FOUND);
    assert_eq!(body["error"]["code"], "task_not_found");

    let (status, body) = cloud.get("/api/v1/does-not-exist", None).await;
    assert_eq!(status, StatusCode::NOT_FOUND);
    assert_eq!(body["error"]["message"], "route not found");

    let (status, body) = cloud
        .post(
            "/api/v1/model/chat",
            Some(("authorization", format!("Bearer {}", device.access_token))),
            Some(json!({
                "messages": [{"role": "user", "content": "hello"}],
                "tools": [],
                "temperature": 0.2,
                "max_output_tokens": 16,
                "stream": false,
            })),
        )
        .await;
    assert_eq!(status, StatusCode::SERVICE_UNAVAILABLE, "{body}");
    assert_eq!(body["error"]["code"], "unavailable");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn rate_limiter_returns_429_with_retry_after() {
    let cloud = start_cloud(1).await;
    let (status, _) = cloud.get("/api/v1/health", None).await;
    assert_eq!(status, StatusCode::OK);
    let response = cloud
        .http
        .get(cloud.url("/api/v1/health"))
        .send()
        .await
        .expect("second health request");
    assert_eq!(response.status(), StatusCode::TOO_MANY_REQUESTS);
    assert_eq!(
        response
            .headers()
            .get("retry-after")
            .and_then(|value| value.to_str().ok()),
        Some("5")
    );
    let body: Value = response.json().await.expect("rate limit body");
    assert_eq!(body["error"]["code"], "rate_limited");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn websocket_runs_tasks_cancel_retry_and_permission_flow() {
    let cloud = start_cloud(120).await;
    let device = cloud.provision("ws@example.com").await;
    let mut socket = connect_agent(cloud.addr, &device.access_token).await;

    send_message(&mut socket, &hello(Vec::new())).await;
    expect_welcome(receive_message(&mut socket).await, 0, 0);

    let task = cloud.create_task(&device, "run the real test suite").await;
    let assignment = expect_assignment(receive_message(&mut socket).await);
    assert_eq!(assignment.task_id, task.task_id);
    assert_eq!(assignment.attempt, 0);
    assert_eq!(assignment.prompt, "run the real test suite");

    send_message(
        &mut socket,
        &ClientMessage::TaskStarted {
            task_id: task.task_id,
            attempt: 0,
            started_at: chrono::Utc::now(),
        },
    )
    .await;

    send_message(
        &mut socket,
        &ClientMessage::Heartbeat {
            ts: chrono::Utc::now(),
        },
    )
    .await;
    match receive_message(&mut socket).await {
        ServerMessage::HeartbeatAck { .. } => {}
        other => panic!("expected heartbeat ack, got {other:?}"),
    }

    let request_id = RequestId::new();
    send_message(
        &mut socket,
        &ClientMessage::PermissionRequest {
            task_id: task.task_id,
            attempt: 0,
            request_id,
            tool: "shell".into(),
            action: "cargo publish".into(),
            details: "requested by test".into(),
            risk: RiskLevel::High,
        },
    )
    .await;
    let pending = wait_for_permissions(&cloud, &device, task.task_id).await;
    assert_eq!(pending.len(), 1);
    assert_eq!(pending[0].request_id, request_id);
    assert_eq!(pending[0].tool, "shell");

    let (status, body) = cloud
        .post(
            &format!("/api/v1/tasks/{}/permissions/{request_id}", task.task_id),
            Some(("authorization", format!("Bearer {}", device.api_key))),
            Some(json!({"granted": true, "reason": "approved by test"})),
        )
        .await;
    assert_eq!(status, StatusCode::OK, "{body}");
    assert_eq!(body["granted"], true);
    match receive_message(&mut socket).await {
        ServerMessage::PermissionDecision {
            task_id,
            request_id: decided,
            granted,
            reason,
        } => {
            assert_eq!(task_id, task.task_id);
            assert_eq!(decided, request_id);
            assert!(granted);
            assert_eq!(reason.as_deref(), Some("approved by test"));
        }
        other => panic!("expected permission decision, got {other:?}"),
    }

    send_message(
        &mut socket,
        &result_message(
            task.task_id,
            0,
            TaskStatus::Succeeded,
            "tests passed with real output",
        ),
    )
    .await;
    expect_result_ack(receive_message(&mut socket).await, task.task_id, 0);
    let view = cloud.task(&device, task.task_id).await;
    assert_eq!(view.status, TaskStatus::Succeeded);
    assert_eq!(
        view.summary.as_deref(),
        Some("tests passed with real output")
    );
    assert_eq!(view.files_changed, vec!["src/main.rs".to_owned()]);

    let (status, body) = cloud
        .post(
            &format!("/api/v1/tasks/{}/retry", task.task_id),
            Some(("authorization", format!("Bearer {}", device.api_key))),
            None,
        )
        .await;
    assert_eq!(status, StatusCode::ACCEPTED, "{body}");
    let assignment = expect_assignment(receive_message(&mut socket).await);
    assert_eq!(assignment.task_id, task.task_id);
    assert_eq!(assignment.attempt, 1);
    send_message(
        &mut socket,
        &ClientMessage::TaskStarted {
            task_id: task.task_id,
            attempt: 1,
            started_at: chrono::Utc::now(),
        },
    )
    .await;
    send_message(
        &mut socket,
        &result_message(task.task_id, 1, TaskStatus::Failed, "second attempt failed"),
    )
    .await;
    expect_result_ack(receive_message(&mut socket).await, task.task_id, 1);
    assert_eq!(
        cloud.task(&device, task.task_id).await.status,
        TaskStatus::Failed
    );

    let cancellable = cloud.create_task(&device, "cancel this task").await;
    let assignment = expect_assignment(receive_message(&mut socket).await);
    assert_eq!(assignment.task_id, cancellable.task_id);
    send_message(
        &mut socket,
        &ClientMessage::TaskStarted {
            task_id: cancellable.task_id,
            attempt: 0,
            started_at: chrono::Utc::now(),
        },
    )
    .await;
    let (status, body) = cloud
        .post(
            &format!("/api/v1/tasks/{}/cancel", cancellable.task_id),
            Some(("authorization", format!("Bearer {}", device.api_key))),
            None,
        )
        .await;
    assert_eq!(status, StatusCode::OK, "{body}");
    assert_eq!(body["status"], "running");
    let (task_id, attempt) = expect_cancel(receive_message(&mut socket).await);
    assert_eq!(task_id, cancellable.task_id);
    assert_eq!(attempt, 0);
    send_message(&mut socket, &ClientMessage::CancelAck { task_id, attempt }).await;
    send_message(
        &mut socket,
        &result_message(
            task_id,
            attempt,
            TaskStatus::Canceled,
            "cancel acknowledged",
        ),
    )
    .await;
    expect_result_ack(receive_message(&mut socket).await, task_id, attempt);
    assert_eq!(
        cloud.task(&device, cancellable.task_id).await.status,
        TaskStatus::Canceled
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn reconnect_resumes_started_tasks_without_duplicate_attempts() {
    let cloud = start_cloud(120).await;
    let device = cloud.provision("resume@example.com").await;
    let mut socket = connect_agent(cloud.addr, &device.access_token).await;
    send_message(&mut socket, &hello(Vec::new())).await;
    expect_welcome(receive_message(&mut socket).await, 0, 0);

    let task = cloud.create_task(&device, "resume after disconnect").await;
    let assignment = expect_assignment(receive_message(&mut socket).await);
    assert_eq!(assignment.attempt, 0);
    send_message(
        &mut socket,
        &ClientMessage::TaskStarted {
            task_id: task.task_id,
            attempt: 0,
            started_at: chrono::Utc::now(),
        },
    )
    .await;
    assert_eq!(
        cloud.task(&device, task.task_id).await.status,
        TaskStatus::Running
    );
    drop(socket);
    sleep(Duration::from_millis(100)).await;

    let mut resumed = connect_agent(cloud.addr, &device.access_token).await;
    send_message(
        &mut resumed,
        &hello(vec![InFlightTask {
            task_id: task.task_id,
            attempt: 0,
            state: LocalTaskState::Started,
        }]),
    )
    .await;
    let (tasks, canceled) = expect_welcome(receive_message(&mut resumed).await, 1, 0);
    assert_eq!(tasks[0].task_id, task.task_id);
    assert_eq!(tasks[0].attempt, 0);
    assert!(canceled.is_empty());
    let view = cloud.task(&device, task.task_id).await;
    assert_eq!(view.status, TaskStatus::Running);
    assert_eq!(view.attempt, 0);

    send_message(
        &mut resumed,
        &result_message(
            task.task_id,
            0,
            TaskStatus::Succeeded,
            "resumed and verified",
        ),
    )
    .await;
    expect_result_ack(receive_message(&mut resumed).await, task.task_id, 0);
    assert_eq!(
        cloud.task(&device, task.task_id).await.status,
        TaskStatus::Succeeded
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn missing_local_record_becomes_unknown_and_is_not_replayed() {
    let cloud = start_cloud(120).await;
    let device = cloud.provision("unknown@example.com").await;
    let mut socket = connect_agent(cloud.addr, &device.access_token).await;
    send_message(&mut socket, &hello(Vec::new())).await;
    expect_welcome(receive_message(&mut socket).await, 0, 0);

    let task = cloud.create_task(&device, "local state was lost").await;
    expect_assignment(receive_message(&mut socket).await);
    send_message(
        &mut socket,
        &ClientMessage::TaskStarted {
            task_id: task.task_id,
            attempt: 0,
            started_at: chrono::Utc::now(),
        },
    )
    .await;
    drop(socket);
    sleep(Duration::from_millis(100)).await;

    let mut resumed = connect_agent(cloud.addr, &device.access_token).await;
    send_message(&mut resumed, &hello(Vec::new())).await;
    let (tasks, canceled) = expect_welcome(receive_message(&mut resumed).await, 0, 0);
    assert!(tasks.is_empty());
    assert!(canceled.is_empty());
    let view = cloud.task(&device, task.task_id).await;
    assert_eq!(view.status, TaskStatus::Unknown);
    assert!(
        view.summary
            .as_deref()
            .is_some_and(|summary| summary.contains("no in-flight record")),
        "{:?}",
        view.summary
    );

    // 迟到的真实结果仍然可以确认这次尝试；Unknown 不代表成功。
    send_message(
        &mut resumed,
        &result_message(
            task.task_id,
            0,
            TaskStatus::Succeeded,
            "late real result from the agent",
        ),
    )
    .await;
    expect_result_ack(receive_message(&mut resumed).await, task.task_id, 0);
    assert_eq!(
        cloud.task(&device, task.task_id).await.status,
        TaskStatus::Succeeded
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn terminal_tasks_are_never_rewound_by_reconnect_state() {
    let cloud = start_cloud(120).await;
    let device = cloud.provision("terminal@example.com").await;
    let mut socket = connect_agent(cloud.addr, &device.access_token).await;
    send_message(&mut socket, &hello(Vec::new())).await;
    expect_welcome(receive_message(&mut socket).await, 0, 0);

    let task = cloud.create_task(&device, "already finished").await;
    expect_assignment(receive_message(&mut socket).await);
    send_message(
        &mut socket,
        &ClientMessage::TaskStarted {
            task_id: task.task_id,
            attempt: 0,
            started_at: chrono::Utc::now(),
        },
    )
    .await;
    send_message(
        &mut socket,
        &result_message(task.task_id, 0, TaskStatus::Succeeded, "finished for real"),
    )
    .await;
    expect_result_ack(receive_message(&mut socket).await, task.task_id, 0);
    drop(socket);
    sleep(Duration::from_millis(100)).await;

    let mut resumed = connect_agent(cloud.addr, &device.access_token).await;
    send_message(
        &mut resumed,
        &hello(vec![InFlightTask {
            task_id: task.task_id,
            attempt: 0,
            state: LocalTaskState::Started,
        }]),
    )
    .await;
    let (tasks, canceled) = expect_welcome(receive_message(&mut resumed).await, 0, 1);
    assert!(tasks.is_empty());
    assert_eq!(canceled[0].task_id, task.task_id);
    assert_eq!(canceled[0].attempt, 0);
    let view = cloud.task(&device, task.task_id).await;
    assert_eq!(view.status, TaskStatus::Succeeded);
    assert_eq!(view.summary.as_deref(), Some("finished for real"));
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn locally_started_but_unassigned_task_is_unknown_without_redispatch() {
    let cloud = start_cloud(120).await;
    let device = cloud.provision("unassigned@example.com").await;
    let task = cloud
        .create_task(&device, "never assigned by the cloud")
        .await;
    assert_eq!(task.status, TaskStatus::Pending);

    let mut socket = connect_agent(cloud.addr, &device.access_token).await;
    send_message(
        &mut socket,
        &hello(vec![InFlightTask {
            task_id: task.task_id,
            attempt: 0,
            state: LocalTaskState::Started,
        }]),
    )
    .await;
    let (tasks, canceled) = expect_welcome(receive_message(&mut socket).await, 0, 1);
    assert!(tasks.is_empty());
    assert_eq!(canceled[0].task_id, task.task_id);
    let view = cloud.task(&device, task.task_id).await;
    assert_eq!(view.status, TaskStatus::Unknown);

    // 欢迎握手后的 pending 泵送不得把同一次尝试重新下发。
    let unexpected = timeout(Duration::from_millis(300), socket.next()).await;
    if let Ok(Some(Ok(Message::Text(text)))) = unexpected {
        let message: ServerMessage = serde_json::from_str(text.as_str()).expect("server message");
        panic!("unexpected message after unknown reconciliation: {message:?}");
    }

    send_message(
        &mut socket,
        &result_message(
            task.task_id,
            0,
            TaskStatus::Canceled,
            "local execution stopped",
        ),
    )
    .await;
    expect_result_ack(receive_message(&mut socket).await, task.task_id, 0);
    assert_eq!(
        cloud.task(&device, task.task_id).await.status,
        TaskStatus::Canceled
    );
}
