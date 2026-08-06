// Coder CLI (.NET 11 port) — config loader.
// Reads (highest priority first):
//   1. CLI flags                 (--api-key, --base-url, --model, --provider, --cwd)
//   2. Environment variables     (ANTHROPIC_API_KEY, OPENAI_API_KEY, OPENAI_BASE_URL, …)
//   3. Project config            (./coder-cli.config.json)        — optional
//   4. User config               (~/.coder-cli/config.json)        — optional
// Ported from src/config/loader.ts.

using Coder.Localization;
using Coder.Providers;
using Coder.Types;
using Coder.Utils;

namespace Coder.Config;

public sealed class CliOverrides
{
    public string? Provider;
    public string? Model;
    public string? ApiKey;
    public string? BaseUrl;
    public string? Cwd;
    public PermissionMode? PermissionMode;
    public ThemeMode? Theme;
    public EffortLevel? EffortLevel;
    public string? Lang;
    public bool? StreamOutput;
}

public sealed class UserConfigFile
{
    public string? Schema { get; set; }
    public bool Initialized { get; set; } = true;
    public string? Lang { get; set; }
    public object? CnMode { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public ThemeMode? Theme { get; set; }
    public EffortLevel? EffortLevel { get; set; }
    public PermissionMode? PermissionMode { get; set; }
    public int? ContextWindow { get; set; }
    public bool? StreamOutput { get; set; }
    public Dictionary<string, string>? Env { get; set; }
}

public sealed class ConfigLayer
{
    public string Source { get; set; } = "";
    public Dictionary<string, object?> Data { get; set; } = new();
}

public static class ConfigEnv
{
    public const string Provider = "CODER_CLI_PROVIDER";
    public const string ContextWindow = "CODER_CLI_CONTEXT_WINDOW";
    public const string BaseUrl = "CODER_CLI_BASE_URL";
    public const string AuthToken = "CODER_CLI_AUTH_TOKEN";
    public const string Model = "CODER_CLI_MODEL";
    public const string Lang = "CODER_CLI_LANG";
    public const string EffortLevel = "CODER_CLI_EFFORT_LEVEL";
    public const string SubagentModel = "CODER_CLI_SUBAGENT_MODEL";
    public const string DeepOptimize = "CODER_CLI_DEEP_OPTIMIZE";
    public const string CnMode = "CN_MODE";
    public const string Language = "LANGUAGE";
    public const string OpenAiBaseUrl = "OPENAI_BASE_URL";
    public const string OllamaBaseUrl = "OLLAMA_BASE_URL";
    public const string AnthropicApiKey = "ANTHROPIC_API_KEY";
    public const string OpenAiApiKey = "OPENAI_API_KEY";
}

public static class ConfigLoader
{
    public static string DefaultUserConfigDir()
    {
        var over = Environment.GetEnvironmentVariable("CODER_USER_CONFIG_DIR");
        if (!string.IsNullOrEmpty(over)) return over;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".coder-cli");
    }

    public static void PreloadUserConfigEnv()
    {
        var file = Path.Combine(DefaultUserConfigDir(), "config.json");
        var layer = TryReadConfig(file);
        if (layer != null) ApplyEnvBlock(layer.Data);
    }

