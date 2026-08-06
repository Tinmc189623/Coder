// Coder CLI (.NET 11 port) — providers (registry, OpenAI-compatible, Anthropic).
// Ported from src/providers/*.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Coder.Types;
using Coder.Utils;

namespace Coder.Providers;

// ── Provider registry / capabilities ────────────────────────────────────────
public static class ProviderRegistry
{
    private sealed record ProviderMeta(
        ProviderCapabilities Caps,
        string? DefaultModel,
        string? DefaultBaseURL,
        string[] ApiKeyEnvVars,
        int DefaultContextWindow);

    private static readonly Dictionary<string, ProviderMeta> Known = new()
    {
        ["anthropic"] = new(
            new ProviderCapabilities(true, true, true, true, true, true, true, "Anthropic", "ANTHROPIC_API_KEY", "claude-sonnet-4-5", 200_000, 200_000),
            "claude-sonnet-4-5", "https://api.anthropic.com", new[] { "ANTHROPIC_API_KEY" }, 200_000),
        ["openai"] = new(
            new ProviderCapabilities(true, true, true, true, false, true, true, "OpenAI", "OPENAI_API_KEY", "gpt-4o", 128_000, 128_000),
            "gpt-4o", "https://api.openai.com/v1", new[] { "OPENAI_API_KEY" }, 128_000),
        ["deepseek"] = new(
            new ProviderCapabilities(true, true, true, false, false, true, true, "DeepSeek", "DEEPSEEK_API_KEY", "deepseek-chat", 64_000, 64_000),
            "deepseek-chat", "https://api.deepseek.com/v1", new[] { "DEEPSEEK_API_KEY" }, 64_000),
        ["kimi"] = new(
            new ProviderCapabilities(true, true, true, false, false, true, true, "Kimi", "KIMI_API_KEY", "moonshot-v1-8k", 128_000, 128_000),
            "moonshot-v1-8k", "https://api.moonshot.cn/v1", new[] { "KIMI_API_KEY" }, 128_000),
        ["minimax"] = new(
            new ProviderCapabilities(true, true, true, false, false, true, true, "MiniMax", "MINIMAX_API_KEY", "abab6.5s-chat", 245_000, 245_000),
            "abab6.5s-chat", "https://api.minimax.chat/v1", new[] { "MINIMAX_API_KEY" }, 245_000),
    };

    public static bool IsKnownProvider(string id)
    {
        var lower = id.ToLowerInvariant();
        return Known.ContainsKey(lower) || CompatCatalog.ById(lower) != null;
    }

    public static ProviderCapabilities? GetProviderCaps(string id, string? baseUrl = null)
    {
        var lower = id.ToLowerInvariant();
        if (Known.TryGetValue(lower, out var m)) return m.Caps;
        var def = CompatCatalog.ById(lower);
        if (def != null) return def.Caps;
        if (!string.IsNullOrEmpty(baseUrl)) return OpenAICompatCaps(id);
        return null;
    }

    public static List<(string id, ProviderCapabilities caps)> ListProviderCaps()
    {
        var list = Known.Select(k => (k.Key, k.Value.Caps)).ToList();
        foreach (var def in CompatCatalog.All)
            if (!Known.ContainsKey(def.Id)) list.Add((def.Id, def.Caps));
        return list;
    }

    public static string? DefaultModelFor(string id)
    {
        var lower = id.ToLowerInvariant();
        if (Known.TryGetValue(lower, out var m)) return m.DefaultModel;
        return CompatCatalog.ById(lower)?.Caps.DefaultModel;
    }

    public static string? DefaultBaseURLFor(string id, bool cnMode)
    {
        var lower = id.ToLowerInvariant();
        if (Known.TryGetValue(lower, out var m)) return m.DefaultBaseURL;
        var def = CompatCatalog.ById(lower);
        return def == null ? null : CompatCatalog.ResolveBaseUrl(def, cnMode);
    }

