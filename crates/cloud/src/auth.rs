use std::sync::Arc;

use argon2::{Argon2, PasswordHash, PasswordHasher, PasswordVerifier, password_hash::SaltString};
use axum::http::{HeaderMap, header::AUTHORIZATION};
use base64::{Engine, engine::general_purpose::URL_SAFE_NO_PAD};
use chrono::{Duration, Utc};
use coder_protocol::{DeviceId, UserId};
use jsonwebtoken::{Algorithm, DecodingKey, EncodingKey, Header, Validation, decode, encode};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::error::CloudError;

/// 管理员密钥请求头。
pub const ADMIN_KEY_HEADER: &str = "x-admin-key";
/// 设备短期令牌签发者。
const TOKEN_ISSUER: &str = "coder-cloud";

/// 使用随机盐计算 Argon2 哈希。
pub fn hash_secret(secret: &str) -> Result<String, CloudError> {
    let salt_bytes: [u8; 16] = rand::random();
    let salt = SaltString::encode_b64(&salt_bytes)
        .map_err(|err| CloudError::Internal(format!("cannot build password salt: {err}")))?;
    Argon2::default()
        .hash_password(secret.as_bytes(), &salt)
        .map(|hash| hash.to_string())
        .map_err(|err| CloudError::Internal(format!("cannot hash secret: {err}")))
}

/// 校验明文是否匹配 Argon2 哈希。
pub fn verify_secret(hash: &str, secret: &str) -> bool {
    let Ok(parsed) = PasswordHash::new(hash) else {
        return false;
    };
    Argon2::default()
        .verify_password(secret.as_bytes(), &parsed)
        .is_ok()
}

/// 计算用于索引查找的 SHA-256 十六进制摘要。
pub fn sha256_hex(value: &str) -> String {
    let mut hasher = Sha256::new();
    hasher.update(value.as_bytes());
    hex::encode(hasher.finalize())
}

/// 生成指定字节数的 URL-safe 随机密钥。
pub fn random_secret(bytes: usize) -> String {
    let mut buffer = vec![0u8; bytes];
    for chunk in buffer.chunks_mut(4) {
        let value = rand::random::<u32>().to_le_bytes();
        chunk.copy_from_slice(&value[..chunk.len()]);
    }
    URL_SAFE_NO_PAD.encode(buffer)
}

/// 设备令牌中的声明。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct DeviceClaims {
    pub device_id: DeviceId,
    pub user_id: UserId,
    pub iss: String,
    pub iat: usize,
    pub exp: usize,
}

/// 通过设备令牌认证后的身份。
#[derive(Clone, Copy, Debug)]
pub struct DevicePrincipal {
    pub device_id: DeviceId,
    pub user_id: UserId,
}

/// 设备短期令牌签发与校验。
#[derive(Clone)]
pub struct AuthService {
    secret: Arc<Vec<u8>>,
    issuer: String,
    ttl_secs: u64,
}

impl AuthService {
    /// 创建认证服务。
    pub fn new(secret: &str, ttl_secs: u64) -> Self {
        Self {
            secret: Arc::new(secret.as_bytes().to_vec()),
            issuer: TOKEN_ISSUER.to_owned(),
            ttl_secs,
        }
    }

    /// 设备令牌有效期。
    pub fn ttl_secs(&self) -> u64 {
        self.ttl_secs
    }

    /// 为设备签发短期令牌。
    pub fn issue_device_token(
        &self,
        device_id: DeviceId,
        user_id: UserId,
    ) -> Result<(String, u64), CloudError> {
        let now = Utc::now();
        let exp = now + Duration::seconds(self.ttl_secs as i64);
        let claims = DeviceClaims {
            device_id,
            user_id,
            iss: self.issuer.clone(),
            iat: now.timestamp() as usize,
            exp: exp.timestamp() as usize,
        };
        let token = encode(
            &Header::new(Algorithm::HS256),
            &claims,
            &EncodingKey::from_secret(self.secret.as_slice()),
        )
        .map_err(|err| CloudError::Internal(format!("cannot sign device token: {err}")))?;
        Ok((token, self.ttl_secs))
    }

    /// 校验设备令牌并返回身份。
    pub fn verify_device_token(&self, token: &str) -> Result<DevicePrincipal, CloudError> {
        let mut validation = Validation::new(Algorithm::HS256);
        validation.set_issuer(&[self.issuer.as_str()]);
        validation.set_required_spec_claims(&["exp", "iat"]);
        let claims = decode::<DeviceClaims>(
            token,
            &DecodingKey::from_secret(self.secret.as_slice()),
            &validation,
        )
        .map_err(|_| CloudError::Unauthorized("invalid device access token".into()))?
        .claims;
        Ok(DevicePrincipal {
            device_id: claims.device_id,
            user_id: claims.user_id,
        })
    }
}

/// 从请求头提取 Bearer 令牌。
pub fn bearer_token(headers: &HeaderMap) -> Result<&str, CloudError> {
    let value = headers
        .get(AUTHORIZATION)
        .and_then(|value| value.to_str().ok())
        .ok_or_else(|| CloudError::Unauthorized("missing Authorization header".into()))?;
    let token = value
        .strip_prefix("Bearer ")
        .or_else(|| value.strip_prefix("bearer "))
        .map(str::trim)
        .filter(|token| !token.is_empty())
        .ok_or_else(|| CloudError::Unauthorized("expected a Bearer token".into()))?;
    Ok(token)
}

/// 从请求头提取管理员密钥。
pub fn admin_key(headers: &HeaderMap) -> Result<&str, CloudError> {
    headers
        .get(ADMIN_KEY_HEADER)
        .and_then(|value| value.to_str().ok())
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| CloudError::Unauthorized("missing admin key".into()))
}

/// 校验管理员密钥；长度不同时也会执行常数时间比较。
pub fn verify_admin_key(configured: &str, provided: &str) -> bool {
    use subtle::ConstantTimeEq;
    configured.as_bytes().ct_eq(provided.as_bytes()).into()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hashes_and_verifies_secrets() {
        let hash = hash_secret("super-secret").unwrap();
        assert!(verify_secret(&hash, "super-secret"));
        assert!(!verify_secret(&hash, "wrong-secret"));
    }

    #[test]
    fn signs_and_verifies_device_tokens() {
        let auth = AuthService::new(&"s".repeat(32), 900);
        let device_id = DeviceId::new();
        let user_id = UserId::new();
        let (token, ttl) = auth.issue_device_token(device_id, user_id).unwrap();
        assert_eq!(ttl, 900);
        let principal = auth.verify_device_token(&token).unwrap();
        assert_eq!(principal.device_id, device_id);
        assert_eq!(principal.user_id, user_id);
    }

    #[test]
    fn rejects_tampered_tokens() {
        let auth = AuthService::new(&"s".repeat(32), 900);
        let (token, _) = auth
            .issue_device_token(DeviceId::new(), UserId::new())
            .unwrap();
        let other = AuthService::new(&"t".repeat(32), 900);
        assert!(other.verify_device_token(&token).is_err());
    }

    #[test]
    fn random_secrets_are_unique() {
        assert_ne!(random_secret(32), random_secret(32));
    }
}
