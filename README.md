<div align="center">

# ◆ Coder CLI

**production-grade AI coding agent for the terminal** — written in **C# / .NET 11**

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

(The banner above is a rendered example — the real banner shows the runtime
version from `Brand.Version` and the configured provider/model, see
`Banner.Render` in `src/Ui.cs`.)

**Docs:** English · [简体中文](README.zh-CN.md) · [Changelog](CHANGELOG.md)

## Repository layout

| Path | What it is |
|------|------------|
| `Coder.csproj` | The project file (root-level) |
| `src/` | C# sources — 32 files, namespaces `Coder.{Cli,Core,Providers,Tools,Ui,Config,Skills,Memory,Agents,…}` |
| `tests/unit/` | C# test project (reflection-based runner, no xUnit) |
| `dist/` | Stale TypeScript build output from the superseded `archive/coder-ts/` tree — not part of the .NET build |
| `archive/coder-ts/` | The original TypeScript/Node implementation (superseded, kept for reference) |
| `archive/web/` | The legacy local web server / Web UI, dropped from the CLI-first design |

## Terminal UI

Nexsteaduser violet palette (electric violet / cyan / amber, matching the logo).
Interactive sessions get: streaming markdown (headings, lists, tables, fenced
code with syntax highlighting), live tool-call cards with progress states, a
persistent bottom status bar (phase machine, context usage, permission mode,
token counters), arrow-key menus, and a permission dialog with 200ms
anti-misclick gate and `y/a/n/e` shortcuts. Piped output stays plain text.

## Build & run

```bash
dotnet build                       # -> bin/Debug/net11.0/coder.exe
dotnet run -- --help
dotnet run -- --version

# one-shot prompt
dotnet run -- --provider openai --model gpt-4o --api-key sk-... "explain this repo"

# local model, no key required
dotnet run -- --provider ollama --model llama3.1

# any OpenAI-compatible endpoint
dotnet run -- --provider openai --base-url http://localhost:11434/v1 --api-key x "hi"

# interactive REPL: just run with no prompt
dotnet run
```

Requires the **.NET 11 SDK** (`<TargetFramework>net11.0</TargetFramework>` in
`Coder.csproj`).

The project file sits at the repository root, so it disables the SDK's default
`**/*.cs` glob and compiles `src/**/*.cs` explicitly — otherwise stray sources under
`archive/` and the vendored Node payload in `build/` would be swept into the build.

## Configuration

Layered **TOML** config, highest priority first: **CLI flags** → project
`./.coder/settings.toml` → user `~/.coder/settings.toml` → env vars (fallback only).

```toml
# ./.coder/settings.toml  (project)  |  ~/.coder/settings.toml  (user)
provider = "anthropic"
model = "claude-sonnet-4-5"
effortLevel = "high"
contextWindow = 200000

[env]                        # injected into the process environment
OPENAI_API_KEY = "sk-…"
```

Recognised keys include the provider API-key env vars (`ANTHROPIC_API_KEY`,
`OPENAI_API_KEY`, `DEEPSEEK_API_KEY`, `KIMI_API_KEY`, `MINIMAX_API_KEY`,
`OLLAMA_API_KEY`), endpoint overrides (`OPENAI_BASE_URL`, `OLLAMA_HOST` /
`OLLAMA_BASE_URL`), `CN_MODE` (when on, catalog providers fall back to their
China-mirror base URL where one is defined), and the
`CODER_CLI_*` family for CLI defaults: `CODER_CLI_PROVIDER`, `CODER_CLI_MODEL`,
`CODER_CLI_BASE_URL`, `CODER_CLI_AUTH_TOKEN`, `CODER_CLI_LANG`, `CODER_CLI_CONTEXT_WINDOW`,
`CODER_CLI_EFFORT_LEVEL`, `CODER_CLI_SUBAGENT_MODEL`, `CODER_CLI_DEEP_OPTIMIZE`.