    public static string[] ApiKeyEnvVarsFor(string id)
    {
        var lower = id.ToLowerInvariant();
        if (Known.TryGetValue(lower, out var m)) return m.ApiKeyEnvVars;
        return CompatCatalog.ById(lower)?.EnvKeys ?? Array.Empty<string>();
    }

    public static int? DefaultContextWindowFor(string id)
    {
        var lower = id.ToLowerInvariant();
        if (Known.TryGetValue(lower, out var m)) return m.DefaultContextWindow;
        return CompatCatalog.ById(lower)?.Caps.DefaultContextWindow;
    }

    public static bool IsCustomCompatProvider(string id, string? baseUrl)
        => !IsKnownProvider(id) && !string.IsNullOrEmpty(baseUrl);

    public static string? InferProviderFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var u = url!.ToLowerInvariant();
        if (u.Contains("deepseek")) return "deepseek";
        if (u.Contains("moonshot") || u.Contains("kimi")) return "kimi";
        if (u.Contains("minimax")) return "minimax";
        if (u.Contains("anthropic")) return "anthropic";
        return CompatCatalog.InferFromUrl(u);
    }

    public static string? InferProviderFromEnvKeys()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))) return "anthropic";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY"))) return "deepseek";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KIMI_API_KEY"))) return "kimi";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MINIMAX_API_KEY"))) return "minimax";
        var inferred = CompatCatalog.InferFromEnv();
        if (inferred != null) return inferred;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))) return "openai";
        return null;
    }

    private static ProviderCapabilities OpenAICompatCaps(string id) => new(
        Tools: true, Streaming: true, SystemChannel: true, Images: false, PromptCaching: false,
        Thinking: false, RequiresApiKey: true, DisplayName: id, ApiKeyEnvVar: "CODER_CLI_AUTH_TOKEN");

    public static IProvider MakeProvider(string id, string? apiKey, string? baseUrl, bool cnMode = false)
    {
        id = id.ToLowerInvariant();
        if (!Known.TryGetValue(id, out var meta))
        {
            // Catalogued OpenAI-compatible vendor (qwen / zhipu / groq / ollama / …).
            var def = CompatCatalog.ById(id);
            if (def != null)
            {
                var compatKey = apiKey ?? ResolveKey(def.EnvKeys) ?? def.ApiKeyFallback;
                if (def.Caps.RequiresApiKey && string.IsNullOrEmpty(compatKey))
                    throw new ProviderError($"{def.Caps.ApiKeyEnvVar} not set (export it or pass --api-key).");
                var compatBase = baseUrl ?? CompatCatalog.ResolveBaseUrl(def, cnMode);
                return new OpenAICompatProvider(def.Id, def.Caps, compatKey ?? "", compatBase)
                {
                    RequiresApiKey = def.Caps.RequiresApiKey
                };
            }

            // Custom OpenAI-compatible endpoint.
            var key = apiKey ?? Environment.GetEnvironmentVariable("CODER_CLI_AUTH_TOKEN") ?? "ollama";
            return new OpenAICompatProvider(id, OpenAICompatCaps(id), key, baseUrl ?? Environment.GetEnvironmentVariable("CODER_CLI_BASE_URL") ?? "http://localhost:11434/v1")
            {
                RequiresApiKey = !string.IsNullOrEmpty(apiKey) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODER_CLI_AUTH_TOKEN"))
            };
        }
        var resolvedKey = apiKey ?? ResolveKey(meta.ApiKeyEnvVars);
        if (meta.Caps.RequiresApiKey && string.IsNullOrEmpty(resolvedKey))
            throw new ProviderError($"{meta.Caps.ApiKeyEnvVar} not set (export it or pass --api-key).");
        var finalBase = baseUrl ?? meta.DefaultBaseURL ?? "";
        return id switch
        {
            "anthropic" => new AnthropicProvider(resolvedKey!, finalBase),
            _ => new OpenAICompatProvider(id, meta.Caps, resolvedKey ?? "ollama", finalBase),
        };
    }

    private static string? ResolveKey(string[] envKeys)
    {
        foreach (var k in envKeys)
        {
            var v = Environment.GetEnvironmentVariable(k);
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return Environment.GetEnvironmentVariable("CODER_CLI_AUTH_TOKEN");
    }
}

