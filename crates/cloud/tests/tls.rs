//! TLS 端到端测试：自签名证书、真实 rustls 握手、HTTPS 认证与 WSS 设备连接。

use std::{net::SocketAddr, sync::Arc, time::Duration};

use axum_server::tls_rustls::RustlsConfig;
use coder_cloud::{
    auth::AuthService, config::CloudConfig, hub::Hub, routes, state::AppState, store::MemoryStore,
};
use coder_core::Secret;
use coder_protocol::{AgentCapabilities, ClientMessage, PROTOCOL_VERSION, ServerMessage};
use futures::{SinkExt, StreamExt};
use rcgen::{CertifiedKey, generate_simple_self_signed};
use serde_json::{Value, json};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    net::TcpStream,
    time::{sleep, timeout},
};
use tokio_rustls::{
    TlsConnector,
    client::TlsStream as ClientTlsStream,
    rustls::{ClientConfig, RootCertStore, pki_types::ServerName},
};
use tokio_tungstenite::{
    WebSocketStream, client_async,
    tungstenite::{Message, client::IntoClientRequest},
};

const ADMIN_KEY: &str = "tls-test-admin-key-0123456789";
type TlsSocket = WebSocketStream<ClientTlsStream<TcpStream>>;

/// 启动 TLS 云端，返回监听地址、配置和自签名证书 DER。
async fn start_tls_cloud() -> (SocketAddr, ClientConfig, Vec<u8>) {
    let config = CloudConfig {
        jwt_secret: Secret("t".repeat(32)),
        admin_api_key: Secret(ADMIN_KEY.into()),
        rate_limit_per_minute: 120,
        ..CloudConfig::default()
    };
    config.validate().expect("tls test config");
    let config = Arc::new(config);
    let store = Arc::new(MemoryStore::new());
    let auth = Arc::new(AuthService::new(
        config.jwt_secret.expose(),
        config.token_ttl_secs,
    ));
    let hub = Arc::new(Hub::new(store.clone(), config.clone()));
    let state = AppState::new(config, store, auth, hub, None, None);

    let CertifiedKey { cert, signing_key } =
        generate_simple_self_signed(vec!["localhost".into(), "127.0.0.1".into()])
            .expect("self-signed certificate");
    let certificate_der = cert.der().to_vec();
    let server_tls = RustlsConfig::from_pem(
        cert.pem().into_bytes(),
        signing_key.serialize_pem().into_bytes(),
    )
    .await
    .expect("server tls config");

    let listener = std::net::TcpListener::bind("127.0.0.1:0").expect("bind tls listener");
    listener
        .set_nonblocking(true)
        .expect("nonblocking listener");
    let addr = listener.local_addr().expect("tls listener address");
    let app = routes::router(state);
    tokio::spawn(async move {
        axum_server::from_tcp_rustls(listener, server_tls)
            .expect("rustls server")
            .serve(app.into_make_service_with_connect_info::<SocketAddr>())
            .await
            .expect("tls server");
    });

    let mut roots = RootCertStore::empty();
    roots
        .add(cert.der().clone())
        .expect("trust self-signed certificate");
    let client_tls = ClientConfig::builder()
        .with_root_certificates(roots)
        .with_no_client_auth();

    // 等待监听端口真正接受连接，避免与服务器任务启动竞争。
    for _ in 0..100 {
        if TcpStream::connect(addr).await.is_ok() {
            break;
        }
        sleep(Duration::from_millis(20)).await;
    }
    (addr, client_tls, certificate_der)
}

