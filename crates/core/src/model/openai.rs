use std::{collections::BTreeMap, time::Duration};

use futures::StreamExt;
use reqwest::{
    Client,
    header::{AUTHORIZATION, CONTENT_TYPE, HeaderMap, HeaderName, HeaderValue},
};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use tokio::sync::mpsc;
use tokio_util::sync::CancellationToken;

use super::{ChatRequest, ModelClient, ModelError, ModelResponse, Role, TokenUsage, ToolCall};
use crate::config::{ModelCapabilities, ModelConfig};

/// OpenAI 兼容的 Chat Completions 适配器。
///
/// 支持非流式与 SSE 流式响应、增量工具调用、429/5xx 退避重试和显式取消。
pub struct OpenAiModel {
    config: ModelConfig,
    client: Client,
    endpoint: String,
    headers: HeaderMap,
}

impl std::fmt::Debug for OpenAiModel {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("OpenAiModel")
            .field("endpoint", &self.endpoint)
            .field("model", &self.config.model)
            .finish_non_exhaustive()
    }
}

impl OpenAiModel {
    /// 根据配置构造适配器。
    pub fn new(config: ModelConfig) -> Result<Self, ModelError> {
        config
            .validate()
            .map_err(|err| ModelError::NotConfigured(err.to_string()))?;
        let client = Client::builder()
            .timeout(Duration::from_secs(config.request_timeout_secs))
            .user_agent(concat!("coder/", env!("CARGO_PKG_VERSION")))
            .build()
            .map_err(|err| ModelError::Http(err.to_string()))?;
        let endpoint = chat_completions_url(&config.base_url);
        let mut headers = HeaderMap::new();
        if !config.api_key.is_empty() {
            let value = format!("Bearer {}", config.api_key.expose());
            let mut value = HeaderValue::from_str(&value).map_err(|err| {
                ModelError::NotConfigured(format!("invalid API key header: {err}"))
            })?;
            value.set_sensitive(true);
            headers.insert(AUTHORIZATION, value);
        }
        for (name, value) in &config.extra_headers {
            let name = HeaderName::from_bytes(name.as_bytes()).map_err(|err| {
                ModelError::NotConfigured(format!("invalid extra header name `{name}`: {err}"))
            })?;
            let value = HeaderValue::from_str(value).map_err(|err| {
                ModelError::NotConfigured(format!("invalid extra header value for `{name}`: {err}"))
            })?;
            headers.insert(name, value);
        }
        Ok(Self {
            config,
            client,
            endpoint,
            headers,
        })
    }

    /// 发送一次请求，按需重试。
    async fn send(
        &self,
        body: &Value,
        stream: bool,
        cancel: &CancellationToken,
        delta_tx: Option<mpsc::UnboundedSender<String>>,
    ) -> Result<ModelResponse, ModelError> {
        let mut attempt = 0u32;
        loop {
            let request = self
                .client
                .post(&self.endpoint)
                .headers(self.headers.clone())
                .header(CONTENT_TYPE, "application/json")
                .json(body);
            let result = tokio::select! {
                _ = cancel.cancelled() => return Err(ModelError::Canceled),
                response = request.send() => response,
            };
            let response = match result {
                Ok(response) => response,
                Err(err) => {
                    if attempt < self.config.max_retries && is_retryable_transport(&err) {
                        sleep_backoff(attempt).await;
                        attempt += 1;
                        continue;
                    }
                    return Err(ModelError::Http(err.to_string()));
                }
            };

            let status = response.status();
            if !status.is_success() {
                let retry_after = retry_after_secs(response.headers());
                let body = response.text().await.unwrap_or_default();
                let body = truncate(&body, 8_192);
                if attempt < self.config.max_retries
                    && (status.as_u16() == 429
                        || status.is_server_error()
                        || status.as_u16() == 408)
                {
                    let delay = retry_after.unwrap_or_else(|| backoff_delay(attempt));
                    let delay = delay.min(Duration::from_secs(30));
                    tokio::select! {
                        _ = cancel.cancelled() => return Err(ModelError::Canceled),
                        _ = tokio::time::sleep(delay) => {}
                    }
                    attempt += 1;
                    continue;
                }
                return Err(ModelError::Status {
                    status: status.as_u16(),
                    body,
                });
            }

            let is_event_stream = response
                .headers()
                .get(CONTENT_TYPE)
                .and_then(|value| value.to_str().ok())
                .map(|value| value.contains("text/event-stream"))
                .unwrap_or(false);

            if stream && is_event_stream {
                return parse_stream(response, cancel.clone(), delta_tx).await;
            }
            let text = tokio::select! {
                _ = cancel.cancelled() => return Err(ModelError::Canceled),
                text = response.text() => text.map_err(|err| ModelError::Http(err.to_string()))?,
            };
            return parse_completion(&text);
        }
    }
}

