use axum::{
    Json,
    http::{
        StatusCode,
        header::{RETRY_AFTER, WWW_AUTHENTICATE},
    },
    response::{IntoResponse, Response},
};
use coder_core::model::ModelError;
use coder_protocol::{ApiErrorBody, ErrorCode};
use thiserror::Error;
use tracing::error;

use crate::store::StoreError;

/// 云端统一错误。
#[derive(Debug, Error)]
pub enum CloudError {
    #[error("configuration error: {0}")]
    Config(String),
    #[error("unauthorized: {0}")]
    Unauthorized(String),
    #[error("forbidden: {0}")]
    Forbidden(String),
    #[error("not found: {0}")]
    NotFound(String),
    #[error("conflict: {0}")]
    Conflict(String),
    #[error("invalid request: {0}")]
    Invalid(String),
    #[error("rate limit exceeded")]
    RateLimited,
    #[error("service unavailable: {0}")]
    Unavailable(String),
    #[error("store error: {0}")]
    Store(#[from] StoreError),
    #[error("model error: {0}")]
    Model(#[from] ModelError),
    #[error("internal error: {0}")]
    Internal(String),
}

impl CloudError {
    /// HTTP 状态码与协议错误码。
    fn classify(&self) -> (StatusCode, ErrorCode, String) {
        match self {
            Self::Config(message) => (
                StatusCode::INTERNAL_SERVER_ERROR,
                ErrorCode::Internal,
                message.clone(),
            ),
            Self::Unauthorized(message) => (
                StatusCode::UNAUTHORIZED,
                ErrorCode::Unauthorized,
                message.clone(),
            ),
            Self::Forbidden(message) => {
                (StatusCode::FORBIDDEN, ErrorCode::Forbidden, message.clone())
            }
            Self::NotFound(message) => (
                StatusCode::NOT_FOUND,
                ErrorCode::TaskNotFound,
                message.clone(),
            ),
            Self::Conflict(message) => (
                StatusCode::CONFLICT,
                ErrorCode::TaskConflict,
                message.clone(),
            ),
            Self::Invalid(message) => (
                StatusCode::BAD_REQUEST,
                ErrorCode::InvalidRequest,
                message.clone(),
            ),
            Self::RateLimited => (
                StatusCode::TOO_MANY_REQUESTS,
                ErrorCode::RateLimited,
                "too many requests; retry later".into(),
            ),
            Self::Unavailable(message) => (
                StatusCode::SERVICE_UNAVAILABLE,
                ErrorCode::Unavailable,
                message.clone(),
            ),
            Self::Store(StoreError::NotFound(message)) => (
                StatusCode::NOT_FOUND,
                ErrorCode::TaskNotFound,
                message.clone(),
            ),
            Self::Store(StoreError::Conflict(message)) => (
                StatusCode::CONFLICT,
                ErrorCode::TaskConflict,
                message.clone(),
            ),
            Self::Store(err @ (StoreError::Database(_) | StoreError::Corrupt(_))) => {
                error!(error = %err, "storage failure");
                (
                    StatusCode::INTERNAL_SERVER_ERROR,
                    ErrorCode::Internal,
                    "internal storage error".into(),
                )
            }
            Self::Model(err) => {
                let message = match err {
                    ModelError::NotConfigured(_) => "cloud model proxy is not configured",
                    ModelError::Unsupported(_) => "cloud model proxy does not support this request",
                    _ => "upstream model request failed",
                };
                error!(error = %err, "model proxy failure");
                (
                    StatusCode::BAD_GATEWAY,
                    ErrorCode::Unavailable,
                    message.into(),
                )
            }
            Self::Internal(message) => {
                error!(error = %message, "internal server error");
                (
                    StatusCode::INTERNAL_SERVER_ERROR,
                    ErrorCode::Internal,
                    "internal server error".into(),
                )
            }
        }
    }
}

impl IntoResponse for CloudError {
    /// 转换为统一 JSON 错误响应。
    fn into_response(self) -> Response {
        let (status, code, message) = self.classify();
        let mut response = (status, Json(ApiErrorBody::new(code, message, None))).into_response();
        if status == StatusCode::UNAUTHORIZED {
            response.headers_mut().insert(
                WWW_AUTHENTICATE,
                "Bearer".parse().expect("static header value"),
            );
        }
        if status == StatusCode::TOO_MANY_REQUESTS {
            response
                .headers_mut()
                .insert(RETRY_AFTER, "5".parse().expect("static header value"));
        }
        response
    }
}

impl From<serde_json::Error> for CloudError {
    /// JSON 编解码失败按内部错误处理。
    fn from(value: serde_json::Error) -> Self {
        Self::Internal(value.to_string())
    }
}
