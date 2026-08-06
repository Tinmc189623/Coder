// Coder CLI (.NET 11 port) — first-run setup wizard.
// Runs only when ~/.coder-cli/config.json is missing and stdin is a TTY.
// Collects language, theme, and optional API key — never blocks on API key.
// Ported from src/config/setup.ts.

using System.Text.Json;
using Coder.Localization;
using Coder.Providers;
using Coder.Types;
using Coder.Ui;

namespace Coder.Config;

public sealed record FirstRunResult(string Lang, ThemeMode Theme, string Provider, string? ApiKey, string ConfigPath);

public static class Setup
{
    /// <summary>True when the user-level config file already exists.</summary>
    public static bool UserConfigExists()
        => File.Exists(Path.Combine(ConfigLoader.DefaultUserConfigDir(), "config.json"));

    /// <summary>Should the wizard run? Only on a TTY with no existing config.</summary>
    public static bool ShouldRun() => !UserConfigExists() && Terminal.IsInteractive();

    public static FirstRunResult Run()
    {
        var initial = I18n.Current;
        var zh = initial == "zh-CN";

        Console.WriteLine();
        Console.WriteLine(Theme.Brand(zh ? "◆ 欢迎使用 Coder CLI" : "◆ Welcome to Coder CLI"));
        Console.WriteLine(Theme.Dim(zh
            ? "首次运行，做几个快速设置（随时可用 /config 修改）。"
            : "First run — a few quick settings (change any time with /config)."));
        Console.WriteLine();

        // ── Language ────────────────────────────────────────────────────────
        var locales = I18n.AvailableLocales;
        var langLabels = locales.Select(l => l.Label).ToList();
        var defaultLangIdx = Math.Max(0, Array.FindIndex(locales, l => l.Code == initial));
        var langIdx = Prompter.Choose(zh ? "界面语言" : "Interface language", langLabels, defaultLangIdx);
        var lang = locales[Math.Clamp(langIdx, 0, locales.Length - 1)].Code;
        I18n.ApplyLocale(lang);
        zh = lang == "zh-CN";

        // ── Theme ───────────────────────────────────────────────────────────
        var themeOptions = zh
            ? new List<string> { "自动", "深色", "浅色" }
            : new List<string> { "Auto", "Dark", "Light" };
        var themeIdx = Prompter.Choose(zh ? "配色主题" : "Color theme", themeOptions);
        var themeValue = themeIdx switch { 1 => ThemeMode.Dark, 2 => ThemeMode.Light, _ => ThemeMode.Auto };

        // ── Provider ────────────────────────────────────────────────────────
        var providerDefs = ProviderRegistry.ListProviderCaps();
        var providerOptions = providerDefs.Select(p => p.caps.DisplayName).ToList();
        var providerIdx = Prompter.Choose(zh ? "默认 provider" : "Default provider", providerOptions);
        var provider = providerIdx >= 0 && providerIdx < providerDefs.Count ? providerDefs[providerIdx].id : "anthropic";

        // ── API key (optional) ──────────────────────────────────────────────
        string? apiKey = null;
        var envVar = ProviderRegistry.ApiKeyEnvVarsFor(provider).FirstOrDefault() ?? "ANTHROPIC_API_KEY";
        var configureKey = Prompter.Confirm(
            zh ? $"现在配置 API key（{envVar}）吗？" : $"Configure the API key now ({envVar})?", false);
        if (configureKey)
        {
            var key = Prompter.Ask(zh ? "粘贴 API key（留空跳过）" : "Paste API key (blank to skip)");
            if (!string.IsNullOrWhiteSpace(key)) apiKey = key.Trim();
        }

        var configPath = WriteFirstRunConfig(lang, themeValue, provider, apiKey);

        Console.WriteLine();
        Console.WriteLine(Theme.Success(zh ? $"✓ 已写入配置：{configPath}" : $"✓ Config written: {configPath}"));
        Console.WriteLine(Theme.Dim(zh
            ? "随时可用 /config 查看，/model 与 /provider 切换模型。"
            : "Use /config to review, /model and /provider to switch."));
        Console.WriteLine();

        return new FirstRunResult(lang, themeValue, provider, apiKey, configPath);
    }

    /// <summary>Persist first-run choices to ~/.coder-cli/config.json.</summary>
    public static string WriteFirstRunConfig(string lang, ThemeMode theme, string provider, string? apiKey)
    {
        var dir = ConfigLoader.DefaultUserConfigDir();
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "config.json");

        var envKeys = ProviderRegistry.ApiKeyEnvVarsFor(provider);
        var envKey = envKeys.FirstOrDefault() ?? "ANTHROPIC_API_KEY";
        var model = ProviderRegistry.DefaultModelFor(provider) ?? "";
        var contextWindow = ContextWindow.DefaultFor(provider, model);
        var isMinimax = provider == "minimax";
        var isZh = lang == "zh-CN";

        var env = new Dictionary<string, string>
        {
            ["CODER_CLI_LANG"] = lang,
            ["LANGUAGE"] = lang,
            ["CN_MODE"] = isZh ? "True" : "False",
            [ContextWindow.EnvProvider] = provider,
            [ContextWindow.EnvContextWindow] = contextWindow.ToString(),
            ["API_TIMEOUT_MS"] = "30000",
            ["C_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
            ["CODER_CLI_EFFORT_LEVEL"] = isMinimax ? "max" : "high",
        };
        if (isMinimax) env[DeepOptimize.EnvDeepOptimize] = "1";
        if (!string.IsNullOrEmpty(apiKey)) env[envKey] = apiKey!;

        var data = new UserConfigFile
        {
            Schema = "",
            Initialized = true,
            Lang = lang,
            CnMode = isZh,
            Provider = provider,
            Model = string.IsNullOrEmpty(model) ? null : model,
            ContextWindow = contextWindow,
            Theme = theme,
            EffortLevel = isMinimax ? Types.EffortLevel.Max : Types.EffortLevel.High,
            Env = env,
        };

        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        File.WriteAllText(file, json + "\n");
        return file;
    }
}