#[async_trait::async_trait]
impl ModelClient for OpenAiModel {
    fn model_name(&self) -> &str {
        &self.config.model
    }

    fn capabilities(&self) -> ModelCapabilities {
        self.config.capabilities.clone()
    }

    async fn complete(
        &self,
        request: ChatRequest,
        cancel: CancellationToken,
        delta_tx: Option<mpsc::UnboundedSender<String>>,
    ) -> Result<ModelResponse, ModelError> {
        if !request.tools.is_empty() && !self.config.capabilities.tools {
            return Err(ModelError::Unsupported(
                "tool calling is disabled for this model".into(),
            ));
        }
        let stream = request.stream && self.config.capabilities.streaming;
        let body = build_request_body(&self.config.model, &request, stream);
        self.send(&body, stream, &cancel, delta_tx).await
    }
}

/// 将 base URL 规范化为 chat completions 端点。
fn chat_completions_url(base_url: &str) -> String {
    let trimmed = base_url.trim_end_matches('/');
    if trimmed.ends_with("/chat/completions") {
        trimmed.to_owned()
    } else {
        format!("{trimmed}/chat/completions")
    }
}

#[derive(Serialize)]
struct WireRequest<'a> {
    model: &'a str,
    messages: Vec<WireMessage>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    tools: Vec<WireTool>,
    temperature: f32,
    max_tokens: u32,
    stream: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    stream_options: Option<WireStreamOptions>,
}

#[derive(Serialize)]
struct WireStreamOptions {
    include_usage: bool,
}

#[derive(Serialize)]
struct WireMessage {
    role: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    content: Option<String>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    tool_calls: Vec<WireToolCall>,
    #[serde(skip_serializing_if = "Option::is_none")]
    tool_call_id: Option<String>,
}

#[derive(Serialize)]
struct WireToolCall {
    id: String,
    #[serde(rename = "type")]
    kind: &'static str,
    function: WireFunction,
}

#[derive(Serialize)]
struct WireFunction {
    name: String,
    arguments: String,
}

#[derive(Serialize)]
struct WireTool {
    #[serde(rename = "type")]
    kind: &'static str,
    function: WireToolSpec,
}

#[derive(Serialize)]
struct WireToolSpec {
    name: String,
    description: String,
    parameters: Value,
}

