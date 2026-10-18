use std::{
    collections::HashMap,
    sync::{RwLock, RwLockReadGuard, RwLockWriteGuard},
};

use async_trait::async_trait;
use chrono::{DateTime, Utc};
use coder_protocol::{DeviceId, RequestId, TaskId, TaskStatus, UserId};

use super::{
    CloudStore, DeviceRecord, NewTask, PermissionRecord, StoreError, TaskRecord, TaskResultData,
    UserRecord, is_active_status,
};

/// 配对令牌内部记录。
struct PairingToken {
    user_id: UserId,
    expires_at: DateTime<Utc>,
    used_at: Option<DateTime<Utc>>,
}

/// 内存数据集合。
#[derive(Default)]
struct Inner {
    users: HashMap<UserId, UserRecord>,
    users_by_lookup: HashMap<String, UserId>,
    pairing: HashMap<String, PairingToken>,
    devices: HashMap<DeviceId, DeviceRecord>,
    tasks: HashMap<TaskId, TaskRecord>,
    permissions: HashMap<RequestId, PermissionRecord>,
}

/// 内存存储，用于测试、本地开发与单实例演示。
///
/// 所有操作在单个 `RwLock` 下保持原子性；生产环境应使用 PostgreSQL 实现。
#[derive(Default)]
pub struct MemoryStore {
    inner: RwLock<Inner>,
}

impl MemoryStore {
    /// 创建空存储。
    pub fn new() -> Self {
        Self::default()
    }

    /// 获取读锁。
    fn read(&self) -> RwLockReadGuard<'_, Inner> {
        self.inner
            .read()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// 获取写锁。
    fn write(&self) -> RwLockWriteGuard<'_, Inner> {
        self.inner
            .write()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    }
}

#[async_trait]
impl CloudStore for MemoryStore {
    /// 创建用户。
    async fn create_user(
        &self,
        email: &str,
        api_key_hash: &str,
        api_key_lookup: &str,
    ) -> Result<UserRecord, StoreError> {
        let mut inner = self.write();
        if inner.users.values().any(|user| user.email == email) {
            return Err(StoreError::Conflict("email already exists".into()));
        }
        if inner.users_by_lookup.contains_key(api_key_lookup) {
            return Err(StoreError::Conflict("api key already exists".into()));
        }
        let record = UserRecord {
            id: UserId::new(),
            email: email.to_owned(),
            api_key_hash: api_key_hash.to_owned(),
            api_key_lookup: api_key_lookup.to_owned(),
            created_at: Utc::now(),
        };
        inner.users.insert(record.id, record.clone());
        inner
            .users_by_lookup
            .insert(api_key_lookup.to_owned(), record.id);
        Ok(record)
    }

    /// 通过 API key 索引查找用户。
    async fn find_user_by_api_lookup(
        &self,
        lookup: &str,
    ) -> Result<Option<UserRecord>, StoreError> {
        let inner = self.read();
        Ok(inner
            .users_by_lookup
            .get(lookup)
            .and_then(|id| inner.users.get(id))
            .cloned())
    }

    /// 按 ID 查询用户。
    async fn get_user(&self, id: UserId) -> Result<Option<UserRecord>, StoreError> {
        Ok(self.read().users.get(&id).cloned())
    }

    /// 保存配对令牌。
    async fn create_pairing_token(
        &self,
        user_id: UserId,
        token_hash: &str,
        expires_at: DateTime<Utc>,
    ) -> Result<(), StoreError> {
        let mut inner = self.write();
        if !inner.users.contains_key(&user_id) {
            return Err(StoreError::NotFound("user".into()));
        }
        inner.pairing.insert(
            token_hash.to_owned(),
            PairingToken {
                user_id,
                expires_at,
                used_at: None,
            },
        );
        Ok(())
    }

    /// 原子消费配对令牌。
    async fn consume_pairing_token(
        &self,
        token_hash: &str,
        now: DateTime<Utc>,
    ) -> Result<Option<UserId>, StoreError> {
        let mut inner = self.write();
        let Some(token) = inner.pairing.get_mut(token_hash) else {
            return Ok(None);
        };
        if token.used_at.is_some() || token.expires_at <= now {
            return Ok(None);
        }
        token.used_at = Some(now);
        Ok(Some(token.user_id))
    }

