use std::{str::FromStr, time::Duration};

use async_trait::async_trait;
use chrono::{DateTime, Utc};
use coder_protocol::{
    DeviceId, RequestId, RiskLevel, SessionId, TaskId, TaskLimits, TaskStatus, UserId,
};
use sqlx::{
    AssertSqlSafe, PgPool, Row,
    postgres::{PgPoolOptions, PgRow},
};
use uuid::Uuid;

use super::{
    CloudStore, DeviceRecord, NewTask, PermissionRecord, StoreError, TaskRecord, TaskResultData,
    UserRecord,
};

const TASK_COLUMNS: &str = "id, device_id, user_id, prompt, workspace, status, attempt, summary, \
    error, files_changed, limits, cancel_requested, session_id, created_at, updated_at, \
    lease_expires_at";

const DEVICE_COLUMNS: &str = "id, user_id, name, platform, agent_version, secret_hash, \
    last_seen_at, created_at, revoked";

const PERMISSION_COLUMNS: &str = "request_id, task_id, user_id, tool, action, details, risk, \
    granted, reason, created_at, resolved_at";

/// PostgreSQL 存储。
#[derive(Clone)]
pub struct PostgresStore {
    pool: PgPool,
}

impl PostgresStore {
    /// 连接数据库并执行迁移。
    pub async fn connect(database_url: &str) -> Result<Self, StoreError> {
        let pool = PgPoolOptions::new()
            .max_connections(20)
            .min_connections(1)
            .acquire_timeout(Duration::from_secs(10))
            .connect(database_url)
            .await?;
        sqlx::migrate!("./migrations-postgres")
            .run(&pool)
            .await
            .map_err(|err| StoreError::Database(format!("migration failed: {err}")))?;
        Ok(Self { pool })
    }

    /// 读取任务，不存在时返回 `NotFound`。
    async fn task_required(&self, id: TaskId) -> Result<TaskRecord, StoreError> {
        self.get_task(id)
            .await?
            .ok_or_else(|| StoreError::NotFound("task".into()))
    }
}

/// 解析任务行。
fn row_to_task(row: &PgRow) -> Result<TaskRecord, StoreError> {
    let status_raw: String = row.try_get("status")?;
    let status = TaskStatus::from_str(&status_raw)
        .map_err(|err| StoreError::Corrupt(format!("invalid task status: {err}")))?;
    let files_value: serde_json::Value = row.try_get("files_changed")?;
    let files_changed: Vec<String> = serde_json::from_value(files_value)
        .map_err(|err| StoreError::Corrupt(format!("invalid files_changed: {err}")))?;
    let limits_value: serde_json::Value = row.try_get("limits")?;
    let limits: TaskLimits = serde_json::from_value(limits_value)
        .map_err(|err| StoreError::Corrupt(format!("invalid task limits: {err}")))?;
    let attempt_raw: i32 = row.try_get("attempt")?;
    let attempt = u32::try_from(attempt_raw)
        .map_err(|_| StoreError::Corrupt(format!("invalid task attempt {attempt_raw}")))?;
    let session_uuid: Option<Uuid> = row.try_get("session_id")?;
    Ok(TaskRecord {
        id: TaskId::from(row.try_get::<Uuid, _>("id")?),
        device_id: DeviceId::from(row.try_get::<Uuid, _>("device_id")?),
        user_id: UserId::from(row.try_get::<Uuid, _>("user_id")?),
        prompt: row.try_get("prompt")?,
        workspace: row.try_get("workspace")?,
        status,
        attempt,
        summary: row.try_get("summary")?,
        error: row.try_get("error")?,
        files_changed,
        limits,
        cancel_requested: row.try_get("cancel_requested")?,
        session_id: session_uuid.map(SessionId::from),
        created_at: row.try_get("created_at")?,
        updated_at: row.try_get("updated_at")?,
        lease_expires_at: row.try_get("lease_expires_at")?,
    })
}

/// 解析设备行。
fn row_to_device(row: &PgRow) -> Result<DeviceRecord, StoreError> {
    Ok(DeviceRecord {
        id: DeviceId::from(row.try_get::<Uuid, _>("id")?),
        user_id: UserId::from(row.try_get::<Uuid, _>("user_id")?),
        name: row.try_get("name")?,
        platform: row.try_get("platform")?,
        agent_version: row.try_get("agent_version")?,
        secret_hash: row.try_get("secret_hash")?,
        last_seen_at: row.try_get("last_seen_at")?,
        created_at: row.try_get("created_at")?,
        revoked: row.try_get("revoked")?,
    })
}