    public static (CoderConfig config, List<ConfigLayer> layers) LoadConfig(CliOverrides cli, string cwd, string? userConfigDir = null)
    {
        var layers = new List<ConfigLayer>();

        var cliData = new Dictionary<string, object?>();
        if (cli.Provider != null) cliData["provider"] = cli.Provider;
        if (cli.Model != null) cliData["model"] = cli.Model;
        if (cli.ApiKey != null) cliData["api_key"] = cli.ApiKey;
        if (cli.BaseUrl != null) cliData["base_url"] = cli.BaseUrl;
        if (cli.Cwd != null) cliData["cwd"] = cli.Cwd;
        if (cli.PermissionMode != null) cliData["permissionMode"] = cli.PermissionMode.ToString();
        if (cli.Theme != null) cliData["theme"] = cli.Theme.ToString();
        if (cli.EffortLevel != null) cliData["effortLevel"] = cli.EffortLevel.ToString();
        if (cli.Lang != null) cliData["lang"] = cli.Lang;
        if (cli.StreamOutput != null) cliData["streamOutput"] = cli.StreamOutput.Value;
        layers.Add(new ConfigLayer { Source = "cli", Data = cliData });

        var cfgDir = userConfigDir ?? DefaultUserConfigDir();
        var userCfg = TryReadConfig(Path.Combine(cfgDir, "config.json"));
        if (userCfg != null)
        {
            layers.Add(userCfg);
            ApplyEnvBlock(userCfg.Data);
        }

        var projectCfg = TryReadConfig(Path.Combine(cwd, "coder-cli.config.json"));
        if (projectCfg != null) layers.Add(projectCfg);

        var merged = new Dictionary<string, object?>();
        foreach (var l in layers)
            foreach (var kv in l.Data)
                merged[kv.Key] = kv.Value;

        string? baseUrl = merged.TryGetValue("base_url", out var bu) ? bu as string : null;
        if (string.IsNullOrEmpty(baseUrl))
            baseUrl = EnvOrNull(ConfigEnv.BaseUrl) ?? EnvOrNull(ConfigEnv.OpenAiBaseUrl) ?? EnvOrNull(ConfigEnv.OllamaBaseUrl);

        bool cnMode = ParseCnMode(merged.TryGetValue("cnMode", out var cm) ? cm : null) ?? (EnvOrNull(ConfigEnv.CnMode) != null);

        string? provider = merged.TryGetValue("provider", out var p) ? p as string : null;
        if (string.IsNullOrEmpty(provider))
            provider = InferProvider(baseUrl, EnvOrNull(ConfigEnv.Provider));

        string? apiKey = merged.TryGetValue("api_key", out var ak) ? ak as string : null;
        if (string.IsNullOrEmpty(apiKey)) apiKey = EnvOrNull(ConfigEnv.AuthToken);
        if (string.IsNullOrEmpty(apiKey))
        {
            foreach (var ek in ProviderRegistry.ApiKeyEnvVarsFor(provider!))
            {
                var v = EnvOrNull(ek);
                if (!string.IsNullOrEmpty(v)) { apiKey = v; break; }
            }
        }

        string? model = merged.TryGetValue("model", out var m) ? m as string : null;
        if (string.IsNullOrEmpty(model)) model = EnvOrNull(ConfigEnv.Model);
        if (string.IsNullOrEmpty(model)) model = ProviderRegistry.DefaultModelFor(provider!) ?? "claude-sonnet-4-5";

        if (string.IsNullOrEmpty(baseUrl))
        {
            var def = ProviderRegistry.DefaultBaseURLFor(provider!, cnMode);
            if (!string.IsNullOrEmpty(def)) baseUrl = def;
        }

        var subagentModel = (merged.TryGetValue("subagentModel", out var sm) ? sm as string : null)
            ?? EnvOrNull(ConfigEnv.SubagentModel) ?? model;

        var effortLevel = ParseEffort(merged.TryGetValue("effortLevel", out var el) ? el as string : null)
            ?? ParseEffort(EnvOrNull(ConfigEnv.EffortLevel))
            ?? EffortLevel.High;

        var theme = merged.TryGetValue("theme", out var th) ? ParseTheme(th as string) : ThemeMode.Auto;

        string? lang = (merged.TryGetValue("lang", out var lg) ? lg as string : null)
            ?? EnvOrNull(ConfigEnv.Lang)
            ?? EnvOrNull(ConfigEnv.Language)
            ?? (cnMode ? "zh-CN" : null);
        if (cnMode && string.IsNullOrEmpty(lang)) lang = "zh-CN";

        int contextWindow = ResolveContextWindow(merged, provider!, model!);

        Environment.SetEnvironmentVariable(ConfigEnv.Provider, provider);
        Environment.SetEnvironmentVariable(ConfigEnv.ContextWindow, contextWindow.ToString());

        string? cwdResolved = cli.Cwd ?? cwd;
        cwdResolved = Path.GetFullPath(cwdResolved);

        var cfg = new CoderConfig(
            Provider: provider!,
            Model: model!,
            Cwd: cwdResolved,
            UserConfigDir: cfgDir,
            SkillPaths: ResolveSkillPaths(cwdResolved, cfgDir),
            MaxTokens: 8192,
            Temperature: 0.2,
            MaxIterations: EffortToSettings(effortLevel).MaxIterations,
            PermissionMode: merged.TryGetValue("permissionMode", out var pm) ? ParsePermissionMode(pm as string) : PermissionMode.Default,
            Theme: theme,
            EffortLevel: effortLevel,
            SubagentModel: subagentModel!,
            ContextWindow: contextWindow,
            ApiKey: apiKey,
            BaseUrl: baseUrl,
            Lang: lang,
            CnMode: cnMode,
            StreamOutput: merged.TryGetValue("streamOutput", out var so) ? (so is bool b ? b : so?.ToString() != "false") : true);

        if (string.IsNullOrEmpty(cfg.Provider))
            throw new ConfigError("No provider configured. Pass --provider or set ANTHROPIC_API_KEY / OPENAI_API_KEY.");
        if (string.IsNullOrEmpty(cfg.Model))
            throw new ConfigError("No model configured. Pass --model or set 'model' in config.");

        return (cfg, layers);
    }

