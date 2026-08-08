// Coder CLI (.NET 11 port) — interactive REPL and slash command router.
// Ported from src/ui/repl.ts and src/ui/slash.ts.

using Coder.Commands;
using Coder.Config;
using Coder.Core;
using Coder.Localization;
using Coder.Memory;
using Coder.Providers;
using Coder.Skills;
using Coder.Tools;
using Coder.Types;
using Coder.Ui;
using Coder.Utils;

namespace Coder.Cli;

/// <summary>Mutable session state shared between the REPL loop and slash commands.</summary>
public sealed class ReplState
{
    public required CoderConfig Config { get; set; }
    public required AgentLoop Agent { get; init; }
    public required StreamRenderer Renderer { get; init; }
    public required ToolRegistry Registry { get; init; }
    public required PermissionEngine PermEngine { get; init; }
    public SkillLoader? SkillLoader { get; set; }
    public HookManager? HookManager { get; set; }
    public string SystemText { get; set; } = "";
    public string ProjectContext { get; set; } = "";
    public string SkillList { get; set; } = "";
    public bool ShouldExit { get; set; }

    /// <summary>Re-derive system prompt + session context after a config change.</summary>
    public void RebuildPrompts()
    {
        SystemText = SystemPrompt.Build(Config);
        Agent.SetConfig(Config);
        Agent.SetSystemPrompt(SystemText);
        Agent.SetSessionContext(RuntimeContext.BuildSessionContext(Config, ProjectContext, SkillList));
    }
}

/// <summary>Slash command registry and dispatcher.</summary>
public static class SlashCommands
{
    public sealed record CommandDef(string Name, string Args, string Zh, string En)
    {
        public string Help => I18n.Current.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? Zh : En;
        public string Signature => Name + (string.IsNullOrEmpty(Args) ? "" : " " + Args);
    }

    public static readonly List<CommandDef> All = new()
    {
        new("/help", "", "显示可用命令", "show available commands"),
        new("/status", "", "会话状态：模型、用量、上下文", "session status: model, usage, context"),
        new("/config", "", "打印当前生效配置", "print effective configuration"),
        new("/model", "[name]", "查看或切换模型", "show or switch model"),
        new("/provider", "[name]", "查看或切换厂商", "show or switch provider"),
        new("/permission-mode", "[mode]", "循环或设置权限模式", "cycle or set permission mode"),
        new("/clear", "", "清空对话历史", "clear conversation history"),
        new("/compact", "", "压缩上下文，保留近期轮次", "compact context, keep recent turns"),
        new("/skills", "[list|load|install]", "技能：列出 / 加载 / 安装", "skills: list, load, install"),
        new("/memory", "[list|read|write]", "长期记忆：列出 / 读取 / 写入", "memory: list, read, write"),
        new("/init", "", "生成 CODER.md 项目规则", "generate CODER.md project rules"),
        new("/cwd", "[path]", "查看或切换工作目录", "show or change working directory"),
        new("/exit", "", "退出", "exit"),
    };

    public static List<string> Names() => All.Select(c => c.Name).ToList();

    /// <summary>Returns true when the input was consumed as a slash command.</summary>
    public static async Task<bool> TryHandleAsync(string input, ReplState st)
    {
        if (!input.StartsWith('/')) return false;

        var parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? string.Join(' ', parts[1..]).Trim() : "";

        switch (cmd)
        {
            case "/exit" or "/quit":
                st.ShouldExit = true;
                return true;

            case "/help":
                PrintHelp();
                return true;

            case "/status":
                PrintStatus(st);
                return true;

            case "/config":
                PrintConfig(st);
                return true;

            case "/clear":
                st.Agent.SetMessages(new List<Message>());
                Console.Out.WriteLine(Theme.Success("✓ ") + Theme.Dim("对话历史已清空 / conversation cleared"));
                return true;

            case "/compact":
                Compact(st);
                return true;

            case "/model":
                SwitchModel(st, rest);
                return true;

            case "/provider":
                SwitchProvider(st, rest);
                return true;

            case "/permission-mode":
                SwitchPermissionMode(st, rest);
                return true;

            case "/cwd":
                SwitchCwd(st, rest);
                return true;

            case "/skills":
                await SkillsAsync(st, rest);
                return true;

            case "/memory":
                await MemoryAsync(st, rest);
                return true;

            case "/init":
                await InitAsync(st);
                return true;

            default:
                Console.Out.WriteLine(Theme.Warn($"未知命令 {cmd} — 输入 /help 查看可用命令"));
                return true;
        }
    }

