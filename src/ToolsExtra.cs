// Coder CLI (.NET 11 port) — extended tools: Task (subagent), skill, memory, ask_user.
// Ported from src/tools/{task,skill,memory,ask_user}.ts.

using System.Text;
using System.Text.Json;
using Coder.Core;
using Coder.Memory;
using Coder.Skills;
using Coder.Types;

namespace Coder.Tools;

public static class ExtraTools
{
    private static string? Str(Dictionary<string, object?> input, string key)
        => input.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static Dictionary<string, object?> Prop(string type, string desc, string[]? enumValues = null)
    {
        var d = new Dictionary<string, object?> { ["type"] = type, ["description"] = desc };
        if (enumValues != null) d["enum"] = enumValues;
        return d;
    }

    private static Dictionary<string, object?> ObjSchema(Dictionary<string, object?> props, string[] required) => new()
    {
        ["type"] = "object",
        ["properties"] = props,
        ["required"] = required,
    };

    /// <summary>
    /// Register Task / skill / memory / ask_user on the given registry.
    /// The Task tool re-uses <paramref name="registry"/> itself as the parent tool pool.
    /// </summary>
    public static void RegisterAll(
        ToolRegistry registry,
        Func<CoderConfig> configProvider,
        SkillLoader? skillLoader,
        string userConfigDir,
        Func<string, List<string>, int>? askChoice = null,
        IReadOnlyList<Coder.Agents.AgentTemplate>? agentTemplates = null)
    {
        RegisterTask(registry, configProvider, agentTemplates);
        if (skillLoader != null) RegisterSkill(registry, skillLoader);
        RegisterMemory(registry, userConfigDir);
        RegisterAskUser(registry, askChoice);
    }

    // ── Task (subagent) ─────────────────────────────────────────────────────
    private static void RegisterTask(ToolRegistry registry, Func<CoderConfig> configProvider,
        IReadOnlyList<Coder.Agents.AgentTemplate>? agentTemplates = null)
    {
        var allTypes = SubAgent.AllTypes(agentTemplates);
        var def = new ToolDefinition(
            "Task",
            "Spawn a subagent to handle a self-contained subtask. The subagent has its own message log and " +
            "returns only a final summary. Use this to delegate research or isolated work. " +
            $"Available subagent_type values: {string.Join(", ", allTypes)}.",
            ObjSchema(new Dictionary<string, object?>
            {
                ["prompt"] = Prop("string", "Detailed instruction for the subagent"),
                ["subagent_type"] = Prop("string", "Type of subagent", allTypes),
                ["model"] = Prop("string", "Override model for the subagent"),
                ["description"] = Prop("string", "Short task description"),
            }, new[] { "prompt", "description" }));

        registry.Register(def, async (input, ctx) =>
        {
            var id = Str(input, "_id") ?? "task";
            var prompt = Str(input, "prompt") ?? "";
            if (string.IsNullOrWhiteSpace(prompt))
                return new ToolResult(id, false, "prompt is required", Error: "prompt is required");

            var result = await SubAgent.RunAsync(
                configProvider(),
                registry,
                prompt,
                Str(input, "subagent_type") ?? "general-purpose",
                Str(input, "model"),
                ctx.OnProgress,
                agentTemplates);

            var text = result.Summary;
            if (result.TokensUsed > 0)
                text += $"\n\n[subagent tokens={result.TokensUsed} iterations={result.Iterations}]";
            return new ToolResult(id, true, text);
        }, input =>
        {
            var d = Str(input, "description");
            if (!string.IsNullOrEmpty(d)) return $"Subagent: {d}";
            var p = Str(input, "prompt") ?? "";
            return $"Subagent: {(p.Length > 60 ? p[..60] : p)}";
        });
    }

    // ── skill ───────────────────────────────────────────────────────────────
    private static void RegisterSkill(ToolRegistry registry, SkillLoader loader)
    {
        var def = new ToolDefinition(
            "skill",
            "Load a skill or plugin by name (SKILL.md bundles). " +
            "Use for domain workflows such as ui-ux-pro-max, review, refactor. " +
            "Run /skills to see installed skills.",
            ObjSchema(new Dictionary<string, object?>
            {
                ["skill_name"] = Prop("string", "Name of the skill (folder name)"),
            }, new[] { "skill_name" }),
            ReadOnly: true);

        registry.Register(def, async (input, ctx) =>
        {
            var id = Str(input, "_id") ?? "skill";
            var name = Str(input, "skill_name") ?? "";
            try
            {
                var skill = await loader.LoadAsync(name);
                var text = $"[SKILL: {skill.Name}] {skill.Description}\nSource: {skill.Source}\n\n{skill.Body}";
                return new ToolResult(id, true, text);
            }
            catch (Exception e)
            {
                return new ToolResult(id, false, e.Message, Error: e.Message);
            }
        }, input => $"Load skill \"{Str(input, "skill_name")}\"");
    }