    // ── helpers ──────────────────────────────────────────────────────────
    private static string? EnvOrNull(string name) => Environment.GetEnvironmentVariable(name);

    private static bool? ParseCnMode(object? v)
    {
        if (v == null) return null;
        if (v is bool b) return b;
        if (v is string s) return s == "1" || s.ToLowerInvariant() == "true" || s.ToLowerInvariant() == "yes";
        return null;
    }

    private static EffortLevel? ParseEffort(string? s) => s?.ToLowerInvariant() switch
    {
        "low" => EffortLevel.Low,
        "medium" => EffortLevel.Medium,
        "high" => EffortLevel.High,
        "xhigh" => EffortLevel.XHigh,
        "max" => EffortLevel.Max,
        _ => null,
    };

    private static ThemeMode ParseTheme(string? s) => s?.ToLowerInvariant() switch
    {
        "dark" => ThemeMode.Dark,
        "light" => ThemeMode.Light,
        _ => ThemeMode.Auto,
    };

    private static PermissionMode ParsePermissionMode(string? s) => s?.ToLowerInvariant() switch
    {
        "acceptedits" => PermissionMode.AcceptEdits,
        "bypasspermissions" => PermissionMode.BypassPermissions,
        "plan" => PermissionMode.Plan,
        _ => PermissionMode.Default,
    };

    public static (int ThinkingBudget, int MaxIterations, bool EnableThinking) EffortToSettings(EffortLevel level) => level switch
    {
        EffortLevel.Low => (0, 10, false),
        EffortLevel.Medium => (1024, 20, true),
        EffortLevel.High => (4000, 30, true),
        EffortLevel.XHigh => (8000, 40, true),
        EffortLevel.Max => (16000, 60, true),
        _ => (4000, 30, true),
    };

    private static int ResolveContextWindow(Dictionary<string, object?> merged, string provider, string model)
    {
        if (merged.TryGetValue("contextWindow", out var cw))
        {
            if (cw is int i) return i;
            if (cw is string s && int.TryParse(s, out var pi)) return pi;
        }
        var fromEnv = EnvOrNull(ConfigEnv.ContextWindow);
        if (!string.IsNullOrEmpty(fromEnv) && int.TryParse(fromEnv, out var fe)) return fe;
        return ProviderRegistry.DefaultContextWindowFor(provider) ?? 200_000;
    }

    private static List<string> ResolveSkillPaths(string cwd, string userConfigDir)
    {
        var paths = new List<string>();
        var local = Path.Combine(cwd, ".coder-skills");
        if (Directory.Exists(local)) paths.Add(local);
        var user = Path.Combine(userConfigDir, "skills");
        if (Directory.Exists(user)) paths.Add(user);
        return paths;
    }

    private static void ApplyEnvBlock(Dictionary<string, object?> data)
    {
        if (!data.TryGetValue("env", out var env) || env is not Dictionary<string, object?> envDict) return;
        foreach (var kv in envDict)
        {
            if (kv.Value == null) continue;
            var val = kv.Value.ToString();
            if (string.IsNullOrEmpty(val)) continue;
            if (Environment.GetEnvironmentVariable(kv.Key) == null)
                Environment.SetEnvironmentVariable(kv.Key, val);
        }
    }