/// 构造兼容 OpenAI Chat Completions 的请求体。
fn build_request_body(model: &str, request: &ChatRequest, stream: bool) -> Value {
    let messages = request
        .messages
        .iter()
        .map(|message| WireMessage {
            role: match message.role {
                Role::System => "system",
                Role::User => "user",
                Role::Assistant => "assistant",
                Role::Tool => "tool",
            },
            content: message.content.clone(),
            tool_calls: message
                .tool_calls
                .iter()
                .map(|call| WireToolCall {
                    id: call.id.clone(),
                    kind: "function",
                    function: WireFunction {
                        name: call.name.clone(),
                        arguments: call.arguments.clone(),
                    },
                })
                .collect(),
            tool_call_id: message.tool_call_id.clone(),
        })
        .collect();
    let tools = request
        .tools
        .iter()
        .map(|tool| WireTool {
            kind: "function",
            function: WireToolSpec {
                name: tool.name.clone(),
                description: tool.description.clone(),
                parameters: tool.parameters.clone(),
            },
        })
        .collect();
    serde_json::to_value(WireRequest {
        model,
        messages,
        tools,
        temperature: request.temperature,
        max_tokens: request.max_output_tokens,
        stream,
        stream_options: stream.then_some(WireStreamOptions {
            include_usage: true,
        }),
    })
    .unwrap_or_else(|_| json!({}))
}

#[derive(Deserialize)]
struct WireCompletion {
    #[serde(default)]
    choices: Vec<WireChoice>,
    #[serde(default)]
    usage: Option<WireUsage>,
}

#[derive(Deserialize)]
struct WireChoice {
    message: WireAssistantMessage,
    #[serde(default)]
    finish_reason: Option<String>,
}

#[derive(Deserialize, Default)]
struct WireAssistantMessage {
    #[serde(default)]
    content: Option<String>,
    #[serde(default)]
    tool_calls: Vec<WireToolCallReply>,
}

#[derive(Deserialize)]
struct WireToolCallReply {
    #[serde(default)]
    id: String,
    function: WireToolFunctionReply,
}

#[derive(Deserialize)]
struct WireToolFunctionReply {
    name: String,
    #[serde(default)]
    arguments: String,
}

#[derive(Deserialize, Default)]
struct WireUsage {
    #[serde(default)]
    prompt_tokens: u64,
    #[serde(default)]
    completion_tokens: u64,
}

/// 解析非流式响应。
fn parse_completion(text: &str) -> Result<ModelResponse, ModelError> {
    let parsed: WireCompletion = serde_json::from_str(text)
        .map_err(|err| ModelError::Decode(format!("{err}; body={}", truncate(text, 2_048))))?;
    let choice = parsed.choices.into_iter().next().ok_or_else(|| {
        ModelError::Decode(format!(
            "response contains no choices; body={}",
            truncate(text, 2_048)
        ))
    })?;
    let tool_calls = choice
        .message
        .tool_calls
        .into_iter()
        .filter(|call| !call.id.is_empty()) // 忽略不完整的占位调用
        .map(|call| ToolCall {
            id: call.id,
            name: call.function.name,
            arguments: call.function.arguments,
        })
        .collect();
    let usage = parsed
        .usage
        .map(|usage| TokenUsage {
            input_tokens: usage.prompt_tokens,
            output_tokens: usage.completion_tokens,
        })
        .unwrap_or_default();
    Ok(ModelResponse {
        content: choice.message.content.filter(|text| !text.is_empty()),
        tool_calls,
        finish_reason: choice.finish_reason,
        usage,
    })
}

#[derive(Default)]
struct PendingToolCall {
    id: String,
    name: String,
    arguments: String,
}

