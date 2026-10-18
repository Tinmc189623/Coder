# 部署

本文覆盖 Windows 11、Linux 与 macOS 上的云端、本地 Agent、PostgreSQL、TLS、反向代理、升级与回滚。所有命令均针对已构建的真实二进制，不依赖脚本模拟。

## 拓扑

三种可用组合：

1. **直接 TLS**：`coder-cloud` 直接监听 443/8080，加载证书，Agent 直连。限流能拿到真实连接 IP。
2. **反向代理**：Caddy 终止 TLS，云端在私网监听明文 HTTP。必须设置 `CODER_CLOUD_ALLOW_INSECURE_HTTP=true`，并且只有代理端口可达。
3. **本机联调**：绑定 127.0.0.1，允许明文，仅用于开发。生产任务包含真实文件与命令，不建议长期这样跑。

云端保存用户、设备、任务与权限记录；模型推理可以发生在 Agent 本机（`model_mode=direct`）或云端代理（`cloud`）。Agent 必须能访问它被授权的工作区，云端不需要挂载这些目录。

## 构建产物

```text
target/release/coder.exe        # Windows CLI：run/agent/serve/admin/doctor
target/release/coder-cloud.exe  # 仅云端服务
target/release/coder            # Linux/macOS CLI
target/release/coder-cloud      # Linux/macOS 云端服务
```

`coder serve` 与 `coder-cloud` 启动同一套服务；`coder-cloud` 只读取环境变量与 `CODER_CLOUD_CONFIG`，适合容器与 systemd。

构建与检查：

```powershell
cargo build --release --workspace
cargo test --workspace
```

要求 Rust 1.94+、PostgreSQL 14+。Windows 需要 MSVC 生成工具；Linux 需要 `pkg-config` 与 C 编译器（SQLx/aws-lc-rs 构建依赖）；macOS 需要 Xcode Command Line Tools。

## PostgreSQL

创建数据库与角色（在数据库主机上执行）：

```bash
sudo -u postgres psql
CREATE ROLE coder LOGIN;
\password coder
CREATE DATABASE coder OWNER coder;
\q
```

`\password` 会提示输入并再次确认，密码不会进入 shell 历史。生成随机密码：

```bash
openssl rand -hex 32
```

连接串写入云端环境变量：

```text
CODER_CLOUD_DATABASE_URL=postgres://coder:<密码>@127.0.0.1:5432/coder
```

密码若包含 URL 保留字符，必须百分号编码；建议使用十六进制密码避免该问题。远程数据库启用 TLS：

```text
CODER_CLOUD_DATABASE_URL=postgres://coder:<密码>@db.internal:5432/coder?sslmode=verify-full
```

云端连接池默认最少 1、最多 20 个连接，连接超时 10 秒。启动时自动执行 `crates/cloud/migrations-postgres` 中内嵌的迁移；迁移失败会拒绝启动，不会带病运行。迁移是前向的：升级后回滚二进制前必须恢复对应版本的数据库备份。

`memory://` 仅用于测试，重启即丢失全部状态，启动时会打印警告。

## 云端配置

配置来源优先级：默认值 < TOML < `CODER_CLOUD_` 环境变量。嵌套字段用双下划线，例如 `CODER_CLOUD_MODEL__BASE_URL`。

