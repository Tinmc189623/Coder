# Coder

Coder 是一个可自主执行编程任务的 Rust 智能体，由本地执行引擎与云端协调服务组成。模型可以调查代码库、读写文件、运行命令与测试、查看 Git 状态，并根据真实构建/测试错误继续修复。所有文件与命令操作都发生在用户明确授权的本地工作区内。

Nexsteaduser 维护此项目，联系邮箱：admin@nexsteaduser.com。

## 能力

- **三种运行方式**：`coder run` 直接在本机执行；`coder agent run` 连接云端接受调度；云端可使用自带模型代理或让 Agent 直连模型端点。
- **真实工具执行**：文件读取/写入/精确替换、全文与符号搜索、终端命令、进程树超时终止、Git 状态/差异、任务计划、结果验证。
- **安全边界**：路径穿越与符号链接逃逸拦截、`.git` 元数据保护、只读模式、命令环境变量白名单、高风险操作审批、提示词注入不改变工具权限。
- **可靠的任务状态机**：`pending → assigned → running → succeeded/failed/canceled/unknown`。断线后未收到结果不会标记成功；有副作用的尝试不会盲目重放；租约过期进入 `unknown`，迟到的真实结果仍可落账。
- **设备身份与 TLS**：一次性配对令牌、Argon2 设备密钥与 API key、JWT 短期令牌、HTTPS/WSS、管理员常量时间密钥比较。
- **可运维性**：健康/就绪/指标端点、结构化日志、请求 ID、限流、请求体限制、超时、优雅关闭、SQLite/PostgreSQL 持久化。
- **CI/CD**：三平台 CI；`v*` 标签发布五个平台的二进制、校验和与多架构 GHCR 镜像；手动工作流经 SSH 部署生产 Compose 栈并验证就绪状态。

## 工作区结构

| crate | 作用 |
| --- | --- |
| `crates/protocol` | 版本化 REST/WebSocket 协议类型与任务状态 |
| `crates/core` | 模型适配器、Agent 循环、工具、权限、工作区、SQLite 会话 |
| `crates/local-agent` | 设备注册、加密凭据、任务账本、WebSocket 客户端、断线恢复 |
| `crates/cloud` | Axum 服务、认证、限流、任务存储、设备 Hub、模型代理、PostgreSQL 迁移 |
| `crates/cli` | `coder` 命令：直接执行、Agent、云端管理、诊断、会话查看 |

## 构建与测试

要求 Rust 1.94 或更高版本；已在 1.94.0（MSRV）与 1.96.0 stable 上验证编译。

```powershell
cargo build --workspace
cargo fmt --all --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
```

在 Windows 11 x64 上，使用 Rust 1.96.0 stable 运行完整工作区测试，共 57 项全部通过，覆盖核心 Agent、工具安全、云端 HTTP/WebSocket、TLS 握手、断线恢复，以及真实本地 Agent 经云端下发写文件并回传成功的完整链路。测试清单与未验证项见下文。

## 直接执行任务

```powershell
cargo build --release -p coder-cli
$env:CODER_MODEL__BASE_URL = "https://api.example.com/v1"
$env:CODER_MODEL__MODEL = "your-model"
$env:CODER_MODEL__API_KEY = "your-key"
.\target\release\coder.exe doctor
.\target\release\coder.exe run -C G:\work\project "运行测试并修复失败"
```

模型端点必须是 OpenAI 兼容的 `/chat/completions` 接口。`base_url` 可写 `https://host/v1`，代码会补全路径；也可以直接写到 `/chat/completions`。非 HTTPS 端点默认拒绝，只有显式设置 `model.allow_insecure_http=true` 且用于可信回环地址时才允许。

配置文件默认位于平台配置目录（Windows 为 `%APPDATA%\coder`，Linux 为 `~/.config/coder`，macOS 为 `~/Library/Application Support/coder`）。`coder config init` 生成模板；密钥建议通过环境变量注入。

常用开关：`--read-only` 禁止写入与命令，`--yes` 自动批准高风险操作，`--json` 输出 JSON Lines 事件，`--verbose` 显示迭代与 token 统计。

## 云端与设备配对

启动云端（生产环境请配置 PostgreSQL 与 TLS）：

```powershell
$env:CODER_CLOUD_JWT_SECRET = "<至少 32 字符>"
$env:CODER_CLOUD_ADMIN_API_KEY = "<至少 24 字符>"
$env:CODER_CLOUD_DATABASE_URL = "postgres://coder:password@127.0.0.1:5432/coder"
$env:CODER_CLOUD_TLS_CERT_PATH = "C:\coder\tls\cert.pem"
$env:CODER_CLOUD_TLS_KEY_PATH = "C:\coder\tls\key.pem"
.\target\release\coder.exe serve
```

创建用户与一次性配对令牌：

