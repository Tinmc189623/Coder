# Changelog

All notable changes to **Coder CLI** are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows SemVer
(see versioning rules: major = epic change, minor = ≥10 feature iteration, patch = routine update).

Released by [Nexsteaduser](https://github.com/Tinmc189623/Coder) · Copyright © 2026 Nexsteaduser. All Rights Reserved.

## [Unreleased]

- (none)

## [0.2.3] — 2026-08-06

### Changed

- Version bump 0.2.2 → 0.2.3 (patch release) across `Coder.csproj`, the installer project, README badges and installer window text.
- Repackaged all release artifacts at 0.2.3: MSIX (`CoderCLI-0.2.3-x64.msix`) and GUI installer (`coder-setup.exe`), both re-signed with the Nexsteaduser code-signing certificate (SHA256 + RFC3161 timestamp).
- `.gitignore` now excludes `.claude/` (local Claude Code runtime state).

## [0.2.2] — 2026-08-06

### Added

- `logo.ico` — multi-size brand icon (16–256 px) in the Nexsteaduser palette (electric violet / cyan / amber), wired into `Coder.csproj` via `<ApplicationIcon>`.
- Graphical WinForms installer (`installer/`): brand-styled window (purple gradient header, logo, live progress bar), extracts embedded payload to `%LOCALAPPDATA%\Programs\Coder CLI`, configures user PATH and Start-menu shortcut; `/uninstall` routes to the graphical uninstaller. Single-file self-contained (`net11.0-windows`, `UseWindowsForms`).
- MSIX packaging pipeline: `AppxManifest.xml`, asset PNGs generated from `logo.ico`, packed with MakeAppx and signed (`CoderCLI-0.2.2-x64.msix`, publisher `CN=Nexsteaduser, O=Nexsteaduser, C=CN`).
- HTML badges and centered brand header in the README.

### Fixed

- Installer completion-state buttons: the "Close" button was left disabled after install (the success path never reverted `SetBusy(true)`), and the lambda-based event unsubscribe was a no-op. Replaced with a named handler plus a `_completed` flag; the cancel button is hidden in the completion state.

### Removed

- The smoke suite (`tests/*.py`: `run_smoke.py`, `mock_provider.py`, `mutation_probe.py`) no longer ships with the repository — internal tooling only. The C# unit project (`tests/unit/`) remains.

## [0.2.1] — 2026-08-05

### Added

- Initial public release of the .NET 11 port: agent loop, providers (Anthropic + OpenAI-compatible with a compat catalog), 13 tools incl. `Task` subagent / `skill` / `memory` / `ask_user`, permission engine, REPL with slash commands, i18n (`en`, `zh-CN`), skills system, memory store, token accounting and compaction.
- Test infrastructure: Python smoke suite (internal) and a zero-dependency C# unit project (reflection-based runner compiling the production `src/` tree verbatim).
- Apache License 2.0 with the Nexsteaduser copyright notice.

[Unreleased]: https://github.com/Tinmc189623/Coder/compare/v0.2.3...HEAD
[0.2.3]: https://github.com/Tinmc189623/Coder/releases/tag/v0.2.3
[0.2.2]: https://github.com/Tinmc189623/Coder/releases/tag/v0.2.2
[0.2.1]: https://github.com/Tinmc189623/Coder/releases/tag/v0.2.1