| 变量 | 必填 | 说明 |
| --- | --- | --- |
| `CODER_CLOUD_BIND` | 是 | 默认 `127.0.0.1:8080`；直接 TLS 可写 `0.0.0.0:8443` |
| `CODER_CLOUD_PUBLIC_URL` | 建议 | 对外地址，用于日志与文档 |
| `CODER_CLOUD_DATABASE_URL` | 是 | `postgres://` 或测试用 `memory://` |
| `CODER_CLOUD_JWT_SECRET` | 是 | 至少 32 字符，生成 `openssl rand -hex 32` |
| `CODER_CLOUD_ADMIN_API_KEY` | 是 | 至少 24 字符，生成 `openssl rand -hex 24` |
| `CODER_CLOUD_TLS_CERT_PATH` | 直接 TLS 必填 | 证书链 PEM |
| `CODER_CLOUD_TLS_KEY_PATH` | 直接 TLS 必填 | 私钥 PEM |
| `CODER_CLOUD_ALLOW_INSECURE_HTTP` | 代理模式必填 | 非回环明文绑定必须显式为 `true` |
| `CODER_CLOUD_TRUST_PROXY_HEADERS` | 代理模式建议 | 信任 `X-Forwarded-For`，仅当端口只对代理开放 |
| `CODER_CLOUD_TOKEN_TTL_SECS` | 否 | 设备 JWT 有效期，60–86400，默认 900 |
| `CODER_CLOUD_PAIRING_TTL_SECS` | 否 | 配对令牌有效期，60–86400，默认 600 |
| `CODER_CLOUD_TASK_LEASE_SECS` | 否 | 任务租约，30–3600，默认 120 |
| `CODER_CLOUD_LEASE_SWEEP_SECS` | 否 | 扫描间隔，5–300，默认 15 |
| `CODER_CLOUD_RATE_LIMIT_PER_MINUTE` | 否 | 每调用方每分钟上限，默认 120 |
| `CODER_CLOUD_REQUEST_BODY_LIMIT_BYTES` | 否 | 默认 2 MiB |
| `CODER_CLOUD_REQUEST_TIMEOUT_SECS` | 否 | 默认 300 |
| `CODER_CLOUD_MAX_WS_MESSAGE_BYTES` | 否 | 默认 1 MiB |
| `CODER_CLOUD_SHUTDOWN_GRACE_SECS` | 否 | 默认 20 |
| `CODER_CLOUD_MODEL__BASE_URL` | 模型代理必填 | OpenAI 兼容端点 |
| `CODER_CLOUD_MODEL__MODEL` | 模型代理必填 | 模型名 |
| `CODER_CLOUD_MODEL__API_KEY` | 模型代理必填 | 上游密钥 |
| `CODER_CLOUD_MODEL__ALLOW_INSECURE_HTTP` | 否 | 仅可信回环端点 |

`CODER_CLOUD_CONFIG` 指向 TOML 文件：

```toml
bind = "0.0.0.0:8443"
public_url = "https://coder.example.com"
database_url = "postgres://coder@127.0.0.1:5432/coder?sslmode=require"
tls_cert_path = "/etc/coder/tls/fullchain.pem"
tls_key_path = "/etc/coder/tls/privkey.pem"
token_ttl_secs = 900
rate_limit_per_minute = 120

[model]
base_url = "https://api.provider.example/v1"
model = "provider-model"
api_key = "从环境变量注入更安全"
```

密钥不要写进版本库。TOML 与 env 文件都应限制为服务账户可读。

## TLS

### 直接 TLS

证书的 SAN 必须包含 Agent 使用的域名。用现有证书：

```bash
sudo install -d -m 0750 -o root -g coder /etc/coder/tls
sudo install -m 0644 fullchain.pem /etc/coder/tls/fullchain.pem
sudo install -m 0640 -o root -g coder privkey.pem /etc/coder/tls/privkey.pem
```

设置两个路径后启动。私钥权限过宽会导致启动失败或被安全审计标记。更新证书后重启服务；当前版本不支持热加载证书。

### Caddy 反向代理

`docker-compose.yml` 已包含 Caddy 与 Postgres。复制模板并生成三个密钥：

```powershell
Copy-Item .env.example .env
$bytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
[Convert]::ToHexString($bytes).ToLower()
```

把输出分别填入 `POSTGRES_PASSWORD`、`CODER_CLOUD_JWT_SECRET`，再生成一次填入 `CODER_CLOUD_ADMIN_API_KEY`。DNS 将 `CODER_DOMAIN` 指向主机，开放 80/443，然后：

```powershell
docker compose up -d --build
docker compose ps
docker compose logs -f cloud
```

Caddy 自动申请与续期证书，代理 WebSocket，并将 `/api/v1/metrics` 对外返回 404。云端容器不发布端口，只能经 Caddy 访问，这是 `CODER_CLOUD_TRUST_PROXY_HEADERS=true` 的前提。

本机验证可设 `CODER_DOMAIN=localhost`，Caddy 使用内部 CA；Agent 默认不信任该 CA，需要把 Caddy 根证书装入系统信任库，或改用直接 TLS。局域网生产不要用 `localhost`。

## Linux

### 云端 systemd

```bash
sudo useradd --system --home /var/lib/coder --shell /usr/sbin/nologin coder
sudo install -d -m 0750 -o coder -g coder /var/lib/coder
sudo install -d -m 0755 -o root -g root /opt/coder/bin
sudo install -m 0755 target/release/coder-cloud /opt/coder/bin/coder-cloud
sudo install -m 0644 deploy/systemd/coder-cloud.service /etc/systemd/system/coder-cloud.service
sudo systemctl daemon-reload
sudo systemctl enable --now coder-cloud
sudo systemctl status coder-cloud
```

环境变量文件：

