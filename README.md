<div align="center">

<h1>🤖 Coder</h1>

<p><strong>在你的授权工作区里，真实地写文件、跑命令、修测试。</strong></p>

<p>
  <a href="https://github.com/Tinmc189623/Coder/actions/workflows/ci.yml"><img src="https://github.com/Tinmc189623/Coder/actions/workflows/ci.yml/badge.svg?branch=main" alt="CI"></a>
  <a href="https://github.com/Tinmc189623/Coder/actions/workflows/release.yml"><img src="https://github.com/Tinmc189623/Coder/actions/workflows/release.yml/badge.svg?branch=main" alt="Release"></a>
  <img src="https://img.shields.io/badge/rust-1.94%2B-dea584?style=flat-square&logo=rust&logoColor=white" alt="Rust 1.94+">
  <img src="https://img.shields.io/badge/license-MIT-2ea44f?style=flat-square" alt="MIT License">
  <img src="https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-4c8bf5?style=flat-square" alt="Windows, Linux, macOS">
</p>

<p><sub>由 Nexsteaduser 维护 · <a href="mailto:admin@nexsteaduser.com">admin@nexsteaduser.com</a></sub></p>

</div>

---

> **Coder 不是只会聊天的代码助手。** 它把模型放进真实的工程环境：读取代码库、修改文件、执行命令、运行构建与测试，再根据真实报错继续修复。所有动作都限制在用户明确授权的工作区内。

## ✨ 能做什么

<table>
  <tr>
    <td width="50%" valign="top">
      <h3>🧠 自主执行循环</h3>
      <p>规划、检索、调用工具、验证结果、根据失败继续修复。迭代次数、token、耗时与工具调用都有预算，任务可随时取消。</p>
    </td>
    <td width="50%" valign="top">
      <h3>🛠️ 真实工具链</h3>
      <p>文件读写与精确替换、全文和符号搜索、终端命令、构建测试、Git 状态与差异、任务计划、结果验证。命令支持工作目录、环境白名单、超时与进程树终止。</p>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <h3>🛡️ 看得见的安全边界</h3>
      <p>拦截路径穿越、符号链接与 Windows junction 逃逸，保护 <code>.git</code> 元数据，支持只读模式与高风险操作审批。提示词注入不能扩大工具权限。</p>
    </td>
    <td width="50%" valign="top">
      <h3>🔁 断线不撒谎</h3>
      <p>任务状态机与租约配合：没收到结果就不判定成功，有副作用的尝试不盲目重放，<code>unknown</code> 只接受迟到且可校验的真实结果。</p>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <h3>🔐 设备身份与加密传输</h3>
      <p>一次性配对令牌、Argon2 设备密钥与 API key、短期 JWT、HTTPS/WSS，管理员密钥使用常量时间比较。</p>
    </td>
    <td width="50%" valign="top">
      <h3>🚢 能交付，也能运维</h3>
      <p>健康、就绪与 Prometheus 指标，结构化日志、请求 ID、限流、请求体限制、超时、优雅关闭。SQLite 本地账本，PostgreSQL 云端存储，三平台 CI 与发布流水线。</p>
    </td>
  </tr>
</table>

## 🗺️ 架构一览

```mermaid
flowchart LR
    subgraph LOCAL["本地机器 · 授权工作区"]
        CLI["coder run"]
        AGENT["coder agent run"]
        CORE["Agent 循环"]
        TOOLS["文件 / 搜索 / 终端 / Git / 测试"]
        LEDGER[("SQLite 账本")]
        CLI --> CORE
        AGENT --> CORE
        CORE --> TOOLS
        AGENT --- LEDGER
    end

    subgraph CLOUD["云端"]
        API["Axum REST + WSS"]
        HUB["设备 Hub · 任务状态机"]
        STORE[("PostgreSQL / 内存存储")]
        API --- HUB
        HUB --- STORE
    end

    MODEL["OpenAI 兼容模型端点"]

    CORE -. direct 模式 .-> MODEL
    AGENT <-->|WSS / TLS| API
    HUB -. cloud 代理 .-> MODEL
```

| 运行方式 | 入口 | 模型调用 |
| --- | --- | --- |
| 本地直接执行 | `coder run` | 本机直连模型端点 |
| 云端调度 | `coder agent run` + `coder serve` | Agent 直连，或经云端模型代理 |
| 云本地协同 | 云端下发任务，本地执行工具 | 两种模型模式都支持 |

## 📦 工作区结构

| crate | 作用 |
| --- | --- |
| 🧩 `crates/protocol` | 版本化 REST/WebSocket 协议类型与任务状态 |
| ⚙️ `crates/core` | 模型适配器、Agent 循环、工具、权限、工作区、SQLite 会话 |
| 📡 `crates/local-agent` | 设备注册、加密凭据、任务账本、WebSocket 客户端、断线恢复 |
| ☁️ `crates/cloud` | Axum 服务、认证、限流、任务存储、设备 Hub、模型代理、PostgreSQL 迁移 |
| ⌨️ `crates/cli` | `coder` 命令：直接执行、Agent、云端管理、诊断、会话查看 |

## 🚀 快速开始

### 1. 构建与自检

需要 Rust 1.94 或更高版本；已在 1.94.0（MSRV）与 1.96.0 stable 上验证编译。Windows 示例使用 PowerShell 7。

```powershell
cargo build --workspace
cargo fmt --all --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
```

当前工作区共 **57 项测试**，覆盖核心 Agent、工具安全、云端 HTTP/WebSocket、TLS 握手、断线恢复，以及真实本地 Agent 经云端下发写文件并回传成功的完整链路。

### 2. 直接在本机执行