/// 解析权限行。
fn row_to_permission(row: &PgRow) -> Result<PermissionRecord, StoreError> {
    let risk_raw: String = row.try_get("risk")?;
    let risk = parse_risk(&risk_raw)?;
    Ok(PermissionRecord {
        request_id: RequestId::from(row.try_get::<Uuid, _>("request_id")?),
        task_id: TaskId::from(row.try_get::<Uuid, _>("task_id")?),
        user_id: UserId::from(row.try_get::<Uuid, _>("user_id")?),
        tool: row.try_get("tool")?,
        action: row.try_get("action")?,
        details: row.try_get("details")?,
        risk,
        granted: row.try_get("granted")?,
        reason: row.try_get("reason")?,
        created_at: row.try_get("created_at")?,
        resolved_at: row.try_get("resolved_at")?,
    })
}

/// 解析风险等级字符串。
fn parse_risk(raw: &str) -> Result<RiskLevel, StoreError> {
    match raw {
        "low" => Ok(RiskLevel::Low),
        "medium" => Ok(RiskLevel::Medium),
        "high" => Ok(RiskLevel::High),
        other => Err(StoreError::Corrupt(format!("invalid risk level `{other}`"))),
    }
}

/// 序列化任务字段，失败时按损坏数据处理。
fn json_error(context: &str, err: serde_json::Error) -> StoreError {
    StoreError::Corrupt(format!("{context}: {err}"))
}

#[async_trait]
impl CloudStore for PostgresStore {
    /// 创建用户。
    async fn create_user(
        &self,
        email: &str,
        api_key_hash: &str,
        api_key_lookup: &str,
    ) -> Result<UserRecord, StoreError> {
        let row = sqlx::query(
            "INSERT INTO users (id, email, api_key_hash, api_key_lookup, created_at) \
             VALUES ($1, $2, $3, $4, $5) RETURNING id, email, api_key_hash, api_key_lookup, created_at",
        )
        .bind(UserId::new().as_uuid())
        .bind(email)
        .bind(api_key_hash)
        .bind(api_key_lookup)
        .bind(Utc::now())
        .fetch_one(&self.pool)
        .await?;
        Ok(UserRecord {
            id: UserId::from(row.try_get::<Uuid, _>("id")?),
            email: row.try_get("email")?,
            api_key_hash: row.try_get("api_key_hash")?,
            api_key_lookup: row.try_get("api_key_lookup")?,
            created_at: row.try_get("created_at")?,
        })
    }

    /// 通过 API key 索引查找用户。
    async fn find_user_by_api_lookup(
        &self,
        lookup: &str,
    ) -> Result<Option<UserRecord>, StoreError> {
        let row = sqlx::query(
            "SELECT id, email, api_key_hash, api_key_lookup, created_at FROM users \
             WHERE api_key_lookup = $1",
        )
        .bind(lookup)
        .fetch_optional(&self.pool)
        .await?;
        row.map(|row| {
            Ok(UserRecord {
                id: UserId::from(row.try_get::<Uuid, _>("id")?),
                email: row.try_get("email")?,
                api_key_hash: row.try_get("api_key_hash")?,
                api_key_lookup: row.try_get("api_key_lookup")?,
                created_at: row.try_get("created_at")?,
            })
        })
        .transpose()
    }

    /// 按 ID 查询用户。
    async fn get_user(&self, id: UserId) -> Result<Option<UserRecord>, StoreError> {
        let row = sqlx::query(
            "SELECT id, email, api_key_hash, api_key_lookup, created_at FROM users WHERE id = $1",
        )
        .bind(id.as_uuid())
        .fetch_optional(&self.pool)
        .await?;
        row.map(|row| {
            Ok(UserRecord {
                id: UserId::from(row.try_get::<Uuid, _>("id")?),
                email: row.try_get("email")?,
                api_key_hash: row.try_get("api_key_hash")?,
                api_key_lookup: row.try_get("api_key_lookup")?,
                created_at: row.try_get("created_at")?,
            })
        })
        .transpose()
    }

