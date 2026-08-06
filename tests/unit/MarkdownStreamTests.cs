using Coder.Ui;

namespace Coder.Tests;

public class MarkdownStreamTests
{
    // 行级提交:完整行产出 committed,未闭合尾行滞留。
    [Test]
    public void CompleteLinesCommitTailStays()
    {
        Theme.Depth = Theme.ColorDepth.None;
        Theme.Enabled = false;
        var md = new StreamingMarkdown();
        md.Append("line one\nline two", out var committed);
        Assert.Equal(1, committed.Count);
        Assert.True(committed[0].Contains("line one"));
        Assert.True(md.RenderTail().Contains("line two"), "unfinished tail stays live");
    }

    // fence 开关与语言标签。
    [Test]
    public void FenceOpenCloseAndLanguage()
    {
        Theme.Depth = Theme.ColorDepth.None;
        Theme.Enabled = false;
        var md = new StreamingMarkdown();
        md.Append("```csharp\n", out var c1);
        Assert.Equal(1, c1.Count);
        Assert.True(c1[0].Contains("```csharp"), "open fence carries language");
        md.Append("return 1;\n```\n", out var c2);
        Assert.Equal(2, c2.Count, "code line + close fence");
        Assert.True(md.RenderTail().Length == 0, "tail empty after fence close");
    }

    // 表格:行缓存,闭合时提交。
    [Test]
    public void TableRowsBufferUntilClosed()
    {
        Theme.Depth = Theme.ColorDepth.None;
        Theme.Enabled = false;
        var md = new StreamingMarkdown();
        md.Append("| a | b |\n|---|---|\n| 1 | 2 |\n", out var c1);
        Assert.Equal(0, c1.Count, "table rows must buffer, not commit");
        md.Append("after table\n", out var c2);
        Assert.Equal(2, c2.Count, "table flush + following row");
        Assert.True(c2[0].Contains("a"), "table content rendered first");
        Assert.True(c2[1].Contains("after table"), "following row committed after table");
    }

    // 不平衡行滞留尾行,闭合后提交。
    [Test]
    public void UnbalancedInlineStaysInTail()
    {
        Theme.Depth = Theme.ColorDepth.None;
        Theme.Enabled = false;
        var md = new StreamingMarkdown();
        md.Append("hello **bold", out var c1);
        Assert.Equal(0, c1.Count, "unclosed bold must stay in tail");
        md.Append(" world**\n", out var c2);
        Assert.Equal(1, c2.Count, "closed bold line commits");
    }

    // 2000 字符护栏。
    [Test]
    public void OverlongTailForcedCommit()
    {
        Theme.Depth = Theme.ColorDepth.None;
        Theme.Enabled = false;
        var md = new StreamingMarkdown();
        var huge = new string('x', 3000);
        md.Append(huge, out var c);
        Assert.Equal(1, c.Count, "overlong tail must be force-committed");
        Assert.True(md.RenderTail().Length == 0, "tail cleared after force commit");
    }

    // thinking 模式不影响提交(由调用方切换)。
    [Test]
    public void ResetClearsState()
    {
        Theme.Depth = Theme.ColorDepth.None;
        Theme.Enabled = false;
        var md = new StreamingMarkdown();
        md.Append("```csharp\npartial", out _);
        md.Reset();
        Assert.True(md.RenderTail().Length == 0, "reset must clear tail");
        md.Append("plain\n", out var c);
        Assert.True(c[0].Contains("plain"), "after reset, lines render as normal markdown");
    }
}
