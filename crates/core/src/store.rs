use std::{path::Path, str::FromStr, time::Duration};

use chrono::{DateTime, Utc};
use coder_protocol::{SessionId, TaskStatus};
use sqlx::{
    Row, SqlitePool,
    sqlite::{SqliteConnectOptions, SqliteJournalMode, SqlitePoolOptions, SqliteSynchronous},
};

use crate::{
    error::CoreError,
    model::{ChatMessage, Role, ToolCall},
};

/// 会话摘要。
#[derive(Clone, Debug)]
pub struct SessionSummary {
    pub session_id: SessionId,
    pub task: String,
    pub workspace: String,
    pub model: String,
    pub status: TaskStatus,
    pub summary: Option<String>,
    pub iterations: u32,
    pub input_tokens: u64,
    pub output_tokens: u64,
    pub created_at: DateTime<Utc>,
    pub updated_at: DateTime<Utc>,
}

/// 会话详情，包含完整本地消息记录。
#[derive(Clone, Debug)]
pub struct SessionDetail {
    pub summary: SessionSummary,
    pub messages: Vec<ChatMessage>,
}

/// 基于 SQLite 的本地持久化。
pub struct SqliteStore {
    pool: SqlitePool,
}

impl SqliteStore {
    /// 打开（或创建）SQLite 数据库并执行迁移。
    pub async fn connect(path: &Path) -> Result<Self, CoreError> {
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let options = SqliteConnectOptions::new()
            .filename(path)
            .create_if_missing(true)
            .journal_mode(SqliteJournalMode::Wal)
            .synchronous(SqliteSynchronous::Normal)
            .foreign_keys(true)
            .busy_timeout(Duration::from_secs(10));
        let pool = SqlitePoolOptions::new()
            .max_connections(5)
            .connect_with(options)
            .await?;
        sqlx::migrate!("./migrations-sqlite")
            .run(&pool)
            .await
            .map_err(|err| CoreError::Migration(err.to_string()))?;
        Ok(Self { pool })
    }

    /// 创建一个运行中的会话记录。
    pub async fn create_session(
        &self,
        session_id: SessionId,
        task: &str,
        workspace: &str,
        model: &str,
    ) -> Result<(), CoreError> {
        let now = Utc::now().to_rfc3339();
        sqlx::query(
            r#"INSERT INTO sessions
               (id, task, workspace, model, status, created_at, updated_at)
               VALUES (?, ?, ?, ?, ?, ?, ?)"#,
        )
        .bind(session_id.to_string())
        .bind(task)
        .bind(workspace)
        .bind(model)
        .bind(TaskStatus::Running.as_str())
        .bind(&now)
        .bind(&now)
        .execute(&self.pool)
        .await?;
        Ok(())
    }

    /// 追加一条对话消息。
    pub async fn append_message(
        &self,
        session_id: SessionId,
        message: &ChatMessage,
    ) -> Result<(), CoreError> {
        let role = match message.role {
            Role::System => "system",
            Role::User => "user",
            Role::Assistant => "assistant",
            Role::Tool => "tool",
        };
        let tool_calls = if message.tool_calls.is_empty() {
            None
        } else {
            Some(
                serde_json::to_string(&message.tool_calls)
                    .map_err(|err| CoreError::Config(format!("cannot encode tool calls: {err}")))?,
            )
        };
        sqlx::query(
            r#"INSERT INTO messages
               (session_id, role, content, tool_calls, tool_call_id, created_at)
               VALUES (?, ?, ?, ?, ?, ?)"#,
        )
        .bind(session_id.to_string())
        .bind(role)
        .bind(message.content.as_deref())
        .bind(tool_calls)
        .bind(message.tool_call_id.as_deref())
        .bind(Utc::now().to_rfc3339())
        .execute(&self.pool)
        .await?;
        Ok(())
    }

    /// 写入会话终态与资源用量。
    #[allow(clippy::too_many_arguments)]
    pub async fn finish_session(
        &self,
        session_id: SessionId,
        status: TaskStatus,
        summary: &str,
        error: Option<&str>,
        iterations: u32,
        input_tokens: u64,
        output_tokens: u64,
    ) -> Result<(), CoreError> {
        sqlx::query(
            r#"UPDATE sessions
               SET status = ?, summary = ?, error = ?, iterations = ?,
                   input_tokens = ?, output_tokens = ?, updated_at = ?
               WHERE id = ?"#,
        )
        .bind(status.as_str())
        .bind(summary)
        .bind(error)
        .bind(iterations as i64)
        .bind(input_tokens as i64)
        .bind(output_tokens as i64)
        .bind(Utc::now().to_rfc3339())
        .bind(session_id.to_string())
        .execute(&self.pool)
        .await?;
        Ok(())
    }