    /// 保存配对令牌。
    async fn create_pairing_token(
        &self,
        user_id: UserId,
        token_hash: &str,
        expires_at: DateTime<Utc>,
    ) -> Result<(), StoreError> {
        sqlx::query(
            "INSERT INTO pairing_tokens (token_hash, user_id, expires_at, created_at) \
             VALUES ($1, $2, $3, $4)",
        )
        .bind(token_hash)
        .bind(user_id.as_uuid())
        .bind(expires_at)
        .bind(Utc::now())
        .execute(&self.pool)
        .await?;
        Ok(())
    }

    /// 原子消费配对令牌。
    async fn consume_pairing_token(
        &self,
        token_hash: &str,
        now: DateTime<Utc>,
    ) -> Result<Option<UserId>, StoreError> {
        let row = sqlx::query(
            "UPDATE pairing_tokens SET used_at = $2 \
             WHERE token_hash = $1 AND used_at IS NULL AND expires_at > $2 \
             RETURNING user_id",
        )
        .bind(token_hash)
        .bind(now)
        .fetch_optional(&self.pool)
        .await?;
        row.map(|row| {
            row.try_get::<Uuid, _>("user_id")
                .map(UserId::from)
                .map_err(StoreError::from)
        })
        .transpose()
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
        let row = sqlx::query(AssertSqlSafe(format!(
            "INSERT INTO devices (id, user_id, name, platform, agent_version, secret_hash, created_at) \
             VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING {DEVICE_COLUMNS}"
        )))
        .bind(DeviceId::new().as_uuid())
        .bind(user_id.as_uuid())
        .bind(name)
        .bind(platform)
        .bind(agent_version)
        .bind(secret_hash)
        .bind(Utc::now())
        .fetch_one(&self.pool)
        .await?;
        row_to_device(&row)
    }

    /// 按 ID 查询设备。
    async fn get_device(&self, id: DeviceId) -> Result<Option<DeviceRecord>, StoreError> {
        let row = sqlx::query(AssertSqlSafe(format!(
            "SELECT {DEVICE_COLUMNS} FROM devices WHERE id = $1"
        )))
        .bind(id.as_uuid())
        .fetch_optional(&self.pool)
        .await?;
        row.map(|row| row_to_device(&row)).transpose()
    }

    /// 列出用户设备。
    async fn list_devices(&self, user_id: UserId) -> Result<Vec<DeviceRecord>, StoreError> {
        let rows = sqlx::query(AssertSqlSafe(format!(
            "SELECT {DEVICE_COLUMNS} FROM devices WHERE user_id = $1 ORDER BY created_at DESC"
        )))
        .bind(user_id.as_uuid())
        .fetch_all(&self.pool)
        .await?;
        rows.iter().map(row_to_device).collect()
    }

    /// 更新设备最后在线时间。
    async fn touch_device(&self, id: DeviceId, now: DateTime<Utc>) -> Result<(), StoreError> {
        sqlx::query("UPDATE devices SET last_seen_at = $2 WHERE id = $1")
            .bind(id.as_uuid())
            .bind(now)
            .execute(&self.pool)
            .await?;
        Ok(())
    }

    /// 创建任务。
    async fn create_task(&self, task: NewTask) -> Result<TaskRecord, StoreError> {
        let owner: Option<Uuid> = sqlx::query_scalar("SELECT user_id FROM devices WHERE id = $1")
            .bind(task.device_id.as_uuid())
            .fetch_optional(&self.pool)
            .await?;
        match owner {
            None => return Err(StoreError::NotFound("device".into())),
            Some(owner) if owner != task.user_id.as_uuid() => {
                return Err(StoreError::Conflict(
                    "device does not belong to the requesting user".into(),
                ));
            }
            Some(_) => {}
        }
        let now = Utc::now();
        let limits = serde_json::to_value(&task.limits).map_err(|err| json_error("limits", err))?;
        let files = serde_json::json!([]);
        let row = sqlx::query(AssertSqlSafe(format!(
            "INSERT INTO tasks (id, device_id, user_id, prompt, workspace, status, attempt, \
             files_changed, limits, cancel_requested, created_at, updated_at) \
             VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, FALSE, $10, $10) \
             RETURNING {TASK_COLUMNS}"
        )))
        .bind(TaskId::new().as_uuid())
        .bind(task.device_id.as_uuid())
        .bind(task.user_id.as_uuid())
        .bind(&task.prompt)
        .bind(&task.workspace)
        .bind(TaskStatus::Pending.as_str())
        .bind(0i32)
        .bind(files)
        .bind(limits)
        .bind(now)
        .fetch_one(&self.pool)
        .await?;
        row_to_task(&row)
    }