    private static ConfigLayer? TryReadConfig(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            var json = File.ReadAllText(file);
            var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
            if (data == null) return null;
            return new ConfigLayer { Source = file, Data = data };
        }
        catch (Exception e)
        {
            throw new ConfigError($"Failed to parse {file}: {e.Message}");
        }
    }

    public static void ReconcileUserConfigEnv(CoderConfig cfg)
    {
        var file = Path.Combine(cfg.UserConfigDir, "config.json");
        if (!File.Exists(file)) return;
        ConfigLayer? layer = TryReadConfig(file);
        if (layer == null) return;
        var data = layer.Data;
        if (!data.TryGetValue("env", out var envObj) || envObj is not Dictionary<string, object?> env)
        {
            env = new Dictionary<string, object?>();
            data["env"] = env;
        }
        bool changed = false;
        if (GetEnv(env, ConfigEnv.Provider) != cfg.Provider) { env[ConfigEnv.Provider] = cfg.Provider; changed = true; }
        var ctx = cfg.ContextWindow.ToString();
        if (GetEnv(env, ConfigEnv.ContextWindow) != ctx) { env[ConfigEnv.ContextWindow] = ctx; changed = true; }
        if (data.TryGetValue("provider", out var dp) && (dp?.ToString() ?? "") != cfg.Provider) { data["provider"] = cfg.Provider; changed = true; }
        if (!changed) return;
        var json = System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(file, json);
        Log.Instance.Debug($"reconcileUserConfigEnv: updated {file}");
    }

    private static string? GetEnv(Dictionary<string, object?> env, string key) => env.TryGetValue(key, out var v) ? v?.ToString() : null;

    public static string InferProvider(string? baseUrl, string? envProvider)
    {
        if (!string.IsNullOrEmpty(envProvider) && ProviderRegistry.IsKnownProvider(envProvider!)) return envProvider!;
        var fromUrl = ProviderRegistry.InferProviderFromUrl(baseUrl);
        if (fromUrl != null) return fromUrl;
        var fromEnv = ProviderRegistry.InferProviderFromEnvKeys();
        if (fromEnv != null) return fromEnv;

        bool hasAnthropic = !string.IsNullOrEmpty(EnvOrNull(ConfigEnv.AnthropicApiKey));
        bool hasOpenAi = !string.IsNullOrEmpty(EnvOrNull(ConfigEnv.OpenAiApiKey));
        bool hasGeneric = !string.IsNullOrEmpty(EnvOrNull(ConfigEnv.AuthToken));
        bool hasCustomBase = !string.IsNullOrEmpty(baseUrl);

        if (hasCustomBase && fromUrl == null) return "openai";
        if (hasAnthropic && !hasOpenAi && !hasGeneric) return "anthropic";
        if (hasOpenAi || !string.IsNullOrEmpty(EnvOrNull(ConfigEnv.OpenAiBaseUrl)) || !string.IsNullOrEmpty(EnvOrNull(ConfigEnv.OllamaBaseUrl))) return "openai";
        if (hasGeneric) return hasCustomBase ? "openai" : "anthropic";
        return "anthropic";
    }
}

// ── System prompt & session context builders ───────────────────────────────
public static class Prompts
{
    public static string BuildSystemPrompt(CoderConfig cfg)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are Coder, a production-grade AI coding agent that runs in the terminal.");
        sb.AppendLine("You help the user with software engineering tasks: reading, writing, and editing files,");
        sb.AppendLine("running shell commands, searching code, and reasoning about systems.");
        sb.AppendLine();
        sb.AppendLine("Guidelines:");
        sb.AppendLine("- Prefer using the provided tools over guessing file contents.");
        sb.AppendLine("- Make small, focused changes; verify with the tools available.");
        sb.AppendLine("- When a task is ambiguous, ask a concise clarifying question.");
        sb.AppendLine($"- Current provider: {cfg.Provider}, model: {cfg.Model}.");
        return sb.ToString();
    }

    public static string BuildSessionContext(CoderConfig cfg, string projectContext, string skillList)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"cwd: {cfg.Cwd}");
        sb.AppendLine($"os: {Environment.OSVersion.Platform}");
        sb.AppendLine($"date: {DateTime.Now:O}");
        if (!string.IsNullOrEmpty(projectContext)) sb.AppendLine().AppendLine(projectContext);
        if (!string.IsNullOrEmpty(skillList)) sb.AppendLine().AppendLine("Available skills:").AppendLine(skillList);
        return sb.ToString();
    }
}