    // ── memory ──────────────────────────────────────────────────────────────
    private static void RegisterMemory(ToolRegistry registry, string userConfigDir)
    {
        var def = new ToolDefinition(
            "memory",
            "Read or write entries in the persistent memory store. Use to remember user preferences, project facts, " +
            "or cross-session learnings. Three scopes: user (across all projects), agent (this agent only), " +
            "project (this repo only).",
            ObjSchema(new Dictionary<string, object?>
            {
                ["action"] = Prop("string", "Memory operation", new[] { "read", "write", "list" }),
                ["scope"] = Prop("string", "Memory scope", new[] { "user", "agent", "project" }),
                ["topic"] = Prop("string", "Topic key (e.g. \"shell preference\")"),
                ["content"] = Prop("string", "For write: the entry content"),
                ["type"] = Prop("string", "Optional type tag (preference, fact, etc.)"),
            }, new[] { "action" }),
            Mutates: true);

        registry.Register(def, async (input, ctx) =>
        {
            var id = Str(input, "_id") ?? "memory";
            var action = (Str(input, "action") ?? "list").ToLowerInvariant();
            var store = new MemoryStore(userConfigDir, ctx.Cwd);

            switch (action)
            {
                case "write":
                {
                    var topic = Str(input, "topic");
                    var content = Str(input, "content");
                    if (string.IsNullOrWhiteSpace(topic) || content == null)
                        return new ToolResult(id, false, "topic and content are required for write", Error: "missing args");
                    var file = await store.WriteAsync(
                        MemoryStore.ParseScope(Str(input, "scope")), topic!, content, Str(input, "type"));
                    return new ToolResult(id, true, $"Saved memory → {file}");
                }
                case "read":
                {
                    var scopeStr = Str(input, "scope");
                    MemoryScope? scope = scopeStr == null ? null : MemoryStore.ParseScope(scopeStr);
                    var entries = await store.ListAsync(scope, Str(input, "topic"));
                    return new ToolResult(id, true, RenderEntries(entries));
                }
                default:
                {
                    var entries = await store.ListAsync();
                    return new ToolResult(id, true, RenderEntries(entries));
                }
            }
        }, input => $"Memory {Str(input, "action")}");
    }

    private static string RenderEntries(List<MemoryEntry> entries)
    {
        if (entries.Count == 0) return "(no memory entries)";
        var sb = new StringBuilder();
        sb.AppendLine($"{entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}:");
        foreach (var e in entries)
        {
            sb.AppendLine($"- [{e.Scope}] {e.Topic}{(e.Type != null ? $" ({e.Type})" : "")}  {e.UpdatedAt}");
            if (!string.IsNullOrWhiteSpace(e.Content))
                sb.AppendLine("  " + e.Content.Replace("\n", "\n  "));
        }
        return sb.ToString().TrimEnd();
    }

    // ── ask_user ────────────────────────────────────────────────────────────
    private static void RegisterAskUser(ToolRegistry registry, Func<string, List<string>, int>? askChoice)
    {
        var optionSchema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["label"] = Prop("string", "Option label"),
                ["description"] = Prop("string", "Option description"),
            },
            ["required"] = new[] { "label" },
        };

        var questionSchema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["question"] = Prop("string", "The question text"),
                ["header"] = Prop("string", "Short label (max 12 chars)"),
                ["options"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = optionSchema,
                    ["minItems"] = 2,
                    ["maxItems"] = 4,
                },
                ["multi_select"] = Prop("boolean", "Allow multiple selections"),
            },
            ["required"] = new[] { "question", "options" },
        };

        var def = new ToolDefinition(
            "ask_user",
            "Ask the user one or more structured questions and wait for their answers. Use when you need clarification " +
            "before proceeding. Each question must have 2-4 options.",
            ObjSchema(new Dictionary<string, object?>
            {
                ["questions"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = questionSchema,
                    ["minItems"] = 1,
                },
            }, new[] { "questions" }),
            ReadOnly: true);

        registry.Register(def, (input, ctx) =>
        {
            var id = Str(input, "_id") ?? "ask_user";
            if (askChoice == null || Console.IsInputRedirected)
                return Task.FromResult(new ToolResult(id, false,
                    "ask_user is unavailable in non-interactive mode. Make a reasonable assumption and continue.",
                    Error: "non_interactive"));

            var questions = ParseQuestions(input);
            if (questions.Count == 0)
                return Task.FromResult(new ToolResult(id, false, "no questions provided", Error: "no_questions"));

            var sb = new StringBuilder();
            foreach (var q in questions)
            {
                var labels = q.Options.Select(o =>
                    string.IsNullOrEmpty(o.Description) ? o.Label : $"{o.Label} — {o.Description}").ToList();
                var idx = askChoice(q.Question, labels);
                var chosen = idx >= 0 && idx < q.Options.Count ? q.Options[idx].Label : "(no answer)";
                sb.AppendLine($"Q: {q.Question}\nA: {chosen}");
            }
            return Task.FromResult(new ToolResult(id, true, sb.ToString().TrimEnd()));
        }, input =>
        {
            var n = ParseQuestions(input).Count;
            return $"Ask user ({n} question(s))";
        });
    }

    private sealed record AskOption(string Label, string? Description);
    private sealed record AskQuestion(string Question, List<AskOption> Options);

    private static List<AskQuestion> ParseQuestions(Dictionary<string, object?> input)
    {
        var outList = new List<AskQuestion>();
        if (!input.TryGetValue("questions", out var raw) || raw == null) return outList;

        JsonElement root;
        try
        {
            root = raw is JsonElement je ? je : JsonSerializer.SerializeToElement(raw);
        }
        catch { return outList; }

        if (root.ValueKind != JsonValueKind.Array) return outList;
        foreach (var q in root.EnumerateArray())
        {
            if (q.ValueKind != JsonValueKind.Object) continue;
            var text = q.TryGetProperty("question", out var t) ? t.GetString() ?? "" : "";
            var opts = new List<AskOption>();
            if (q.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in o.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        opts.Add(new AskOption(item.GetString() ?? "", null));
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        var label = item.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                        var desc = item.TryGetProperty("description", out var d) ? d.GetString() : null;
                        if (label.Length > 0) opts.Add(new AskOption(label, desc));
                    }
                }
            }
            if (text.Length > 0 && opts.Count > 0) outList.Add(new AskQuestion(text, opts));
        }
        return outList;
    }
}
