//! 使用本地 OpenAI 兼容流式服务验证真实 Agent 循环、文件写入与命令执行。

use std::{
    sync::{
        Arc, Mutex,
        atomic::{AtomicUsize, Ordering},
    },
    time::Duration,
};

use axum::{
    Json, Router, extract::State, http::header::CONTENT_TYPE, response::IntoResponse, routing::post,
};
use coder_core::{
    AgentEvent, ApprovalHandler, RunnerConfig, Secret, Workspace,
    agent::{AgentRunner, AutoApprove},
    config::{ModelConfig, PermissionMode},
    model::OpenAiModel,
    store::SqliteStore,
};
use coder_protocol::{SessionId, TaskStatus};
use serde_json::{Value, json};
use tokio_util::sync::CancellationToken;

/// 模拟服务的共享状态。
#[derive(Clone)]
struct TestState {
    calls: Arc<AtomicUsize>,
    last_request: Arc<Mutex<Option<Value>>>,
}

/// 启动本地模型服务并返回根地址。
async fn start_model_server(state: TestState) -> String {
    let app = Router::new()
        .route("/v1/chat/completions", post(completions))
        .with_state(state);
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0")
        .await
        .expect("bind model test server");
    let addr = listener.local_addr().expect("model test server address");
    tokio::spawn(async move {
        let _ = axum::serve(listener, app).await;
    });
    format!("http://{addr}/v1")
}

/// 按请求顺序返回工具调用与最终回答。
async fn completions(State(state): State<TestState>, Json(body): Json<Value>) -> impl IntoResponse {
    let index = state.calls.fetch_add(1, Ordering::SeqCst);
    let payload = match index {
        0 => tool_call_event(
            "call_write",
            "write_file",
            json!({"path": "greeting.txt", "content": "hello from coder e2e\n"}),
        ),
        1 => tool_call_event(
            "call_shell",
            "shell",
            json!({"command": "echo coder-e2e-marker"}),
        ),
        _ => {
            *state
                .last_request
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner()) = Some(body);
            text_event("任务完成：已写入文件并执行命令。")
        }
    };
    ([(CONTENT_TYPE, "text/event-stream")], payload)
}

/// 构造包含单个工具调用的 SSE 响应。
fn tool_call_event(id: &str, name: &str, arguments: Value) -> String {
    let arguments = serde_json::to_string(&arguments).expect("encode tool arguments");
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
        "usage": {"prompt_tokens": 120, "completion_tokens": 16}
    });
    format!("data: {event}\n\ndata: [DONE]\n\n")
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn agent_writes_file_executes_command_and_reports_real_output() {
    let state = TestState {
        calls: Arc::new(AtomicUsize::new(0)),
        last_request: Arc::new(Mutex::new(None)),
    };
    let base_url = start_model_server(state.clone()).await;
    let workspace_dir = tempfile::tempdir().expect("workspace");
    let data_dir = tempfile::tempdir().expect("data");

    let mut config = RunnerConfig::default();
    config.workspace.root = workspace_dir.path().to_path_buf();
    config.workspace.writable = true;
    config.permissions.mode = PermissionMode::WorkspaceWrite;
    config.storage.sqlite_path = data_dir.path().join("coder.db");
    config.agent.max_iterations = 8;
    config.agent.max_duration_secs = 60;
    config.model = ModelConfig {
        base_url,
        api_key: Secret("test-key".into()),
        model: "test-model".into(),
        stream: true,
        allow_insecure_http: true,
        max_output_tokens: 1_024,
        ..ModelConfig::default()
    };

    let workspace = Workspace::new(config.workspace.clone()).expect("workspace");
    let store = Arc::new(
        SqliteStore::connect(&config.storage.sqlite_path)
            .await
            .expect("sqlite store"),
    );
    let model = Arc::new(OpenAiModel::new(config.model.clone()).expect("model"));
    let approval: Arc<dyn ApprovalHandler> = Arc::new(AutoApprove);
    let runner = AgentRunner::new(config, workspace, model, Some(store.clone()), approval);
    let mut events = runner.subscribe();

    let outcome = tokio::time::timeout(
        Duration::from_secs(30),
        runner.run(
            "write and verify",
            SessionId::new(),
            CancellationToken::new(),
        ),
    )
    .await
    .expect("agent should finish before timeout")
    .expect("agent run");

    assert_eq!(outcome.status, TaskStatus::Succeeded);
    assert_eq!(
        std::fs::read_to_string(workspace_dir.path().join("greeting.txt")).expect("written file"),
        "hello from coder e2e\n"
    );
    assert!(outcome.files_changed.contains(&"greeting.txt".to_owned()));
    assert!(outcome.tool_calls >= 2);

    let mut saw_command_output = false;
    let mut saw_file_change = false;
    loop {
        match events.try_recv() {
            Ok(AgentEvent::CommandOutput {
                command,
                exit_code,
                stdout_tail,
                ..
            }) => {
                assert!(command.contains("coder-e2e-marker"));
                assert_eq!(exit_code, Some(0));
                assert!(stdout_tail.contains("coder-e2e-marker"));
                saw_command_output = true;
            }
            Ok(AgentEvent::FileChanged { path, .. }) if path == "greeting.txt" => {
                saw_file_change = true;
            }
            Ok(_) => {}
            Err(_) => break,
        }
    }
    assert!(saw_command_output, "missing real command output event");
    assert!(saw_file_change, "missing file change event");

    let last = state
        .last_request
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
        .clone()
        .expect("final model request should be captured");
    let messages = last["messages"].as_array().expect("messages");
    assert!(
        messages.iter().any(|message| message["role"] == "tool"
            && message["content"]
                .as_str()
                .is_some_and(|text| text.contains("coder-e2e-marker"))),
        "shell output must be fed back to the model"
    );
    let tools = last["tools"].as_array().expect("tool specs");
    assert!(
        tools
            .iter()
            .any(|tool| tool["function"]["name"] == "write_file"),
        "tool schemas must be advertised"
    );

    let sessions = store.list_sessions(10).await.expect("sessions");
    assert_eq!(sessions.len(), 1);
    assert_eq!(sessions[0].status, TaskStatus::Succeeded);
}

#[tokio::test]
async fn cancellation_stops_agent_and_reports_canceled() {
    let state = TestState {
        calls: Arc::new(AtomicUsize::new(0)),
        last_request: Arc::new(Mutex::new(None)),
    };
    let base_url = start_model_server(state).await;
    let workspace_dir = tempfile::tempdir().expect("workspace");
    let data_dir = tempfile::tempdir().expect("data");
    let mut config = RunnerConfig::default();
    config.workspace.root = workspace_dir.path().to_path_buf();
    config.storage.sqlite_path = data_dir.path().join("coder.db");
    config.model = ModelConfig {
        base_url,
        api_key: Secret("test-key".into()),
        model: "test-model".into(),
        stream: true,
        allow_insecure_http: true,
        ..ModelConfig::default()
    };
    let workspace = Workspace::new(config.workspace.clone()).expect("workspace");
    let model = Arc::new(OpenAiModel::new(config.model.clone()).expect("model"));
    let approval: Arc<dyn ApprovalHandler> = Arc::new(AutoApprove);
    let runner = AgentRunner::new(config, workspace, model, None, approval);
    let cancel = CancellationToken::new();
    cancel.cancel();
    let outcome = runner
        .run("will be canceled", SessionId::new(), cancel)
        .await
        .expect("canceled run");
    assert_eq!(outcome.status, TaskStatus::Canceled);
}
