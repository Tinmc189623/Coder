// Coder CLI (.NET 11 port) — tool call cards & todo iconography.
// ToolCard models a tool invocation's lifecycle (queued → running → done/failed)
// for the live block; TodoIcons maps status strings to glyph + color.

using Coder.Core;
using Coder.Types;

namespace Coder.Ui;

public enum CardPhase { Queued, Running, Done, Failed }

/// <summary>工具调用卡片:live block 中的状态化展示。</summary>
public sealed class ToolCard
{
    public required ToolInvocation Inv { get; init; }
    public ToolDefinition? Def { get; set; }
    public string? Describe { get; set; }
    public RiskTier Tier { get; set; }
    public string RiskLabel { get; set; } = "";
    public CardPhase Phase { get; set; } = CardPhase.Queued;
    public long StartedMs { get; set; }
    public long EndedMs { get; set; }
    public ToolResult? Result { get; set; }

    /// <summary>头部行:状态字形 + 工具名 + 风险徽章 + 耗时 + describe。</summary>
    public string Header(string spinnerFrame = "⠋")
    {
        var elapsed = Phase == CardPhase.Running
            ? (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds - StartedMs
            : EndedMs - StartedMs;
        var mark = Phase switch
        {
            CardPhase.Queued => Theme.Dim("○ "),
            CardPhase.Running => Theme.Brand(spinnerFrame + " "),
            CardPhase.Done => Theme.Success("✓ "),
            _ => Theme.Error("✗ "),
        };
        var risk = RiskLevel.Badge(Tier, RiskLabel);
        var head = $"{mark}{Theme.Bold(Inv.Name)} {risk}{Theme.Dim($"  {Math.Max(0, elapsed)}ms")}";
        if (!string.IsNullOrEmpty(Describe)) head += Theme.Dim($"  {Describe}");
        return head;
    }

    /// <summary>终态正文:错误置顶 + 输出截断(cap 8 行)。</summary>
    public IEnumerable<string> Body(int cols)
    {
        var r = Result;
        if (r == null) yield break;
        if (!r.Ok && !string.IsNullOrEmpty(r.Error))
            yield return Theme.Error(TextFmt.TruncateVisible(r.Error, cols - 6));
        var lines = (r.Output ?? "").Replace("\r\n", "\n").Split('\n')
            .Where(l => !string.IsNullOrEmpty(l)).ToList();
        const int cap = 8;
        foreach (var ln in lines.Take(cap))
            yield return TextFmt.TruncateVisible(ln, cols - 6);
        if (lines.Count > cap)
            yield return Theme.Dim($"  … (+{lines.Count - cap} 行)");
    }
}

/// <summary>Todo 状态图标表:几何字形 + 语义色;NO_COLOR 时回退纯文本。</summary>
public static class TodoIcons
{
    public static readonly Dictionary<string, (string Glyph, Func<string, string> Paint)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["completed"] = ("✓", Theme.Success),
        ["in_progress"] = ("◐", Theme.Warn),
        ["cancelled"] = ("✕", Theme.Muted),
        ["pending"] = ("○", Theme.Dim),
    };

    /// <summary>取图标;未知状态回退问号 + Dim。</summary>
    public static (string Glyph, Func<string, string> Paint) For(string? status)
        => status != null && Map.TryGetValue(status, out var v) ? v : ("?", Theme.Dim);
}
