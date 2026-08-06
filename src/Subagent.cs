// Coder CLI (.NET 11 port) — subagent (Task tool) execution.
// Spawns an isolated AgentLoop with a narrower tool set, returns only the final summary.
// Ported from src/core/subagent.ts + src/ui/subagent_renderer.ts.

using Coder.Providers;
using Coder.Tools;
using Coder.Types;
using Coder.Utils;

namespace Coder.Core;

public sealed record SubAgentResult(string Summary, int TokensUsed, int Iterations);

/// <summary>Minimal hooks: subagent output is suppressed, only the summary bubbles up.</summary>
public sealed class SilentAgentHooks : IAgentHooks
{
    private readonly Action<string>? _onToolTrace;
    public SilentAgentHooks(Action<string>? onToolTrace = null) => _onToolTrace = onToolTrace;

    public void OnTextDelta(string text) { }
    public void OnThinkingDelta(string text) { }
    public void OnToolStart(ToolInvocation inv, ToolDefinition def, string? describe) => _onToolTrace?.Invoke($"  ↳ {inv.Name}");
    public void OnToolEnd(ToolInvocation inv, ToolResult result) { }
    public void OnTodoUpdate(List<Dictionary<string, object?>> items) { }
    public void OnIteration(int idx) { }
}

public static class SubAgent
{
    /// <summary>Tool allowlist per subagent type.</summary>
    public static readonly Dictionary<string, string[]> ToolNames = new(StringComparer.Ordinal)
    {
        ["general-purpose"] = new[] { "file_read", "file_write", "file_edit", "bash", "glob", "grep", "web_fetch", "web_search", "todo_write" },
        ["Explore"] = new[] { "file_read", "glob", "grep", "web_fetch", "web_search" },
        ["Plan"] = new[] { "file_read", "glob", "grep", "web_fetch" },
    };

    public static string[] Types => ToolNames.Keys.ToArray();

    /// <summary>内置类型 + 文件模板名合并去重(Task 工具 schema 用)。</summary>
    public static string[] AllTypes(IReadOnlyList<Coder.Agents.AgentTemplate>? templates)
        => templates is { Count: > 0 }
            ? ToolNames.Keys.Concat(templates.Select(t => t.Name)).Distinct().ToArray()
            : ToolNames.Keys.ToArray();

    public static async Task<SubAgentResult> RunAsync(
        CoderConfig config,
        ToolRegistry parentRegistry,
        string prompt,
        string subagentType,
        string? modelOverride = null,
        Action<string>? onToolTrace = null,
        IReadOnlyList<Coder.Agents.AgentTemplate>? templates = null,
        HookManager? hookManager = null)
    {
        // 三级降级:模板 Tools → 内置白名单 → general-purpose。
        var template = templates?.FirstOrDefault(t => t.Name.Equals(subagentType, StringComparison.OrdinalIgnoreCase));
        string[] allowed;
        if (template?.Tools is { Length: > 0 } tTools)
            allowed = tTools;
        else if (ToolNames.TryGetValue(subagentType, out var a))
            allowed = a;
        else
        {
            if (template == null) Log.Instance.Warn($"subagent type '{subagentType}' not found, falling back to general-purpose");
            allowed = ToolNames["general-purpose"];
        }

        // Build a restricted registry by re-using the parent's handlers.
        var registry = new ToolRegistry();
        foreach (var name in allowed)
        {
            if (!parentRegistry.Has(name)) continue;
            var entry = parentRegistry.Get(name);
            registry.Register(entry.Def, entry.Handler, entry.Describe);
        }

        if (registry.List().Count == 0)
            return new SubAgentResult($"Subagent ({subagentType}) could not start: no inherited tools.", 0, 0);

        IProvider provider;
        try
        {
            provider = ProviderRegistry.MakeProvider(config.Provider, config.ApiKey, config.BaseUrl, config.CnMode, config);
        }
        catch (Exception e)
        {
            return new SubAgentResult($"Subagent ({subagentType}) could not start: {e.Message}", 0, 0);
        }

        // 模型优先级:模板 Model → 调用级 modelOverride → SubagentModel 配置。
        var model = template?.Model ?? modelOverride
            ?? (string.IsNullOrEmpty(config.SubagentModel) ? config.Model : config.SubagentModel);
        var cfg = config with { Model = model };

        // Subagents auto-allow: the parent already gated the Task invocation.
        var permEngine = new PermissionEngine((_, _) =>
            Task.FromResult(new PermissionDecision("allow", "subagent auto-allow")));
        permEngine.SetMode(PermissionMode.BypassPermissions);

        var hooks = new SilentAgentHooks(onToolTrace);
        var loop = new AgentLoop(
            cfg, provider, registry, permEngine, hooks,
            systemPrompt: template?.Body ?? Config.SystemPrompt.Build(cfg),
            sessionContext: Config.RuntimeContext.BuildSessionContext(cfg, "", ""),
            hookManager: hookManager);   // 子代理内工具调用同样过 hook

        try
        {
            var (finalText, iterations) = await loop.Run(prompt);
            var usage = loop.GetUsage();
            return new SubAgentResult(
                string.IsNullOrWhiteSpace(finalText) ? "(no summary returned)" : finalText,
                usage.InputTokens + usage.OutputTokens,
                iterations);
        }
        catch (Exception e)
        {
            Log.Instance.Warn("Subagent failed:", e.Message);
            return new SubAgentResult($"Subagent ({subagentType}) failed: {e.Message}", 0, 0);
        }
    }
}
