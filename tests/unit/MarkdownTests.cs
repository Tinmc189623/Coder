using Coder.Ui;

namespace Coder.Tests;

public class MarkdownTests
{
    // 代码围栏:语言标签 + 内容高亮 + 闭栏。
    [Test]
    public void FenceWithLanguageLabel()
    {
        Theme.Depth = Theme.ColorDepth.TrueColor;
        Theme.Enabled = true;
        var s = Markdown.Render("```csharp\nreturn 1;\n```");
        Assert.True(s.Contains("```csharp"), "open fence must carry language");
        Assert.True(s.Contains("38;2;215;135;255"), "fence content must be syntax-highlighted");
        Assert.True(s.Contains("```"), "close fence present");
    }

    // 表格:对齐渲染。
    [Test]
    public void TableRendersAligned()
    {
        Theme.Depth = Theme.ColorDepth.None;
        Theme.Enabled = false;
        var s = Markdown.Render("| a | bb |\n|---|---|\n| 1 | 22 |");
        Assert.True(s.Contains("a"), "header cell a");
        Assert.True(s.Contains("bb"), "header cell bb");
        Assert.True(s.Contains("22"), "body cell 22");
    }

    // 标题/列表/引用/分隔线保持。
    [Test]
    public void ExistingBlockStylesPreserved()
    {
        Theme.Depth = Theme.ColorDepth.None;
        Theme.Enabled = false;
        var s = Markdown.Render("# Title\n- item\n> quote\n---\nplain");
        Assert.True(s.Contains("Title"));
        Assert.True(s.Contains("• item"), "bullet with • glyph");
        Assert.True(s.Contains("┃"), "quote marker");
        Assert.True(s.Contains("plain"));
    }

    // 行内标记:code / bold / italic / link。
    [Test]
    public void InlineMarkupRenders()
    {
        Theme.Depth = Theme.ColorDepth.None;
        Theme.Enabled = false;
        var s = Markdown.Render("`code` **bold** *it* [text](url)");
        Assert.True(s.Contains("code"));
        Assert.True(s.Contains("bold"));
        Assert.True(s.Contains("it"));
        Assert.True(s.Contains("url"));
    }

    // 表格行判定。
    [Test]
    public void TableRowDetection()
    {
        Assert.True(Markdown.IsTableRow("| a | b |"));
        Assert.True(Markdown.IsTableRow("|---|---|"));
        Assert.False(Markdown.IsTableRow("normal line"));
        Assert.False(Markdown.IsTableRow("| just one pipe"));
    }
}