// ── Base provider ───────────────────────────────────────────────────────────
public abstract class BaseProvider : IProvider
{
    public abstract string Id { get; }
    public abstract ProviderCapabilities Capabilities { get; }

    public abstract Task<CompletionResult> CompleteAsync(CompletionRequest req);
    public abstract Task<CompletionResult> StreamAsync(CompletionRequest req, Action<IStreamEvent> onEvent);

    protected List<Message> Normalize(List<Message> messages)
    {
        var outList = new List<Message>();
        foreach (var m in messages)
        {
            if (m.Role == Role.Tool)
            {
                if (m.ToolResults != null)
                {
                    var blocks = m.ToolResults.Select(tr => new UserContentBlock(
                        "tool_result", Content: tr.Ok ? tr.Output : (tr.Error ?? tr.Output), ToolUseId: tr.InvocationId, IsError: !tr.Ok)).ToList();
                    outList.Add(new Message(Role.User, blocks));
                }
            }
            else outList.Add(m);
        }
        return outList;
    }

    protected static List<AssistantContent> FinalizeContent(string text, List<AssistantToolUse> toolCalls)
    {
        var outList = new List<AssistantContent>();
        if (!string.IsNullOrEmpty(text)) outList.Add(new AssistantText(text));
        outList.AddRange(toolCalls);
        return outList;
    }
}

// ── OpenAI-compatible provider ──────────────────────────────────────────────
public class OpenAICompatProvider : BaseProvider
{
    protected readonly HttpClient Http;
    protected readonly string ApiKey;
    protected readonly string BaseURL;
    public bool RequiresApiKey { get; set; } = true;

