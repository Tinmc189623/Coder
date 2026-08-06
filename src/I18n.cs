// Coder CLI (.NET 11 port) — internationalization (en + zh-CN).
// Ported from src/i18n/index.ts.

namespace Coder.Localization;

public sealed record Locale(string Code, string Label);

public static class I18n
{
    public static readonly Locale[] AvailableLocales =
    {
        new("en", "English"),
        new("zh-CN", "简体中文"),
    };

    private static readonly Dictionary<string, Dictionary<string, string>> Resources = new()
    {
        ["en"] = new()
        {
            ["cli.title"] = "Coder CLI",
            ["cli.tagline"] = "production-grade AI coding agent for the terminal",
            ["cli.usageTitle"] = "Usage",
            ["cli.optionsTitle"] = "Options",
            ["cli.runtimeLine"] = "runtime: {0} ({1})",
            ["cli.configTitle"] = "Configuration",
            ["cli.userConfigLabel"] = "user config:",
            ["cli.projectConfigLabel"] = "project config:",
            ["cli.slashTitle"] = "Slash commands",
            ["cli.examplesTitle"] = "Examples",
            ["cli.availableLocalesLabel"] = "Locales:",
            ["setup.intro"] = "First run? Configure via 'coder config' or environment variables.",
            ["flags.prompt"] = "Run a single prompt and exit",
            ["flags.provider"] = "LLM provider id (anthropic | openai | ollama | …)",
            ["flags.model"] = "Model name",
            ["flags.apiKey"] = "API key (or set ANTHROPIC_API_KEY / OPENAI_API_KEY)",
            ["flags.baseUrl"] = "Base URL for a custom OpenAI-compatible endpoint",
            ["flags.cwd"] = "Project working directory",
            ["flags.permissionMode"] = "Permission mode (default|acceptEdits|bypassPermissions|plan)",
            ["flags.theme"] = "Display theme (auto|dark|light)",
            ["flags.effortLevel"] = "Reasoning effort (low|medium|high|xhigh|max)",
            ["flags.lang"] = "UI language (en|zh-CN)",
            ["flags.stream"] = "Toggle live streaming of assistant text",
            ["flags.debug"] = "Verbose debug logging",
            ["flags.version"] = "Print version and exit",
            ["flags.help"] = "Print this help and exit",
            ["config.userPath"] = "~/.coder/settings.toml",
            ["config.userNote"] = "(optional)",
            ["config.projectPath"] = "./.coder/settings.toml",
            ["error.runtime.wrongBun"] = "Requires {0}, found {1}.",
            ["error.runtime.wrongNode"] = "Requires Node >= {0}, found {1}.",
            ["error.unknownProvider.title"] = "Unknown provider: '{0}'",
            ["error.unknownProvider.supported"] = "Supported providers:",
            ["error.unknownProvider.customHint"] = "Use any OpenAI-compatible endpoint by passing --base-url.",
            ["error.unknownProvider.hint"] = "Add it to {0} under 'provider'.",
            ["error.unknownProvider.customUsing"] = "Using custom compat provider '{0}' @ {1}",
            ["error.noApiKey.title"] = "No API key for {0}",
            ["error.noApiKey.deferred"] = "API key is required to talk to the model.",
            ["error.noApiKey.deferredHint"] = "You can still use slash commands; set a key to chat.",
            ["error.noApiKey.way1"] = "Pass it inline: {0}",
            ["error.noApiKey.way2"] = "Export the env var: {0}",
            ["error.noApiKey.way3Header"] = "Persist in {0}",
            ["error.noApiKey.way3Body"] = "Set '{0}' to your key.",
            ["logger.session"] = "session log: {0}",
            ["logger.starting"] = "starting — provider={0} model={1} cwd={2}",
            ["repl.interruptHint"] = "Press Ctrl+C again to force quit.",
            ["repl.interrupted"] = "Interrupted.",
            ["repl.exit.bye"] = "Goodbye.",
            ["todos.title"] = "Todos",
            ["unknownFlag"] = "Unknown flag: {0}",
        },
        ["zh-CN"] = new()
        {
            ["cli.title"] = "Coder 命令行",
            ["cli.tagline"] = "面向终端的生产级 AI 编程智能体",
            ["cli.usageTitle"] = "用法",
            ["cli.optionsTitle"] = "选项",
            ["cli.runtimeLine"] = "运行时: {0} ({1})",
            ["cli.configTitle"] = "配置",
            ["cli.userConfigLabel"] = "用户配置:",
            ["cli.projectConfigLabel"] = "项目配置:",
            ["cli.slashTitle"] = "斜杠命令",
            ["cli.examplesTitle"] = "示例",
            ["cli.availableLocalesLabel"] = "语言:",
            ["setup.intro"] = "首次运行？通过 'coder config' 或环境变量配置。",
            ["flags.prompt"] = "执行单条提示并退出",
            ["flags.provider"] = "LLM 提供方 id（anthropic | openai | ollama | …）",
            ["flags.model"] = "模型名称",
            ["flags.apiKey"] = "API 密钥（或设置 ANTHROPIC_API_KEY / OPENAI_API_KEY）",
            ["flags.baseUrl"] = "自定义 OpenAI 兼容端点的基础 URL",
            ["flags.cwd"] = "项目工作目录",
            ["flags.permissionMode"] = "权限模式（default|acceptEdits|bypassPermissions|plan）",
            ["flags.theme"] = "显示主题（auto|dark|light）",
            ["flags.effortLevel"] = "推理强度（low|medium|high|xhigh|max）",
            ["flags.lang"] = "界面语言（en|zh-CN）",
            ["flags.stream"] = "切换助手文本的实时流式输出",
            ["flags.debug"] = "详细调试日志",
            ["flags.version"] = "打印版本并退出",
            ["flags.help"] = "打印本帮助并退出",
            ["config.userPath"] = "~/.coder-cli/config.json",
            ["config.userNote"] = "（可选）",
            ["config.projectPath"] = "./coder-cli.config.json",
            ["error.runtime.wrongBun"] = "需要 {0}，当前为 {1}。",
            ["error.runtime.wrongNode"] = "需要 Node >= {0}，当前为 {1}。",
            ["error.unknownProvider.title"] = "未知提供方: '{0}'",
            ["error.unknownProvider.supported"] = "支持的提供方:",
            ["error.unknownProvider.customHint"] = "传入 --base-url 即可使用任意 OpenAI 兼容端点。",
            ["error.unknownProvider.hint"] = "将其加入 {0} 的 'provider' 字段。",
            ["error.unknownProvider.customUsing"] = "使用自定义兼容提供方 '{0}' @ {1}",
            ["error.noApiKey.title"] = "无 {0} 的 API 密钥",
            ["error.noApiKey.deferred"] = "调用模型需要 API 密钥。",
            ["error.noApiKey.deferredHint"] = "你仍可使用斜杠命令；设置密钥后即可对话。",
            ["error.noApiKey.way1"] = "内联传入: {0}",
            ["error.noApiKey.way2"] = "导出环境变量: {0}",
            ["error.noApiKey.way3Header"] = "持久化到 {0}",
            ["error.noApiKey.way3Body"] = "将 '{0}' 设为你的密钥。",
            ["logger.session"] = "会话日志: {0}",
            ["logger.starting"] = "启动中 — provider={0} model={1} cwd={2}",
            ["repl.interruptHint"] = "再次按 Ctrl+C 强制退出。",
            ["repl.interrupted"] = "已中断。",
            ["repl.exit.bye"] = "再见。",
            ["todos.title"] = "待办",
            ["unknownFlag"] = "未知参数: {0}",
        },
    };

    public static string Current { get; private set; } = "en";

    public static void ApplyLocale(string? lang = null, bool cnMode = false)
    {
        if (!string.IsNullOrEmpty(lang) && Resources.ContainsKey(lang!))
        {
            Current = lang!;
            return;
        }
        if (cnMode)
        {
            Current = "zh-CN";
            return;
        }
        if (!Resources.ContainsKey(Current)) Current = "en";
    }

    public static string T(string key)
    {
        if (Resources.TryGetValue(Current, out var dict) && dict.TryGetValue(key, out var v)) return v;
        if (Resources["en"].TryGetValue(key, out var en)) return en;
        return key;
    }

    public static string Tf(string key, params object[] args)
    {
        try
        {
            return string.Format(T(key), args);
        }
        catch
        {
            return T(key);
        }
    }
}