    /// 创建设备。
    async fn create_device(
        &self,
        user_id: UserId,
        name: &str,
        platform: &str,
        agent_version: &str,
        secret_hash: &str,
    ) -> Result<DeviceRecord, StoreError> {
        let mut inner = self.write();
        if !inner.users.contains_key(&user_id) {
            return Err(StoreError::NotFound("user".into()));
        }
        let record = DeviceRecord {
            id: DeviceId::new(),
            user_id,
            name: name.to_owned(),
            platform: platform.to_owned(),
            agent_version: agent_version.to_owned(),
            secret_hash: secret_hash.to_owned(),
            last_seen_at: None,
            created_at: Utc::now(),
            revoked: false,
        };
        inner.devices.insert(record.id, record.clone());
        Ok(record)
    }

    /// 按 ID 查询设备。
    async fn get_device(&self, id: DeviceId) -> Result<Option<DeviceRecord>, StoreError> {
        Ok(self.read().devices.get(&id).cloned())
    }

    /// 列出用户设备。
    async fn list_devices(&self, user_id: UserId) -> Result<Vec<DeviceRecord>, StoreError> {
        let inner = self.read();
        let mut devices: Vec<_> = inner
            .devices
            .values()
            .filter(|device| device.user_id == user_id)
            .cloned()
            .collect();
        devices.sort_by_key(|device| std::cmp::Reverse(device.created_at));
        Ok(devices)
    }

    /// 更新设备最后在线时间。
    async fn touch_device(&self, id: DeviceId, now: DateTime<Utc>) -> Result<(), StoreError> {
        let mut inner = self.write();
        if let Some(device) = inner.devices.get_mut(&id) {
            device.last_seen_at = Some(now);
        }
        Ok(())
    }

    /// 创建任务。
    async fn create_task(&self, task: NewTask) -> Result<TaskRecord, StoreError> {
        let mut inner = self.write();
        let Some(device) = inner.devices.get(&task.device_id) else {
            return Err(StoreError::NotFound("device".into()));
        };
        if device.user_id != task.user_id {
            return Err(StoreError::Conflict(
                "device does not belong to the requesting user".into(),
            ));
        }
        let now = Utc::now();
        let record = TaskRecord {
            id: TaskId::new(),
            device_id: task.device_id,
            user_id: task.user_id,
            prompt: task.prompt,
            workspace: task.workspace,
            status: TaskStatus::Pending,
            attempt: 0,
            summary: None,
            error: None,
            files_changed: Vec::new(),
            limits: task.limits,
            cancel_requested: false,
            session_id: None,
            created_at: now,
            updated_at: now,
            lease_expires_at: None,
        };
        inner.tasks.insert(record.id, record.clone());
        Ok(record)
    }

    /// 按 ID 查询任务。
    async fn get_task(&self, id: TaskId) -> Result<Option<TaskRecord>, StoreError> {
        Ok(self.read().tasks.get(&id).cloned())
    }

    /// 列出用户任务。
    async fn list_user_tasks(
        &self,
        user_id: UserId,
        limit: i64,
    ) -> Result<Vec<TaskRecord>, StoreError> {
        let inner = self.read();
        let mut tasks: Vec<_> = inner
            .tasks
            .values()
            .filter(|task| task.user_id == user_id)
            .cloned()
            .collect();
        tasks.sort_by_key(|task| std::cmp::Reverse(task.created_at));
        tasks.truncate(usize::try_from(limit.max(1)).unwrap_or(usize::MAX));
        Ok(tasks)
    }

    /// 列出设备活动任务。
    async fn list_device_active_tasks(
        &self,
        device_id: DeviceId,
    ) -> Result<Vec<TaskRecord>, StoreError> {
        let inner = self.read();
        let mut tasks: Vec<_> = inner
            .tasks
            .values()
            .filter(|task| task.device_id == device_id && is_active_status(task.status))
            .cloned()
            .collect();
        tasks.sort_by_key(|task| task.created_at);
        Ok(tasks)
    }

    /// 标记任务已分配。
    async fn assign_task(
        &self,
        id: TaskId,
        attempt: u32,
        lease_expires_at: DateTime<Utc>,
    ) -> Result<TaskRecord, StoreError> {
        let mut inner = self.write();
        let task = inner
            .tasks
            .get_mut(&id)
            .ok_or_else(|| StoreError::NotFound("task".into()))?;
        if task.attempt != attempt {
            return Err(StoreError::Conflict("attempt mismatch".into()));
        }
        if !is_active_status(task.status) {
            return Err(StoreError::Conflict("task is already terminal".into()));
        }
        if task.status == TaskStatus::Pending {
            task.status = TaskStatus::Assigned;
        }
        task.lease_expires_at = Some(lease_expires_at);
        task.updated_at = Utc::now();
        Ok(task.clone())
    }