    public OpenAICompatProvider(string id, ProviderCapabilities caps, string apiKey, string baseUrl)
    {
        Id = id;
        Capabilities = caps;
        ApiKey = apiKey;
        BaseURL = baseUrl.TrimEnd('/');
        Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public override string Id { get; }
    public override ProviderCapabilities Capabilities { get; }

    protected virtual string Endpoint() => $"{BaseURL}/chat/completions";

    protected virtual void ApplyAuth(HttpRequestMessage req)
    {
        if (RequiresApiKey) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
    }

    public override async Task<CompletionResult> CompleteAsync(CompletionRequest req)
    {
        var body = BuildBody(req, stream: false);
        var resp = await PostAsync(body);
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var (text, toolCalls, usage) = ParseChoice(root.GetProperty("choices")[0]);
        return new CompletionResult(FinalizeContent(text, toolCalls), StopReason: "stop", Usage: usage);
    }

    public override async Task<CompletionResult> StreamAsync(CompletionRequest req, Action<IStreamEvent> onEvent)
    {
        var body = BuildBody(req, stream: true);
        var resp = await PostAsync(body, stream: true);
        using var stream = await resp.Content.ReadAsStreamAsync();
        var (text, toolCalls, usage) = await ParseSseAsync(stream, onEvent, req.Signal);
        onEvent(new StreamEventDone(Usage: usage));
        return new CompletionResult(FinalizeContent(text, toolCalls), Usage: usage);
    }

    // ── request building ────────────────────────────────────────────────
    protected virtual Dictionary<string, object?> BuildBody(CompletionRequest req, bool stream)
    {
        var messages = new List<Dictionary<string, object?>>();

        // OpenAI-compatible endpoints carry the system prompt as the first message.
        var system = req.System;
        if (!string.IsNullOrWhiteSpace(system))
            messages.Add(new() { ["role"] = "system", ["content"] = system });

        foreach (var m in Normalize(req.Messages))
        {
            switch (m.Content)
            {
                case string s:
                    messages.Add(new() { ["role"] = m.Role.ToString().ToLowerInvariant(), ["content"] = s });
                    break;
                case List<AssistantContent> ac:
                    messages.Add(ConvertAssistant(ac));
                    break;
                case List<UserContentBlock> uc:
                    messages.Add(ConvertUserBlocks(uc));
                    break;
            }
        }
        var body = new Dictionary<string, object?>
        {
            ["model"] = req.Model,
            ["messages"] = messages,
            ["stream"] = stream,
            ["temperature"] = req.Temperature ?? 0.2,
            ["max_tokens"] = req.MaxTokens ?? 8192,
        };
        if (req.Tools != null && req.Tools.Count > 0)
        {
            body["tools"] = req.Tools.Select(t => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?> { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.InputSchema },
            }).ToList();
            body["tool_choice"] = "auto";
        }
        if (stream) body["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true };
        return body;
    }

    private Dictionary<string, object?> ConvertAssistant(List<AssistantContent> ac)
    {
        var textParts = new List<string>();
        var toolCalls = new List<Dictionary<string, object?>>();
        int idx = 0;
        foreach (var c in ac)
        {
            switch (c)
            {
                case AssistantText t: textParts.Add(t.Text); break;
                case AssistantToolUse tu:
                    toolCalls.Add(new Dictionary<string, object?>
                    {
                        ["id"] = tu.Id,
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?> { ["name"] = tu.Name, ["arguments"] = JsonSerializer.Serialize(tu.Input) },
                    });
                    idx++;
                    break;
            }
        }
        var d = new Dictionary<string, object?> { ["role"] = "assistant" };
        if (textParts.Count > 0) d["content"] = string.Join("", textParts);
        else d["content"] = null;
        if (toolCalls.Count > 0) d["tool_calls"] = toolCalls;
        return d;
    }

    private Dictionary<string, object?> ConvertUserBlocks(List<UserContentBlock> uc)
    {
        var content = uc.Select(b =>
        {
            if (b.Type == "tool_result")
                return new Dictionary<string, object?> { ["type"] = "tool_result", ["tool_call_id"] = b.ToolUseId, ["content"] = b.Content?.ToString() ?? "" };
            return new Dictionary<string, object?> { ["type"] = b.Type, ["text"] = b.Text ?? "" };
        }).ToList();
        return new Dictionary<string, object?> { ["role"] = "user", ["content"] = content };
    }

    // ── HTTP ────────────────────────────────────────────────────────────
    protected async Task<HttpResponseMessage> PostAsync(object body, bool stream = false)
    {
        var json = JsonSerializer.Serialize(body);
        var req = new HttpRequestMessage(HttpMethod.Post, Endpoint())
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        ApplyAuth(req);
        var resp = await Http.SendAsync(req, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new ProviderError($"HTTP {(int)resp.StatusCode}: {err}");
        }
        return resp;
    }

    // ── response parsing ────────────────────────────────────────────────
    protected (string text, List<AssistantToolUse> toolCalls, TokenUsage?) ParseChoice(JsonElement choice)
    {
        var text = "";
        var toolCalls = new List<AssistantToolUse>();
        if (choice.TryGetProperty("message", out var msg))
        {
            if (msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String) text = c.GetString() ?? "";
            if (msg.TryGetProperty("tool_calls", out var tcs))
                foreach (var tc in tcs.EnumerateArray())
                {
                    var id = tc.GetProperty("id").GetString() ?? "";
                    var name = tc.GetProperty("function").GetProperty("name").GetString() ?? "";
                    var argsJson = tc.GetProperty("function").GetProperty("arguments").GetString() ?? "{}";
                    var input = ParseArgs(argsJson);
                    toolCalls.Add(new AssistantToolUse(id, name, input));
                }
        }
        var usage = TryParseUsage(choice);
        return (text, toolCalls, usage);
    }

    protected static Dictionary<string, object?> ParseArgs(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new(); }
        catch { return new() { ["_raw"] = json }; }
    }