    // ── /help ───────────────────────────────────────────────────────────────
    private static void PrintHelp()
    {
        var zh = I18n.Current.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var lines = new List<string>();
        var width = All.Max(c => TextFmt.DisplayWidth(c.Signature)) + 2;
        var descWidth = Math.Max(16, Chrome.BoxInner - 2 - width);
        foreach (var c in All)
        {
            var left = TextFmt.PadVisible(c.Signature, width);
            lines.Add(Theme.Info(left) + Theme.Dim(TextFmt.TruncateVisible(c.Help, descWidth)));
        }
        lines.Add("");
        lines.Add(Theme.Dim(zh
            ? "直接输入内容即可对话；Ctrl+C 中断当前回合。"
            : "Type anything to chat; Ctrl+C aborts the current turn."));
        Console.Out.Write(Chrome.BoxPanel(lines, "Commands"));
    }

    // ── /status ─────────────────────────────────────────────────────────────
    private static void PrintStatus(ReplState st)
    {
        var cfg = st.Config;
        var usage = st.Agent.GetUsage();
        var msgs = st.Agent.GetMessages();
        var used = Tokens.Estimate(st.SystemText) + msgs.Sum(Tokens.EstimateMessage);
        var budget = Tokens.Budget(used, cfg.ContextWindow);
        var caps = ProviderRegistry.GetProviderCaps(cfg.Provider, cfg.BaseUrl);

        var lines = new List<string>
        {
            Row("provider", $"{cfg.Provider}{(caps != null ? Theme.Dim($"  ({caps.DisplayName})") : "")}"),
            Row("model", cfg.Model),
            Row("subagent", cfg.SubagentModel),
            Row("permission", StatusLine.ModeLabel(cfg.PermissionMode)),
            Row("effort", cfg.EffortLevel.ToString().ToLowerInvariant()),
            Row("cwd", cfg.Cwd),
            "",
            Row("messages", msgs.Count.ToString()),
            Row("context", $"{budget.Used:N0} / {budget.Total:N0}  ({budget.PctUsed:P1})"),
            Row("tokens in", usage.InputTokens.ToString("N0")),
            Row("tokens out", usage.OutputTokens.ToString("N0")),
        };
        if (usage.CacheReadTokens > 0 || usage.CacheWriteTokens > 0)
            lines.Add(Row("cache r/w", $"{usage.CacheReadTokens:N0} / {usage.CacheWriteTokens:N0}"));
        Console.Out.Write(Chrome.BoxPanel(lines, "Status"));
    }

    private static string Row(string label, string value)
        => Theme.Muted(label.PadRight(12)) + value;

    // ── /config ─────────────────────────────────────────────────────────────
    private static void PrintConfig(ReplState st)
    {
        var c = st.Config;
        var lines = new List<string>
        {
            Row("provider", c.Provider),
            Row("model", c.Model),
            Row("baseUrl", c.BaseUrl ?? Theme.Dim("(default)")),
            Row("apiKey", string.IsNullOrEmpty(c.ApiKey) ? Theme.Warn("(unset)") : Mask(c.ApiKey!)),
            Row("maxTokens", c.MaxTokens.ToString("N0")),
            Row("temperature", c.Temperature.ToString("0.##")),
            Row("maxIters", c.MaxIterations.ToString()),
            Row("contextWin", c.ContextWindow.ToString("N0")),
            Row("permission", c.PermissionMode.ToString()),
            Row("theme", c.Theme.ToString()),
            Row("effort", c.EffortLevel.ToString()),
            Row("lang", c.Lang ?? I18n.Current),
            Row("cnMode", c.CnMode ? "on" : "off"),
            Row("deepOpt", c.DeepOptimize ? "on" : "off"),
            Row("stream", c.StreamOutput ? "on" : "off"),
            Row("userDir", c.UserConfigDir),
            Row("agentsFile", c.AgentsFile ?? Theme.Dim("(none)")),
            Row("skillPaths", c.SkillPaths.Count.ToString() + Theme.Dim(" dirs")),
        };
        Console.Out.Write(Chrome.BoxPanel(lines, "Config"));
    }

