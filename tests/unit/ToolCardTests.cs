using Coder.Core;
using Coder.Types;
using Coder.Ui;

namespace Coder.Tests;

public class ToolCardTests
{
    private static ToolCard MakeCard(CardPhase phase, ToolResult? result = null)
        => new()
        {
            Inv = new ToolInvocation("t1", "bash", new Dictionary<string, object?>()),
            Def = new ToolDefinition("bash", "run", new Dictionary<string, object?>()),
            Tier = RiskTier.Low,
            RiskLabel = "low",
            Phase = phase,
            StartedMs = 1000,
            EndedMs = 1500,
            Result = result,
        };

    // 运行态:闪烁字形 + 工具名 + 耗时。
    [Test]
    public void RunningHeaderShowsSpinner()
    {
        Theme.Depth = Theme.ColorDepth.None;
        Theme.Enabled = false;
        var card = MakeCard(CardPhase.Running);
        var h = card.Header("⠋");
        Assert.True(h.Contains("bash"), "tool name present");
        Assert.True(h.Contains("⠋"), "spinner frame present");
    }

    // 成功态:✓。
    [Test]
    public void DoneHeaderShowsCheck()
    {
        Theme.Depth = Theme.ColorDepth.None;
        Theme.Enabled = false;
        var card = MakeCard(CardPhase.Done, new ToolResult("t1", true, "out"));
        Assert.True(card.Header().Contains("✓"), "done shows ✓");
    }

    // 失败态:✗ + 错误置顶。
    [Test]
    public void FailedBodyPutsErrorFirst()
    {
        Theme.Depth = Theme.ColorDepth.None;
        Theme.Enabled = false;
        var card = MakeCard(CardPhase.Failed, new ToolResult("t1", false, "stderr line", Error: "boom"));
        var body = card.Body(80).ToList();
        Assert.True(body.Count > 0);
        Assert.True(body[0].Contains("boom"), "error must be first line");
    }

    // 输出截断 cap 8 行 + 超限提示。
    [Test]
    public void BodyTruncatesLongOutput()
    {
        Theme.Depth = Theme.ColorDepth.None;
        Theme.Enabled = false;
        var output = string.Join("\n", Enumerable.Range(0, 15).Select(i => $"line{i}"));
        var card = MakeCard(CardPhase.Done, new ToolResult("t1", true, output));
        var body = card.Body(80).ToList();
        Assert.True(body.Count <= 9, "cap 8 + overflow notice");
        Assert.True(body.Any(l => l.Contains("+7 行")), "overflow notice present");
    }

    // TodoIcons 映射。
    [Test]
    public void TodoIconsMapStatuses()
    {
        Assert.Equal("✓", TodoIcons.For("completed").Glyph);
        Assert.Equal("◐", TodoIcons.For("in_progress").Glyph);
        Assert.Equal("✕", TodoIcons.For("cancelled").Glyph);
        Assert.Equal("○", TodoIcons.For("pending").Glyph);
        Assert.Equal("?", TodoIcons.For("weird").Glyph);
    }
}