/// 解析 SSE 流，累积文本与工具调用增量。
async fn parse_stream(
    response: reqwest::Response,
    cancel: CancellationToken,
    delta_tx: Option<mpsc::UnboundedSender<String>>,
) -> Result<ModelResponse, ModelError> {
    let mut stream = response.bytes_stream();
    let mut buffer = String::new();
    let mut event_lines: Vec<String> = Vec::new();
    let mut content = String::new();
    let mut pending: BTreeMap<usize, PendingToolCall> = BTreeMap::new();
    let mut finish_reason = None;
    let mut usage = TokenUsage::default();
    let mut done = false;

    while !done {
        let chunk = tokio::select! {
            _ = cancel.cancelled() => return Err(ModelError::Canceled),
            chunk = stream.next() => chunk,
        };
        let Some(chunk) = chunk else { break };
        let chunk = chunk.map_err(|err| ModelError::Http(err.to_string()))?;
        buffer.push_str(&String::from_utf8_lossy(&chunk));

        while let Some(position) = buffer.find('\n') {
            let line = buffer[..position].trim_end_matches('\r').to_owned();
            buffer.drain(..=position);
            if line.is_empty() {
                if !event_lines.is_empty() {
                    let data = event_lines.join("\n");
                    event_lines.clear();
                    if process_sse_data(
                        &data,
                        &mut content,
                        &mut pending,
                        &mut finish_reason,
                        &mut usage,
                        delta_tx.as_ref(),
                    )? {
                        done = true;
                        break;
                    }
                }
                continue;
            }
            if let Some(data) = line.strip_prefix("data:") {
                event_lines.push(data.trim_start().to_owned());
            }
        }
        if done {
            break;
        }
    }

    if !event_lines.is_empty() && !done {
        let data = event_lines.join("\n");
        let _ = process_sse_data(
            &data,
            &mut content,
            &mut pending,
            &mut finish_reason,
            &mut usage,
            delta_tx.as_ref(),
        )?;
    }

    let tool_calls = pending
        .into_values()
        .filter(|call| !call.id.is_empty() && !call.name.is_empty())
        .map(|call| ToolCall {
            id: call.id,
            name: call.name,
            arguments: call.arguments,
        })
        .collect();

    Ok(ModelResponse {
        content: (!content.is_empty()).then_some(content),
        tool_calls,
        finish_reason,
        usage,
    })
}

/// 处理一条完整的 SSE data 事件，返回是否收到 `[DONE]`。
fn process_sse_data(
    data: &str,
    content: &mut String,
    pending: &mut BTreeMap<usize, PendingToolCall>,
    finish_reason: &mut Option<String>,
    usage: &mut TokenUsage,
    delta_tx: Option<&mpsc::UnboundedSender<String>>,
) -> Result<bool, ModelError> {
    let data = data.trim();
    if data.is_empty() {
        return Ok(false);
    }
    if data == "[DONE]" {
        return Ok(true);
    }
    let value: Value = serde_json::from_str(data)
        .map_err(|err| ModelError::Decode(format!("invalid SSE payload: {err}")))?;
    if let Some(error) = value.get("error") {
        return Err(ModelError::Decode(format!("model stream error: {error}")));
    }
    if let Some(parsed_usage) = value.get("usage").filter(|value| !value.is_null()) {
        usage.input_tokens = parsed_usage
            .get("prompt_tokens")
            .and_then(Value::as_u64)
            .unwrap_or(usage.input_tokens);
        usage.output_tokens = parsed_usage
            .get("completion_tokens")
            .and_then(Value::as_u64)
            .unwrap_or(usage.output_tokens);
    }
    let Some(choice) = value
        .get("choices")
        .and_then(Value::as_array)
        .and_then(|choices| choices.first())
    else {
        return Ok(false);
    };
    if let Some(reason) = choice.get("finish_reason").and_then(Value::as_str) {
        *finish_reason = Some(reason.to_owned());
    }
    let Some(delta) = choice.get("delta") else {
        return Ok(false);
    };
    if let Some(text) = delta.get("content").and_then(Value::as_str)
        && !text.is_empty()
    {
        content.push_str(text);
        if let Some(sender) = delta_tx {
            let _ = sender.send(text.to_owned());
        }
    }
    if let Some(calls) = delta.get("tool_calls").and_then(Value::as_array) {
        for call in calls {
            let index = call.get("index").and_then(Value::as_u64).unwrap_or(0) as usize;
            let entry = pending.entry(index).or_default();
            if let Some(id) = call.get("id").and_then(Value::as_str)
                && !id.is_empty()
            {
                entry.id = id.to_owned();
            }
            if let Some(function) = call.get("function") {
                if let Some(name) = function.get("name").and_then(Value::as_str) {
                    entry.name.push_str(name);
                }
                if let Some(arguments) = function.get("arguments").and_then(Value::as_str) {
                    entry.arguments.push_str(arguments);
                }
            }
        }
    }
    Ok(false)
}

