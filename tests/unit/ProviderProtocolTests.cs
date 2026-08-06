using System.Text;
using Coder.Providers;
using Coder.Types;

namespace Coder.Tests;

/// <summary>暴露 OpenAIResponsesProvider 的 SSE 解析器与请求体构建供测试驱动。</summary>
public class TestResponsesProvider : OpenAIResponsesProvider
{
    public TestResponsesProvider()
        : base("test", new ProviderCapabilities(true, true, true, false, false, false, false, "Test"), "k", "http://x") { }

    public Task<(string text, List<AssistantToolUse> toolCalls, TokenUsage? usage)> Parse(Stream s, Action<IStreamEvent> onEvent)
        => ParseResponsesSseAsync(s, onEvent, CancellationToken.None);

    public Dictionary<string, object?> Body(CompletionRequest req, bool stream)
        => BuildBody(req, stream);
}

/// <summary>暴露 AnthropicProvider 的请求体构建,断言 thinking/cache 挂载。</summary>
public class TestAnthropicProvider : AnthropicProvider
{
    public TestAnthropicProvider(int budget = 4000, bool thinking = true, bool cache = true)
        : base("k", "http://x", budget, thinking, cache) { }

    public Dictionary<string, object?> Body(CompletionRequest req, bool stream)
        => BuildBody(req, stream);
}

public class ProviderProtocolTests
{
    // ── OpenAI Responses API:SSE 事件面 ─────────────────────────────────

    [Test]
    public void ResponsesSseEmitsTextAndToolUse()
    {
        var sse =
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"hel\"}\n\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"lo\"}\n\n" +
            "data: {\"type\":\"response.output_item.added\",\"item\":{\"type\":\"function_call\",\"call_id\":\"fc_1\",\"name\":\"file_write\"}}\n\n" +
            "data: {\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"fc_1\",\"delta\":\"{\\\"path\\\":\\\"a\\\"}\"}\n\n" +
            "data: {\"type\":\"response.output_item.done\",\"output_item\":{\"type\":\"function_call\",\"call_id\":\"fc_1\",\"name\":\"file_write\"}}\n\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}}\n\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var events = new List<IStreamEvent>();
        var prov = new TestResponsesProvider();
        var (text, tools, usage) = prov.Parse(ms, e => events.Add(e)).Result;

        Assert.Equal("hello", text, "output_text deltas must concatenate");
        Assert.Equal(1, tools.Count);
        Assert.Equal("fc_1", tools[0].Id);
        Assert.Equal("file_write", tools[0].Name);
        Assert.True(tools[0].Input.TryGetValue("path", out var p) && p?.ToString() == "a",
            "function_call arguments must be parsed from buffered deltas");
        Assert.True(events.Any(e => e is StreamEventToolUseStart), "must emit tool_use_start");
        Assert.True(events.Any(e => e is StreamEventToolUseEnd), "must emit tool_use_end");
        Assert.Equal(10, usage?.InputTokens ?? -1);
    }

    [Test]
    public void ResponsesBodyConvertsToolResultsToFunctionCallOutput()
    {
        var prov = new TestResponsesProvider();
        var req = new CompletionRequest("m", new List<Message>
        {
            new(Role.Tool, new List<UserContentBlock>(), ToolResults: new List<ToolResult>
            {
                new("fc_1", true, "ok"),
            }),
        });
        var body = prov.Body(req, stream: false);
        var input = body["input"] as List<object?>;
        Assert.Equal(1, input?.Count ?? -1);
        var item = input![0] as Dictionary<string, object?>;
        Assert.Equal("function_call_output", item?["type"]?.ToString());
        Assert.Equal("fc_1", item?["call_id"]?.ToString());
        Assert.Equal("ok", item?["output"]?.ToString());
    }

    [Test]
    public void ResponsesBodyConvertsToolUseToFunctionCallItems()
    {
        var prov = new TestResponsesProvider();
        var req = new CompletionRequest("m", new List<Message>
        {
            new(Role.Assistant, new List<AssistantContent>
            {
                new AssistantText("checking"),
                new AssistantToolUse("fc_2", "glob", new Dictionary<string, object?> { ["pattern"] = "**/*.cs" }),
            }),
        });
        var body = prov.Body(req, stream: false);
        var input = body["input"] as List<object?>;
        // assistant 文本消息 + 顶层 function_call 条目
        Assert.Equal(2, input?.Count ?? -1);
        var callItem = input![1] as Dictionary<string, object?>;
        Assert.Equal("function_call", callItem?["type"]?.ToString());
        Assert.Equal("fc_2", callItem?["call_id"]?.ToString());
        Assert.Equal("glob", callItem?["name"]?.ToString());
    }

    // ── Anthropic:thinking + cache_control 请求体 ───────────────────────

    [Test]
    public void AnthropicBodyIncludesThinkingBudget()
    {
        var prov = new TestAnthropicProvider(budget: 4000, thinking: true, cache: false);
        var body = prov.Body(new CompletionRequest("m", new List<Message> { new(Role.User, "hi") }, System: "sys"), stream: false);

        Assert.True(body.TryGetValue("thinking", out var th), "thinking must be present when enabled");
        var d = th as Dictionary<string, object?>;
        Assert.Equal("enabled", d?["type"]?.ToString());
        Assert.Equal(4000, d?["budget_tokens"]);
    }

    [Test]
    public void AnthropicBodySkipsThinkingWhenDisabled()
    {
        var prov = new TestAnthropicProvider(budget: 4000, thinking: false, cache: false);
        var body = prov.Body(new CompletionRequest("m", new List<Message> { new(Role.User, "hi") }), stream: false);
        Assert.False(body.ContainsKey("thinking"), "thinking must be absent when disabled");
    }

    [Test]
    public void AnthropicBodyAttachesCacheControl()
    {
        var prov = new TestAnthropicProvider(budget: 0, thinking: false, cache: true);
        var tools = new List<ToolDefinition>
        {
            new("bash", "run", new Dictionary<string, object?>()),
            new("file_read", "read", new Dictionary<string, object?>()),
        };
        var body = prov.Body(new CompletionRequest("m", new List<Message> { new(Role.User, "hi") }, System: "sys", Tools: tools), stream: false);

        // system 必须变成 block 数组并挂 cache_control
        var sysBlocks = body["system"] as List<object?>;
        Assert.True(sysBlocks is { Count: > 0 }, "system must be a block array for caching");
        var sysBlock = sysBlocks![0] as Dictionary<string, object?>;
        Assert.True(sysBlock != null && sysBlock.ContainsKey("cache_control"), "system block must carry cache_control");

        // 工具列表末项挂 cache_control
        var toolList = body["tools"] as List<Dictionary<string, object?>>;
        Assert.Equal(2, toolList?.Count ?? -1);
        Assert.True(toolList![^1].ContainsKey("cache_control"), "last tool must carry cache_control breakpoint");
        Assert.False(toolList[0].ContainsKey("cache_control"), "first tool must NOT carry cache_control");
    }

    [Test]
    public void AnthropicBodySkipsCacheControlWhenDisabled()
    {
        var prov = new TestAnthropicProvider(budget: 0, thinking: false, cache: false);
        var body = prov.Body(new CompletionRequest("m", new List<Message> { new(Role.User, "hi") }, System: "sys"), stream: false);
        Assert.Equal("sys", body["system"] as string, "system stays a plain string without caching");
    }
}