```powershell
$env:CODER_CLOUD_URL = "https://coder.example.com"
$env:CODER_ADMIN_KEY = "<管理员密钥>"
coder admin create-user --email user@example.com
coder admin create-pairing --user-id <用户 ID>
```

在目标机器注册并运行 Agent：

```powershell
coder agent register --server-url https://coder.example.com --pairing-token <一次性令牌> --name workstation
coder agent run --server-url https://coder.example.com --workspace G:\work\project --model-mode direct
```

`--model-mode direct` 使用 Agent 本机的模型配置；`--model-mode cloud` 通过云端模型代理推理，Agent 只需持有设备令牌。设备密钥存入 Windows DPAPI 或 Unix 0600 凭据文件。

创建任务：

```powershell
coder tasks create --device-id <设备 ID> --workspace G:\work\project --prompt "修复 cargo test 暴露的失败"
coder tasks permissions <任务 ID>
coder tasks decide <任务 ID> <权限请求 ID> --grant
```

完整接口见 [docs/API.md](docs/API.md) 与 [docs/openapi.yaml](docs/openapi.yaml)；部署见 [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)；安全模型见 [docs/SECURITY.md](docs/SECURITY.md)。

## 恢复语义

| 断线时本地状态 | 云端记录 | 重连后的行为 |
| --- | --- | --- |
| 未开始 | 任意活动状态 | 云端按原 attempt 下发或保留 pending，不会重复执行已开始的工作 |
| 已开始 | 同一 attempt 活动 | 重发同一 assignment，继续同一次尝试 |
| 已开始 | 已有终态 | 发送取消引用，本地停止并核对终态，云端不回退 |
| 本地无记录 | 活动任务 | 标记 `unknown`，不判定成功，也不自动补发 |
| 本地已开始但云端无下发 | pending | 标记 `unknown` 并取消该 attempt，避免握手后被 pending 泵送重放 |
| 结果未确认 | 活动任务 | 本地账本重发同一 attempt 的终态结果，云端幂等确认 |

`unknown` 只表示“结果无法确认”。只有迟到且可校验的真实结果可以把它推进到 `succeeded`、`failed` 或 `canceled`。

## 已验证与未验证

已实际运行验证：

- `cargo fmt --all --check`、`cargo clippy --workspace --all-targets -- -D warnings`、`cargo test --workspace`（Rust 1.96.0 stable，57 项）、`cargo build --workspace --release`（Rust 1.96.0 stable）。
- 真实 `AgentClient` 连接真实云端路由：配对注册、令牌交换、WebSocket 下发、模型工具调用、工作区写文件、任务终态 `succeeded` 全部在同一测试进程内完成。
- `cargo +1.94.0 check --workspace --all-targets` 通过，MSRV 与 `rust-version` 一致。
- `cargo deny check`（cargo-deny 0.20.2）通过：advisories、bans、licenses、sources 全部 ok。
- 工具安全测试覆盖真实文件写入/编辑/读取、父目录穿越、工作区外绝对路径、`.git` 保护、Windows junction 逃逸、只读拒绝、真实命令输出与退出码、超时进程树终止、输出上限。
- 云端测试覆盖管理员认证、配对令牌一次性消费、设备密钥、用户数据隔离、任务创建/取消/重试、权限请求与批准、WebSocket 下发/心跳/结果确认、限流 429、TLS 健康检查、HTTPS 注册、WSS 会话、四类断线恢复路径。
- TLS 使用进程内自签名证书与真实 rustls 握手；测试同时确认明文 HTTP 不能从 TLS 监听端口获得应用响应。

尚未在本机验证，需在对应环境执行：

- PostgreSQL 实库迁移与并发行为（代码编译通过，迁移在连接时自动运行；本次测试使用内存存储）。
- 真实外部模型 API 的端到端任务（需要有效端点、模型名与密钥）。
- Linux/macOS 的实际运行（当前仅完成 Windows 11 x64 的运行验证；仓库已提供三平台 CI 工作流，尚未在 GitHub Actions 上实际运行）。
- systemd、launchd、Windows 计划任务、Docker Compose、Caddy 的实际部署（模板已提供，未在本机启动）。
- 浏览器/前端界面不存在；本项目只提供 CLI 与 HTTP/WebSocket API。

## 文档

- [API](docs/API.md)：REST/WebSocket 认证、错误码与消息语义。
- [OpenAPI](docs/openapi.yaml)：可直接导入客户端生成器或接口调试工具。
- [部署](docs/DEPLOYMENT.md)：Windows/Linux/macOS、PostgreSQL、TLS、反向代理、升级回滚与备份。
- [安全](docs/SECURITY.md)：信任边界、凭据、权限约束、威胁模型与密钥轮换。

## 许可证

MIT。版权声明见 [LICENSE](LICENSE)。

Copyright © 2026 Nexsteaduser. All Rights Reserved.