```bash
sudo install -d -m 0750 -o root -g coder /etc/coder
sudo sh -c 'umask 027; {
  printf "CODER_CLOUD_BIND=%s\n" "0.0.0.0:8443"
  printf "CODER_CLOUD_PUBLIC_URL=%s\n" "https://coder.example.com"
  printf "CODER_CLOUD_DATABASE_URL=%s\n" "postgres://coder@127.0.0.1:5432/coder?sslmode=require"
  printf "CODER_CLOUD_JWT_SECRET=%s\n" "$(openssl rand -hex 32)"
  printf "CODER_CLOUD_ADMIN_API_KEY=%s\n" "$(openssl rand -hex 24)"
  printf "CODER_CLOUD_TLS_CERT_PATH=%s\n" "/etc/coder/tls/fullchain.pem"
  printf "CODER_CLOUD_TLS_KEY_PATH=%s\n" "/etc/coder/tls/privkey.pem"
} > /etc/coder/cloud.env'
sudo chown root:coder /etc/coder/cloud.env
sudo chmod 0640 /etc/coder/cloud.env
```

unit 已启用 `NoNewPrivileges`、`ProtectSystem=strict`、空 capability 集合、系统调用过滤与地址族限制。云端只需要读取证书/配置、连接网络与 Postgres；若把 PostgreSQL 放在 Unix socket 上，确认 `RestrictAddressFamilies` 已包含 `AF_UNIX`。

日志：

```bash
journalctl -u coder-cloud -f
journalctl -u coder-cloud --since "1 hour ago"
```

### Agent 用户服务

```bash
install -d -m 0755 ~/.local/bin ~/.config/systemd/user
install -m 0755 target/release/coder ~/.local/bin/coder
install -m 0644 deploy/systemd/coder-agent.service ~/.config/systemd/user/coder-agent.service
systemctl --user daemon-reload
systemctl --user enable --now coder-agent
loginctl enable-linger "$USER"
```

Agent 配置 `~/.config/coder/agent.toml`：

```toml
server_url = "https://coder.example.com"
device_name = "workstation"
workspace_roots = ["/home/alice/work"]
model_mode = "direct"
heartbeat_secs = 20
approval_timeout_secs = 120
max_parallel_tasks = 1

[runner.workspace]
root = "/home/alice/work"
writable = true
protect_git_dir = true

[runner.permissions]
mode = "workspace_write"
auto_approve_max_risk = "medium"
auto_approve_high_risk = false
```

模型密钥放 `~/.config/coder/agent.env`（0600）：`CODER_RUNNER_MODEL__API_KEY=...`。环境变量前缀为 `CODER_`，嵌套字段双下划线。首次注册：

```bash
coder agent register --server-url https://coder.example.com --pairing-token <一次性令牌> --name workstation
```

注册后删除配对令牌配置。凭据写入 `~/.local/share/coder/device.json`，权限 0600；任务账本 `agent.db` 同目录。

## macOS

### 云端 LaunchDaemon

```bash
sudo sysadminctl -addUser coder -shell /usr/bin/false -home /var/coder -password -
sudo install -d -m 0755 -o root -g wheel /usr/local/coder/bin /usr/local/coder/etc
sudo install -d -m 0750 -o coder -g staff /usr/local/coder/log /var/coder
sudo install -m 0755 target/release/coder-cloud /usr/local/coder/bin/coder-cloud
sudo install -m 0640 -o root -g staff cloud.toml /usr/local/coder/etc/cloud.toml
sudo install -m 0644 deploy/launchd/com.nexsteaduser.coder-cloud.plist /Library/LaunchDaemons/
sudo chown root:wheel /Library/LaunchDaemons/com.nexsteaduser.coder-cloud.plist
sudo launchctl bootstrap system /Library/LaunchDaemons/com.nexsteaduser.coder-cloud.plist
sudo launchctl print system/com.nexsteaduser.coder-cloud
```

`cloud.toml` 放非密钥字段与环境变量不能覆盖的设置；密钥仍建议写入仅 coder 可读的环境文件或 TOML。launchd 不解析 shell 环境文件，因此示例将两份数据都放在 0640 的 `cloud.toml` 中，由 root:staff 持有。

日志位于 `/usr/local/coder/log/`。macOS 的 `launchd` 会自动重启异常退出的进程，`ThrottleInterval=10` 防止崩溃循环。

### Agent LaunchAgent

```bash
install -d -m 0755 ~/.local/bin
install -m 0755 target/release/coder ~/.local/bin/coder
chmod +x deploy/launchd/install-agent.sh
CODER_BIN="$HOME/.local/bin/coder" AGENT_CONFIG="$HOME/.config/coder/agent.toml" \
    deploy/launchd/install-agent.sh
launchctl print "gui/$(id -u)/com.nexsteaduser.coder-agent"
```