    /// 按 ID 查询任务。
    async fn get_task(&self, id: TaskId) -> Result<Option<TaskRecord>, StoreError> {
        let row = sqlx::query(AssertSqlSafe(format!(
            "SELECT {TASK_COLUMNS} FROM tasks WHERE id = $1"
        )))
        .bind(id.as_uuid())
        .fetch_optional(&self.pool)
        .await?;
        row.map(|row| row_to_task(&row)).transpose()
    }

    /// 列出用户任务。
    async fn list_user_tasks(
        &self,
        user_id: UserId,
        limit: i64,
    ) -> Result<Vec<TaskRecord>, StoreError> {
        let rows = sqlx::query(AssertSqlSafe(format!(
            "SELECT {TASK_COLUMNS} FROM tasks WHERE user_id = $1 \
             ORDER BY created_at DESC LIMIT $2"
        )))
        .bind(user_id.as_uuid())
        .bind(limit.clamp(1, 500))
        .fetch_all(&self.pool)
        .await?;
        rows.iter().map(row_to_task).collect()
    }

    /// 列出设备活动任务。
    async fn list_device_active_tasks(
        &self,
        device_id: DeviceId,
    ) -> Result<Vec<TaskRecord>, StoreError> {
        let rows = sqlx::query(AssertSqlSafe(format!(
            "SELECT {TASK_COLUMNS} FROM tasks WHERE device_id = $1 \
             AND status IN ('pending', 'assigned', 'running') ORDER BY created_at ASC"
        )))
        .bind(device_id.as_uuid())
        .fetch_all(&self.pool)
        .await?;
        rows.iter().map(row_to_task).collect()
    }

    /// 标记任务已分配。
    async fn assign_task(
        &self,
        id: TaskId,
        attempt: u32,
        lease_expires_at: DateTime<Utc>,
    ) -> Result<TaskRecord, StoreError> {
        let attempt_i =
            i32::try_from(attempt).map_err(|_| StoreError::Corrupt("attempt overflow".into()))?;
        let row = sqlx::query(AssertSqlSafe(format!(
            "UPDATE tasks SET status = CASE WHEN status = 'pending' THEN 'assigned' ELSE status END, \
             lease_expires_at = $3, updated_at = $4 \
             WHERE id = $1 AND attempt = $2 AND status IN ('pending', 'assigned', 'running') \
             RETURNING {TASK_COLUMNS}"
        )))
        .bind(id.as_uuid())
        .bind(attempt_i)
        .bind(lease_expires_at)
        .bind(Utc::now())
        .fetch_optional(&self.pool)
        .await?;
        match row {
            Some(row) => row_to_task(&row),
            None => {
                let task = self.task_required(id).await?;
                if task.attempt != attempt {
                    return Err(StoreError::Conflict("attempt mismatch".into()));
                }
                Err(StoreError::Conflict("task is already terminal".into()))
            }
        }
    }

    /// 标记任务开始执行。
    async fn start_task(
        &self,
        id: TaskId,
        attempt: u32,
        lease_expires_at: DateTime<Utc>,
    ) -> Result<TaskRecord, StoreError> {
        let attempt_i =
            i32::try_from(attempt).map_err(|_| StoreError::Corrupt("attempt overflow".into()))?;
        let row = sqlx::query(AssertSqlSafe(format!(
            "UPDATE tasks SET status = 'running', lease_expires_at = $3, updated_at = $4 \
             WHERE id = $1 AND attempt = $2 AND status IN ('pending', 'assigned', 'running') \
             RETURNING {TASK_COLUMNS}"
        )))
        .bind(id.as_uuid())
        .bind(attempt_i)
        .bind(lease_expires_at)
        .bind(Utc::now())
        .fetch_optional(&self.pool)
        .await?;
        match row {
            Some(row) => row_to_task(&row),
            None => {
                let task = self.task_required(id).await?;
                if task.attempt != attempt {
                    return Err(StoreError::Conflict("attempt mismatch".into()));
                }
                Err(StoreError::Conflict("task is already terminal".into()))
            }
        }
    }