    /// 列出最近的会话。
    pub async fn list_sessions(&self, limit: u32) -> Result<Vec<SessionSummary>, CoreError> {
        let rows = sqlx::query(
            r#"SELECT id, task, workspace, model, status, summary, iterations,
                      input_tokens, output_tokens, created_at, updated_at
               FROM sessions ORDER BY created_at DESC LIMIT ?"#,
        )
        .bind(limit.clamp(1, 500) as i64)
        .fetch_all(&self.pool)
        .await?;
        rows.into_iter()
            .map(|row| parse_summary(&row))
            .collect::<Result<Vec<_>, _>>()
    }

    /// 读取单个会话摘要。
    pub async fn get_session(
        &self,
        session_id: SessionId,
    ) -> Result<Option<SessionSummary>, CoreError> {
        let row = sqlx::query(
            r#"SELECT id, task, workspace, model, status, summary, iterations,
                      input_tokens, output_tokens, created_at, updated_at
               FROM sessions WHERE id = ?"#,
        )
        .bind(session_id.to_string())
        .fetch_optional(&self.pool)
        .await?;
        row.map(|row| parse_summary(&row)).transpose()
    }

    /// 读取会话详情与完整消息。
    pub async fn session_detail(
        &self,
        session_id: SessionId,
    ) -> Result<Option<SessionDetail>, CoreError> {
        let Some(summary) = self.get_session(session_id).await? else {
            return Ok(None);
        };
        let rows = sqlx::query(
            r#"SELECT role, content, tool_calls, tool_call_id
               FROM messages WHERE session_id = ? ORDER BY id ASC"#,
        )
        .bind(session_id.to_string())
        .fetch_all(&self.pool)
        .await?;
        let mut messages = Vec::with_capacity(rows.len());
        for row in rows {
            let role: String = row.try_get("role")?;
            let content: Option<String> = row.try_get("content")?;
            let tool_calls: Option<String> = row.try_get("tool_calls")?;
            let tool_call_id: Option<String> = row.try_get("tool_call_id")?;
            let role = match role.as_str() {
                "system" => Role::System,
                "user" => Role::User,
                "assistant" => Role::Assistant,
                "tool" => Role::Tool,
                other => {
                    return Err(CoreError::Config(format!(
                        "database contains unknown message role `{other}`"
                    )));
                }
            };
            let calls: Vec<ToolCall> = match tool_calls {
                Some(value) => serde_json::from_str(&value).map_err(|err| {
                    CoreError::Config(format!("database contains invalid tool calls: {err}"))
                })?,
                None => Vec::new(),
            };
            messages.push(ChatMessage {
                role,
                content,
                tool_calls: calls,
                tool_call_id,
            });
        }
        Ok(Some(SessionDetail { summary, messages }))
    }

    /// 将长时间停留在 running 的孤儿会话标记为 unknown。
    pub async fn mark_orphaned_sessions(&self, older_than: Duration) -> Result<u64, CoreError> {
        let cutoff = Utc::now() - chrono::Duration::from_std(older_than).unwrap_or_default();
        let result = sqlx::query(
            r#"UPDATE sessions
               SET status = ?, summary = ?, updated_at = ?
               WHERE status = ? AND updated_at < ?"#,
        )
        .bind(TaskStatus::Unknown.as_str())
        .bind("agent process exited before recording a terminal state")
        .bind(Utc::now().to_rfc3339())
        .bind(TaskStatus::Running.as_str())
        .bind(cutoff.to_rfc3339())
        .execute(&self.pool)
        .await?;
        Ok(result.rows_affected())
    }
}

/// 从数据库行解析会话摘要。
fn parse_summary(row: &sqlx::sqlite::SqliteRow) -> Result<SessionSummary, CoreError> {
    let id: String = row.try_get("id")?;
    let status: String = row.try_get("status")?;
    Ok(SessionSummary {
        session_id: SessionId::from_str(&id)
            .map_err(|err| CoreError::Config(format!("invalid session id in database: {err}")))?,
        task: row.try_get("task")?,
        workspace: row.try_get("workspace")?,
        model: row.try_get("model")?,
        status: TaskStatus::from_str(&status).map_err(|err| {
            CoreError::Config(format!("invalid session status in database: {err}"))
        })?,
        summary: row.try_get("summary")?,
        iterations: row.try_get::<i64, _>("iterations")?.max(0) as u32,
        input_tokens: row.try_get::<i64, _>("input_tokens")?.max(0) as u64,
        output_tokens: row.try_get::<i64, _>("output_tokens")?.max(0) as u64,
        created_at: parse_time(row.try_get("created_at")?),
        updated_at: parse_time(row.try_get("updated_at")?),
    })
}

/// 解析 RFC3339 时间；损坏数据回退到当前时间以保持列表可用。
fn parse_time(value: String) -> DateTime<Utc> {
    DateTime::parse_from_rfc3339(&value)
        .map(|value| value.with_timezone(&Utc))
        .unwrap_or_else(|_| Utc::now())
}