    protected TokenUsage? TryParseUsage(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var u))
        {
            int inp = u.TryGetProperty("prompt_tokens", out var i) ? i.GetInt32() : 0;
            int outp = u.TryGetProperty("completion_tokens", out var o) ? o.GetInt32() : 0;
            return new TokenUsage(inp, outp);
        }
        return null;
    }

    protected virtual async Task<(string text, List<AssistantToolUse> toolCalls, TokenUsage?)> ParseSseAsync(
        Stream stream, Action<IStreamEvent> onEvent, CancellationToken signal)
    {
        var text = "";
        var toolCalls = new List<AssistantToolUse>();
        var toolBuffers = new Dictionary<int, string>();
        var toolIds = new Dictionary<int, string>();
        var toolNames = new Dictionary<int, string>();
        var started = new HashSet<int>();
        TokenUsage? usage = null;

        // A tool call is announced across several frames: the first carries id +
        // name, the rest stream the JSON arguments. Emit ToolUseStart as soon as
        // both id and name are known so the UI can show the call while it builds.
        void MaybeStart(int idx)
        {
            if (started.Contains(idx)) return;
            if (!toolIds.TryGetValue(idx, out var id) || string.IsNullOrEmpty(id)) return;
            if (!toolNames.TryGetValue(idx, out var name) || string.IsNullOrEmpty(name)) return;
            started.Add(idx);
            onEvent(new StreamEventToolUseStart(id, name));
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (signal.IsCancellationRequested) break;
            if (!line.StartsWith("data:")) continue;
            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]") break;
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                if (root.TryGetProperty("usage", out var ru) && ru.ValueKind == JsonValueKind.Object)
                    usage = ParseUsage(ru);
                continue;
            }
            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta))
            {
                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    var piece = c.GetString() ?? "";
                    text += piece;
                    onEvent(new StreamEventText(piece));
                }
                if (delta.TryGetProperty("tool_calls", out var tcs))
                {
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        int idx = tc.TryGetProperty("index", out var ix) ? ix.GetInt32() : 0;
                        if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                            toolIds[idx] = idEl.GetString() ?? "";
                        if (tc.TryGetProperty("function", out var fn))
                        {
                            if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                                toolNames[idx] = n.GetString() ?? "";
                            MaybeStart(idx);
                            if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String)
                            {
                                var piece = a.GetString() ?? "";
                                if (!toolBuffers.ContainsKey(idx)) toolBuffers[idx] = "";
                                toolBuffers[idx] += piece;
                                if (piece.Length > 0 && toolIds.TryGetValue(idx, out var tid))
                                    onEvent(new StreamEventToolUseDelta(tid, piece));
                            }
                        }
                        MaybeStart(idx);
                    }
                }
            }
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                usage = ParseUsage(u);
        }
        // Finalize tool calls: arguments are only valid JSON once fully buffered.
        foreach (var kv in toolIds.OrderBy(k => k.Key))
        {
            var idx = kv.Key;
            var id = kv.Value;
            var name = toolNames.TryGetValue(idx, out var nm) ? nm : "tool";
            var buf = toolBuffers.TryGetValue(idx, out var b) && b.Length > 0 ? b : "{}";
            var input = ParseArgs(buf);
            toolCalls.Add(new AssistantToolUse(id, name, input));
            MaybeStart(idx);
            onEvent(new StreamEventToolUseEnd(id, input));
        }
        return (text, toolCalls, usage);
    }

    protected TokenUsage? ParseUsage(JsonElement u)
    {
        int inp = u.TryGetProperty("prompt_tokens", out var i) ? i.GetInt32() : 0;
        int outp = u.TryGetProperty("completion_tokens", out var o) ? o.GetInt32() : 0;
        int cr = u.TryGetProperty("prompt_tokens_details", out var pd) && pd.TryGetProperty("cached_tokens", out var ct) ? ct.GetInt32() : 0;
        return new TokenUsage(inp, outp, cr, 0);
    }
}

