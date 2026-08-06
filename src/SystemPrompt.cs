// Coder CLI (.NET 11 port) — identity system prompt, content policy,
// project context, runtime session context, CN mode, deep optimize.
// Ported from src/config/{system_prompt,content_policy,project_context,
// runtime_context,cn_mode,deep_optimize,context_window}.ts and src/utils/platform.ts.

using System.Runtime.InteropServices;
using System.Text;
using Coder.Core;
using Coder.Providers;
using Coder.Types;

namespace Coder.Config;

// ── Platform facts ──────────────────────────────────────────────────────────
public static class Platform
{
    public static string Name()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macOS";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "Linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD)) return "FreeBSD";
        return RuntimeInformation.OSDescription;
    }

    public static string Moniker()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win32"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "darwin"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
            : "unknown";
        return $"{os}/{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
    }

    public static string ShellName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PSModulePath")))
                return "PowerShell";
            var comspec = Environment.GetEnvironmentVariable("ComSpec");
            return string.IsNullOrEmpty(comspec) ? "cmd.exe" : System.IO.Path.GetFileName(comspec);
        }
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrEmpty(shell)) return "sh";
        return shell!.Split('/', '\\').LastOrDefault() ?? "sh";
    }

    public static string RuntimeVersion() => $".NET {Environment.Version}";
}

// ── CN mode ─────────────────────────────────────────────────────────────────
public static class CnMode
{
    /// <summary>Parse CN_MODE from env / config. Accepts True/true/1/yes/on.</summary>
    public static bool Parse(object? raw)
    {
        if (raw is bool b) return b;
        if (raw == null) return false;
        var s = raw.ToString()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s)) return false;
        return s is "true" or "1" or "yes" or "on";
    }
}

// ── Content policy ──────────────────────────────────────────────────────────
public static class ContentPolicy
{
    public static string Chinese() => string.Join("\n", new[]
    {
        "## 内容合规（必须遵守）",
        "- **禁止**生成、传播、讨论、引用或协助任何「台独」分裂主张及相关敏感政治议题。",
        "- 若用户提出上述话题，**礼貌拒绝**，说明 Coder CLI 仅提供编程与技术协助，并引导回到开发任务。",
        "- 涉及中国台湾地区时，使用规范表述；**不承认**任何形式的「台独」。",
        "- 不展开政治辩论，不提供分裂主义相关素材、口号、宣传文案或行动建议。",
    });

    public static string English() => string.Join("\n", new[]
    {
        "## Content compliance (mandatory)",
        "- Do **not** generate, spread, discuss, or assist with Taiwan independence separatist claims or related sensitive political topics.",
        "- If asked, decline politely, state that Coder CLI is for coding and technical assistance only, and redirect to development tasks.",
        "- Do not provide separatist propaganda, slogans, or action guidance.",
    });

    public static string Block(bool cnMode, string? lang)
        => cnMode || lang == "zh-CN" ? Chinese() : English();
}

// ── Identity system prompt ──────────────────────────────────────────────────
public static class SystemPrompt
{
    /// <summary>Build the identity system prompt (role + conduct only, no runtime facts).</summary>
    public static string Build(CoderConfig cfg)
        => cfg.CnMode || cfg.Lang == "zh-CN" ? BuildChinese() : BuildEnglish();

    private static string BuildChinese() => string.Join("\n\n", new[]
    {
        Join("# 身份",
            "你是 **Coder CLI**，一名运行在终端里的 AI 编程智能体。",
            "你由 Nexsteaduser 生态下的开源工具链驱动，不绑定单一厂商或单一模型。",
            "你的职责是理解开发者意图，善用工具，交付可验证的技术结果。"),
        ContentPolicy.Chinese(),
        Join("# 语言",
            "- 面向用户的说明使用 **zh-CN + 适量英文技术词**（model、provider、workspace、mode、task 等）。",
            "- 代码、命令、路径、API 与库名保持原文。",
            "- 仅在用户明确要求时使用其他语言。"),
        Join("# 行事准则",
            "- **行动优先**：能调查、能修改、能运行验证时，不要只给空泛建议。",
            "- **诚实谦逊**：不确定时先读文件、跑命令、再下结论；不臆测环境。",
            "- **最小改动**：只动与任务相关的代码，不顺手做大重构。",
            "- **尊重确认**：破坏性操作需用户许可；被拒后换方案或解释原因。",
            "- **开放生态**：工具与提供商均可替换；根据**实际会话上下文**选择命令与 API，不做平台刻板假设。",
            "- **工具静默**：直接调用 schema 中的工具，不向用户复述工具名称。"),
        Join("# 技艺标准",
            "- 修改前先阅读相关文件；优先精准编辑，避免无谓整文件覆盖。",
            "- 遵循项目既有风格；代码应自解释，注释只写非显而易见之处。",
            "- 交付完整可运行成果，禁止用占位符糊弄关键逻辑。",
            "- 修复后尽量验证（构建、测试、类型检查——以项目实际脚本为准）。",
            "- 多步骤任务可用 todo_write 同步进度。"),
        Join("# 安全",
            "- 不泄露凭据；不协助恶意、入侵、盗版等违法行为。",
            "- 破坏性命令前须警示；从网络引入的代码须审查后再建议采用。"),
        Join("# 输出",
            "- 简洁、结构化；必要时用 Markdown。",
            "- 先一句话说明意图，再给出关键改动或结论。",
            "- 长输出摘录要点，避免刷屏。"),
    });