    /// 延长活动任务租约。
    async fn extend_leases(
        &self,
        device_id: DeviceId,
        until: DateTime<Utc>,
    ) -> Result<u64, StoreError> {
        let result = sqlx::query(
            "UPDATE tasks SET lease_expires_at = $2 \
             WHERE device_id = $1 AND status IN ('pending', 'assigned', 'running')",
        )
        .bind(device_id.as_uuid())
        .bind(until)
        .execute(&self.pool)
        .await?;
        Ok(result.rows_affected())
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
        let files = serde_json::to_value(&result.files_changed)
            .map_err(|err| json_error("files_changed", err))?;
        let now = Utc::now();
        let mut tx = self.pool.begin().await?;
        let row = sqlx::query(AssertSqlSafe(format!(
            "SELECT {TASK_COLUMNS} FROM tasks WHERE id = $1 FOR UPDATE"
        )))
        .bind(id.as_uuid())
        .fetch_optional(&mut *tx)
        .await?
        .ok_or_else(|| StoreError::NotFound("task".into()))?;
        let task = row_to_task(&row)?;
        if task.attempt != attempt {
            return Err(StoreError::Conflict("attempt mismatch".into()));
        }
        let should_apply = !task.status.is_terminal()
            || (task.status == TaskStatus::Unknown && result.status != TaskStatus::Unknown);
        let updated = if should_apply {
            let row = sqlx::query(AssertSqlSafe(format!(
                "UPDATE tasks SET status = $2, summary = $3, error = $4, files_changed = $5, \
                 session_id = $6, lease_expires_at = NULL, updated_at = $7 \
                 WHERE id = $1 RETURNING {TASK_COLUMNS}"
            )))
            .bind(id.as_uuid())
            .bind(result.status.as_str())
            .bind(&result.summary)
            .bind(&result.error)
            .bind(files)
            .bind(result.session_id.map(SessionId::as_uuid))
            .bind(now)
            .fetch_one(&mut *tx)
            .await?;
            sqlx::query(
                "UPDATE permissions SET granted = FALSE, \
                 reason = 'task finished before approval', resolved_at = $2 \
                 WHERE task_id = $1 AND granted IS NULL",
            )
            .bind(id.as_uuid())
            .bind(now)
            .execute(&mut *tx)
            .await?;
            row_to_task(&row)?
        } else {
            task
        };
        tx.commit().await?;
        Ok(updated)
    }

    /// 标记任务被拒绝。
    async fn reject_task(
        &self,
        id: TaskId,
        attempt: u32,
        reason: &str,
    ) -> Result<TaskRecord, StoreError> {
        let attempt_i =
            i32::try_from(attempt).map_err(|_| StoreError::Corrupt("attempt overflow".into()))?;
        let now = Utc::now();
        let mut tx = self.pool.begin().await?;
        let row = sqlx::query(AssertSqlSafe(format!(
            "UPDATE tasks SET status = 'failed', summary = $3, error = $4, \
             lease_expires_at = NULL, updated_at = $5 \
             WHERE id = $1 AND attempt = $2 AND status IN ('pending', 'assigned', 'running') \
             RETURNING {TASK_COLUMNS}"
        )))
        .bind(id.as_uuid())
        .bind(attempt_i)
        .bind(format!("agent rejected the task: {reason}"))
        .bind(reason)
        .bind(now)
        .fetch_optional(&mut *tx)
        .await?;
        let updated = match row {
            Some(row) => {
                sqlx::query(
                    "UPDATE permissions SET granted = FALSE, \
                     reason = 'task finished before approval', resolved_at = $2 \
                     WHERE task_id = $1 AND granted IS NULL",
                )
                .bind(id.as_uuid())
                .bind(now)
                .execute(&mut *tx)
                .await?;
                row_to_task(&row)?
            }
            None => {
                let task = self.task_required(id).await?;
                if task.attempt != attempt && !task.status.is_terminal() {
                    return Err(StoreError::Conflict("attempt mismatch".into()));
                }
                task
            }
        };
        tx.commit().await?;
        Ok(updated)
    }

