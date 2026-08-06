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

    public static async Task<SubAgentResult> RunAsync(
        CoderConfig config,
        ToolRegistry parentRegistry,
        string prompt,
        string subagentType,
        string? modelOverride = null,
        Action<string>? onToolTrace = null)
    {
        var allowed = ToolNames.TryGetValue(subagentType, out var a) ? a : ToolNames["general-purpose"];

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
            provider = ProviderRegistry.MakeProvider(config.Provider, config.ApiKey, config.BaseUrl);
        }
        catch (Exception e)
        {
            return new SubAgentResult($"Subagent ({subagentType}) could not start: {e.Message}", 0, 0);
        }

        var cfg = config with { Model = modelOverride ?? (string.IsNullOrEmpty(config.SubagentModel) ? config.Model : config.SubagentModel) };

        // Subagents auto-allow: the parent already gated the Task invocation.
        var permEngine = new PermissionEngine((_, _) =>
            Task.FromResult(new PermissionDecision("allow", "subagent auto-allow")));
        permEngine.SetMode(PermissionMode.BypassPermissions);

        var hooks = new SilentAgentHooks(onToolTrace);
        var loop = new AgentLoop(
            cfg, provider, registry, permEngine, hooks,
            systemPrompt: Config.SystemPrompt.Build(cfg),
            sessionContext: Config.RuntimeContext.BuildSessionContext(cfg, "", ""));

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
