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

    /// <summary>OpenAI 协议选择:读 CODER_CLI_OPENAI_PROTOCOL(config/env),仅 "responses" 启用新协议,其余默认 "chat"。</summary>
    public static string ResolveOpenAiProtocol(CoderConfig? config)
        => config?.OpenAiProtocol?.ToLowerInvariant() ?? string.Empty;

    public static IProvider MakeProvider(string id, string? apiKey, string? baseUrl, bool cnMode = false, CoderConfig? config = null)
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
            "anthropic" => CreateAnthropic(resolvedKey!, finalBase, config),
            // OpenAI: CODER_CLI_OPENAI_PROTOCOL=responses 走 /v1/responses,默认仍为 /chat/completions。
            "openai" when ResolveOpenAiProtocol(config) == "responses" =>
                new OpenAIResponsesProvider("openai", meta.Caps, resolvedKey ?? "ollama", finalBase),
            _ => new OpenAICompatProvider(id, meta.Caps, resolvedKey ?? "ollama", finalBase),
        };
    }

    /// <summary>创建 Anthropic provider,按 EffortLevel 映射 thinking 预算,读取 thinking/cache 开关。</summary>
    private static AnthropicProvider CreateAnthropic(string apiKey, string baseUrl, CoderConfig? config)
    {
        if (config == null) return new AnthropicProvider(apiKey, baseUrl);
        var (budget, _, _) = Coder.Config.ConfigLoader.EffortToSettings(config.EffortLevel);
        return new AnthropicProvider(apiKey, baseUrl,
            thinkingBudget: budget,
            enableThinking: config.AnthropicThinking,
            enableCache: config.AnthropicCache);
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

    /// <summary>精确 token 计数;基类不支持,子类(如 Anthropic count_tokens)可覆写。</summary>
    public virtual Task<int?> CountInputTokensAsync(CompletionRequest req) => Task.FromResult<int?>(null);

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

// ── OpenAI Responses API provider (/v1/responses) ───────────────────────────
// The Responses API is OpenAI's successor to chat/completions: `input` array
// instead of `messages`, function_call items instead of tool_calls, and a
// different SSE event surface. Kept as a separate provider because the wire
// formats share almost nothing with the chat-completions family.
public class OpenAIResponsesProvider : BaseProvider
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _id;

    public OpenAIResponsesProvider(string id, ProviderCapabilities caps, string apiKey, string baseUrl)
    {
        _id = id;
        Capabilities = caps;
        _apiKey = apiKey;
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public override string Id => _id;
    public override ProviderCapabilities Capabilities { get; }

    private string Endpoint() => $"{_baseUrl}/responses";

    // ── complete / stream ───────────────────────────────────────────────
    public override async Task<CompletionResult> CompleteAsync(CompletionRequest req)
    {
        var resp = await PostAsync(BuildBody(req, stream: false));
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = "";
        var toolCalls = new List<AssistantToolUse>();
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                var kind = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (kind)
                {
                    case "message":
                        if (item.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
                            foreach (var b in blocks.EnumerateArray())
                                if (b.TryGetProperty("type", out var bt) && bt.GetString() == "output_text"
                                    && b.TryGetProperty("text", out var tEl))
                                    text += tEl.GetString() ?? "";
                        break;
                    case "function_call":
                        toolCalls.Add(new AssistantToolUse(
                            item.TryGetProperty("call_id", out var cid) ? cid.GetString() ?? "" : "",
                            item.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                            ParseArgs(item.TryGetProperty("arguments", out var ar) ? ar.GetString() ?? "{}" : "{}")));
                        break;
                }
            }
        }
        var usage = ParseUsageJson(root);
        return new CompletionResult(FinalizeContent(text, toolCalls), StopReason: "stop", Usage: usage);
    }

    public override async Task<CompletionResult> StreamAsync(CompletionRequest req, Action<IStreamEvent> onEvent)
    {
        var resp = await PostAsync(BuildBody(req, stream: true), stream: true);
        using var stream = await resp.Content.ReadAsStreamAsync();
        var (text, toolCalls, usage) = await ParseResponsesSseAsync(stream, onEvent, req.Signal);
        onEvent(new StreamEventDone(Usage: usage));
        return new CompletionResult(FinalizeContent(text, toolCalls), Usage: usage);
    }

    // ── request building ────────────────────────────────────────────────
    protected virtual Dictionary<string, object?> BuildBody(CompletionRequest req, bool stream)
    {
        var input = new List<object?>();
        if (!string.IsNullOrWhiteSpace(req.System))
            input.Add(new Dictionary<string, object?> { ["role"] = "system", ["content"] = req.System });

        foreach (var m in Normalize(req.Messages))
        {
            switch (m.Content)
            {
                case string s:
                    input.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = s });
                    break;
                case List<AssistantContent> ac:
                    // Assistant turn: text becomes an assistant message, tool
                    // calls become top-level function_call items.
                    var textParts = new List<string>();
                    var toolCalls = new List<object?>();
                    foreach (var c in ac)
                    {
                        switch (c)
                        {
                            case AssistantText t: textParts.Add(t.Text); break;
                            case AssistantToolUse tu:
                                toolCalls.Add(new Dictionary<string, object?>
                                {
                                    ["type"] = "function_call",
                                    ["call_id"] = tu.Id,
                                    ["name"] = tu.Name,
                                    ["arguments"] = JsonSerializer.Serialize(tu.Input),
                                });
                                break;
                        }
                    }
                    if (textParts.Count > 0)
                        input.Add(new Dictionary<string, object?>
                        {
                            ["role"] = "assistant",
                            ["content"] = new List<object?>
                            {
                                new Dictionary<string, object?> { ["type"] = "output_text", ["text"] = string.Join("", textParts) },
                            },
                        });
                    input.AddRange(toolCalls);
                    break;
                case List<UserContentBlock> uc:
                    // User turn: text blocks become input_text, tool results
                    // become function_call_output items.
                    var content = new List<object?>();
                    foreach (var b in uc)
                    {
                        if (b.Type == "tool_result")
                            input.Add(new Dictionary<string, object?>
                            {
                                ["type"] = "function_call_output",
                                ["call_id"] = b.ToolUseId,
                                ["output"] = b.Content?.ToString() ?? b.Text ?? "",
                            });
                        else
                            content.Add(new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = b.Text ?? "" });
                    }
                    if (content.Count > 0)
                        input.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = content });
                    break;
            }
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = req.Model,
            ["input"] = input,
            ["stream"] = stream,
            ["temperature"] = req.Temperature ?? 0.2,
            ["max_output_tokens"] = req.MaxTokens ?? 8192,
        };
        if (req.Tools != null && req.Tools.Count > 0)
        {
            body["tools"] = req.Tools.Select(t => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = t.InputSchema,
            }).ToList();
            body["tool_choice"] = req.ToolChoice switch
            {
                ToolChoiceNone => "none",
                ToolChoiceAny => "auto",
                _ => "auto",
            };
        }
        return body;
    }

    // ── HTTP ────────────────────────────────────────────────────────────
    private static Dictionary<string, object?> ParseArgs(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new(); }
        catch { return new() { ["_raw"] = json }; }
    }

    private async Task<HttpResponseMessage> PostAsync(object body, bool stream = false)
    {
        var json = JsonSerializer.Serialize(body);
        var req = new HttpRequestMessage(HttpMethod.Post, Endpoint())
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        var resp = await _http.SendAsync(req, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new ProviderError($"HTTP {(int)resp.StatusCode}: {err}");
        }
        return resp;
    }

    // ── SSE parsing (responses event surface) ───────────────────────────
    protected virtual async Task<(string text, List<AssistantToolUse> toolCalls, TokenUsage?)> ParseResponsesSseAsync(
        Stream stream, Action<IStreamEvent> onEvent, CancellationToken signal)
    {
        var text = "";
        var toolCalls = new List<AssistantToolUse>();
        var toolById = new Dictionary<string, (string name, string args)>();
        var started = new HashSet<string>();

        void MaybeStart(string id)
        {
            if (started.Contains(id)) return;
            if (!toolById.TryGetValue(id, out var info)) return;
            started.Add(id);
            onEvent(new StreamEventToolUseStart(id, info.name));
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (signal.IsCancellationRequested) break;
            if (!line.StartsWith("data:")) continue;
            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0 || payload == "[DONE]") continue;
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) continue;
            var evt = typeEl.GetString();

            switch (evt)
            {
                case "response.output_text.delta":
                    if (root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String)
                    {
                        var piece = d.GetString() ?? "";
                        text += piece;
                        onEvent(new StreamEventText(piece));
                    }
                    break;

                case "response.output_item.added":
                    if (root.TryGetProperty("item", out var item)
                        && item.TryGetProperty("type", out var it) && it.GetString() == "function_call")
                    {
                        var id = item.TryGetProperty("call_id", out var cid) ? cid.GetString() ?? "" : "";
                        var name = item.TryGetProperty("name", out var nm) ? nm.GetString() ?? "tool" : "tool";
                        toolById[id] = (name, "");
                        MaybeStart(id);
                    }
                    break;

                case "response.function_call_arguments.delta":
                    if (root.TryGetProperty("item_id", out var iid) && root.TryGetProperty("delta", out var ad))
                    {
                        var key = iid.GetString() ?? "";
                        var piece = ad.GetString() ?? "";
                        if (toolById.TryGetValue(key, out var info))
                        {
                            toolById[key] = (info.name, info.args + piece);
                            if (piece.Length > 0) onEvent(new StreamEventToolUseDelta(key, piece));
                        }
                    }
                    break;

                case "response.output_item.done":
                    if (root.TryGetProperty("output_item", out var doneItem))
                    {
                        var id = doneItem.TryGetProperty("call_id", out var cid2) ? cid2.GetString() ?? "" : "";
                        if (toolById.TryGetValue(id, out var fin))
                        {
                            var input = ParseArgs(fin.args.Length > 0 ? fin.args : "{}");
                            toolCalls.Add(new AssistantToolUse(id, fin.name, input));
                            onEvent(new StreamEventToolUseEnd(id, input));
                        }
                    }
                    break;

                case "response.completed":
                    if (root.TryGetProperty("response", out var respEl))
                    {
                        var u = ParseUsageJson(respEl);
                        if (u != null) return (text, toolCalls, u);
                    }
                    break;
            }
        }
        return (text, toolCalls, null);
    }

    /// <summary>Responses API 的 usage 字段名(input_tokens/output_tokens)。</summary>
    private static TokenUsage? ParseUsageJson(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            int inp = u.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0;
            int outp = u.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0;
            int cr = u.TryGetProperty("input_tokens_details", out var id)
                && id.TryGetProperty("cached_tokens", out var ct) ? ct.GetInt32() : 0;
            return new TokenUsage(inp, outp, cr, 0);
        }
        return null;
    }
}

