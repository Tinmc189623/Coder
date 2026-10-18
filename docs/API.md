# Coder API

云端根地址由部署决定。所有 REST 路由位于 `/api/v1` 下，请求与响应使用 JSON，时间使用 RFC 3339 UTC。WebSocket 使用同一主机与端口，路径为 `/api/v1/agent/ws`。

机器可读的接口描述见 [openapi.yaml](openapi.yaml)。两者冲突时以 `crates/cloud/src/routes.rs` 的实现为准。

## 认证

| 范围 | 凭据 | 说明 |
| --- | --- | --- |
| 普通用户 | `Authorization: Bearer ck_...` | 管理员创建用户时返回一次，保存在用户侧 |
| 设备 | `Authorization: Bearer <JWT>` | 由 `/devices/token` 用设备密钥换取，默认 900 秒 |
| 管理员 | `x-admin-key: ...` | 仅用于创建用户与配对令牌 |
| 配对/注册 | 请求体中的一次性令牌或设备密钥 | 不需要 Authorization |

设备 JWT 用于 `/model/chat` 与 WebSocket。用户 API key 不能代替设备令牌，设备令牌也不能调用用户管理接口。

所有响应携带 `x-request-id`。未认证请求返回 401 和 `WWW-Authenticate: Bearer`；超限请求返回 429 和 `Retry-After: 5`。

## 错误结构

```json
{
  "error": {
    "code": "invalid_request",
    "message": "prompt must contain between 1 and 32000 characters",
    "request_id": null
  }
}
```

稳定错误码：`unauthorized`、`forbidden`、`invalid_request`、`protocol_version_unsupported`、`task_not_found`、`task_conflict`、`permission_denied`、`rate_limited`、`internal`、`unavailable`。

| HTTP | 典型场景 |
| --- | --- |
| 400 | JSON 结构、字段长度或路径非法 |
| 401 | 缺失/无效凭据、配对令牌过期或已使用 |
| 403 | 设备或任务不属于当前用户 |
| 404 | 路由或资源不存在；跨用户访问任务同样返回 404 |
| 409 | 非法状态转换，例如对非终态任务重试 |
| 429 | 按调用方限流 |
| 500 | 存储或内部故障；响应不暴露 SQL/密钥 |
| 503 | 数据库未就绪或云端模型代理未配置 |

## 健康与运维

### `GET /api/v1/health`

无需认证。返回 `{"status":"ok","version":"0.1.0","uptime_secs":123}`。

### `GET /api/v1/ready`

无需认证。存储可用时返回 `{"status":"ready"}`，否则 503。

### `GET /api/v1/metrics`

无需认证。返回 Prometheus 文本格式指标，包括任务创建、模型请求、租约过期计数。生产部署应仅允许监控网络访问该路径。

## 管理员

### `POST /api/v1/admin/users`

请求头 `x-admin-key`。

```json
{"email": "user@example.com"}
```

201 响应：

```json
{
  "user_id": "0f5b1c1e-5d1c-4f53-9a57-6f7a6f7f1a11",
  "email": "user@example.com",
  "api_key": "ck_...",
  "created_at": "2026-10-18T08:00:00Z"
}
```

`api_key` 只返回一次，数据库只保存 Argon2 哈希与 SHA-256 查找值。

### `POST /api/v1/admin/pairing-tokens`

```json
{"user_id": "0f5b1c1e-5d1c-4f53-9a57-6f7a6f7f1a11"}
```

201 响应：

```json
{"pairing_token": "...", "expires_at": "2026-10-18T08:10:00Z"}
```

令牌一次性使用，默认 10 分钟过期。

## 设备

### `POST /api/v1/devices/register`

```json
{
  "pairing_token": "...",
  "name": "workstation",
  "platform": "windows",
  "agent_version": "0.1.0"
}
```

201 响应：

```json
{
  "device_id": "...",
  "device_secret": "ck_dev_...",
  "user_id": "..."
}
```

`device_secret` 只返回一次。注册会消费配对令牌，重复使用返回 401。

### `POST /api/v1/devices/token`

```json
{"device_id": "...", "device_secret": "ck_dev_..."}
```

200 响应：

```json
{"access_token": "...", "token_type": "Bearer", "expires_in_secs": 900}
```

### `GET /api/v1/devices`

用户 API key。返回当前用户的设备列表：

```json
[
  {
    "device_id": "...",
    "user_id": "...",
    "name": "workstation",
    "platform": "windows",
    "agent_version": "0.1.0",
    "online": true,
    "last_seen_at": "2026-10-18T08:01:00Z",
    "created_at": "2026-10-18T07:00:00Z"
  }
]
```