    private static string BuildEnglish() => string.Join("\n\n", new[]
    {
        Join("# Identity",
            "You are **Coder CLI**, an AI coding agent in the terminal.",
            "You are provider-agnostic and ecosystem-open — not locked to one vendor or model.",
            "Your job: understand intent, use tools, deliver verifiable technical results."),
        ContentPolicy.English(),
        Join("# Language",
            "- Match the user's language; default to clear professional English.",
            "- Keep code, commands, paths, and API names literal."),
        Join("# Conduct",
            "- **Bias toward action** — investigate, edit, and verify rather than only advising.",
            "- **Intellectual honesty** — read files and run commands before assuming environment details.",
            "- **Minimal diffs** — touch only what the task requires.",
            "- **Respect confirmations** — destructive work needs approval.",
            "- **Open ecosystem** — adapt commands to the **actual session context**; never hardcode an OS.",
            "- **Quiet tools** — call tools directly; don't narrate tool names to the user."),
        Join("# Craft",
            "- Read before editing; prefer precise edits over full rewrites.",
            "- Match project conventions; ship complete, runnable work — no placeholders.",
            "- Verify fixes when the project provides scripts.",
            "- Use todo_write for multi-step work when helpful."),
        Join("# Safety",
            "- Never expose secrets; refuse malware, intrusion, or piracy assistance.",
            "- Warn before destructive commands."),
        Join("# Output",
            "- Concise and structured; Markdown when it helps.",
            "- State intent first, then key changes or conclusions.",
            "- Truncate noisy command output."),
    });

    private static string Join(params string[] lines) => string.Join("\n", lines);
}

// ── Project context ─────────────────────────────────────────────────────────
public sealed class ProjectContextInfo
{
    public string? AgentsFile { get; set; }
    public string? AgentsBody { get; set; }
    public string? ReadmeBody { get; set; }
    public List<string> FilesFound { get; } = new();
}

public static class ProjectContextLoader
{
    private static readonly string[] ProjectDocs = { "AGENTS.md", "CLAUDE.md", "README.md" };
    private const string LegacyAgentRules = "CLAUDE.md";

    public static async Task<ProjectContextInfo> LoadAsync(string cwd)
    {
        var ctx = new ProjectContextInfo();
        foreach (var name in ProjectDocs)
        {
            var full = System.IO.Path.Combine(cwd, name);
            if (!File.Exists(full)) continue;
            try
            {
                var body = await File.ReadAllTextAsync(full);
                ctx.FilesFound.Add(name);
                if (name is "AGENTS.md" or LegacyAgentRules)
                {
                    if (ctx.AgentsFile == null) { ctx.AgentsFile = full; ctx.AgentsBody = body; }
                }
                else if (name == "README.md" && ctx.ReadmeBody == null)
                {
                    ctx.ReadmeBody = body;
                }
            }
            catch { /* skip unreadable */ }
        }
        return ctx;
    }

    /// <summary>Render the project context into a compact text block for the session prompt.</summary>
    public static string Render(ProjectContextInfo ctx, int maxChars = 8000)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(ctx.AgentsBody))
        {
            sb.AppendLine($"# {System.IO.Path.GetFileName(ctx.AgentsFile)}");
            sb.AppendLine(Truncate(ctx.AgentsBody!.Trim(), maxChars));
        }
        else if (!string.IsNullOrWhiteSpace(ctx.ReadmeBody))
        {
            sb.AppendLine("# README.md");
            sb.AppendLine(Truncate(ctx.ReadmeBody!.Trim(), Math.Min(maxChars, 4000)));
        }
        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "\n… (truncated)";
}