/// 通过真实 TLS 发送一个 HTTP/1.1 GET 请求并返回完整响应。
async fn tls_get(addr: SocketAddr, tls: &ClientConfig, host: &str, path: &str) -> (String, String) {
    let stream = TcpStream::connect(addr).await.expect("tcp connect");
    let connector = TlsConnector::from(Arc::new(tls.clone()));
    let name = ServerName::try_from(host.to_owned()).expect("server name");
    let mut stream = connector
        .connect(name, stream)
        .await
        .expect("tls handshake");
    let request = format!("GET {path} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
    stream
        .write_all(request.as_bytes())
        .await
        .expect("write http request");
    let mut response = Vec::new();
    stream
        .read_to_end(&mut response)
        .await
        .expect("read http response");
    let response = String::from_utf8(response).expect("utf-8 http response");
    let (head, body) = response
        .split_once("\r\n\r\n")
        .expect("http response separator");
    (head.to_owned(), body.to_owned())
}

/// 使用自签名 CA 构造可信的 HTTPS 客户端。
fn https_client(certificate_der: &[u8]) -> reqwest::Client {
    let certificate = reqwest::Certificate::from_der(certificate_der).expect("reqwest certificate");
    reqwest::Client::builder()
        .add_root_certificate(certificate)
        .build()
        .expect("https client")
}

/// 解析 HTTPS JSON 响应。
async fn parse_https(response: reqwest::Response) -> (u16, Value) {
    let status = response.status().as_u16();
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

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn tls_serves_health_and_secure_websocket_sessions() {
    let (addr, client_tls, certificate_der) = start_tls_cloud().await;

    let (head, body) = tls_get(addr, &client_tls, "localhost", "/api/v1/health").await;
    assert!(head.starts_with("HTTP/1.1 200"), "{head}");
    assert!(body.contains("\"status\":\"ok\""), "{body}");

    let http = https_client(&certificate_der);
    let base = format!("https://127.0.0.1:{}", addr.port());
    let (status, body) = parse_https(
        http.post(format!("{base}/api/v1/admin/users"))
            .header("x-admin-key", ADMIN_KEY)
            .json(&json!({"email": "tls@example.com"}))
            .send()
            .await
            .expect("create user over https"),
    )
    .await;
    assert_eq!(status, 201, "{body}");
    let user_id = string_field(&body, "user_id");

    let (status, body) = parse_https(
        http.post(format!("{base}/api/v1/admin/pairing-tokens"))
            .header("x-admin-key", ADMIN_KEY)
            .json(&json!({"user_id": user_id}))
            .send()
            .await
            .expect("create pairing token over https"),
    )
    .await;
    assert_eq!(status, 201, "{body}");
    let pairing_token = string_field(&body, "pairing_token");

    let (status, body) = parse_https(
        http.post(format!("{base}/api/v1/devices/register"))
            .json(&json!({
                "pairing_token": pairing_token,
                "name": "tls-agent",
                "platform": "windows",
                "agent_version": "0.1.0",
            }))
            .send()
            .await
            .expect("register device over https"),
    )
    .await;
    assert_eq!(status, 201, "{body}");
    let device_id = string_field(&body, "device_id");
    let device_secret = string_field(&body, "device_secret");

    let (status, body) = parse_https(
        http.post(format!("{base}/api/v1/devices/token"))
            .json(&json!({"device_id": device_id, "device_secret": device_secret}))
            .send()
            .await
            .expect("exchange device token over https"),
    )
    .await;
    assert_eq!(status, 200, "{body}");
    let access_token = string_field(&body, "access_token");

    let mut request = format!("wss://localhost:{}/api/v1/agent/ws", addr.port())
        .into_client_request()
        .expect("wss request");
    request.headers_mut().insert(
        "authorization",
        format!("Bearer {access_token}")
            .parse()
            .expect("authorization header"),
    );
    let stream = TcpStream::connect(addr).await.expect("tcp connect for wss");
    let connector = TlsConnector::from(Arc::new(client_tls));
    let name = ServerName::try_from("localhost").expect("wss server name");
    let tls_stream = connector
        .connect(name, stream)
        .await
        .expect("wss handshake");
    let (mut socket, response): (TlsSocket, _) = client_async(request, tls_stream)
        .await
        .expect("wss connect");
    assert_eq!(response.status().as_u16(), 101);

    let hello = ClientMessage::Hello {
        protocol_version: PROTOCOL_VERSION,
        agent_version: "0.1.0".into(),
        device_name: "tls-agent".into(),
        platform: "windows".into(),
        capabilities: AgentCapabilities {
            tools: vec![],
            workspace_roots: vec![],
            max_parallel_tasks: 1,
        },
        in_flight: vec![],
    };
    socket
        .send(Message::Text(
            serde_json::to_string(&hello).expect("encode hello").into(),
        ))
        .await
        .expect("send hello over wss");
    let frame = timeout(Duration::from_secs(10), socket.next())
        .await
        .expect("welcome timeout")
        .expect("wss closed")
        .expect("wss read error");
    let message: ServerMessage = match frame {
        Message::Text(text) => serde_json::from_str(text.as_str()).expect("welcome"),
        other => panic!("expected text welcome, got {other:?}"),
    };
    match message {
        ServerMessage::Welcome {
            protocol_version,
            tasks,
            ..
        } => {
            assert_eq!(protocol_version, PROTOCOL_VERSION);
            assert!(tasks.is_empty());
        }
        other => panic!("expected welcome over wss, got {other:?}"),
    }

    // 明文 HTTP 连到 TLS 端口不能得到有效应用响应。
    let mut plain = TcpStream::connect(addr).await.expect("plain tcp connect");
    plain
        .write_all(b"GET /api/v1/health HTTP/1.1\r\nHost: localhost\r\n\r\n")
        .await
        .expect("write plaintext request");
    let mut buf = Vec::new();
    let _ = timeout(Duration::from_secs(3), plain.read_to_end(&mut buf)).await;
    assert!(
        !String::from_utf8_lossy(&buf).contains("\"status\":\"ok\""),
        "plaintext HTTP must not be served on the TLS listener"
    );
}