A legacy `~/.coder-cli/config.json` is migrated to `~/.coder/settings.toml` once
on first run (the old file is kept).

### Rules files

Project rules, first match wins: `CODER.md` → `CLAUDE.md` → `AGENTS.md` →
`AGENT.md` (README.md as fallback). Global rules: `~/.coder/CODER.md`.
Both are injected into the session context (global first, project after).

### Event hooks

Five events, defined in either settings file under `[hooks]` as array-of-tables.
Each hook spawns a process; stdout follows the Claude Code JSON protocol.

```toml
[hooks]
[[hooks.PreToolUse]]
matcher = "bash|file_edit"     # optional regex on tool name / prompt
command = "sh .coder/hooks/guard.sh"
timeout = 30                   # seconds, default 60
blocked = true                 # PreToolUse only: deny without spawning

[[hooks.PostToolUse]]
command = "notify.sh"
```

`PreToolUse` can deny (JSON `{"permissionDecision":"deny",…}` or exit code 2) or
rewrite arguments (`{"args":{…}}`); `UserPromptSubmit` can block or append
context (`{"block":true}` / `{"additionalContext":"…"}`); `SessionStart` stdout
is appended to the session context; `Stop` fires on session end. Hooks receive
`CODER_HOOK_*` environment variables (event, cwd, tool name/input/id/result, prompt).

### Agent templates

Custom subagents as frontmatter files, project `.coder/agents/*.md` then user
`~/.coder/agents/*.md` (same name → project wins):

```markdown
---
name: "security-review"
description: "Reviews code for security issues"
tools: file_read, glob, grep, bash
model: "claude-sonnet-4-5"
---
You are a security reviewer. Focus on OWASP Top 10…
```

The template body becomes the subagent's system prompt; templates appear in the
`Task` tool's `subagent_type` options.

On first interactive run a setup wizard asks for language, provider and key.

## Providers

Native wire protocols: **Anthropic** (`/v1/messages`) and **OpenAI-compatible**
(`/v1/chat/completions`), both with SSE streaming and tool calling.

Built in: `anthropic`, `openai`, `deepseek`, `kimi`, `minimax`, plus an
OpenAI-compatible catalog covering `qwen`, `zhipu`, `siliconflow`, `ollama`,
`groq`, `mistral`, `openrouter`, `gemini`. The provider is also inferred from a
`--base-url` or from whichever API-key env var is present.

## Tools

`bash`, `file_read`, `file_write`, `file_edit`, `glob`, `grep`, `web_fetch`,
`web_search`, `todo_write`, `Task` (subagent), `skill`, `memory`, `ask_user`.

Every mutating tool goes through the permission engine. Modes:
`default` · `acceptEdits` · `bypassPermissions` · `plan`.

## REPL commands

```
/help                        show commands            /clear      reset history
/status                      model, usage, context    /compact    compress context
/config                      effective configuration  /cwd        show/change directory
/model [name]                show/switch model        /skills     list | load | install
/provider [name]             show/switch provider     /memory     list | read | write
/permission-mode [mode]      cycle permission mode    /init       generate CODER.md
/exit
```

`Shift+Tab` cycles permission mode, `Ctrl+C` aborts the current turn, `Ctrl+D` exits.
UI language: `en` and `zh-CN`.

## Tests

```bash
dotnet run --project tests/unit               # C# unit tests (reflection runner)
```

The unit project (`tests/unit/`) compiles the production `src/` tree verbatim
(minus `Program.cs`) and runs `[Test]` methods on the real implementations via a
small reflection-based runner — no mocking framework, no external test SDK. It
covers file editing, glob, permissions, SSE parsing, text formatting, themes and
token counting.

The end-to-end smoke suite (mock LLM server + scenario driver) is internal
tooling and does not ship with the repository.

## License

<div align="center">

Copyright © 2026 **Nexsteaduser**. All Rights Reserved.

Released under the [Apache License 2.0](LICENSE) · Contact: `admin@nexsteaduser.com`

</div>
