use std::{path::Path, str::FromStr, time::Duration};

use chrono::Utc;
use coder_protocol::{InFlightTask, LocalTaskState, SessionId, TaskId, TaskStatus};
use serde::{Deserialize, Serialize};
use sqlx::{
    Row,
    sqlite::{
        SqliteConnectOptions, SqliteJournalMode, SqlitePool, SqlitePoolOptions, SqliteSynchronous,
    },
};

use crate::error::AgentError;

/// 一次尝试的终态结果，持久化后可在重连时原样重发。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct StoredTaskResult {
    pub status: TaskStatus,
    pub summary: String,
    pub error: Option<String>,
    pub duration_ms: u64,
    pub files_changed: Vec<String>,
    pub session_id: Option<SessionId>,
}

/// 带任务标识的未确认结果。
#[derive(Clone, Debug)]
pub struct UnreportedResult {
    pub task_id: TaskId,
    pub attempt: u32,
    pub result: StoredTaskResult,
}

/// 本地任务账本。
///
/// 账本记录每次尝试是否开始、终态结果是否已被云端确认。终态记录不会自动
/// 重放任务，只会在重连时重发结果；`started` 在进程重启后一律转为
/// `unknown`，避免把“可能执行过”的命令当作未执行再跑一遍。
pub struct TaskLedger {
    pool: SqlitePool,
}

impl TaskLedger {
    /// 打开或创建任务账本。
    pub async fn connect(path: &Path) -> Result<Self, AgentError> {
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let options = SqliteConnectOptions::new()
            .filename(path)
            .create_if_missing(true)
            .journal_mode(SqliteJournalMode::Wal)
            .synchronous(SqliteSynchronous::Normal)
            .busy_timeout(Duration::from_secs(10))
            .foreign_keys(true);
        let pool = SqlitePoolOptions::new()
            .max_connections(4)
            .connect_with(options)
            .await?;
        let ledger = Self { pool };
        ledger.migrate().await?;
        Ok(ledger)
    }

    /// 创建账本表与索引。
    async fn migrate(&self) -> Result<(), AgentError> {
        sqlx::query(
            r#"
            CREATE TABLE IF NOT EXISTS task_records (
                task_id TEXT NOT NULL,
                attempt INTEGER NOT NULL,
                state TEXT NOT NULL,
                result_json TEXT,
                reported INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (task_id, attempt)
            )
            "#,
        )
        .execute(&self.pool)
        .await?;
        sqlx::query(
            "CREATE INDEX IF NOT EXISTS idx_task_records_state ON task_records(state, reported)",
        )
        .execute(&self.pool)
        .await?;
        Ok(())
    }

    /// 记录一次尝试开始。
    ///
    /// 返回 `None` 表示这是首次收到该尝试；返回已有状态表示重复投递，
    /// 调用方不得再次执行。
    pub async fn begin_task(
        &self,
        task_id: TaskId,
        attempt: u32,
    ) -> Result<Option<LocalTaskState>, AgentError> {
        let now = now();
        let inserted = sqlx::query(
            r#"
            INSERT OR IGNORE INTO task_records
                (task_id, attempt, state, reported, created_at, updated_at)
            VALUES (?, ?, 'started', 0, ?, ?)
            "#,
        )
        .bind(task_id.to_string())
        .bind(i64::from(attempt))
        .bind(&now)
        .bind(&now)
        .execute(&self.pool)
        .await?;
        if inserted.rows_affected() == 1 {
            return Ok(None);
        }
        let row = sqlx::query("SELECT state FROM task_records WHERE task_id = ? AND attempt = ?")
            .bind(task_id.to_string())
            .bind(i64::from(attempt))
            .fetch_optional(&self.pool)
            .await?;
        match row {
            Some(row) => {
                let raw: String = row.try_get("state")?;
                Ok(Some(parse_local_state(&raw)?))
            }
            None => Ok(None),
        }
    }

    /// 查询某次尝试的本地状态。
    pub async fn state(
        &self,
        task_id: TaskId,
        attempt: u32,
    ) -> Result<Option<LocalTaskState>, AgentError> {
        let row = sqlx::query("SELECT state FROM task_records WHERE task_id = ? AND attempt = ?")
            .bind(task_id.to_string())
            .bind(i64::from(attempt))
            .fetch_optional(&self.pool)
            .await?;
        row.map(|row| {
            let raw: String = row.try_get("state")?;
            parse_local_state(&raw)
        })
        .transpose()
    }