脚本在安装时展开当前用户的绝对路径，写入 `~/Library/LaunchAgents`，因此不存在跨用户路径错误。卸载：

```bash
launchctl bootout "gui/$(id -u)/com.nexsteaduser.coder-agent"
rm ~/Library/LaunchAgents/com.nexsteaduser.coder-agent.plist
```

## Windows 11

所有 PowerShell 命令要求 7.0+。

### 准备

```powershell
cargo build --release -p coder-cli
New-Item -ItemType Directory -Force 'C:\Program Files\Coder','C:\ProgramData\Coder\tools' | Out-Null
Copy-Item .\target\release\coder.exe 'C:\Program Files\Coder\coder.exe'
Invoke-WebRequest 'https://nssm.cc/release/nssm-2.24.zip' -OutFile 'C:\ProgramData\Coder\tools\nssm.zip'
Expand-Archive 'C:\ProgramData\Coder\tools\nssm.zip' 'C:\ProgramData\Coder\tools\nssm' -Force
Copy-Item 'C:\ProgramData\Coder\tools\nssm\nssm-2.24\win64\nssm.exe' 'C:\ProgramData\Coder\tools\nssm.exe'
```

创建 `C:\ProgramData\Coder\cloud.toml`，字段与 Linux TOML 相同，路径使用 Windows 形式：

```toml
bind = "0.0.0.0:8443"
public_url = "https://coder.example.com"
database_url = "postgres://coder@127.0.0.1:5432/coder?sslmode=require"
tls_cert_path = "C:\\ProgramData\\Coder\\tls\\fullchain.pem"
tls_key_path = "C:\\ProgramData\\Coder\\tls\\privkey.pem"
```

密钥同样放在 TOML，或通过 NSSM 的环境变量注入。限制 ACL：

```powershell
icacls.exe 'C:\ProgramData\Coder\cloud.toml' /inheritance:r /grant:r 'SYSTEM:F' 'Administrators:F' 'NT AUTHORITY\LOCAL SERVICE:R'
icacls.exe 'C:\ProgramData\Coder\tls\privkey.pem' /inheritance:r /grant:r 'SYSTEM:F' 'Administrators:F' 'NT AUTHORITY\LOCAL SERVICE:R'
```

### 注册服务

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\deploy\windows\install-cloud-service.ps1 -NssmPath 'C:\ProgramData\Coder\tools\nssm.exe'
Get-Service CoderCloud
Invoke-RestMethod 'http://127.0.0.1:8080/api/v1/health'
```

脚本使用 NSSM 启动 `coder.exe serve --config ...`，账户为 `NT AUTHORITY\LocalService`，只授予数据目录与日志的所需权限。NSSM 通过控制台停止信号让云端优雅关闭；超过 30 秒后终止。日志位于 `C:\ProgramData\Coder\cloud.stdout.log` 与 `cloud.stderr.log`，10 MiB 轮转。

TLS 直连时健康检查改用 `https://localhost:8443` 并在请求中包含可信证书；证书 SAN 必须覆盖使用的主机名。

### Agent 计划任务

Agent 需要当前用户的 DPAPI 凭据与工作区，因此不要用 LocalSystem 运行。复制二进制并准备配置：

```powershell
New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\Coder\bin" | Out-Null
Copy-Item .\target\release\coder.exe "$env:LOCALAPPDATA\Coder\bin\coder.exe"
.\target\release\coder.exe config init
# 编辑 %APPDATA%\coder\config.toml 改为 agent 配置，或直接创建 %APPDATA%\coder\agent.toml
.\deploy\windows\install-agent-task.ps1
```

任务以当前用户身份在登录时启动，失败后每分钟重试。机器重启后需要用户登录才会恢复 Agent；这是 DPAPI 绑定用户账户的直接结果。若要求未登录也运行，应为专用服务账户注册设备、授予工作区权限，并使用 NSSM 以该账户运行，同时用该账户重新执行 `coder agent register`。

## 用户、配对与首个任务

```powershell
$env:CODER_CLOUD_URL = 'https://coder.example.com'
$env:CODER_ADMIN_KEY = '<管理员密钥>'
coder admin create-user --email owner@example.com
coder admin create-pairing --user-id <用户 ID>
```

在 Agent 机器：

```powershell
coder agent register --server-url https://coder.example.com --pairing-token <一次性令牌> --name workstation
coder agent run --server-url https://coder.example.com --workspace G:\work\project --model-mode direct
```

在用户机器：

