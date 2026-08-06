using System.Text;
using Coder.Providers;
using Coder.Types;

namespace Coder.Tests;

public class TestOpenAIProvider : OpenAICompatProvider
{
    public TestOpenAIProvider()
        : base("test", new ProviderCapabilities(true, true, true, false, false, false, false, "Test"), "k", "http://x") { }

    public Task<(string text, List<AssistantToolUse> toolCalls, TokenUsage? usage)> Parse(Stream s, Action<IStreamEvent> onEvent)
        => ParseSseAsync(s, onEvent, CancellationToken.None);
}

public class SseTests
{
    // Kills mutant "sse-drops-tool-events": a streamed tool call must surface as
    // a tool_use_end event (this is exactly the bug that was fixed earlier).
    [Test]
    public void OpenAiSseEmitsToolUseEnd()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"file_write\",\"arguments\":\"{\\\"path\\\":\\\"a\\\"}\"}}]}}]}\n\n" +
            "data: [DONE]\n\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var events = new List<IStreamEvent>();
        var prov = new TestOpenAIProvider();
        var (_, tools, _) = prov.Parse(ms, e => events.Add(e)).Result;

        Assert.True(events.Any(e => e is StreamEventToolUseEnd), "streaming must emit a tool_use_end event");
        Assert.Equal(1, tools.Count);
        Assert.Equal("file_write", tools[0].Name);
    }

    [Test]
    public void OpenAiSseEmitsTextDeltas()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
            "data: [DONE]\n\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var events = new List<IStreamEvent>();
        var prov = new TestOpenAIProvider();
        var (text, _, _) = prov.Parse(ms, e => events.Add(e)).Result;
        Assert.Equal("hi", text);
    }
}