// ── Runtime session context ─────────────────────────────────────────────────
public static class RuntimeContext
{
    /// <summary>
    /// Build the runtime facts block injected into the user turn (never the identity prompt).
    /// </summary>
    public static string BuildSessionContext(CoderConfig cfg, string projectContext, string skillList)
    {
        var lines = new List<string>
        {
            $"cwd: {cfg.Cwd}",
            $"platform: {Platform.Name()} ({Platform.Moniker()})",
            $"shell: {Platform.ShellName()}",
            $"runtime: {Platform.RuntimeVersion()}",
            $"model: {cfg.Model}",
            $"provider: {cfg.Provider}",
            $"permission_mode: {cfg.PermissionMode.ToString().ToLowerInvariant()}",
            $"effort: {cfg.EffortLevel.ToString().ToLowerInvariant()}",
            $"context_window: {cfg.ContextWindow}",
            $"locale: {cfg.Lang ?? "auto"}",
            $"date: {DateTime.UtcNow:yyyy-MM-dd}",
        };
        if (cfg.DeepOptimize) lines.Add("deep_optimize: true");
        if (!string.IsNullOrWhiteSpace(projectContext))
        {
            lines.Add("");
            lines.Add("[project]");
            lines.Add(projectContext.Trim());
        }
        if (!string.IsNullOrWhiteSpace(skillList))
        {
            lines.Add("");
            lines.Add("[skills]");
            lines.Add(skillList.Trim());
        }
        return string.Join("\n", lines);
    }
}

// ── Context window ──────────────────────────────────────────────────────────
public static class ContextWindow
{
    public const string EnvContextWindow = "CODER_CLI_CONTEXT_WINDOW";
    public const string EnvProvider = "CODER_CLI_PROVIDER";

    public const int MiniMaxM3RecommendedContext = 512_000;
    public const int MiniMaxM3MaxContext = 1_000_000;
    public const int MiniMaxDeepMaxTokens = 32_768;

    public static int DefaultFor(string provider, string model)
    {
        var caps = ProviderRegistry.GetProviderCaps(provider);
        if (caps?.DefaultContextWindow is int n and > 0) return n;
        if (model.ToLowerInvariant().Contains("minimax")) return MiniMaxM3RecommendedContext;
        return Compaction.Default.MaxContextTokens;
    }

    public static int Resolve(Dictionary<string, object?> merged, string provider, string model)
    {
        object? raw = merged.TryGetValue("contextWindow", out var v) ? v : null;
        raw ??= Environment.GetEnvironmentVariable(EnvContextWindow);
        var s = raw?.ToString();
        if (!string.IsNullOrEmpty(s) && int.TryParse(s, out var n) && n > 0) return n;
        return DefaultFor(provider, model);
    }

    public static CompactionConfig CompactionConfigFor(CoderConfig cfg)
        => new(cfg.ContextWindow, Compaction.Default.TrimThreshold, Compaction.Default.MinKeepTurns);

    public static void SyncProviderEnv(string provider)
        => Environment.SetEnvironmentVariable(EnvProvider, provider);

    public static void SyncContextWindowEnv(int tokens)
        => Environment.SetEnvironmentVariable(EnvContextWindow, tokens.ToString());
}

// ── MiniMax deep optimize ───────────────────────────────────────────────────
public static class DeepOptimize
{
    public const string EnvDeepOptimize = "CODER_CLI_DEEP_OPTIMIZE";
    public const int MiniMaxDeepTimeoutMs = 120_000;

    public static bool ShouldApply(string provider)
    {
        if (provider != "minimax") return false;
        var raw = Environment.GetEnvironmentVariable(EnvDeepOptimize)?.ToLowerInvariant();
        return raw is not ("0" or "false" or "off");
    }

    public static CoderConfig Apply(CoderConfig cfg)
    {
        if (!ShouldApply(cfg.Provider)) return cfg;

        var (_, maxIterations, _) = ConfigLoader.EffortToSettings(EffortLevel.Max);
        Environment.SetEnvironmentVariable(EnvDeepOptimize, "1");
        var timeoutRaw = Environment.GetEnvironmentVariable("API_TIMEOUT_MS");
        if (string.IsNullOrEmpty(timeoutRaw) || !int.TryParse(timeoutRaw, out var t) || t < MiniMaxDeepTimeoutMs)
            Environment.SetEnvironmentVariable("API_TIMEOUT_MS", MiniMaxDeepTimeoutMs.ToString());

        return cfg with
        {
            DeepOptimize = true,
            EffortLevel = EffortLevel.Max,
            MaxIterations = maxIterations,
            ContextWindow = Math.Max(cfg.ContextWindow, ContextWindow.MiniMaxM3RecommendedContext),
            MaxTokens = Math.Max(cfg.MaxTokens, ContextWindow.MiniMaxDeepMaxTokens),
            Temperature = 0.05,
            SubagentModel = string.IsNullOrEmpty(cfg.SubagentModel) ? cfg.Model : cfg.SubagentModel,
        };
    }
}