    /// 持久化终态结果并把记录标记为未确认。
    pub async fn finish_task(
        &self,
        task_id: TaskId,
        attempt: u32,
        result: &StoredTaskResult,
    ) -> Result<(), AgentError> {
        if !result.status.is_terminal() {
            return Err(AgentError::Config(
                "ledger only stores terminal task results".into(),
            ));
        }
        let state = local_state_for(result.status);
        let result_json = serde_json::to_string(result)?;
        let now = now();
        sqlx::query(
            r#"
            INSERT INTO task_records
                (task_id, attempt, state, result_json, reported, created_at, updated_at)
            VALUES (?, ?, ?, ?, 0, ?, ?)
            ON CONFLICT(task_id, attempt) DO UPDATE SET
                state = excluded.state,
                result_json = excluded.result_json,
                reported = 0,
                updated_at = excluded.updated_at
            "#,
        )
        .bind(task_id.to_string())
        .bind(i64::from(attempt))
        .bind(local_state_str(state))
        .bind(result_json)
        .bind(&now)
        .bind(&now)
        .execute(&self.pool)
        .await?;
        Ok(())
    }

    /// 云端确认收到结果后停止重发。
    pub async fn mark_reported(&self, task_id: TaskId, attempt: u32) -> Result<(), AgentError> {
        sqlx::query(
            "UPDATE task_records SET reported = 1, updated_at = ? WHERE task_id = ? AND attempt = ?",
        )
        .bind(now())
        .bind(task_id.to_string())
        .bind(i64::from(attempt))
        .execute(&self.pool)
        .await?;
        Ok(())
    }

    /// 进程启动时把残留的 `started` 记录改为 `unknown`。
    ///
    /// 返回被转换的记录数。这些任务的结果无法确认，必须由云端标记为
    /// `unknown`，绝不能自动重跑。
    pub async fn recover_orphans(&self) -> Result<u64, AgentError> {
        let result = StoredTaskResult {
            status: TaskStatus::Unknown,
            summary: "agent restarted before reporting the task outcome".into(),
            error: Some(
                "the local process exited while this attempt was running; execution may have had side effects"
                    .into(),
            ),
            duration_ms: 0,
            files_changed: Vec::new(),
            session_id: None,
        };
        let result_json = serde_json::to_string(&result)?;
        let now = now();
        let updated = sqlx::query(
            r#"
            UPDATE task_records
            SET state = 'unknown', result_json = ?, reported = 0, updated_at = ?
            WHERE state = 'started'
            "#,
        )
        .bind(result_json)
        .bind(&now)
        .execute(&self.pool)
        .await?;
        Ok(updated.rows_affected())
    }

    /// 列出尚未被云端确认的终态结果，按更新顺序重发。
    pub async fn unreported_results(&self) -> Result<Vec<UnreportedResult>, AgentError> {
        let rows = sqlx::query(
            r#"
            SELECT task_id, attempt, result_json
            FROM task_records
            WHERE reported = 0 AND result_json IS NOT NULL
            ORDER BY updated_at ASC
            "#,
        )
        .fetch_all(&self.pool)
        .await?;
        let mut results = Vec::with_capacity(rows.len());
        for row in rows {
            let task_id: String = row.try_get("task_id")?;
            let attempt: i64 = row.try_get("attempt")?;
            let result_json: String = row.try_get("result_json")?;
            let result = serde_json::from_str::<StoredTaskResult>(&result_json).map_err(|err| {
                AgentError::Config(format!("corrupt ledger result for {task_id}: {err}"))
            })?;
            results.push(UnreportedResult {
                task_id: TaskId::from_str(&task_id).map_err(|err| {
                    AgentError::Config(format!("corrupt task id in ledger: {err}"))
                })?,
                attempt: u32::try_from(attempt).map_err(|_| {
                    AgentError::Config(format!("invalid attempt {attempt} in ledger"))
                })?,
                result,
            });
        }
        Ok(results)
    }

    /// 生成重连 Hello 中上报的未完成尝试列表。
    pub async fn in_flight(&self) -> Result<Vec<InFlightTask>, AgentError> {
        let rows = sqlx::query(
            r#"
            SELECT task_id, attempt, state FROM task_records
            WHERE state IN ('started', 'unknown') OR reported = 0
            ORDER BY updated_at ASC
            "#,
        )
        .fetch_all(&self.pool)
        .await?;
        let mut tasks = Vec::with_capacity(rows.len());
        for row in rows {
            let task_id: String = row.try_get("task_id")?;
            let attempt: i64 = row.try_get("attempt")?;
            let state: String = row.try_get("state")?;
            tasks.push(InFlightTask {
                task_id: TaskId::from_str(&task_id).map_err(|err| {
                    AgentError::Config(format!("corrupt task id in ledger: {err}"))
                })?,
                attempt: u32::try_from(attempt).map_err(|_| {
                    AgentError::Config(format!("invalid attempt {attempt} in ledger"))
                })?,
                state: parse_local_state(&state)?,
            });
        }
        Ok(tasks)
    }