    /// 标记任务开始执行。
    async fn start_task(
        &self,
        id: TaskId,
        attempt: u32,
        lease_expires_at: DateTime<Utc>,
    ) -> Result<TaskRecord, StoreError> {
        let mut inner = self.write();
        let task = inner
            .tasks
            .get_mut(&id)
            .ok_or_else(|| StoreError::NotFound("task".into()))?;
        if task.attempt != attempt {
            return Err(StoreError::Conflict("attempt mismatch".into()));
        }
        if task.status.is_terminal() {
            return Err(StoreError::Conflict("task is already terminal".into()));
        }
        task.status = TaskStatus::Running;
        task.lease_expires_at = Some(lease_expires_at);
        task.updated_at = Utc::now();
        Ok(task.clone())
    }

    /// 延长活动任务租约。
    async fn extend_leases(
        &self,
        device_id: DeviceId,
        until: DateTime<Utc>,
    ) -> Result<u64, StoreError> {
        let mut inner = self.write();
        let mut extended = 0;
        for task in inner.tasks.values_mut() {
            if task.device_id == device_id && is_active_status(task.status) {
                task.lease_expires_at = Some(until);
                extended += 1;
            }
        }
        Ok(extended)
    }

    /// 记录任务终态。
    async fn record_result(
        &self,
        id: TaskId,
        attempt: u32,
        result: TaskResultData,
    ) -> Result<TaskRecord, StoreError> {
        if !result.status.is_terminal() {
            return Err(StoreError::Conflict(
                "task result status must be terminal".into(),
            ));
        }
        let mut inner = self.write();
        let task = inner
            .tasks
            .get_mut(&id)
            .ok_or_else(|| StoreError::NotFound("task".into()))?;
        if task.attempt != attempt {
            return Err(StoreError::Conflict(format!(
                "attempt {attempt} does not match current attempt {}",
                task.attempt
            )));
        }
        let should_apply = !task.status.is_terminal()
            || (task.status == TaskStatus::Unknown && result.status != TaskStatus::Unknown);
        if should_apply {
            task.status = result.status;
            task.summary = Some(result.summary);
            task.error = result.error;
            task.files_changed = result.files_changed;
            task.session_id = result.session_id;
            task.lease_expires_at = None;
            task.updated_at = Utc::now();
        }
        let updated = task.clone();
        if should_apply {
            close_permissions(&mut inner, id);
        }
        Ok(updated)
    }

    /// 标记任务被拒绝。
    async fn reject_task(
        &self,
        id: TaskId,
        attempt: u32,
        reason: &str,
    ) -> Result<TaskRecord, StoreError> {
        let mut inner = self.write();
        let (updated, closed) = {
            let task = inner
                .tasks
                .get_mut(&id)
                .ok_or_else(|| StoreError::NotFound("task".into()))?;
            if task.attempt != attempt {
                return Err(StoreError::Conflict("attempt mismatch".into()));
            }
            let changed = !task.status.is_terminal();
            if changed {
                task.status = TaskStatus::Failed;
                task.summary = Some(format!("agent rejected the task: {reason}"));
                task.error = Some(reason.to_owned());
                task.lease_expires_at = None;
                task.updated_at = Utc::now();
            }
            (task.clone(), changed)
        };
        if closed {
            close_permissions(&mut inner, id);
        }
        Ok(updated)
    }

    /// 标记任务结果未知。
    async fn mark_unknown(
        &self,
        id: TaskId,
        attempt: u32,
        reason: &str,
    ) -> Result<TaskRecord, StoreError> {
        let mut inner = self.write();
        let (updated, closed) = {
            let task = inner
                .tasks
                .get_mut(&id)
                .ok_or_else(|| StoreError::NotFound("task".into()))?;
            if task.attempt != attempt {
                return Err(StoreError::Conflict("attempt mismatch".into()));
            }
            let changed = !task.status.is_terminal();
            if changed {
                task.status = TaskStatus::Unknown;
                task.summary = Some(reason.to_owned());
                task.error = Some(reason.to_owned());
                task.lease_expires_at = None;
                task.updated_at = Utc::now();
            }
            (task.clone(), changed)
        };
        if closed {
            close_permissions(&mut inner, id);
        }
        Ok(updated)
    }

