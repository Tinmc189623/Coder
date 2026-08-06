// Coder CLI (.NET 11 port) — first-run setup wizard + legacy config migration.
// Runs only when ~/.coder/settings.toml is missing and stdin is a TTY.
// Collects language, theme, and optional API key — never blocks on API key.

using System.Text;
using System.Text.Json;
using Coder.Localization;
using Coder.Providers;
using Coder.Types;
using Coder.Ui;
using Coder.Utils;

namespace Coder.Config;

public sealed record FirstRunResult(string Lang, ThemeMode Theme, string Provider, string? ApiKey, string ConfigPath);

public static class Setup
{
    /// <summary>True when the user-level settings.toml already exists.</summary>
    public static bool UserConfigExists()
        => File.Exists(Path.Combine(ConfigLoader.DefaultUserConfigDir(), "settings.toml"));

    /// <summary>Should the wizard run? Only on a TTY with no existing config.</summary>
    public static bool ShouldRun() => !UserConfigExists() && Terminal.IsInteractive();

    /// <summary>
    /// 一次性迁移旧 JSON 配置(~/.coder-cli/config.json → ~/.coder/settings.toml)。
    /// 目标 settings.toml 已存在或旧文件不存在时不做任何事;旧文件保留不删除。
    /// </summary>
    public static bool MigrateLegacyIfPresent()
    {
        var target = Path.Combine(ConfigLoader.DefaultUserConfigDir(), "settings.toml");
        if (File.Exists(target)) return false;
        // 旧目录 ~/.coder-cli 与新目录 ~/.coder 共享同一 home:从新目录父级推导。
        var newDir = ConfigLoader.DefaultUserConfigDir();
        var home = Path.GetDirectoryName(newDir.TrimEnd(Path.DirectorySeparatorChar)) ?? newDir;
        var legacy = Path.Combine(home, ".coder-cli", "config.json");
        if (!File.Exists(legacy)) return false;
        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(legacy));
            if (data == null) return false;
            var sb = new StringBuilder();
            foreach (var key in new[] { "lang", "provider", "model" })
            {
                if (data.TryGetValue(key, out var v) && v is JsonElement je && je.ValueKind == JsonValueKind.String)
                {
                    var s = je.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) sb.AppendLine($"{key} = \"{EscapeToml(s)}\"");
                }
            }
            // 枚举数字映射:theme 0=auto/1=dark/2=light;effortLevel 0=low..4=max。
            if (data.TryGetValue("theme", out var th) && th is JsonElement thEl && thEl.ValueKind == JsonValueKind.Number)
                sb.AppendLine($"theme = \"{ThemeName(thEl.GetInt32())}\"");
            if (data.TryGetValue("effortLevel", out var ef) && ef is JsonElement efEl && efEl.ValueKind == JsonValueKind.Number)
                sb.AppendLine($"effortLevel = \"{EffortName(efEl.GetInt32())}\"");
            if (data.TryGetValue("contextWindow", out var cw) && cw is JsonElement cwEl && cwEl.ValueKind == JsonValueKind.Number)
                sb.AppendLine($"contextWindow = {cwEl.GetInt32()}");
            if (data.TryGetValue("cnMode", out var cm))
            {
                bool cn = cm is JsonElement cmEl && (cmEl.ValueKind == JsonValueKind.True
                    || (cmEl.ValueKind == JsonValueKind.String && cmEl.GetString() == "True"));
                sb.AppendLine($"cnMode = {(cn ? "true" : "false")}");
            }
            if (data.TryGetValue("env", out var e) && e is JsonElement env && env.ValueKind == JsonValueKind.Object)
            {
                sb.AppendLine();
                sb.AppendLine("[env]");
                foreach (var p in env.EnumerateObject())
                    sb.AppendLine($"{p.Name} = \"{EscapeToml(p.Value.GetString() ?? p.Value.ToString())}\"");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, sb.ToString());
            Log.Instance.Warn($"Migrated legacy config {legacy} → {target}");
            Console.WriteLine(Theme.Dim(
                "已检测到旧配置 ~/.coder-cli/config.json,已迁移至 ~/.coder/settings.toml(旧文件保留,可手动删除)。"));
            return true;
        }
        catch (Exception ex)
        {
            Log.Instance.Warn("legacy config migration failed:", ex.Message);
            return false;
        }
    }

    private static string ThemeName(int i) => i switch { 1 => "dark", 2 => "light", _ => "auto" };
    private static string EffortName(int i) => i switch { 0 => "low", 1 => "medium", 2 => "high", 3 => "xhigh", _ => "max" };
    private static string EscapeToml(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

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

    /// <summary>Persist first-run choices to ~/.coder/settings.toml。</summary>
    public static string WriteFirstRunConfig(string lang, ThemeMode theme, string provider, string? apiKey)
    {
        var dir = ConfigLoader.DefaultUserConfigDir();
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "settings.toml");

        var envKeys = ProviderRegistry.ApiKeyEnvVarsFor(provider);
        var envKey = envKeys.FirstOrDefault() ?? "ANTHROPIC_API_KEY";
        var model = ProviderRegistry.DefaultModelFor(provider) ?? "";
        var contextWindow = ContextWindow.DefaultFor(provider, model);
        var isMinimax = provider == "minimax";
        var isZh = lang == "zh-CN";

        var sb = new StringBuilder();
        sb.AppendLine($"lang = \"{lang}\"");
        sb.AppendLine($"theme = \"{theme.ToString().ToLowerInvariant()}\"");
        sb.AppendLine($"provider = \"{provider}\"");
        if (!string.IsNullOrEmpty(model)) sb.AppendLine($"model = \"{model}\"");
        sb.AppendLine($"effortLevel = \"{(isMinimax ? "max" : "high")}\"");
        sb.AppendLine($"contextWindow = {contextWindow}");
        sb.AppendLine();
        sb.AppendLine("[env]");
        sb.AppendLine($"CODER_CLI_LANG = \"{lang}\"");
        sb.AppendLine($"LANGUAGE = \"{lang}\"");
        sb.AppendLine($"CN_MODE = \"{isZh}\"");
        sb.AppendLine($"CODER_CLI_PROVIDER = \"{provider}\"");
        sb.AppendLine($"CODER_CLI_CONTEXT_WINDOW = \"{contextWindow}\"");
        sb.AppendLine("API_TIMEOUT_MS = \"30000\"");
        sb.AppendLine("C_DISABLE_NONESSENTIAL_TRAFFIC = \"1\"");
        sb.AppendLine($"CODER_CLI_EFFORT_LEVEL = \"{(isMinimax ? "max" : "high")}\"");
        if (isMinimax) sb.AppendLine("CODER_CLI_DEEP_OPTIMIZE = \"1\"");
        if (!string.IsNullOrEmpty(apiKey)) sb.AppendLine($"{envKey} = \"{apiKey}\"");
        File.WriteAllText(file, sb.ToString());
        return file;
    }
}