// ── Anthropic provider ──────────────────────────────────────────────────────
public sealed class AnthropicProvider : OpenAICompatProvider
{
    public AnthropicProvider(string apiKey, string baseUrl) : base("anthropic", ProviderRegistry.GetProviderCaps("anthropic")!, apiKey, baseUrl) { }

    protected override string Endpoint() => $"{BaseURL}/v1/messages";

    // Anthropic replies with a `content` block array, not OpenAI `choices`, so
    // the inherited non-streaming parser cannot be reused.
    public override async Task<CompletionResult> CompleteAsync(CompletionRequest req)
    {
        var resp = await PostAsync(BuildBody(req, stream: false));
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = "";
        var toolCalls = new List<AssistantToolUse>();
        if (root.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in blocks.EnumerateArray())
            {
                var kind = b.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                if (kind == "text" && b.TryGetProperty("text", out var tEl))
                    text += tEl.GetString() ?? "";
                else if (kind == "tool_use")
                    toolCalls.Add(new AssistantToolUse(
                        b.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                        b.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "",
                        b.TryGetProperty("input", out var inEl)
                            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(inEl.GetRawText()) ?? new()
                            : new()));
            }
        }

        TokenUsage? usage = null;
        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            usage = new TokenUsage(
                ReadInt(u, "input_tokens", 0), ReadInt(u, "output_tokens", 0),
                ReadInt(u, "cache_read_input_tokens", 0), ReadInt(u, "cache_creation_input_tokens", 0));