    private static string Mask(string key)
        => key.Length <= 8 ? "••••" : key[..4] + new string('•', 6) + key[^4..];

    // ── /compact ────────────────────────────────────────────────────────────
    private static void Compact(ReplState st)
    {
        var before = st.Agent.GetMessages();
        if (before.Count == 0)
        {
            Console.Out.WriteLine(Theme.Dim("对话为空，无需压缩 / nothing to compact"));
            return;
        }
        var sysTokens = Tokens.Estimate(st.SystemText);
        var usedBefore = sysTokens + before.Sum(Tokens.EstimateMessage);
        var after = Compaction.Compact(before, sysTokens, ContextWindow.CompactionConfigFor(st.Config));
        st.Agent.SetMessages(after);
        var usedAfter = sysTokens + after.Sum(Tokens.EstimateMessage);
        Console.Out.WriteLine(Theme.Success("✓ ") +
            Theme.Dim($"上下文已压缩：{before.Count} → {after.Count} 条消息，约 {usedBefore:N0} → {usedAfter:N0} tokens"));
    }

    // ── /model ──────────────────────────────────────────────────────────────
    private static void SwitchModel(ReplState st, string rest)
    {
        if (string.IsNullOrEmpty(rest))
        {
            Console.Out.WriteLine(Theme.Info("model  ") + st.Config.Model);
            var def = ProviderRegistry.DefaultModelFor(st.Config.Provider);
            if (def != null) Console.Out.WriteLine(Theme.Dim($"       默认: {def}   用法: /model <name>"));
            return;
        }
        st.Config = st.Config with { Model = rest };
        st.RebuildPrompts();
        Console.Out.WriteLine(Theme.Success("✓ ") + Theme.Dim($"模型已切换为 {rest}"));
    }

    // ── /provider ───────────────────────────────────────────────────────────
    private static void SwitchProvider(ReplState st, string rest)
    {
        if (string.IsNullOrEmpty(rest))
        {
            Console.Out.WriteLine(Theme.Info("provider  ") + st.Config.Provider);
            var lines = ProviderRegistry.ListProviderCaps()
                .Select(p => Theme.Info(p.id.PadRight(14)) +
                             Theme.Dim(p.caps.DisplayName + (p.caps.RequiresApiKey ? "  (API key)" : "  (local)")))
                .ToList();
            lines.Add("");
            lines.Add(Theme.Dim("用法: /provider <name>"));
            Console.Out.Write(Chrome.BoxPanel(lines, "Providers"));
            return;
        }

        var id = rest.ToLowerInvariant();
        if (!ProviderRegistry.IsKnownProvider(id) && string.IsNullOrEmpty(st.Config.BaseUrl))
        {
            Console.Out.WriteLine(Theme.Error($"未知厂商 '{id}' — 自定义端点请配合 --base-url"));
            return;
        }

        var cfg = st.Config with { Provider = id };
        var model = ProviderRegistry.DefaultModelFor(id);
        if (model != null) cfg = cfg with { Model = model };
        var baseUrl = ProviderRegistry.DefaultBaseURLFor(id, cfg.CnMode);
        cfg = cfg with { BaseUrl = baseUrl };

        var envKeys = ProviderRegistry.ApiKeyEnvVarsFor(id);
        string? key = null;
        foreach (var k in envKeys)
        {
            var v = Environment.GetEnvironmentVariable(k);
            if (!string.IsNullOrEmpty(v)) { key = v; break; }
        }
        cfg = cfg with { ApiKey = key };

        var ctx = ProviderRegistry.DefaultContextWindowFor(id);
        if (ctx is int n && n > 0) cfg = cfg with { ContextWindow = n };

        st.Config = cfg;
        st.RebuildPrompts();
        ContextWindow.SyncProviderEnv(id);
        ContextWindow.SyncContextWindowEnv(cfg.ContextWindow);

        Console.Out.WriteLine(Theme.Success("✓ ") + Theme.Dim($"厂商已切换为 {id}  (model: {cfg.Model})"));
        if (string.IsNullOrEmpty(cfg.ApiKey) && (ProviderRegistry.GetProviderCaps(id, cfg.BaseUrl)?.RequiresApiKey ?? true))
            Console.Out.WriteLine(Theme.Warn("  ! ") + Theme.Dim($"未检测到密钥，请设置 {string.Join(" / ", envKeys)}"));
    }

