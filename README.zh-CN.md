<div align="center">

# ◆ Coder CLI

**面向终端的生产级 AI 编程智能体** —— 使用 **C# / .NET 11** 编写

![Version](https://img.shields.io/badge/version-0.3.0-af87ff?style=flat-square)
![.NET](https://img.shields.io/badge/.NET-11-00d7ff?style=flat-square)
![License](https://img.shields.io/badge/license-Apache%202.0-ffaf00?style=flat-square)
![Platform](https://img.shields.io/badge/platform-Windows-af87ff?style=flat-square)
![Language](https://img.shields.io/badge/language-C%23-00d7ff?style=flat-square)

```
◆ Coder CLI  v0.3.0  ·  anthropic/claude-sonnet-4-5
production-grade AI coding agent for the terminal
```

</div>

(上方的 banner 是示例——真实 banner 显示运行时版本号与当前配置的提供商/模型,见 `Brand.Version` 与 `Banner.Render`(src/Ui.cs)。)

**文档语言:** [English](README.md) · **简体中文**

## 仓库结构

| 路径 | 说明 |
|------|------|
| `Coder.csproj` | 项目文件(仓库根) |
| `src/` | C# 源码 — 32 个文件,命名空间 `Coder.{Cli,Core,Providers,Tools,Ui,Config,Skills,Memory,Agents,…}` |
| `tests/unit/` | C# 测试项目(反射 runner,无 xUnit) |
| `installer/` | 图形安装器源码(WinForms,自包含单文件) |
| `logo.ico` | 品牌图标(16–256 px,嵌入主程序与安装器) |
| `CHANGELOG.md` | 版本变更记录 |
| `dist/` | 旧 TypeScript 构建产物(已被 `archive/coder-ts/` 取代,不属于 .NET 构建) |
| `archive/coder-ts/` | 最初的 TypeScript/Node 实现(已被取代,留作参考) |
| `archive/web/` | 遗留的本地 Web 服务 / Web UI(已从 CLI 优先设计中移除) |

## 终端界面

Nexsteaduser 紫色系色板(电光紫 / 青蓝 / 琥珀,与 logo.ico 一致)。交互会话提供:流式 markdown(标题、列表、表格、带语法高亮的代码围栏)、工具调用卡片(进行中/成功/失败状态)、常驻底部状态行(回合状态机、上下文用量、权限模式、token 计数)、方向键菜单、带 200ms 防误触与 `y/a/n/e` 快捷键的权限对话框。管道输出保持纯文本。

## 构建与运行

```bash
dotnet build                       # -> bin/Debug/net11.0/coder.exe
dotnet run -- --help
dotnet run -- --version

# 一次性提示
dotnet run -- --provider openai --model gpt-4o --api-key sk-... "解释这个仓库"

# 本地模型,无需 API key
dotnet run -- --provider ollama --model llama3.1

# 任意 OpenAI 兼容端点
dotnet run -- --provider openai --base-url http://localhost:11434/v1 --api-key x "你好"

# 交互式 REPL:直接运行不带提示词
dotnet run
```

需要 **.NET 11 SDK**(`Coder.csproj` 中 `<TargetFramework>net11.0</TargetFramework>`)。

项目文件位于仓库根,因此禁用了 SDK 默认的 `**/*.cs` 通配,显式编译 `src/**/*.cs`——否则 `archive/` 与 `build/` 下的散落源码会被卷入构建。

## 配置

分层 **TOML** 配置,优先级从高到低:**CLI 参数** → 项目 `./.coder/settings.toml` → 用户 `~/.coder/settings.toml` → 环境变量(仅兜底)。

```toml
# ./.coder/settings.toml(项目)  |  ~/.coder/settings.toml(用户)
provider = "anthropic"
model = "claude-sonnet-4-5"
effortLevel = "high"
contextWindow = 200000

[env]                        # 注入进程环境
OPENAI_API_KEY = "sk-…"
```

已识别的键包括提供商 API-key 环境变量(`ANTHROPIC_API_KEY`、`OPENAI_API_KEY`、`DEEPSEEK_API_KEY`、`KIMI_API_KEY`、`MINIMAX_API_KEY`、`OLLAMA_API_KEY`)、端点覆盖(`OPENAI_BASE_URL`、`OLLAMA_HOST` / `OLLAMA_BASE_URL`)、`CN_MODE`(开启时兼容目录提供商回退到已定义的国内镜像 base URL),以及用于 CLI 默认值的 `CODER_CLI_*` 系列:`CODER_CLI_PROVIDER`、`CODER_CLI_MODEL`、`CODER_CLI_BASE_URL`、`CODER_CLI_AUTH_TOKEN`、`CODER_CLI_LANG`、`CODER_CLI_CONTEXT_WINDOW`、`CODER_CLI_EFFORT_LEVEL`、`CODER_CLI_SUBAGENT_MODEL`、`CODER_CLI_DEEP_OPTIMIZE`。

旧 `~/.coder-cli/config.json` 首次运行时会一次性迁移到 `~/.coder/settings.toml`(旧文件保留)。

### 规则文件

项目规则,首个命中生效:`CODER.md` → `CLAUDE.md` → `AGENTS.md` → `AGENT.md`(README.md 兜底)。全局规则:`~/.coder/CODER.md`。两者注入会话上下文(全局在前、项目在后)。

### 事件钩子(hooks)

五个事件,定义在任一 settings 文件的 `[hooks]` 段(array-of-tables)。每个 hook 启动一个进程;stdout 遵循 Claude Code JSON 协议。

```toml
[hooks]
[[hooks.PreToolUse]]
matcher = "bash|file_edit"     # 可选:正则匹配工具名/提示词
command = "sh .coder/hooks/guard.sh"
timeout = 30                   # 秒,默认 60
blocked = true                 # 仅 PreToolUse:不 spawn 直接拒绝

[[hooks.PostToolUse]]
command = "notify.sh"
```

`PreToolUse` 可拒绝(JSON `{"permissionDecision":"deny",…}` 或退出码 2)或改写参数(`{"args":{…}}`);`UserPromptSubmit` 可拦截或追加上下文(`{"block":true}` / `{"additionalContext":"…"}`);`SessionStart` 的 stdout 追加进会话上下文;`Stop` 在会话结束时触发。钩子收到 `CODER_HOOK_*` 环境变量(事件、cwd、工具名/输入/ID/结果、提示词)。

### agent 模板

自定义子代理,frontmatter 格式,项目 `.coder/agents/*.md` 优先、用户 `~/.coder/agents/*.md` 兜底(同名项目覆盖):

```markdown
---
name: "security-review"
description: "Reviews code for security issues"
tools: file_read, glob, grep, bash
model: "claude-sonnet-4-5"
---
You are a security reviewer. Focus on OWASP Top 10…
```

模板正文成为该子代理的系统提示;模板名出现在 `Task` 工具的 `subagent_type` 选项中。

首次交互运行时,设置向导会询问语言、提供商与 API key。

## 提供商

原生协议:**Anthropic**(`/v1/messages`)与 **OpenAI 兼容**(`/v1/chat/completions`),均支持 SSE 流式与工具调用。

内置:`anthropic`、`openai`、`deepseek`、`kimi`、`minimax`,另有 OpenAI 兼容目录覆盖 `qwen`、`zhipu`、`siliconflow`、`ollama`、`groq`、`mistral`、`openrouter`、`gemini`。提供商也可以从 `--base-url` 或已设置的 API-key 环境变量推断。

## 工具

`bash`、`file_read`、`file_write`、`file_edit`、`glob`、`grep`、`web_fetch`、`web_search`、`todo_write`、`Task`(子代理)、`skill`、`memory`、`ask_user`。

所有修改类工具都经过权限引擎。模式:`default` · `acceptEdits` · `bypassPermissions` · `plan`。

## REPL 命令

```
/help                        显示可用命令            /clear      清空对话历史
/status                      模型、用量、上下文      /compact    压缩上下文
/config                      打印当前生效配置        /cwd        查看/切换工作目录
/model [name]                查看/切换模型          /skills     技能:列出 / 加载 / 安装
/provider [name]             查看/切换厂商          /memory     长期记忆:列出 / 读取 / 写入
/permission-mode [mode]      循环或设置权限模式      /init       生成 CODER.md 项目规则
/exit                        退出
```

`Shift+Tab` 循环切换权限模式,`Ctrl+C` 中断当前回合,`Ctrl+D` 退出。
界面语言:`en` 与 `zh-CN`。

## 测试

```bash
dotnet run --project tests/unit               # C# 单元测试(反射 runner)
```

单元测试项目(`tests/unit/`)原样编译生产 `src/` 树(排除 `Program.cs`),通过轻量反射 runner 对真实实现运行 `[Test]` 方法——无 mock 框架、无外部测试 SDK。覆盖文件编辑、glob、权限、SSE 解析、文本格式化、主题与 token 计数。

端到端冒烟测试(mock LLM 服务器 + 场景驱动)是内部工具,不随仓库分发。

## 许可

<div align="center">

Copyright © 2026 **Nexsteaduser**. All Rights Reserved.

基于 [Apache License 2.0](LICENSE) 发布 · 联系:`admin@nexsteaduser.com`

</div>
