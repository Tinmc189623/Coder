use std::time::Duration;

use reqwest::{
    Client,
    header::{AUTHORIZATION, CONTENT_TYPE, HeaderValue},
};
use tokio::sync::mpsc;
use tokio_util::sync::CancellationToken;

use super::{ChatRequest, ModelClient, ModelError, ModelResponse};
use crate::config::ModelCapabilities;

/// 短期访问令牌提供者，由本地 Agent 实现设备凭据刷新。
#[async_trait::async_trait]
pub trait AccessTokenProvider: Send + Sync {
    /// 获取访问令牌；`force_refresh` 用于收到 401 后强制刷新。
    async fn access_token(&self, force_refresh: bool) -> Result<String, ModelError>;
}

/// 通过 Coder Cloud 模型代理执行推理。
///
/// 该适配器只发送结构化对话数据，不向云端暴露本地文件系统或工具。
pub struct ProxyModel {
    client: Client,
    endpoint: String,
    model_name: String,
    capabilities: ModelCapabilities,
    tokens: std::sync::Arc<dyn AccessTokenProvider>,
    max_retries: u32,
}

impl ProxyModel {
    /// 构造云端代理模型适配器。
    pub fn new(
        server_url: &str,
        model_name: String,
        capabilities: ModelCapabilities,
        tokens: std::sync::Arc<dyn AccessTokenProvider>,
        timeout_secs: u64,
        max_retries: u32,
    ) -> Result<Self, ModelError> {
        let client = Client::builder()
            .timeout(Duration::from_secs(timeout_secs))
            .user_agent(concat!("coder/", env!("CARGO_PKG_VERSION")))
            .build()
            .map_err(|err| ModelError::Http(err.to_string()))?;
        let endpoint = format!("{}/api/v1/model/chat", server_url.trim_end_matches('/'));
        Ok(Self {
            client,
            endpoint,
            model_name,
            capabilities,
            tokens,
            max_retries,
        })
    }

    /// 使用指定令牌发送一次代理请求。
    async fn send_once(
        &self,
        request: &ChatRequest,
        token: &str,
        cancel: &CancellationToken,
    ) -> Result<reqwest::Response, ModelError> {
        let mut auth = HeaderValue::from_str(&format!("Bearer {token}"))
            .map_err(|err| ModelError::Http(format!("invalid access token: {err}")))?;
        auth.set_sensitive(true);
        tokio::select! {
            _ = cancel.cancelled() => Err(ModelError::Canceled),
            response = self.client
                .post(&self.endpoint)
                .header(AUTHORIZATION, auth)
                .header(CONTENT_TYPE, "application/json")
                .json(request)
                .send() => response.map_err(|err| ModelError::Http(err.to_string())),
        }
    }

    /// 发送代理请求，401 时强制刷新令牌并重试一次。
    async fn send(
        &self,
        request: &ChatRequest,
        cancel: &CancellationToken,
    ) -> Result<ModelResponse, ModelError> {
        let mut force_refresh = false;
        let mut delayed_retry_used = false;
        loop {
            let token = self.tokens.access_token(force_refresh).await?;
            let response = self.send_once(request, &token, cancel).await?;

            if response.status().as_u16() == 401 && !force_refresh {
                force_refresh = true;
                continue;
            }
            if !response.status().is_success() {
                let status = response.status().as_u16();
                let body = response.text().await.unwrap_or_default();
                if (status == 429 || status >= 500) && self.max_retries > 0 && !delayed_retry_used {
                    delayed_retry_used = true;
                    tokio::select! {
                        _ = cancel.cancelled() => return Err(ModelError::Canceled),
                        _ = tokio::time::sleep(Duration::from_millis(750)) => {}
                    }
                    continue;
                }
                return Err(ModelError::Status { status, body });
            }
            let text = tokio::select! {
                _ = cancel.cancelled() => return Err(ModelError::Canceled),
                text = response.text() => text.map_err(|err| ModelError::Http(err.to_string()))?,
            };
            return serde_json::from_str(&text)
                .map_err(|err| ModelError::Decode(format!("{err}; body={text}")));
        }
    }
}

#[async_trait::async_trait]
impl ModelClient for ProxyModel {
    fn model_name(&self) -> &str {
        &self.model_name
    }

    fn capabilities(&self) -> ModelCapabilities {
        self.capabilities.clone()
    }

    async fn complete(
        &self,
        mut request: ChatRequest,
        cancel: CancellationToken,
        _delta_tx: Option<mpsc::UnboundedSender<String>>,
    ) -> Result<ModelResponse, ModelError> {
        // 代理端点返回聚合结果，因此强制关闭流式标志。
        request.stream = false;
        self.send(&request, &cancel).await
    }
}