    /// 清理已确认的历史记录，保留最近 `keep` 条。
    pub async fn prune_reported(&self, keep: u32) -> Result<u64, AgentError> {
        let deleted = sqlx::query(
            r#"
            DELETE FROM task_records
            WHERE reported = 1 AND (task_id, attempt) NOT IN (
                SELECT task_id, attempt FROM task_records
                WHERE reported = 1
                ORDER BY updated_at DESC
                LIMIT ?
            )
            "#,
        )
        .bind(i64::from(keep))
        .execute(&self.pool)
        .await?;
        Ok(deleted.rows_affected())
    }
}

/// 本地状态到稳定字符串。
fn local_state_str(state: LocalTaskState) -> &'static str {
    match state {
        LocalTaskState::Started => "started",
        LocalTaskState::Completed => "completed",
        LocalTaskState::Failed => "failed",
        LocalTaskState::Canceled => "canceled",
        LocalTaskState::Unknown => "unknown",
    }
}

/// 解析账本中的本地状态。
fn parse_local_state(raw: &str) -> Result<LocalTaskState, AgentError> {
    match raw {
        "started" => Ok(LocalTaskState::Started),
        "completed" => Ok(LocalTaskState::Completed),
        "failed" => Ok(LocalTaskState::Failed),
        "canceled" => Ok(LocalTaskState::Canceled),
        "unknown" => Ok(LocalTaskState::Unknown),
        other => Err(AgentError::Config(format!(
            "unknown task ledger state `{other}`"
        ))),
    }
}

/// 终态任务状态映射为本地账本状态。
fn local_state_for(status: TaskStatus) -> LocalTaskState {
    match status {
        TaskStatus::Succeeded => LocalTaskState::Completed,
        TaskStatus::Failed => LocalTaskState::Failed,
        TaskStatus::Canceled => LocalTaskState::Canceled,
        TaskStatus::Unknown => LocalTaskState::Unknown,
        TaskStatus::Pending | TaskStatus::Assigned | TaskStatus::Running => LocalTaskState::Started,
    }
}

/// 当前 UTC 时间字符串。
fn now() -> String {
    Utc::now().to_rfc3339()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 创建临时账本。
    async fn ledger() -> (tempfile::TempDir, TaskLedger) {
        let dir = tempfile::tempdir().unwrap();
        let ledger = TaskLedger::connect(&dir.path().join("agent.db"))
            .await
            .unwrap();
        (dir, ledger)
    }

    #[tokio::test]
    async fn tracks_attempt_lifecycle() {
        let (_dir, ledger) = ledger().await;
        let task_id = TaskId::new();
        assert!(ledger.begin_task(task_id, 0).await.unwrap().is_none());
        assert_eq!(
            ledger.begin_task(task_id, 0).await.unwrap(),
            Some(LocalTaskState::Started)
        );
        ledger
            .finish_task(
                task_id,
                0,
                &StoredTaskResult {
                    status: TaskStatus::Succeeded,
                    summary: "done".into(),
                    error: None,
                    duration_ms: 12,
                    files_changed: vec!["a.txt".into()],
                    session_id: None,
                },
            )
            .await
            .unwrap();
        let pending = ledger.unreported_results().await.unwrap();
        assert_eq!(pending.len(), 1);
        assert_eq!(pending[0].task_id, task_id);
        assert_eq!(pending[0].result.status, TaskStatus::Succeeded);
        ledger.mark_reported(task_id, 0).await.unwrap();
        assert!(ledger.unreported_results().await.unwrap().is_empty());
        assert!(ledger.in_flight().await.unwrap().is_empty());
    }

    #[tokio::test]
    async fn orphaned_start_becomes_unknown_not_replayed() {
        let (_dir, ledger) = ledger().await;
        let task_id = TaskId::new();
        ledger.begin_task(task_id, 0).await.unwrap();
        assert_eq!(ledger.recover_orphans().await.unwrap(), 1);
        assert_eq!(
            ledger.state(task_id, 0).await.unwrap(),
            Some(LocalTaskState::Unknown)
        );
        let pending = ledger.unreported_results().await.unwrap();
        assert_eq!(pending[0].result.status, TaskStatus::Unknown);
        assert!(
            ledger
                .begin_task(task_id, 0)
                .await
                .unwrap()
                .is_some_and(|state| state == LocalTaskState::Unknown)
        );
    }

    #[tokio::test]
    async fn retry_attempt_is_independent() {
        let (_dir, ledger) = ledger().await;
        let task_id = TaskId::new();
        ledger.begin_task(task_id, 0).await.unwrap();
        ledger
            .finish_task(
                task_id,
                0,
                &StoredTaskResult {
                    status: TaskStatus::Failed,
                    summary: "failed".into(),
                    error: Some("boom".into()),
                    duration_ms: 5,
                    files_changed: Vec::new(),
                    session_id: None,
                },
            )
            .await
            .unwrap();
        assert!(ledger.begin_task(task_id, 1).await.unwrap().is_none());
    }
}