`online` 仅在 Agent 完成 WebSocket 握手并收到 Welcome 后为 true。

### `GET /api/v1/me`

用户 API key。返回 `user_id`、`email`、`created_at`。

## 任务

任务状态：

| 状态 | 含义 |
| --- | --- |
| `pending` | 已创建，等待下发；重试后也回到此状态 |
| `assigned` | 已下发给在线设备，本地尚未确认开始 |
| `running` | 本地已确认开始，租约有效 |
| `succeeded` | 收到可确认的成功结果 |
| `failed` | 收到可确认的失败结果 |
| `canceled` | 分配前取消，或本地确认取消后的终态 |
| `unknown` | 租约过期或本地状态缺失，结果无法确认 |

`unknown` 不是成功。只有同一 attempt 的迟到真实结果可以把它推进到终态。

### `POST /api/v1/tasks`

用户 API key。202 响应。

```json
{
  "device_id": "...",
  "prompt": "运行 cargo test 并修复失败",
  "workspace": "G:\\work\\project",
  "limits": {
    "max_iterations": 30,
    "max_duration_secs": 1800,
    "max_tool_calls": 100,
    "token_budget": 200000
  }
}
```

`prompt` 最多 32,000 字符；`workspace` 最多 4,096 字符且不能包含控制字符。`limits` 可省略，服务端收敛到：迭代 1–200、时长 1–7,200 秒、工具调用 1–1,000、token 1–2,000,000。

响应为 `TaskView`：

```json
{
  "task_id": "...",
  "device_id": "...",
  "user_id": "...",
  "prompt": "...",
  "workspace": "G:\\work\\project",
  "status": "pending",
  "attempt": 0,
  "summary": null,
  "error": null,
  "files_changed": [],
  "created_at": "2026-10-18T08:00:00Z",
  "updated_at": "2026-10-18T08:00:00Z",
  "lease_expires_at": null,
  "limits": {"max_iterations": 30, "max_duration_secs": 1800, "max_tool_calls": 100, "token_budget": 200000}
}
```

### `GET /api/v1/tasks?limit=50`

用户 API key。按创建时间倒序返回当前用户任务；`limit` 默认 50，范围 1–200。

### `GET /api/v1/tasks/{task_id}`

用户 API key。返回 `TaskView`。不存在或属于其他用户时返回 404。

### `POST /api/v1/tasks/{task_id}/cancel`

用户 API key。`pending` 任务直接转为 `canceled`；活动任务设置取消标志，状态保持原值并推送 WebSocket `task_cancel`，Agent 确认后提交 `canceled` 结果。返回最新 `TaskView`。

### `POST /api/v1/tasks/{task_id}/retry`

用户 API key。仅终态任务可重试。attempt 加一，清空 summary/error/files/session/cancel 标志，状态回到 `pending`，并尝试立即下发。202 响应返回新的 `TaskView`。

### `GET /api/v1/tasks/{task_id}/permissions`

用户 API key。返回尚未处理的权限请求：

```json
[
  {
    "task_id": "...",
    "request_id": "...",
    "tool": "shell",
    "action": "cargo publish",
    "details": "workspace=/repo",
    "risk": "high",
    "created_at": "2026-10-18T08:02:00Z"
  }
]
```

`risk` 为 `low`、`medium` 或 `high`。

### `POST /api/v1/tasks/{task_id}/permissions/{request_id}`

```json
{"granted": true, "reason": "reviewed the command"}
```

批准或拒绝后，云端向设备推送 `permission_decision`。响应：

```json
{"task_id": "...", "request_id": "...", "granted": true, "reason": "reviewed the command"}
```

重复决定返回 409。

## 模型代理

### `POST /api/v1/model/chat`

设备 JWT。请求体与 `coder_core::model::ChatRequest` 一致：

```json
{
  "messages": [
    {"role": "system", "content": "你是编程智能体"},
    {"role": "user", "content": "检查仓库"}
  ],
  "tools": [],
  "temperature": 0.2,
  "max_output_tokens": 4096,
  "stream": false
}
```

消息 1–256 条，总字符数不超过 1,000,000，`max_output_tokens` 不超过云端配置。`tools` 使用 OpenAI 兼容的 function 描述。响应：

```json
{
  "content": "检查完成",
  "tool_calls": [],
  "finish_reason": "stop",
  "usage": {"input_tokens": 120, "output_tokens": 30}
}
```