    /// 标记任务结果未知。
    async fn mark_unknown(
        &self,
        id: TaskId,
        attempt: u32,
        reason: &str,
    ) -> Result<TaskRecord, StoreError> {
        let attempt_i =
            i32::try_from(attempt).map_err(|_| StoreError::Corrupt("attempt overflow".into()))?;
        let now = Utc::now();
        let mut tx = self.pool.begin().await?;
        let row = sqlx::query(AssertSqlSafe(format!(
            "UPDATE tasks SET status = 'unknown', summary = $3, error = $3, \
             lease_expires_at = NULL, updated_at = $4 \
             WHERE id = $1 AND attempt = $2 AND status IN ('pending', 'assigned', 'running') \
             RETURNING {TASK_COLUMNS}"
        )))
        .bind(id.as_uuid())
        .bind(attempt_i)
        .bind(reason)
        .bind(now)
        .fetch_optional(&mut *tx)
        .await?;
        let updated = match row {
            Some(row) => {
                sqlx::query(
                    "UPDATE permissions SET granted = FALSE, \
                     reason = 'task outcome became unknown', resolved_at = $2 \
                     WHERE task_id = $1 AND granted IS NULL",
                )
                .bind(id.as_uuid())
                .bind(now)
                .execute(&mut *tx)
                .await?;
                row_to_task(&row)?
            }
            None => {
                let task = self.task_required(id).await?;
                if task.attempt != attempt && !task.status.is_terminal() {
                    return Err(StoreError::Conflict("attempt mismatch".into()));
                }
                task
            }
        };
        tx.commit().await?;
        Ok(updated)
    }

    /// 请求取消任务。
    async fn request_cancel(&self, id: TaskId) -> Result<TaskRecord, StoreError> {
        let now = Utc::now();
        let mut tx = self.pool.begin().await?;
        let row = sqlx::query(AssertSqlSafe(format!(
            "SELECT {TASK_COLUMNS} FROM tasks WHERE id = $1 FOR UPDATE"
        )))
        .bind(id.as_uuid())
        .fetch_optional(&mut *tx)
        .await?
        .ok_or_else(|| StoreError::NotFound("task".into()))?;
        let task = row_to_task(&row)?;
        if task.status.is_terminal() {
            tx.commit().await?;
            return Ok(task);
        }
        let cancel_now = task.status == TaskStatus::Pending;
        let row = sqlx::query(AssertSqlSafe(format!(
            "UPDATE tasks SET cancel_requested = TRUE, \
             status = CASE WHEN $2 THEN 'canceled' ELSE status END, \
             summary = CASE WHEN $2 THEN 'canceled before assignment' ELSE summary END, \
             lease_expires_at = CASE WHEN $2 THEN NULL ELSE lease_expires_at END, \
             updated_at = $3 WHERE id = $1 RETURNING {TASK_COLUMNS}"
        )))
        .bind(id.as_uuid())
        .bind(cancel_now)
        .bind(now)
        .fetch_one(&mut *tx)
        .await?;
        if cancel_now {
            sqlx::query(
                "UPDATE permissions SET granted = FALSE, \
                 reason = 'task canceled before approval', resolved_at = $2 \
                 WHERE task_id = $1 AND granted IS NULL",
            )
            .bind(id.as_uuid())
            .bind(now)
            .execute(&mut *tx)
            .await?;
        }
        let task = row_to_task(&row)?;
        tx.commit().await?;
        Ok(task)
    }

    /// 以新 attempt 重试终态任务。
    async fn retry_task(&self, id: TaskId) -> Result<TaskRecord, StoreError> {
        let now = Utc::now();
        let mut tx = self.pool.begin().await?;
        let row = sqlx::query(AssertSqlSafe(format!(
            "SELECT {TASK_COLUMNS} FROM tasks WHERE id = $1 FOR UPDATE"
        )))
        .bind(id.as_uuid())
        .fetch_optional(&mut *tx)
        .await?
        .ok_or_else(|| StoreError::NotFound("task".into()))?;
        let task = row_to_task(&row)?;
        if !task.status.is_terminal() {
            return Err(StoreError::Conflict(
                "only terminal tasks can be retried".into(),
            ));
        }
        let row = sqlx::query(AssertSqlSafe(format!(
            "UPDATE tasks SET attempt = attempt + 1, status = 'pending', summary = NULL, \
             error = NULL, files_changed = '[]'::jsonb, session_id = NULL, \
             cancel_requested = FALSE, lease_expires_at = NULL, updated_at = $2 \
             WHERE id = $1 RETURNING {TASK_COLUMNS}"
        )))
        .bind(id.as_uuid())
        .bind(now)
        .fetch_one(&mut *tx)
        .await?;
        let task = row_to_task(&row)?;
        tx.commit().await?;
        Ok(task)
    }

