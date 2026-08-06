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
}