    // ── /permission-mode ────────────────────────────────────────────────────
    private static void SwitchPermissionMode(ReplState st, string rest)
    {
        PermissionMode mode;
        if (string.IsNullOrEmpty(rest))
        {
            mode = StatusLine.Next(st.Config.PermissionMode);
        }
        else
        {
            mode = rest.ToLowerInvariant().Replace("-", "") switch
            {
                "default" => PermissionMode.Default,
                "acceptedits" => PermissionMode.AcceptEdits,
                "bypasspermissions" or "bypass" => PermissionMode.BypassPermissions,
                "plan" => PermissionMode.Plan,
                _ => st.Config.PermissionMode,
            };
        }
        st.Config = st.Config with { PermissionMode = mode };
        st.PermEngine.SetMode(mode);
        st.RebuildPrompts();
        Console.Out.WriteLine(Theme.Success("✓ ") + Theme.Dim("权限模式: ") + StatusLine.ModeLabel(mode));
    }

    // ── /cwd ────────────────────────────────────────────────────────────────
    private static void SwitchCwd(ReplState st, string rest)
    {
        if (string.IsNullOrEmpty(rest))
        {
            Console.Out.WriteLine(Theme.Info("cwd  ") + st.Config.Cwd);
            return;
        }
        var target = Path.GetFullPath(rest, st.Config.Cwd);
        if (!Directory.Exists(target))
        {
            Console.Out.WriteLine(Theme.Error($"目录不存在: {target}"));
            return;
        }
        st.Config = st.Config with { Cwd = target };
        st.RebuildPrompts();
        Console.Out.WriteLine(Theme.Success("✓ ") + Theme.Dim($"工作目录: {target}"));
    }

    // ── /skills ─────────────────────────────────────────────────────────────
    private static async Task SkillsAsync(ReplState st, string rest)
    {
        if (st.SkillLoader == null)
        {
            Console.Out.WriteLine(Theme.Warn("技能系统未启用"));
            return;
        }

        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "list";
        var arg = parts.Length > 1 ? parts[1].Trim() : "";

        switch (sub)
        {
            case "load":
                if (string.IsNullOrEmpty(arg)) { Console.Out.WriteLine(Theme.Dim("用法: /skills load <name>")); return; }
                try
                {
                    var skill = await st.SkillLoader.LoadAsync(arg);
                    Console.Out.Write(Chrome.BoxPanel(new[]
                    {
                        Row("name", skill.Name),
                        Row("origin", skill.Origin.ToString().ToLowerInvariant()),
                        Row("source", skill.Source),
                        "",
                        Theme.Dim(skill.Description),
                    }, "Skill"));
                    Console.Out.Write(Markdown.Render(skill.Body));
                }
                catch (Exception e) { Console.Out.WriteLine(Theme.Error(e.Message)); }
                return;

            case "install":
                if (string.IsNullOrEmpty(arg))
                {
                    Console.Out.WriteLine(Theme.Dim("可安装: " + string.Join(", ", SkillInstaller.ListInstallable())));
                    return;
                }
                var res = await SkillInstaller.InstallAsync(arg, st.Config.UserConfigDir);
                Console.Out.WriteLine((res.Ok ? Theme.Success("✓ ") : Theme.Error("✗ ")) + res.Message);
                if (res.Ok)
                {
                    st.SkillLoader = new SkillLoader(SkillPaths.Resolve(st.Config.Cwd, st.Config.UserConfigDir, st.Config.SkillPaths));
                    await RefreshSkillListAsync(st);
                }
                return;

            default:
                var found = await st.SkillLoader.DiscoverAsync();
                if (found.Count == 0)
                {
                    Console.Out.WriteLine(Theme.Dim("未发现技能。搜索路径:"));
                    foreach (var p in st.SkillLoader.GetPaths()) Console.Out.WriteLine(Theme.Muted("  " + p));
                    return;
                }
                var w = found.Max(s => s.Name.Length) + 2;
                var lines = found.Select(s =>
                    Theme.Info(s.Name.PadRight(w)) +
                    Theme.Dim(TextFmt.TruncateVisible(s.Description, Math.Max(20, Chrome.BoxInner - w - 2)))).ToList();
                Console.Out.Write(Chrome.BoxPanel(lines, $"Skills ({found.Count})"));
                return;
        }
    }

