using Coder.Ui;

namespace Coder.Tests;

public class TextFmtTests
{
    // Kills mutant "wrap-drops-ansi-state": a color open at line start must be
    // reset at the wrap boundary and reapplied, not leaked across lines.
    [Test]
    public void WrapPreservesAnsiResetAcrossLines()
    {
        var text = "\u001b[31m" + "HELLO WORLD" + "\u001b[0m";
        var lines = TextFmt.Wrap(text, 5);
        var joined = string.Join("\n", lines);
        // Each wrapped line re-establishes the color, so the reset count must
        // equal the number of lines. The input ends with one reset, and the
        // wrap boundaries add one each — a mutant that drops the boundary reset
        // collapses this to a single trailing reset.
        var resets = Assert.CountOccurrences(joined, "\u001b[0m");
        Assert.Equal(lines.Count, resets);
    }

    [Test]
    public void StripRemovesAnsiCodes()
    {
        Assert.Equal("red", TextFmt.Strip("\u001b[31mred\u001b[0m"));
    }

    [Test]
    public void DisplayWidthCountsCjkAsTwo()
    {
        Assert.Equal(2, TextFmt.DisplayWidth("中"));
        Assert.Equal(1, TextFmt.DisplayWidth("a"));
    }

    [Test]
    public void WrapKeepsFullEscapeSequence()
    {
        var text = "\u001b[38;5;220mabcdefg\u001b[0m";
        var joined = string.Join("\n", TextFmt.Wrap(text, 4));
        Assert.Contains(joined, "\u001b[38;5;220m");
        Assert.Contains(joined, "\u001b[0m");
    }

    [Test]
    public void TruncateVisibleAddsEllipsis()
    {
        Assert.Equal("ab…", TextFmt.TruncateVisible("abcdefg", 3));
    }

    // Ambiguous 字符双宽模式下,"…" 占 2 列,截断预算要同步收紧,
    // 否则结果行超宽破坏框对齐(CJK 终端经典错位源)。
    [Test]
    public void TruncateVisibleAccountsForAmbiguousWideEllipsis()
    {
        var prev = TextFmt.AmbiguousWide;
        try
        {
            TextFmt.AmbiguousWide = true;
            Assert.Equal("a…", TextFmt.TruncateVisible("abcdefg", 3));
            TextFmt.AmbiguousWide = false;
            Assert.Equal("ab…", TextFmt.TruncateVisible("abcdefg", 3));
        }
        finally { TextFmt.AmbiguousWide = prev; }
    }

    // 宽模式下,框线/块元素等 UI 装饰字符按 2 列计宽,与 CJK 终端渲染一致。
    [Test]
    public void AmbiguousCharsAreDoubleWidthInCjkMode()
    {
        var prev = TextFmt.AmbiguousWide;
        try
        {
            TextFmt.AmbiguousWide = true;
            Assert.Equal(2, TextFmt.DisplayWidth("│"));
            Assert.Equal(2, TextFmt.DisplayWidth("▐"));
            Assert.Equal(2, TextFmt.DisplayWidth("·"));
            TextFmt.AmbiguousWide = false;
            Assert.Equal(1, TextFmt.DisplayWidth("│"));
            Assert.Equal(1, TextFmt.DisplayWidth("·"));
        }
        finally { TextFmt.AmbiguousWide = prev; }
    }
}
