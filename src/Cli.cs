// Coder CLI (.NET 11 port) — main entry point.
// Wires CLI parsing, config, provider, registry, agent loop, REPL.
// Ported from src/cli.ts.

using Coder.Agents;
using Coder.Config;
using Coder.Core;
using Coder.Localization;
using Coder.Providers;
using Coder.Skills;
using Coder.Tools;
using Coder.Types;
using Coder.Ui;
using Coder.Utils;

namespace Coder.Cli;

public static class Cli
{
    public static async Task<int> Run(string[] argv)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        var parsed = ParseCliArgs(argv);
        Setup.MigrateLegacyIfPresent();   // 旧 ~/.coder-cli/config.json → ~/.coder/settings.toml(一次性)
        ConfigLoader.PreloadUserConfigEnv();
        I18n.ApplyLocale(parsed.Lang);

        if (parsed.PrintHelp) { PrintHelp(); return 0; }
        if (parsed.PrintVersion) { Console.WriteLine($"Coder CLI v{Brand.Version}  ({(OperatingSystem.IsWindows() ? "win" : "unix")}, locale: {I18n.Current})"); return 0; }

        Log.Instance.SetLevel(parsed.LogLevel);
        var logFile = Log.Instance.InitFileLogger(ConfigLoader.DefaultUserConfigDir());
        Log.Instance.Info(I18n.Tf("logger.session", logFile));

        var cwd = parsed.Overrides.Cwd ?? Directory.GetCurrentDirectory();
        var interactive = !Console.IsInputRedirected && parsed.Positional.Count == 0;

        // First-run wizard (only when no user config exists and we own a TTY).
        if (interactive && Setup.ShouldRun())
        {
            try
            {
                var first = Setup.Run();
                I18n.ApplyLocale(first.Lang);
                Theme.Apply(first.Theme);
                Console.WriteLine(Theme.Success("✓ ") + Theme.Dim(first.ConfigPath));
                ConfigLoader.PreloadUserConfigEnv();
            }
            catch (Exception e) { Log.Instance.Warn("Setup skipped:", e.Message); }
        }

        var (config, layers) = ConfigLoader.LoadConfig(parsed.Overrides, cwd);

        if (parsed.Lang == null)
            I18n.ApplyLocale(config.Lang, config.CnMode);

        Theme.Apply(config.Theme);

        // Vendor-specific quality preset (e.g. MiniMax deep-optimize).
        if (config.DeepOptimize || DeepOptimize.ShouldApply(config.Provider))
            config = DeepOptimize.Apply(config);

        ContextWindow.SyncProviderEnv(config.Provider);
        ContextWindow.SyncContextWindowEnv(config.ContextWindow);

        // Provider readiness.
        var caps = ProviderRegistry.GetProviderCaps(config.Provider, config.BaseUrl);
        if (caps == null && !ProviderRegistry.IsCustomCompatProvider(config.Provider, config.BaseUrl))
        {
            StreamRenderer.PrintError(I18n.Tf("error.unknownProvider.title", config.Provider));
            Console.WriteLine(Theme.Dim("  " + I18n.T("error.unknownProvider.supported")));
            foreach (var c in ProviderRegistry.ListProviderCaps())
                Console.WriteLine($"    {Theme.Info(c.id.PadRight(12))} {Theme.Dim(c.caps.DisplayName + (c.caps.RequiresApiKey ? " (API key)" : ""))}");
            Console.WriteLine(Theme.Dim("  " + I18n.T("error.unknownProvider.customHint")));
            return 2;
        }

        Log.Instance.Info(I18n.Tf("logger.starting", config.Provider, config.Model, config.Cwd));

        // Skill discovery (project + user + plugin dirs).
        var skillLoader = new SkillLoader(SkillPaths.Resolve(config.Cwd, config.UserConfigDir, config.SkillPaths));
        var skillList = "";
        try
        {
            var discovered = await skillLoader.DiscoverAsync();
            skillList = string.Join("\n", discovered.Select(s => $"- {s.Name}: {s.Description}"));
            if (discovered.Count > 0) Log.Instance.Debug($"skills: {discovered.Count} discovered");
        }
        catch (Exception e) { Log.Instance.Debug("skill discovery failed: " + e.Message); }

        // Agent templates (.coder/agents + ~/.coder/agents).
        var agentTemplates = await AgentTemplateLoader.LoadAsync(config.Cwd, config.UserConfigDir);

