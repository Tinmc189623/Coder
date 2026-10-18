//! 云端持久化抽象：内存实现用于测试与本地开发，PostgreSQL 用于生产。

use async_trait::async_trait;
use chrono::{DateTime, Utc};
use coder_protocol::{
    DeviceId, DeviceView, RequestId, RiskLevel, SessionId, TaskAssignment, TaskId, TaskLimits,
    TaskStatus, TaskView, UserId,
};
use thiserror::Error;

pub mod memory;
pub mod postgres;

pub use memory::MemoryStore;
pub use postgres::PostgresStore;

/// 存储层错误。
#[derive(Debug, Error)]
pub enum StoreError {
    #[error("record not found: {0}")]
    NotFound(String),
    #[error("conflict: {0}")]
    Conflict(String),
    #[error("database error: {0}")]
    Database(String),
    #[error("stored value is invalid: {0}")]
    Corrupt(String),
}

impl From<sqlx::Error> for StoreError {
    /// 将 SQLx 错误映射为存储错误，并识别唯一约束冲突。
    fn from(value: sqlx::Error) -> Self {
        if let sqlx::Error::Database(database) = &value
            && database.code().as_deref() == Some("23505")
        {
            return Self::Conflict(database.message().to_owned());
        }
        Self::Database(value.to_string())
    }
}

/// 用户记录。
#[derive(Clone, Debug)]
pub struct UserRecord {
    pub id: UserId,
    pub email: String,
    pub api_key_hash: String,
    pub api_key_lookup: String,
    pub created_at: DateTime<Utc>,
}

/// 设备记录。`secret_hash` 只用于校验设备密钥。
#[derive(Clone, Debug)]
pub struct DeviceRecord {
    pub id: DeviceId,
    pub user_id: UserId,
    pub name: String,
    pub platform: String,
    pub agent_version: String,
    pub secret_hash: String,
    pub last_seen_at: Option<DateTime<Utc>>,
    pub created_at: DateTime<Utc>,
    pub revoked: bool,
}

impl DeviceRecord {
    /// 转换为对外视图。
    pub fn to_view(&self, online: bool) -> DeviceView {
        DeviceView {
            device_id: self.id,
            user_id: self.user_id,
            name: self.name.clone(),
            platform: self.platform.clone(),
            agent_version: self.agent_version.clone(),
            online,
            last_seen_at: self.last_seen_at,
            created_at: self.created_at,
        }
    }
}

/// 任务记录。
#[derive(Clone, Debug)]
pub struct TaskRecord {
    pub id: TaskId,
    pub device_id: DeviceId,
    pub user_id: UserId,
    pub prompt: String,
    pub workspace: String,
    pub status: TaskStatus,
    pub attempt: u32,
    pub summary: Option<String>,
    pub error: Option<String>,
    pub files_changed: Vec<String>,
    pub limits: TaskLimits,
    pub cancel_requested: bool,
    pub session_id: Option<SessionId>,
    pub created_at: DateTime<Utc>,
    pub updated_at: DateTime<Utc>,
    pub lease_expires_at: Option<DateTime<Utc>>,
}

impl TaskRecord {
    /// 构造下发给本地 Agent 的任务分配。
    pub fn to_assignment(&self, lease_expires_at: DateTime<Utc>) -> TaskAssignment {
        TaskAssignment {
            task_id: self.id,
            attempt: self.attempt,
            prompt: self.prompt.clone(),
            workspace: self.workspace.clone(),
            limits: self.limits.clone(),
            lease_expires_at,
            created_at: self.created_at,
        }
    }

    /// 转换为 HTTP 任务视图。
    pub fn to_view(&self) -> TaskView {
        TaskView {
            task_id: self.id,
            device_id: self.device_id,
            user_id: self.user_id,
            prompt: self.prompt.clone(),
            workspace: self.workspace.clone(),
            status: self.status,
            attempt: self.attempt,
            summary: self.summary.clone(),
            error: self.error.clone(),
            files_changed: self.files_changed.clone(),
            created_at: self.created_at,
            updated_at: self.updated_at,
            lease_expires_at: self.lease_expires_at,
            limits: self.limits.clone(),
        }
    }
}

/// 新任务输入。
#[derive(Clone, Debug)]
pub struct NewTask {
    pub device_id: DeviceId,
    pub user_id: UserId,
    pub prompt: String,
    pub workspace: String,
    pub limits: TaskLimits,
}

/// Agent 上报的终态结果。
#[derive(Clone, Debug)]
pub struct TaskResultData {
    pub status: TaskStatus,
    pub summary: String,
    pub error: Option<String>,
    pub files_changed: Vec<String>,
    pub session_id: Option<SessionId>,
}

/// 权限请求记录。
#[derive(Clone, Debug)]
pub struct PermissionRecord {
    pub request_id: RequestId,
    pub task_id: TaskId,
    pub user_id: UserId,
    pub tool: String,
    pub action: String,
    pub details: String,
    pub risk: RiskLevel,
    pub granted: Option<bool>,
    pub reason: Option<String>,
    pub created_at: DateTime<Utc>,
    pub resolved_at: Option<DateTime<Utc>>,
}

impl PermissionRecord {
    /// 转换为待处理权限视图。
    pub fn to_pending_view(&self) -> coder_protocol::PendingPermission {
        coder_protocol::PendingPermission {
            task_id: self.task_id,
            request_id: self.request_id,
            tool: self.tool.clone(),
            action: self.action.clone(),
            details: self.details.clone(),
            risk: self.risk,
            created_at: self.created_at,
        }
    }
}