    internal static async Task RefreshSkillListAsync(ReplState st)
    {
        if (st.SkillLoader == null) { st.SkillList = ""; return; }
        try
        {
            var skills = await st.SkillLoader.DiscoverAsync();
            st.SkillList = string.Join("\n", skills.Select(s => $"- {s.Name}: {s.Description}"));
        }
        catch { st.SkillList = ""; }
        st.RebuildPrompts();
    }

    // ── /memory ─────────────────────────────────────────────────────────────
    private static async Task MemoryAsync(ReplState st, string rest)
    {
        var store = new MemoryStore(st.Config.UserConfigDir, st.Config.Cwd);
        var parts = rest.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "list";

        switch (sub)
        {
            case "write":
                if (parts.Length < 3)
                {
                    Console.Out.WriteLine(Theme.Dim("用法: /memory write <topic> <content>"));
                    return;
                }
                var path = await store.WriteAsync(MemoryScope.Project, parts[1], parts[2]);
                Console.Out.WriteLine(Theme.Success("✓ ") + Theme.Dim(path));
                return;

            case "read":
                if (parts.Length < 2) { Console.Out.WriteLine(Theme.Dim("用法: /memory read <topic>")); return; }
                var entry = await store.ReadAsync(MemoryScope.Project, parts[1])
                            ?? await store.ReadAsync(MemoryScope.User, parts[1]);
                if (entry == null) { Console.Out.WriteLine(Theme.Warn($"未找到记忆 '{parts[1]}'")); return; }
                Console.Out.Write(Chrome.BoxPanel(new[]
                {
                    Row("scope", entry.Scope),
                    Row("updated", entry.UpdatedAt),
                    Row("path", entry.Path),
                }, $"Memory: {entry.Topic}"));
                Console.Out.Write(Markdown.Render(entry.Content));
                return;

            default:
                var all = await store.ListAsync();
                if (all.Count == 0) { Console.Out.WriteLine(Theme.Dim("暂无记忆条目")); return; }
                var w = all.Max(e => e.Topic.Length) + 2;
                var lines = all.Select(e =>
                    Theme.Info(e.Topic.PadRight(w)) +
                    Theme.Muted(e.Scope.PadRight(9)) +
                    Theme.Dim(e.UpdatedAt)).ToList();
                Console.Out.Write(Chrome.BoxPanel(lines, $"Memory ({all.Count})"));
                return;
        }
    }

    // ── /init ───────────────────────────────────────────────────────────────
    private static async Task InitAsync(ReplState st)
    {
        var target = Path.Combine(st.Config.Cwd, "CODER.md");
        if (File.Exists(target) && !Prompter.Confirm($"CODER.md 已存在，覆盖?", false))
        {
            Console.Out.WriteLine(Theme.Dim("已取消"));
            return;
        }
        try
        {
            var written = await InitCommand.GenerateAgentsAsync(st.Config,
                info => Console.Out.WriteLine(Theme.Dim("  " + info)));
            Console.Out.WriteLine(Theme.Success("✓ ") + Theme.Dim($"已生成 {written}"));
            var ctx = await ProjectContextLoader.LoadAsync(st.Config.Cwd, st.Config.UserConfigDir);
            st.ProjectContext = ProjectContextLoader.Render(ctx);
            st.RebuildPrompts();
        }
        catch (Exception e) { StreamRenderer.PrintError(e.Message); }
    }
}

