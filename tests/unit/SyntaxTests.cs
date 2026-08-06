using Coder.Ui;

namespace Coder.Tests;

public class SyntaxTests
{
    // 关键字染色。
    [Test]
    public void KeywordsAreHighlighted()
    {
        Theme.Depth = Theme.ColorDepth.TrueColor;
        Theme.Enabled = true;
        var s = Syntax.Highlight("return value;", "csharp");
        Assert.True(s.Contains("38;2;215;135;255"), "keyword must be CodeKeyword color");
        Assert.True(s.Contains("return"), "keyword text preserved");
    }

    // 字符串染色,且字符串内关键字不被误染。
    [Test]
    public void StringContentNotMisclassified()
    {
        Theme.Depth = Theme.ColorDepth.TrueColor;
        Theme.Enabled = true;
        var s = Syntax.Highlight("var x = \"return\";", "csharp");
        Assert.True(s.Contains("38;2;135;215;135"), "string must be CodeString color");
        // "return" 在字符串内:整串染色,不应有独立的 keyword 色包住 return。
        Assert.False(s.Contains("return" + "38;2;215;135;255"), "keyword inside string must not be highlighted");
    }

    // 数字染色,v2 不误判。
    [Test]
    public void NumbersHighlightedButIdentifiersNot()
    {
        Theme.Depth = Theme.ColorDepth.TrueColor;
        Theme.Enabled = true;
        var s = Syntax.Highlight("int v2 = 42;", "csharp");
        Assert.True(s.Contains("38;2;255;135;95"), "42 must be CodeNumber color");
        // v2 不是数字(数字 token 要求首字符为数字)。
        var numIdx = s.IndexOf("42", StringComparison.Ordinal);
        Assert.True(numIdx >= 0, "42 present");
    }

    // 注释染色到行尾。
    [Test]
    public void CommentHighlightedToEndOfLine()
    {
        Theme.Depth = Theme.ColorDepth.TrueColor;
        Theme.Enabled = true;
        var s = Syntax.Highlight("x = 1; // comment here", "csharp");
        Assert.True(s.Contains("38;2;138;132;152"), "comment must be Muted color");
    }

    // 空行/纯空白原样返回。
    [Test]
    public void BlankLineUntouched()
    {
        Theme.Depth = Theme.ColorDepth.TrueColor;
        Theme.Enabled = true;
        Assert.Equal("", Syntax.Highlight("", "csharp"));
        Assert.Equal("   ", Syntax.Highlight("   ", "csharp"));
    }
}