```powershell
$env:CODER_API_KEY = '<用户 API key>'
coder devices list
coder tasks create --device-id <设备 ID> --workspace G:\work\project --prompt "运行测试并修复失败"
coder tasks show <任务 ID>
coder tasks permissions <任务 ID>
coder tasks decide <任务 ID> <权限请求 ID> --grant
```

配对令牌只能使用一次；设备密钥只显示一次，放失后需删除设备并重新配对。用户 API key 也只显示一次。

## 升级与回滚

升级：

1. `pg_dump` 备份数据库与 `coder.db` 备份 Agent 账本。
2. 停止云端（`systemctl stop coder-cloud` / `launchctl bootout` / `nssm stop CoderCloud`）。
3. 替换 `coder-cloud`/`coder.exe`，保留上一版本二进制。
4. 启动并观察 `/api/v1/ready`。迁移在启动时自动执行，失败则进程退出。
5. 创建一条小任务验证真实下发与结果回传。

回滚：

1. 停止服务。
2. 恢复升级前的数据库备份到空数据库，修改连接串指向恢复后的库。
3. 放回上一版本二进制。
4. 启动并验证 `/api/v1/ready`。

不要只回退二进制而保留已迁移的数据库：迁移是前向的，旧代码不保证理解新 schema。Agent 账本没有内嵌迁移；跨大版本升级前同样备份，若 schema 不兼容则删除账本会让未完成任务在云端转为 `unknown`，这是安全但会丢失本地执行记录。

## 备份

PostgreSQL：

```bash
pg_dump -Fc -U coder -h 127.0.0.1 coder > coder-$(date +%F).dump
pg_restore -d coder_restore --clean --if-exists coder-2026-10-18.dump
```

每日备份，保留至少 14 天，异地保存。备份包含设备哈希与任务记录，不含明文设备密钥或 API key。

Agent 账本：

| 平台 | 文件 |
| --- | --- |
| Windows | `%LOCALAPPDATA%\coder\agent.db`、`device.json` |
| Linux | `~/.local/share/coder/agent.db`、`device.json` |
| macOS | `~/Library/Application Support/coder/agent.db`、`device.json` |

Windows 的 `device.json` 由 DPAPI 保护，只能在原用户、原机器解密。账本应在停止 Agent 后复制。

TLS 私钥、`cloud.env`/`cloud.toml` 与数据库备份分开保存，限制访问。

## 运维与安全基线

- 云端口只允许 443（或代理端口）；PostgreSQL 只允许云端主机访问。不要向公网暴露 `memory://` 或明文 8080。
- `/api/v1/metrics` 无认证，必须限制为监控网络；Caddy 模板已对外隐藏。
- 日志为结构化 tracing 输出，不记录 API key、设备密钥或密码；数据库 URL 在启动日志中脱敏。
- 健康检查：`/api/v1/health`；就绪检查：`/api/v1/ready`；`/api/v1/metrics` 暴露任务与模型计数。
- 反向代理模式必须保证云端端口不被绕过，否则客户端可伪造 `X-Forwarded-For` 绕过限流。
- 云端以最小权限账户运行。Linux unit 清空 capability 并启用严格文件系统保护；Windows 服务使用 LocalService；macOS 使用无登录 shell 的 coder 用户。
- 定期轮换管理员密钥需要重启服务；设备 JWT 到期自动通过设备密钥续取，设备密钥泄露时删除设备并重新配对。
- 任务租约过期会进入 `unknown` 而不是成功；这是有意的安全语义。排查时查看 `coder_cloud_tasks_unknown_total`。

## 故障排查

| 现象 | 检查 |
| --- | --- |
| 启动即退出，提示 `jwt_secret` | 密钥长度是否 ≥32，env 文件是否被 systemd 读取 |
| `refusing plaintext HTTP on a non-loopback bind` | 配置 TLS，或确认代理模式并显式设置 `ALLOW_INSECURE_HTTP=true` |
| `failed to load TLS certificate or key` | 证书链与私钥是否匹配、权限是否可读 |
| Agent 一直重连 401 | 设备 JWT 是否过期、设备是否被删除或撤销、系统时间 |
| 任务停留在 `pending` | 设备是否 online、WebSocket 是否完成 hello/welcome、工作区是否在授权根目录内 |
| 任务变 `unknown` | 租约到期或 Agent 丢失本地记录；查看云端与 Agent 日志，不要把它当成功 |
| 反向代理后限流异常 | 是否启用 `trust_proxy_headers`，代理是否覆盖客户端提供的 `X-Forwarded-For` |
| PostgreSQL 连接失败 | `pg_isready`、`pg_hba.conf`、TLS 模式、连接池上限 |

Copyright © 2026 Nexsteaduser. All Rights Reserved.