// ── Anthropic provider ──────────────────────────────────────────────────────
public class AnthropicProvider : OpenAICompatProvider
{
    private readonly int _thinkingBudget;
    private readonly bool _enableThinking;
    private readonly bool _enableCache;

    public AnthropicProvider(string apiKey, string baseUrl, int thinkingBudget = 4000, bool enableThinking = true, bool enableCache = true)
        : base("anthropic", ProviderRegistry.GetProviderCaps("anthropic")!, apiKey, baseUrl)
    {
        _thinkingBudget = thinkingBudget;
        _enableThinking = enableThinking;
        _enableCache = enableCache;
    }

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
        var thinking = new List<string>();
        if (root.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in blocks.EnumerateArray())
            {
                var kind = b.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                if (kind == "text" && b.TryGetProperty("text", out var tEl))
                    text += tEl.GetString() ?? "";
                else if (kind == "thinking" && b.TryGetProperty("thinking", out var thEl))
                    thinking.Add(thEl.GetString() ?? "");
                else if (kind == "tool_use")
                    toolCalls.Add(new AssistantToolUse(
                        b.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                        b.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "",
                        b.TryGetProperty("input", out var inEl)
                            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(inEl.GetRawText()) ?? new()
                            : new()));
            }
        }
        // Thinking 块跟随内容一起返回(UI 通过 hooks 消费,消息历史保留推理轨迹)。
        var content = FinalizeContent(text, toolCalls);
        if (thinking.Count > 0)
            content.InsertRange(0, thinking.Select(t => (AssistantContent)new AssistantThinking(t)));

        TokenUsage? usage = null;
        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            usage = new TokenUsage(
                ReadInt(u, "input_tokens", 0), ReadInt(u, "output_tokens", 0),
                ReadInt(u, "cache_read_input_tokens", 0), ReadInt(u, "cache_creation_input_tokens", 0));

        var stop = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
        return new CompletionResult(content, StopReason: stop ?? "stop", Usage: usage);
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
        // Extended Thinking:预算来自 EffortLevel 映射,缓存命中时按 cache_control 链路计费。
        if (_enableThinking && _thinkingBudget > 0)
            body["thinking"] = new Dictionary<string, object?> { ["type"] = "enabled", ["budget_tokens"] = _thinkingBudget };

        // Prompt Caching:system 必须是 block 数组才能挂 cache_control;工具列表末项挂 breakpoint。
        if (_enableCache)
        {
            if (!string.IsNullOrEmpty(req.System))
                body["system"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = req.System,
                        ["cache_control"] = new Dictionary<string, object?> { ["type"] = "ephemeral" },
                    },
                };
            else if (req.System != null)
                body["system"] = req.System;
        }
        else if (!string.IsNullOrEmpty(req.System))
        {
            body["system"] = req.System;
        }

        if (req.Tools != null && req.Tools.Count > 0)
        {
            var tools = req.Tools.Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.InputSchema,
            }).ToList();
            if (_enableCache)
                tools[^1]["cache_control"] = new Dictionary<string, object?> { ["type"] = "ephemeral" };
            body["tools"] = tools;
        }
        return body;
    }

    // ── Count Tokens API ─────────────────────────────────────────────────
    /// <summary>POST /v1/messages/count_tokens 精确统计输入 token,供上下文预算使用。</summary>
    public override async Task<int?> CountInputTokensAsync(CompletionRequest req)
    {
        var body = BuildBody(req, stream: false);
        body.Remove("stream");
        body.Remove("max_tokens");
        body.Remove("thinking");
        var resp = await PostAsync(body);
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("input_tokens", out var it))
            return it.GetInt32();
        return null;
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