/// 云端存储接口。
#[async_trait]
pub trait CloudStore: Send + Sync {
    /// 创建用户。
    async fn create_user(
        &self,
        email: &str,
        api_key_hash: &str,
        api_key_lookup: &str,
    ) -> Result<UserRecord, StoreError>;

    /// 通过 API key 查找索引获取用户。
    async fn find_user_by_api_lookup(&self, lookup: &str)
    -> Result<Option<UserRecord>, StoreError>;

    /// 按 ID 获取用户。
    async fn get_user(&self, id: UserId) -> Result<Option<UserRecord>, StoreError>;

    /// 保存一次性配对令牌。
    async fn create_pairing_token(
        &self,
        user_id: UserId,
        token_hash: &str,
        expires_at: DateTime<Utc>,
    ) -> Result<(), StoreError>;

    /// 原子消费配对令牌，返回绑定的用户。
    async fn consume_pairing_token(
        &self,
        token_hash: &str,
        now: DateTime<Utc>,
    ) -> Result<Option<UserId>, StoreError>;

    /// 创建设备。
    #[allow(clippy::too_many_arguments)]
    async fn create_device(
        &self,
        user_id: UserId,
        name: &str,
        platform: &str,
        agent_version: &str,
        secret_hash: &str,
    ) -> Result<DeviceRecord, StoreError>;

    /// 按 ID 获取设备。
    async fn get_device(&self, id: DeviceId) -> Result<Option<DeviceRecord>, StoreError>;

    /// 列出用户的设备。
    async fn list_devices(&self, user_id: UserId) -> Result<Vec<DeviceRecord>, StoreError>;

    /// 更新设备最后在线时间。
    async fn touch_device(&self, id: DeviceId, now: DateTime<Utc>) -> Result<(), StoreError>;

    /// 创建任务。
    async fn create_task(&self, task: NewTask) -> Result<TaskRecord, StoreError>;

    /// 按 ID 获取任务。
    async fn get_task(&self, id: TaskId) -> Result<Option<TaskRecord>, StoreError>;

    /// 列出用户任务。
    async fn list_user_tasks(
        &self,
        user_id: UserId,
        limit: i64,
    ) -> Result<Vec<TaskRecord>, StoreError>;

    /// 列出设备上尚未结束的任务。
    async fn list_device_active_tasks(
        &self,
        device_id: DeviceId,
    ) -> Result<Vec<TaskRecord>, StoreError>;

    /// 标记任务已分配给设备。
    async fn assign_task(
        &self,
        id: TaskId,
        attempt: u32,
        lease_expires_at: DateTime<Utc>,
    ) -> Result<TaskRecord, StoreError>;

    /// 标记任务开始执行。
    async fn start_task(
        &self,
        id: TaskId,
        attempt: u32,
        lease_expires_at: DateTime<Utc>,
    ) -> Result<TaskRecord, StoreError>;

    /// 延长设备上活动任务的租约。
    async fn extend_leases(
        &self,
        device_id: DeviceId,
        until: DateTime<Utc>,
    ) -> Result<u64, StoreError>;

    /// 幂等记录终态结果。
    async fn record_result(
        &self,
        id: TaskId,
        attempt: u32,
        result: TaskResultData,
    ) -> Result<TaskRecord, StoreError>;

    /// 标记任务被拒绝。
    async fn reject_task(
        &self,
        id: TaskId,
        attempt: u32,
        reason: &str,
    ) -> Result<TaskRecord, StoreError>;

    /// 标记任务结果未知。
    async fn mark_unknown(
        &self,
        id: TaskId,
        attempt: u32,
        reason: &str,
    ) -> Result<TaskRecord, StoreError>;

    /// 请求取消任务；未下发的任务直接终止。
    async fn request_cancel(&self, id: TaskId) -> Result<TaskRecord, StoreError>;

    /// 以新的 attempt 重试终态任务。
    async fn retry_task(&self, id: TaskId) -> Result<TaskRecord, StoreError>;

    /// 将租约过期的活动任务标记为 unknown。
    async fn expire_leases(&self, now: DateTime<Utc>) -> Result<Vec<TaskId>, StoreError>;

    /// 保存权限请求。
    async fn create_permission(&self, permission: PermissionRecord) -> Result<(), StoreError>;

    /// 查询权限请求。
    async fn get_permission(
        &self,
        request_id: RequestId,
    ) -> Result<Option<PermissionRecord>, StoreError>;

    /// 批准或拒绝权限请求，返回更新后的记录。
    async fn resolve_permission(
        &self,
        request_id: RequestId,
        granted: bool,
        reason: Option<&str>,
    ) -> Result<Option<PermissionRecord>, StoreError>;

    /// 列出任务下未处理的权限请求。
    async fn list_pending_permissions(
        &self,
        task_id: TaskId,
    ) -> Result<Vec<PermissionRecord>, StoreError>;

    /// 关闭任务下所有未处理权限请求。
    async fn close_task_permissions(&self, task_id: TaskId) -> Result<(), StoreError>;

    /// 轻量健康检查，用于就绪探针。
    async fn ping(&self) -> Result<(), StoreError> {
        Ok(())
    }
}

/// 活动任务状态集合。
pub fn is_active_status(status: TaskStatus) -> bool {
    matches!(
        status,
        TaskStatus::Pending | TaskStatus::Assigned | TaskStatus::Running
    )
}