    /// 过期租约转为 unknown。
    async fn expire_leases(&self, now: DateTime<Utc>) -> Result<Vec<TaskId>, StoreError> {
        let rows = sqlx::query(
            "UPDATE tasks SET status = 'unknown', \
             summary = 'agent lease expired before a terminal result was received', \
             error = 'task outcome could not be confirmed before the lease expired', \
             lease_expires_at = NULL, updated_at = $1 \
             WHERE status IN ('pending', 'assigned', 'running') \
             AND lease_expires_at IS NOT NULL AND lease_expires_at <= $1 \
             RETURNING id",
        )
        .bind(now)
        .fetch_all(&self.pool)
        .await?;
        let ids: Vec<Uuid> = rows
            .iter()
            .map(|row| row.try_get::<Uuid, _>("id"))
            .collect::<Result<_, _>>()?;
        if !ids.is_empty() {
            sqlx::query(
                "UPDATE permissions SET granted = FALSE, \
                 reason = 'task lease expired before approval', resolved_at = $2 \
                 WHERE task_id = ANY($1) AND granted IS NULL",
            )
            .bind(&ids)
            .bind(now)
            .execute(&self.pool)
            .await?;
        }
        Ok(ids.into_iter().map(TaskId::from).collect())
    }

    /// 保存权限请求。
    async fn create_permission(&self, permission: PermissionRecord) -> Result<(), StoreError> {
        sqlx::query(
            "INSERT INTO permissions (request_id, task_id, user_id, tool, action, details, risk, \
             created_at) VALUES ($1, $2, $3, $4, $5, $6, $7, $8)",
        )
        .bind(permission.request_id.as_uuid())
        .bind(permission.task_id.as_uuid())
        .bind(permission.user_id.as_uuid())
        .bind(&permission.tool)
        .bind(&permission.action)
        .bind(&permission.details)
        .bind(permission.risk.as_str())
        .bind(permission.created_at)
        .execute(&self.pool)
        .await?;
        Ok(())
    }

    /// 查询权限请求。
    async fn get_permission(
        &self,
        request_id: RequestId,
    ) -> Result<Option<PermissionRecord>, StoreError> {
        let row = sqlx::query(AssertSqlSafe(format!(
            "SELECT {PERMISSION_COLUMNS} FROM permissions WHERE request_id = $1"
        )))
        .bind(request_id.as_uuid())
        .fetch_optional(&self.pool)
        .await?;
        row.map(|row| row_to_permission(&row)).transpose()
    }

    /// 处理权限请求。
    async fn resolve_permission(
        &self,
        request_id: RequestId,
        granted: bool,
        reason: Option<&str>,
    ) -> Result<Option<PermissionRecord>, StoreError> {
        let row = sqlx::query(AssertSqlSafe(format!(
            "UPDATE permissions SET granted = COALESCE(granted, $2), \
             reason = CASE WHEN granted IS NULL THEN $3 ELSE reason END, \
             resolved_at = COALESCE(resolved_at, $4) \
             WHERE request_id = $1 RETURNING {PERMISSION_COLUMNS}"
        )))
        .bind(request_id.as_uuid())
        .bind(granted)
        .bind(reason)
        .bind(Utc::now())
        .fetch_optional(&self.pool)
        .await?;
        row.map(|row| row_to_permission(&row)).transpose()
    }

    /// 列出待处理权限。
    async fn list_pending_permissions(
        &self,
        task_id: TaskId,
    ) -> Result<Vec<PermissionRecord>, StoreError> {
        let rows = sqlx::query(AssertSqlSafe(format!(
            "SELECT {PERMISSION_COLUMNS} FROM permissions \
             WHERE task_id = $1 AND granted IS NULL ORDER BY created_at ASC"
        )))
        .bind(task_id.as_uuid())
        .fetch_all(&self.pool)
        .await?;
        rows.iter().map(row_to_permission).collect()
    }

    /// 关闭任务权限。
    async fn close_task_permissions(&self, task_id: TaskId) -> Result<(), StoreError> {
        sqlx::query(
            "UPDATE permissions SET granted = FALSE, reason = 'task finished before approval', \
             resolved_at = $2 WHERE task_id = $1 AND granted IS NULL",
        )
        .bind(task_id.as_uuid())
        .bind(Utc::now())
        .execute(&self.pool)
        .await?;
        Ok(())
    }

    /// 检查数据库连接。
    async fn ping(&self) -> Result<(), StoreError> {
        sqlx::query("SELECT 1").execute(&self.pool).await?;
        Ok(())
    }
}