    /// 请求取消任务。
    async fn request_cancel(&self, id: TaskId) -> Result<TaskRecord, StoreError> {
        let mut inner = self.write();
        let (updated, closed) = {
            let task = inner
                .tasks
                .get_mut(&id)
                .ok_or_else(|| StoreError::NotFound("task".into()))?;
            let mut closed = false;
            if !task.status.is_terminal() {
                task.cancel_requested = true;
                if task.status == TaskStatus::Pending {
                    task.status = TaskStatus::Canceled;
                    task.summary = Some("canceled before assignment".into());
                    task.lease_expires_at = None;
                    closed = true;
                }
                task.updated_at = Utc::now();
            }
            (task.clone(), closed)
        };
        if closed {
            close_permissions(&mut inner, id);
        }
        Ok(updated)
    }

    /// 以新 attempt 重试终态任务。
    async fn retry_task(&self, id: TaskId) -> Result<TaskRecord, StoreError> {
        let mut inner = self.write();
        let task = inner
            .tasks
            .get_mut(&id)
            .ok_or_else(|| StoreError::NotFound("task".into()))?;
        if !task.status.is_terminal() {
            return Err(StoreError::Conflict(
                "only terminal tasks can be retried".into(),
            ));
        }
        task.attempt = task.attempt.saturating_add(1);
        task.status = TaskStatus::Pending;
        task.summary = None;
        task.error = None;
        task.files_changed.clear();
        task.session_id = None;
        task.cancel_requested = false;
        task.lease_expires_at = None;
        task.updated_at = Utc::now();
        Ok(task.clone())
    }

    /// 过期租约转为 unknown。
    async fn expire_leases(&self, now: DateTime<Utc>) -> Result<Vec<TaskId>, StoreError> {
        let mut inner = self.write();
        let mut expired = Vec::new();
        for task in inner.tasks.values_mut() {
            if is_active_status(task.status)
                && task.lease_expires_at.is_some_and(|lease| lease <= now)
            {
                task.status = TaskStatus::Unknown;
                task.summary =
                    Some("agent lease expired before a terminal result was received".into());
                task.error =
                    Some("task outcome could not be confirmed before the lease expired".into());
                task.lease_expires_at = None;
                task.updated_at = now;
                expired.push(task.id);
            }
        }
        for task_id in &expired {
            close_permissions(&mut inner, *task_id);
        }
        Ok(expired)
    }

    /// 保存权限请求。
    async fn create_permission(&self, permission: PermissionRecord) -> Result<(), StoreError> {
        let mut inner = self.write();
        if inner.permissions.contains_key(&permission.request_id) {
            return Err(StoreError::Conflict("permission request exists".into()));
        }
        inner.permissions.insert(permission.request_id, permission);
        Ok(())
    }

    /// 查询权限请求。
    async fn get_permission(
        &self,
        request_id: RequestId,
    ) -> Result<Option<PermissionRecord>, StoreError> {
        Ok(self.read().permissions.get(&request_id).cloned())
    }

    /// 处理权限请求。
    async fn resolve_permission(
        &self,
        request_id: RequestId,
        granted: bool,
        reason: Option<&str>,
    ) -> Result<Option<PermissionRecord>, StoreError> {
        let mut inner = self.write();
        let Some(permission) = inner.permissions.get_mut(&request_id) else {
            return Ok(None);
        };
        if permission.granted.is_none() {
            permission.granted = Some(granted);
            permission.reason = reason.map(str::to_owned);
            permission.resolved_at = Some(Utc::now());
        }
        Ok(Some(permission.clone()))
    }

    /// 列出待处理权限。
    async fn list_pending_permissions(
        &self,
        task_id: TaskId,
    ) -> Result<Vec<PermissionRecord>, StoreError> {
        let inner = self.read();
        let mut permissions: Vec<_> = inner
            .permissions
            .values()
            .filter(|permission| permission.task_id == task_id && permission.granted.is_none())
            .cloned()
            .collect();
        permissions.sort_by_key(|permission| permission.created_at);
        Ok(permissions)
    }

    /// 关闭任务权限。
    async fn close_task_permissions(&self, task_id: TaskId) -> Result<(), StoreError> {
        let mut inner = self.write();
        close_permissions(&mut inner, task_id);
        Ok(())
    }
}

/// 将任务下未处理的权限请求标记为拒绝。
fn close_permissions(inner: &mut Inner, task_id: TaskId) {
    let now = Utc::now();
    for permission in inner.permissions.values_mut() {
        if permission.task_id == task_id && permission.granted.is_none() {
            permission.granted = Some(false);
            permission.reason = Some("task finished before approval".into());
            permission.resolved_at = Some(now);
        }
    }
}