```powershell
cargo build --release -p coder-cli
$env:CODER_MODEL__BASE_URL = "https://api.your-provider.com/v1"
$env:CODER_MODEL__MODEL = "your-model"
$env:CODER_MODEL__API_KEY = "your-key"
.\target\release\coder.exe doctor
.\target\release\coder.exe run -C G:\work\project "运行测试并修复失败"
```

> 模型端点必须是 OpenAI 兼容的 `/chat/completions` 接口。`base_url` 可写 `https://host/v1`，代码会补全路径；也可以直接写到 `/chat/completions`。非 HTTPS 端点默认拒绝，只有显式设置 `model.allow_insecure_http=true` 且用于可信回环地址时才允许。

配置文件默认位于平台配置目录（Windows 为 `%APPDATA%\coder`，Linux 为 `~/.config/coder`，macOS 为 `~/Library/Application Support/coder`）。`coder config init` 生成模板，密钥建议通过环境变量注入。

常用开关：`--read-only` 禁止写入与命令，`--yes` 自动批准高风险操作，`--json` 输出 JSON Lines 事件，`--verbose` 显示迭代与 token 统计。

### 3. 启动云端并配对设备

生产环境请配置 PostgreSQL 与 TLS：

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

创建任务与处理权限请求：

```powershell
coder tasks create --device-id <设备 ID> --workspace G:\work\project --prompt "修复 cargo test 暴露的失败"
coder tasks permissions <任务 ID>
coder tasks decide <任务 ID> <权限请求 ID> --grant
```

## 🔁 恢复语义

| 断线时本地状态 | 云端记录 | 重连后的行为 |
| --- | --- | --- |
| 未开始 | 任意活动状态 | 云端按原 attempt 下发或保留 pending，不会重复执行已开始的工作 |
| 已开始 | 同一 attempt 活动 | 重发同一 assignment，继续同一次尝试 |
| 已开始 | 已有终态 | 发送取消引用，本地停止并核对终态，云端不回退 |
| 本地无记录 | 活动任务 | 标记 `unknown`，不判定成功，也不自动补发 |
| 本地已开始但云端无下发 | pending | 标记 `unknown` 并取消该 attempt，避免握手后被 pending 泵送重放 |
| 结果未确认 | 活动任务 | 本地账本重发同一 attempt 的终态结果，云端幂等确认 |

> `unknown` 只表示“结果无法确认”。只有迟到且可校验的真实结果，才能把它推进到 `succeeded`、`failed` 或 `canceled`。

## ✅ 已验证与 ⏳ 待验证

**已实际运行验证**

- `cargo fmt --all --check`、`cargo clippy --workspace --all-targets -- -D warnings`、`cargo test --workspace`（Rust 1.96.0 stable，57 项）、`cargo build --workspace --release`（Rust 1.96.0 stable）。
- 真实 `AgentClient` 连接真实云端路由：配对注册、令牌交换、WebSocket 下发、模型工具调用、工作区写文件、任务终态 `succeeded` 全部在同一测试进程内完成。
- `cargo +1.94.0 check --workspace --all-targets` 通过，MSRV 与 `rust-version` 一致。
- `cargo deny check`（cargo-deny 0.20.2）通过：advisories、bans、licenses、sources 全部 ok。
- 工具安全测试覆盖真实文件写入/编辑/读取、父目录穿越、工作区外绝对路径、`.git` 保护、Windows junction 逃逸、只读拒绝、真实命令输出与退出码、超时进程树终止、输出上限。
- 云端测试覆盖管理员认证、配对令牌一次性消费、设备密钥、用户数据隔离、任务创建/取消/重试、权限请求与批准、WebSocket 下发/心跳/结果确认、限流 429、TLS 健康检查、HTTPS 注册、WSS 会话、四类断线恢复路径。
- TLS 使用进程内自签名证书与真实 rustls 握手；测试同时确认明文 HTTP 不能从 TLS 监听端口获得应用响应。
- GitHub Actions CI 在 `ubuntu-latest`、`windows-latest`、`macos-latest` 上实际执行，fmt、clippy、workspace 测试、release 构建全部通过；MSRV 1.94、cargo-deny、OpenAPI lint 同样通过。
- Release 工作流成功构建并推送 `ghcr.io/tinmc189623/coder-cloud:edge` 多架构镜像，覆盖 `linux/amd64` 与 `linux/arm64`。

**尚未在本机验证，需在对应环境执行**

- PostgreSQL 实库迁移与并发行为（代码编译通过，迁移在连接时自动运行；本次测试使用内存存储）。
- 真实外部模型 API 的端到端任务（需要有效端点、模型名与密钥）。
- Linux/macOS 上的人工交互运行与真实部署（三平台 CI 已执行完整构建与测试，本机只做了 Windows 11 x64 的实际运行）。
- systemd、launchd、Windows 计划任务、Docker Compose、Caddy 的实际部署（模板已提供，未在本机启动）。
- 标签驱动的五平台二进制打包与 SSH 部署工作流尚未实际执行验证。
- 浏览器/前端界面不存在；本项目只提供 CLI 与 HTTP/WebSocket API。

## 📚 文档

| 文档 | 内容 |
| --- | --- |
| 📡 [API](docs/API.md) | REST/WebSocket 认证、错误码与消息语义 |
| 🧾 [OpenAPI](docs/openapi.yaml) | 可直接导入客户端生成器或接口调试工具 |
| 🚀 [部署](docs/DEPLOYMENT.md) | Windows/Linux/macOS、PostgreSQL、TLS、反向代理、升级回滚与备份 |
| 🛡️ [安全](docs/SECURITY.md) | 信任边界、凭据、权限约束、威胁模型与密钥轮换 |

---

<div align="center">
<sub>MIT Licensed · Copyright © 2026 Nexsteaduser. All Rights Reserved.</sub>
</div>