        var stop = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
        return new CompletionResult(FinalizeContent(text, toolCalls), StopReason: stop ?? "stop", Usage: usage);
    }

    protected override void ApplyAuth(HttpRequestMessage req)
    {
        req.Headers.Add("x-api-key", ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
    }

    protected override Dictionary<string, object?> BuildBody(CompletionRequest req, bool stream)
    {
        var msgs = new List<Dictionary<string, object?>>();
        foreach (var m in Normalize(req.Messages))
        {
            if (m.Role == Role.System) continue; // system handled separately
            switch (m.Content)
            {
                case string s:
                    msgs.Add(new() { ["role"] = "user", ["content"] = s });
                    break;
                case List<AssistantContent> ac:
                    msgs.Add(ConvertAssistantAnthropic(ac));
                    break;
                case List<UserContentBlock> uc:
                    msgs.Add(new() { ["role"] = "user", ["content"] = uc.Select(b => (object)new Dictionary<string, object?>
                    {
                        ["type"] = b.Type == "tool_result" ? "tool_result" : b.Type,
                        ["tool_use_id"] = b.ToolUseId,
                        ["content"] = b.Content?.ToString() ?? b.Text ?? "",
                    }).ToList() });
                    break;
            }
        }
        var body = new Dictionary<string, object?>
        {
            ["model"] = req.Model,
            ["messages"] = msgs,
            ["max_tokens"] = req.MaxTokens ?? 8192,
            ["stream"] = stream,
        };
        if (!string.IsNullOrEmpty(req.System)) body["system"] = req.System;
        if (req.Tools != null && req.Tools.Count > 0)
        {
            body["tools"] = req.Tools.Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.InputSchema,
            }).ToList();
        }
        return body;
    }

    private Dictionary<string, object?> ConvertAssistantAnthropic(List<AssistantContent> ac)
    {
        var blocks = new List<Dictionary<string, object?>>();
        foreach (var c in ac)
        {
            switch (c)
            {
                case AssistantText t: blocks.Add(new() { ["type"] = "text", ["text"] = t.Text }); break;
                case AssistantToolUse tu: blocks.Add(new() { ["type"] = "tool_use", ["id"] = tu.Id, ["name"] = tu.Name, ["input"] = tu.Input }); break;
            }
        }
        return new() { ["role"] = "assistant", ["content"] = blocks };
    }

    protected override async Task<(string text, List<AssistantToolUse> toolCalls, TokenUsage?)> ParseSseAsync(
        Stream stream, Action<IStreamEvent> onEvent, CancellationToken signal)
    {
        var text = "";
        // Anthropic addresses every block by its position in the content array,
        // so buffers must be keyed by that index -- text and thinking blocks can
        // sit in front of a tool_use block and shift its position.
        var toolByIndex = new SortedDictionary<int, AssistantToolUse>();
        var toolInputs = new Dictionary<int, string>();
        int inputTokens = 0, outputTokens = 0, cacheRead = 0, cacheWrite = 0;
        bool sawUsage = false;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (signal.IsCancellationRequested) break;
            if (!line.StartsWith("data:")) continue;
            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0) continue;
            if (payload == "[DONE]") break;
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) continue;
            var idx = root.TryGetProperty("index", out var ixEl) && ixEl.ValueKind == JsonValueKind.Number
                ? ixEl.GetInt32() : -1;

            switch (typeEl.GetString())
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var ms) && ms.TryGetProperty("usage", out var mu))
                    {
                        sawUsage = true;
                        inputTokens = ReadInt(mu, "input_tokens", inputTokens);
                        outputTokens = ReadInt(mu, "output_tokens", outputTokens);
                        cacheRead = ReadInt(mu, "cache_read_input_tokens", cacheRead);
                        cacheWrite = ReadInt(mu, "cache_creation_input_tokens", cacheWrite);
                    }
                    break;

                case "content_block_start":
                    if (root.TryGetProperty("content_block", out var cb)
                        && cb.TryGetProperty("type", out var cbt) && cbt.GetString() == "tool_use")
                    {
                        var id = cb.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                        var name = cb.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                        toolByIndex[idx < 0 ? toolByIndex.Count : idx] = new AssistantToolUse(id, name, new());
                        onEvent(new StreamEventToolUseStart(id, name));
                    }
                    break;

                case "content_block_delta":
                    if (root.TryGetProperty("delta", out var d) && d.TryGetProperty("type", out var dt))
                    {
                        switch (dt.GetString())
                        {
                            case "text_delta":
                                {
                                    var piece = d.TryGetProperty("text", out var tEl) ? tEl.GetString() ?? "" : "";
                                    text += piece;
                                    onEvent(new StreamEventText(piece));
                                    break;
                                }
                            case "thinking_delta":
                                onEvent(new StreamEventThinking(d.TryGetProperty("thinking", out var thEl) ? thEl.GetString() ?? "" : ""));
                                break;
                            case "input_json_delta":
                                {
                                    var piece = d.TryGetProperty("partial_json", out var pj) ? pj.GetString() ?? "" : "";
                                    if (idx >= 0 && piece.Length > 0)
                                    {
                                        toolInputs[idx] = (toolInputs.TryGetValue(idx, out var b) ? b : "") + piece;
                                        if (toolByIndex.TryGetValue(idx, out var pending))
                                            onEvent(new StreamEventToolUseDelta(pending.Id, piece));
                                    }
                                    break;
                                }
                        }
                    }
                    break;

                case "content_block_stop":
                    if (idx >= 0 && toolByIndex.TryGetValue(idx, out var done))
                    {
                        var input = toolInputs.TryGetValue(idx, out var buf) && buf.Length > 0 ? ParseArgs(buf) : new();
                        toolByIndex[idx] = done with { Input = input };
                        onEvent(new StreamEventToolUseEnd(done.Id, input));
                    }
                    break;

                case "message_delta":
                    if (root.TryGetProperty("usage", out var du))
                    {
                        sawUsage = true;
                        outputTokens = ReadInt(du, "output_tokens", outputTokens);
                        inputTokens = ReadInt(du, "input_tokens", inputTokens);
                    }
                    break;
            }
        }

        var toolCalls = toolByIndex.Values.ToList();
        var usage = sawUsage ? new TokenUsage(inputTokens, outputTokens, cacheRead, cacheWrite) : null;
        return (text, toolCalls, usage);
    }

    private static int ReadInt(JsonElement obj, string name, int fallback)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : fallback;
}