/// <summary>The interactive read-eval-print loop.</summary>
public static class Repl
{
    public static async Task RunAsync(ReplState st)
    {
        Console.Out.Write(Banner.Render(st.Config));
        Console.Out.Write(SuggestionLines(st.Config));

        st.Renderer.SetStatusSource(() => new StatusModel
        {
            Phase = st.Renderer.CurrentPhase,
            ContextUsed = Cli.EstimateContext(st.Config, st.Agent),
            ContextTotal = st.Config.ContextWindow,
            Mode = st.Config.PermissionMode,
            InputTokens = st.Agent.GetUsage().InputTokens,
            OutputTokens = st.Agent.GetUsage().OutputTokens,
            Turn = st.Agent.GetMessages().Count,
            Model = $"{st.Config.Provider}/{st.Config.Model}",
            Cwd = st.Config.Cwd,
        });

        using var cancel = new ConsoleCancelHandler(st);

        while (!st.ShouldExit)
        {
            string? input;
            try { input = LineInput.Read(Theme.Brand(">") + " ", SlashCommands.Names()); }
            catch (IOException) { break; }
            if (input == null) break;
            input = input.Trim();
            if (input.Length == 0) continue;

            if (await SlashCommands.TryHandleAsync(input, st)) continue;

            var cfg = st.Config;
            if (string.IsNullOrEmpty(cfg.ApiKey) &&
                (ProviderRegistry.GetProviderCaps(cfg.Provider, cfg.BaseUrl)?.RequiresApiKey ?? true))
            {
                Cli.PrintMissingApiKeyHints(cfg);
                continue;
            }

            // UserPromptSubmit hook:block 拦截本轮,additionalContext 追加到 prompt。
            if (st.HookManager != null)
            {
                var pd = await st.HookManager.UserPromptSubmitAsync(input);
                if (pd.Block)
                {
                    Console.Out.WriteLine(Theme.Dim(pd.Reason ?? "Prompt blocked by hook"));
                    continue;
                }
                if (!string.IsNullOrEmpty(pd.AdditionalContext)) input += "\n\n" + pd.AdditionalContext;
            }

            Cli.RefreshProvider(cfg, st.Agent);
            st.Renderer.BeginTurn();
            var aborted = false;
            try
            {
                await st.Agent.Run(input);
            }
            catch (AbortError) { aborted = true; StreamRenderer.PrintInfo(I18n.T("repl.interrupted")); }
            catch (CoderError e) { StreamRenderer.PrintError(e.Message); }
            catch (Exception e) { StreamRenderer.PrintError(e.Message); Log.Instance.Debug(e.ToString()); }
            st.Renderer.EndTurn(aborted);

            AutoCompact(st);
        }

        if (st.HookManager != null)
        {
            try { await st.HookManager.StopAsync(); } catch { }
        }
        Console.Out.WriteLine("\n" + Theme.Dim(I18n.T("repl.exit.bye")));
    }

    /// <summary>欢迎框下方的建议行:当前模型/思考力度 + 常用命令提示。</summary>
    private static string SuggestionLines(CoderConfig cfg)
    {
        var zh = I18n.Current.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var effort = cfg.EffortLevel.ToString();
        var model = $"{cfg.Provider}/{cfg.Model}";
        var l1 = zh
            ? $"使用 {model} · {effort} 思考力度 — 兼顾 token 消耗与能力的最佳平衡"
            : $"Use {model} with {effort} thinking effort - for the best balance between token spend and capability";
        var l2 = zh
            ? "运行 /help 查看命令 · /model 切换模型 · Shift+Tab 切换权限模式"
            : "Run /help for commands · /model to switch model · Shift+Tab to cycle permission mode";
        return Theme.Brand("✦ ") + l1 + "\n" + Theme.Dim(l2) + "\n\n";
    }

    /// <summary>Trim history automatically once the context budget crosses the threshold.</summary>
    private static void AutoCompact(ReplState st)
    {
        var cc = ContextWindow.CompactionConfigFor(st.Config);
        var sysTokens = Tokens.Estimate(st.SystemText);
        var msgs = st.Agent.GetMessages();
        var used = sysTokens + msgs.Sum(Tokens.EstimateMessage);
        if (used < cc.MaxContextTokens * cc.TrimThreshold) return;

        var after = Compaction.Compact(msgs, sysTokens, cc);
        if (after.Count == msgs.Count) return;
        st.Agent.SetMessages(after);
        Console.Out.WriteLine(Theme.Dim($"  (auto-compact: {msgs.Count} → {after.Count} 条消息)"));
    }

    /// <summary>Ctrl+C aborts the current turn instead of killing the process.</summary>
    private sealed class ConsoleCancelHandler : IDisposable
    {
        private readonly ConsoleCancelEventHandler _handler;

        public ConsoleCancelHandler(ReplState st)
        {
            _handler = (_, e) => { e.Cancel = true; st.Agent.Abort(); };
            Console.CancelKeyPress += _handler;
        }

        public void Dispose() => Console.CancelKeyPress -= _handler;
    }
}