        // Project rules (CODER.md / CLAUDE.md / AGENTS.md / AGENT.md) + global ~/.coder/CODER.md.
        var projectContext = "";
        try
        {
            var pc = await ProjectContextLoader.LoadAsync(config.Cwd, config.UserConfigDir);
            projectContext = ProjectContextLoader.Render(pc);
        }
        catch (Exception e) { Log.Instance.Debug("project context failed: " + e.Message); }

        // Live session state — tools read config through this so that runtime
        // slash-command switches (/model, /provider, /cwd) take effect immediately.
        ReplState? state = null;

        // Build tool registry.
        var registry = new ToolRegistry();
        BuiltinTools.RegisterAll(registry);
        ExtraTools.RegisterAll(
            registry,
            () => state?.Config ?? config,
            skillLoader,
            config.UserConfigDir,
            (prompt, options) => Terminal.IsInteractive() ? Prompter.Choose(prompt, options, 0) : -1,
            agentTemplates);

        // Provider (null until API key present).
        IProvider? provider = null;
        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            try { provider = ProviderRegistry.MakeProvider(config.Provider, config.ApiKey, config.BaseUrl, config.CnMode, config); }
            catch (Exception e) { Log.Instance.Warn("Provider init failed:", e.Message); }
        }

        var renderer = new StreamRenderer();

        AskUserFn askUser = async (tool, inv) =>
        {
            await Task.CompletedTask;
            var mode = (state?.Config ?? config).PermissionMode;
            if (mode == PermissionMode.Plan && tool.Mutates)
                return new PermissionDecision("deny", "plan mode");
            if (mode == PermissionMode.BypassPermissions)
                return new PermissionDecision("allow", "bypass");
            if (mode == PermissionMode.AcceptEdits &&
                (inv.Name == "file_edit" || inv.Name == "file_write" || inv.Name == "bash"))
                return new PermissionDecision("allow", "acceptEdits");
            if (Terminal.IsInteractive())
            {
                var choice = PermissionDialog.Prompt(tool, inv, (state?.Config ?? config).Cwd);
                if (choice == 0) return new PermissionDecision("allow");
                if (choice == 1) return new PermissionDecision("allow",
                    UpdatedRules: new() { new PermissionRule(tool.Name, Decision: "allow", Origin: "session") });
                return new PermissionDecision("deny", "user declined");
            }
            return tool.ReadOnly ? new PermissionDecision("allow", "non-interactive read-only") : new PermissionDecision("allow", "non-interactive");
        };

        var permEngine = new PermissionEngine(askUser);
        permEngine.SetMode(config.PermissionMode);

        var hookManager = HookManager.FromLayers(layers, config.Cwd);

        var systemText = SystemPrompt.Build(config);
        var sessionCtx = RuntimeContext.BuildSessionContext(config, projectContext, skillList);
        try
        {
            var extra = await hookManager.SessionStartAsync();
            if (!string.IsNullOrWhiteSpace(extra)) sessionCtx += "\n" + extra;
        }
        catch (Exception e) { Log.Instance.Debug("SessionStart hook failed: " + e.Message); }

        var agent = new AgentLoop(config, provider, registry, permEngine, renderer, systemText, sessionCtx, hookManager);

        if (parsed.Positional.Count > 0)
        {
            var prompt = string.Join(" ", parsed.Positional);
            var oneShotCaps = ProviderRegistry.GetProviderCaps(config.Provider, config.BaseUrl);
            if (string.IsNullOrEmpty(config.ApiKey) && (oneShotCaps?.RequiresApiKey ?? true))
            {
                PrintMissingApiKeyHints(config);
                return 2;
            }
            RefreshProvider(config, agent);
            renderer.SetStatusSource(() => new StatusModel
            {
                Phase = Phase.Requesting,
                ContextUsed = EstimateContext(config, agent),
                ContextTotal = config.ContextWindow,
                Mode = config.PermissionMode,
                InputTokens = agent.GetUsage().InputTokens,
                OutputTokens = agent.GetUsage().OutputTokens,
                Turn = 1,
                Model = $"{config.Provider}/{config.Model}",
                Cwd = config.Cwd,
            });
            renderer.BeginTurn();
            try
            {
                var pd = await hookManager.UserPromptSubmitAsync(prompt);
                if (pd.Block)
                {
                    renderer.EndTurn();
                    Console.WriteLine(Theme.Dim(pd.Reason ?? "Prompt blocked by hook"));
                    return 0;
                }
                var finalPrompt = string.IsNullOrEmpty(pd.AdditionalContext) ? prompt : prompt + "\n\n" + pd.AdditionalContext;
                await agent.Run(finalPrompt);
            }
            catch (Exception e) { renderer.EndTurn(aborted: true); StreamRenderer.PrintError(e.Message); return 1; }
            renderer.EndTurn();
            try { await hookManager.StopAsync(); } catch { }
            return 0;
        }

        state = new ReplState
        {
            Config = config,
            Agent = agent,
            Renderer = renderer,
            Registry = registry,
            PermEngine = permEngine,
            SkillLoader = skillLoader,
            HookManager = hookManager,
            SystemText = systemText,
            ProjectContext = projectContext,
            SkillList = skillList,
        };
        await Repl.RunAsync(state);
        return 0;
    }

    /// <summary>估算当前上下文用量(system prompt + 消息历史)。</summary>
    internal static int EstimateContext(CoderConfig cfg, AgentLoop agent)
    {
        var sys = Tokens.Estimate(agent.GetSystemPrompt());
        var msgs = agent.GetMessages();
        return sys + msgs.Sum(Tokens.EstimateMessage);
    }

    internal static void RefreshProvider(CoderConfig cfg, AgentLoop agent)
    {
        if (string.IsNullOrEmpty(cfg.ApiKey) && (ProviderRegistry.GetProviderCaps(cfg.Provider, cfg.BaseUrl)?.RequiresApiKey ?? true))
        {
            agent.SetProvider(null);
            return;
        }
        try { agent.SetProvider(ProviderRegistry.MakeProvider(cfg.Provider, cfg.ApiKey, cfg.BaseUrl, cfg.CnMode, cfg)); }
        catch (Exception e) { agent.SetProvider(null); Log.Instance.Warn("Provider refresh failed:", e.Message); }
    }

    internal static void PrintMissingApiKeyHints(CoderConfig cfg)
    {
        var caps = ProviderRegistry.GetProviderCaps(cfg.Provider, cfg.BaseUrl);
        var envName = caps?.ApiKeyEnvVar ?? "API_KEY";
        StreamRenderer.PrintError(I18n.Tf("error.noApiKey.title", caps?.DisplayName ?? cfg.Provider));
        Console.WriteLine(Theme.Dim("  " + I18n.T("error.noApiKey.deferred")));
        Console.WriteLine(Theme.Dim("  " + I18n.T("error.noApiKey.deferredHint")));
        Console.WriteLine($"  {Theme.Info("1.")} {I18n.Tf("error.noApiKey.way1", "coder --api-key sk-…")}");
        Console.WriteLine($"  {Theme.Info("2.")} {I18n.Tf("error.noApiKey.way2", envName)}");
        Console.WriteLine($"  {Theme.Info("3.")} {I18n.Tf("error.noApiKey.way3Header", $"{cfg.UserConfigDir}/settings.toml")}:");
        Console.WriteLine($"        {Theme.Dim(I18n.Tf("error.noApiKey.way3Body", envName))}");
    }

    private sealed class ParsedArgs
    {
        public CliOverrides Overrides = new();
        public bool PrintHelp;
        public bool PrintVersion;
        public List<string> Positional = new();
        public LogLevel LogLevel = LogLevel.Info;
        public string? Lang;
    }

    private static ParsedArgs ParseCliArgs(string[] argv)
    {
        var p = new ParsedArgs();
        int i = 0;
        while (i < argv.Length)
        {
            var a = argv[i];
            switch (a)
            {
                case "-h" or "--help": p.PrintHelp = true; i++; break;
                case "-V" or "--version": p.PrintVersion = true; i++; break;
                case "--debug": p.LogLevel = LogLevel.Debug; i++; break;
                case "--quiet": p.LogLevel = LogLevel.Warn; i++; break;
                case "--provider" when i + 1 < argv.Length: p.Overrides.Provider = argv[++i]; i++; break;
                case "--model" when i + 1 < argv.Length: p.Overrides.Model = argv[++i]; i++; break;
                case "--api-key" when i + 1 < argv.Length: p.Overrides.ApiKey = argv[++i]; i++; break;
                case "--base-url" when i + 1 < argv.Length: p.Overrides.BaseUrl = argv[++i]; i++; break;
                case "--cwd" when i + 1 < argv.Length: p.Overrides.Cwd = argv[++i]; i++; break;
                case "--permission-mode" when i + 1 < argv.Length:
                    p.Overrides.PermissionMode = ParseMode(argv[++i]); i++; break;
                case "--theme" when i + 1 < argv.Length:
                    p.Overrides.Theme = ParseTheme(argv[++i]); i++; break;
                case "--effort-level" when i + 1 < argv.Length:
                    p.Overrides.EffortLevel = ParseEffort(argv[++i]); i++; break;
                case "--lang" when i + 1 < argv.Length:
                    p.Lang = argv[++i]; p.Overrides.Lang = p.Lang; i++; break;
                case "--stream": p.Overrides.StreamOutput = true; i++; break;
                case "--no-stream": p.Overrides.StreamOutput = false; i++; break;
                case "-p" or "--prompt" when i + 1 < argv.Length: p.Positional.Add(argv[++i]); i++; break;
                case "--":
                    p.Positional.AddRange(argv.Skip(i + 1));
                    i = argv.Length; break;
                default:
                    if (a.StartsWith("--")) { Log.Instance.Warn(I18n.Tf("unknownFlag", a)); i++; }
                    else { p.Positional.Add(a); i++; }
                    break;
            }
        }
        return p;
    }

    private static PermissionMode ParseMode(string s) => s.ToLowerInvariant() switch
    {
        "acceptedits" => PermissionMode.AcceptEdits,
        "bypasspermissions" => PermissionMode.BypassPermissions,
        "plan" => PermissionMode.Plan,
        _ => PermissionMode.Default,
    };
    private static ThemeMode ParseTheme(string s) => s.ToLowerInvariant() switch
    {
        "dark" => ThemeMode.Dark,
        "light" => ThemeMode.Light,
        _ => ThemeMode.Auto,
    };
    private static EffortLevel ParseEffort(string s) => s.ToLowerInvariant() switch
    {
        "low" => EffortLevel.Low,
        "medium" => EffortLevel.Medium,
        "high" => EffortLevel.High,
        "xhigh" => EffortLevel.XHigh,
        "max" => EffortLevel.Max,
        _ => EffortLevel.High,
    };

    private static void PrintHelp()
    {
        var rows = new List<string>
        {
            Theme.Gradient($"{Brand.Glyph} {I18n.T("cli.title")}"),
            Theme.Dim(I18n.T("cli.tagline")),
            "",
            Theme.Bold(I18n.T("cli.usageTitle")),
            "  " + Theme.Dim("coder [options] [prompt...]"),
            "",
            Theme.Bold(I18n.T("cli.optionsTitle")),
            "  -p, --prompt <text>      " + Theme.Dim(I18n.T("flags.prompt")),
            "  --provider <name>        " + Theme.Dim(I18n.T("flags.provider")),
            "  --model <name>           " + Theme.Dim(I18n.T("flags.model")),
            "  --api-key <key>          " + Theme.Dim(I18n.T("flags.apiKey")),
            "  --base-url <url>         " + Theme.Dim(I18n.T("flags.baseUrl")),
            "  --cwd <path>             " + Theme.Dim(I18n.T("flags.cwd")),
            "  --permission-mode <mode> " + Theme.Dim(I18n.T("flags.permissionMode")),
            "  --theme <theme>          " + Theme.Dim(I18n.T("flags.theme")),
            "  --effort-level <level>   " + Theme.Dim(I18n.T("flags.effortLevel")),
            "  --lang <code>            " + Theme.Dim(I18n.T("flags.lang")),
            "  --stream/--no-stream     " + Theme.Dim(I18n.T("flags.stream")),
            "  --debug/--quiet          " + Theme.Dim(I18n.T("flags.debug")),
            "  -V, --version            " + Theme.Dim(I18n.T("flags.version")),
            "  -h, --help               " + Theme.Dim(I18n.T("flags.help")),
            "",
            Theme.Bold(I18n.T("cli.configTitle")),
            "  " + Theme.Muted(I18n.T("cli.userConfigLabel") + "  ") + Theme.Info("~/.coder/settings.toml"),
            "  " + Theme.Muted(I18n.T("cli.projectConfigLabel") + "  ") + Theme.Dim("./.coder/settings.toml"),
            "",
            Theme.Bold(I18n.T("cli.examplesTitle")),
            "  " + Theme.Dim("coder \"explain this repo\""),
            "  " + Theme.Dim("coder --provider ollama --model llama3.1"),
        };
        Console.WriteLine("\n" + string.Join("\n", rows) + "\n");
    }
}