未配置模型端点时返回 503；上游失败返回 502 与 `unavailable`。

## WebSocket 协议

连接：`GET /api/v1/agent/ws`，`Authorization: Bearer <设备 JWT>`，升级为 WebSocket。每条消息是一个 JSON 对象，使用 `type` 标签区分。协议版本为 1。

### 握手

客户端必须先发送 `hello`：

```json
{
  "type": "hello",
  "protocol_version": 1,
  "agent_version": "0.1.0",
  "device_name": "workstation",
  "platform": "windows",
  "capabilities": {
    "tools": ["read_file", "write_file", "shell"],
    "workspace_roots": ["G:\\work\\project"],
    "max_parallel_tasks": 1
  },
  "in_flight": [
    {"task_id": "...", "attempt": 0, "state": "started"}
  ]
}
```

`in_flight.state` 为 `started`、`completed`、`failed`、`canceled` 或 `unknown`，描述本地账本中的真实状态。服务端校验版本后返回 `welcome`，其中包含需要继续执行的 assignment 与应取消的旧 attempt：

```json
{
  "type": "welcome",
  "protocol_version": 1,
  "server_version": "0.1.0",
  "session_id": "...",
  "server_time": "2026-10-18T08:00:00Z",
  "tasks": [],
  "canceled": []
}
```

同一设备只保留一个会话；新连接会使旧连接关闭。

### 客户端消息

| type | 字段 | 说明 |
| --- | --- | --- |
| `hello` | 见上 | 每个连接的第一条消息 |
| `heartbeat` | `ts` | 续租并保持会话，建议 20 秒一次 |
| `task_started` | `task_id`, `attempt`, `started_at` | 确认开始；云端转为 `running` |
| `task_progress` | `task_id`, `attempt`, `seq`, `event` | 进度事件，不改变终态 |
| `task_result` | `task_id`, `attempt`, `status`, `summary`, `error`, `duration_ms`, `files_changed`, `session_id` | 终态结果；status 只能是终态 |
| `task_rejected` | `task_id`, `attempt`, `reason` | 拒绝执行，任务转 `failed` |
| `task_interrupted` | `task_id`, `attempt`, `reason` | 本地中断，任务转 `unknown` |
| `permission_request` | `task_id`, `attempt`, `request_id`, `tool`, `action`, `details`, `risk` | 高风险操作等待用户决定 |
| `cancel_ack` | `task_id`, `attempt` | 已停止本地执行，随后提交 `canceled` 结果 |
| `goodbye` | `reason` | 主动断开 |

### 服务端消息

| type | 字段 | 说明 |
| --- | --- | --- |
| `welcome` | 见上 | 握手完成 |
| `heartbeat_ack` | `ts` | 心跳确认 |
| `task_assign` | `assignment` | 下发任务；`assignment` 含 task_id、attempt、prompt、workspace、limits、lease_expires_at、created_at |
| `task_cancel` | `task_id`, `attempt` | 请求取消；Agent 应停止并确认 |
| `task_result_ack` | `task_id`, `attempt` | 结果已持久化，本地可停止重发 |
| `permission_decision` | `task_id`, `request_id`, `granted`, `reason` | 用户决定 |
| `error` | `code`, `message`, `request_id` | 协议错误 |
| `server_shutdown` | `reason`, `retry_after_secs` | 服务端维护，客户端应退避重连 |

### 重连与幂等

- 云端不会因为重连而把终态任务改回运行态。
- 本地 `started` 且云端 attempt 相同：重发同一 assignment，不增加 attempt。
- 本地缺少记录而云端任务活动：标记 `unknown`，不判成功。
- 本地 `started` 但云端没有下发记录：标记 `unknown` 并取消该 attempt，握手后的 pending 泵送不会重放它。
- 有副作用的 attempt 不自动重放；用户必须显式调用 retry 生成新 attempt。
- 客户端按指数退避与抖动重连；默认初始 500 ms、最大 30 秒、抖动 750 ms、无限重试。设备 JWT 过期前应重新换取令牌。

## 限制

云端默认值：请求体 2 MiB、请求超时 300 秒、每调用方每分钟 120 个 REST 请求、WebSocket 单条消息 1 MiB、任务租约 120 秒、租约扫描 15 秒、优雅关闭 20 秒。均可在 `CloudConfig`/环境变量中调整。

任务租约到期后由 reaper 标记 `unknown`，并记录 `coder_cloud_tasks_unknown_total` 指标。