/// 判断传输错误是否值得重试。
fn is_retryable_transport(err: &reqwest::Error) -> bool {
    err.is_timeout() || err.is_connect() || err.is_request()
}

/// 解析 Retry-After 响应头（仅支持秒数形式）。
fn retry_after_secs(headers: &HeaderMap) -> Option<Duration> {
    headers
        .get("retry-after")
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.parse::<u64>().ok())
        .map(Duration::from_secs)
}

/// 指数退避并加入随机抖动。
async fn sleep_backoff(attempt: u32) {
    tokio::time::sleep(backoff_delay(attempt)).await;
}

/// 计算第 `attempt` 次重试的等待时间。
fn backoff_delay(attempt: u32) -> Duration {
    let base_ms = 500u64.saturating_mul(1u64 << attempt.min(5));
    let jitter_ms = rand::random_range(0..=250);
    Duration::from_millis(base_ms + jitter_ms)
}

/// 截断过长文本，避免日志和错误响应无限增长。
fn truncate(text: &str, max: usize) -> String {
    if text.len() <= max {
        return text.to_owned();
    }
    let mut end = max;
    while !text.is_char_boundary(end) {
        end -= 1;
    }
    format!("{}…[truncated {} bytes]", &text[..end], text.len() - end)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn builds_chat_completions_url() {
        assert_eq!(
            chat_completions_url("https://api.example.com/v1/"),
            "https://api.example.com/v1/chat/completions"
        );
        assert_eq!(
            chat_completions_url("https://api.example.com/v1/chat/completions"),
            "https://api.example.com/v1/chat/completions"
        );
    }

    #[test]
    fn parses_non_stream_tool_calls() {
        let body = r#"{
            "choices": [{
                "message": {
                    "content": null,
                    "tool_calls": [{
                        "id": "call_1",
                        "type": "function",
                        "function": {"name": "read_file", "arguments": "{\"path\":\"a.txt\"}"}
                    }]
                },
                "finish_reason": "tool_calls"
            }],
            "usage": {"prompt_tokens": 11, "completion_tokens": 7}
        }"#;
        let response = parse_completion(body).unwrap();
        assert_eq!(response.tool_calls.len(), 1);
        assert_eq!(response.tool_calls[0].name, "read_file");
        assert_eq!(response.usage.input_tokens, 11);
    }

    #[test]
    fn accumulates_stream_tool_call_deltas() {
        let mut content = String::new();
        let mut pending = BTreeMap::new();
        let mut finish = None;
        let mut usage = TokenUsage::default();
        let events = [
            r#"{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"write_file","arguments":"{\"path\":"}}]}}]}"#,
            r#"{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"a.txt\"}"}}]},"finish_reason":"tool_calls"}]}"#,
        ];
        for event in events {
            assert!(
                !process_sse_data(
                    event,
                    &mut content,
                    &mut pending,
                    &mut finish,
                    &mut usage,
                    None
                )
                .unwrap()
            );
        }
        let call = pending.get(&0).unwrap();
        assert_eq!(call.id, "call_1");
        assert_eq!(call.name, "write_file");
        assert_eq!(call.arguments, r#"{"path":"a.txt"}"#);
        assert_eq!(finish.as_deref(), Some("tool_calls"));
    }

    #[test]
    fn stops_on_done_marker() {
        let mut content = String::new();
        let mut pending = BTreeMap::new();
        let mut finish = None;
        let mut usage = TokenUsage::default();
        assert!(
            process_sse_data(
                "[DONE]",
                &mut content,
                &mut pending,
                &mut finish,
                &mut usage,
                None
            )
            .unwrap()
        );
    }
}
