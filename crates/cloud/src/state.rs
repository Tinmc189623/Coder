use std::{
    sync::Arc,
    time::{Duration, Instant},
};

use axum::http::HeaderMap;
use chrono::{DateTime, Utc};
use coder_core::model::OpenAiModel;
use dashmap::DashMap;
use metrics_exporter_prometheus::PrometheusHandle;

use crate::{
    auth::{
        AuthService, DevicePrincipal, admin_key, bearer_token, sha256_hex, verify_admin_key,
        verify_secret,
    },
    config::CloudConfig,
    error::CloudError,
    hub::Hub,
    store::{CloudStore, UserRecord},
};

/// 每个密钥的令牌桶。
struct Bucket {
    tokens: f64,
    updated_at: Instant,
}

/// 按调用方限速的令牌桶集合。
pub struct RateLimiter {
    buckets: DashMap<String, Bucket>,
    rate_per_minute: f64,
}

impl RateLimiter {
    /// 创建限速器。
    pub fn new(rate_per_minute: u32) -> Self {
        Self {
            buckets: DashMap::new(),
            rate_per_minute: f64::from(rate_per_minute.max(1)),
        }
    }

    /// 消费一个令牌；返回是否允许本次请求。
    pub fn allow(&self, key: &str) -> bool {
        let now = Instant::now();
        let per_second = self.rate_per_minute / 60.0;
        let mut bucket = self.buckets.entry(key.to_owned()).or_insert(Bucket {
            tokens: self.rate_per_minute,
            updated_at: now,
        });
        let elapsed = now.duration_since(bucket.updated_at).as_secs_f64();
        bucket.tokens = (bucket.tokens + elapsed * per_second).min(self.rate_per_minute);
        bucket.updated_at = now;
        if bucket.tokens >= 1.0 {
            bucket.tokens -= 1.0;
            true
        } else {
            false
        }
    }

    /// 清理长时间未使用的桶，避免内存随来源数量增长。
    pub fn prune(&self, idle: Duration) {
        let now = Instant::now();
        self.buckets
            .retain(|_, bucket| now.duration_since(bucket.updated_at) < idle);
    }

    /// 当前桶数量，供指标与测试使用。
    pub fn len(&self) -> usize {
        self.buckets.len()
    }

    /// 是否没有任何限流桶。
    pub fn is_empty(&self) -> bool {
        self.buckets.is_empty()
    }
}

/// 所有路由共享的应用状态。
#[derive(Clone)]
pub struct AppState {
    pub config: Arc<CloudConfig>,
    pub store: Arc<dyn CloudStore>,
    pub auth: Arc<AuthService>,
    pub hub: Arc<Hub>,
    pub limiter: Arc<RateLimiter>,
    pub model: Option<Arc<OpenAiModel>>,
    pub metrics: Option<PrometheusHandle>,
    pub started_at: DateTime<Utc>,
}

impl AppState {
    /// 组装应用状态。
    pub fn new(
        config: Arc<CloudConfig>,
        store: Arc<dyn CloudStore>,
        auth: Arc<AuthService>,
        hub: Arc<Hub>,
        model: Option<Arc<OpenAiModel>>,
        metrics: Option<PrometheusHandle>,
    ) -> Self {
        let limiter = Arc::new(RateLimiter::new(config.rate_limit_per_minute));
        Self {
            config,
            store,
            auth,
            hub,
            limiter,
            model,
            metrics,
            started_at: Utc::now(),
        }
    }

    /// 校验管理员请求头。
    pub fn authenticate_admin(&self, headers: &HeaderMap) -> Result<(), CloudError> {
        let provided = admin_key(headers)?;
        if verify_admin_key(self.config.admin_api_key.expose(), provided) {
            Ok(())
        } else {
            Err(CloudError::Unauthorized("invalid admin key".into()))
        }
    }

    /// 通过 API key 认证用户；哈希校验在工作线程执行。
    pub async fn authenticate_api_key(&self, token: &str) -> Result<UserRecord, CloudError> {
        if token.len() > 512 {
            return Err(CloudError::Unauthorized("invalid API key".into()));
        }
        let lookup = sha256_hex(token);
        let user = self
            .store
            .find_user_by_api_lookup(&lookup)
            .await?
            .ok_or_else(|| CloudError::Unauthorized("invalid API key".into()))?;
        let hash = user.api_key_hash.clone();
        let candidate = token.to_owned();
        let valid = tokio::task::spawn_blocking(move || verify_secret(&hash, &candidate))
            .await
            .map_err(|err| CloudError::Internal(format!("hash worker failed: {err}")))?;
        if !valid {
            return Err(CloudError::Unauthorized("invalid API key".into()));
        }
        Ok(user)
    }

    /// 认证已注册且未撤销的设备令牌。
    pub async fn authenticate_device(
        &self,
        headers: &HeaderMap,
    ) -> Result<DevicePrincipal, CloudError> {
        let token = bearer_token(headers)?;
        let principal = self.auth.verify_device_token(token)?;
        let device = self
            .store
            .get_device(principal.device_id)
            .await?
            .ok_or_else(|| CloudError::Unauthorized("device is not registered".into()))?;
        if device.revoked {
            return Err(CloudError::Unauthorized("device has been revoked".into()));
        }
        if device.user_id != principal.user_id {
            return Err(CloudError::Unauthorized(
                "device token does not match its owner".into(),
            ));
        }
        Ok(principal)
    }

    /// 判断存储是否可用。
    pub async fn ready(&self) -> bool {
        self.store.ping().await.is_ok()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn limiter_allows_burst_then_blocks() {
        let limiter = RateLimiter::new(60);
        assert!(limiter.allow("device-a"));
        assert!(limiter.allow("device-a"));
        let mut allowed = 0;
        for _ in 0..120 {
            if limiter.allow("device-a") {
                allowed += 1;
            }
        }
        assert!(allowed < 120);
        assert!(limiter.allow("device-b"));
    }

    #[test]
    fn limiter_prunes_idle_buckets() {
        let limiter = RateLimiter::new(60);
        assert!(limiter.allow("device-a"));
        limiter.prune(Duration::ZERO);
        assert_eq!(limiter.len(), 0);
    }
}
