using Coder.Types;
using Coder.Ui;

namespace Coder.Tests;

public class PaletteTests
{
    private static string Strip(string s) => s.Replace("[", "ESC[");

    // TrueColor:电光紫 #AF87FF 输出 38;2;175;135;255。
    [Test]
    public void BrandTrueColorUsesRgb()
    {
        Theme.Depth = Theme.ColorDepth.TrueColor;
        Theme.Enabled = true;
        var s = Theme.Brand("x");
        Assert.True(s.Contains("38;2;175;135;255"), $"expected rgb violet, got: {Strip(s)}");
    }

    // Ansi256:主色精确命中 xterm 141。
    [Test]
    public void BrandAnsi256UsesXterm141()
    {
        Theme.Depth = Theme.ColorDepth.Ansi256;
        Theme.Enabled = true;
        var s = Theme.Brand("x");
        Assert.True(s.Contains("38;5;141"), $"expected xterm 141, got: {Strip(s)}");
    }

    // Basic 16 色:紫色系 basic 95。
    [Test]
    public void BrandBasicUses95()
    {
        Theme.Depth = Theme.ColorDepth.Basic;
        Theme.Enabled = true;
        var s = Theme.Brand("x");
        Assert.True(s.Contains("95"), $"expected basic 95, got: {Strip(s)}");
    }

    // 无色:纯文本无 ANSI。
    [Test]
    public void DisabledReturnsPlainText()
    {
        Theme.Depth = Theme.ColorDepth.None;
        Theme.Enabled = false;
        var s = Theme.Brand("x");
        Assert.False(s.Contains(""), "must not contain ANSI when disabled");
        Assert.Equal("x", s);
    }

    // 代码 token 色与语义色分开命名空间。
    [Test]
    public void CodeInksUseDedicatedColors()
    {
        Theme.Depth = Theme.ColorDepth.TrueColor;
        Theme.Enabled = true;
        Assert.True(Theme.CodeKeyword("k").Contains("215;135;255"), "keyword = #D787FF");
        Assert.True(Theme.CodeString("s").Contains("135;215;135"), "string = #87D787");
        Assert.True(Theme.CodeNumber("1").Contains("255;135;95"), "number = #FF875F");
        // CodeKeyword 与 Brand 不同色。
        Assert.False(Theme.CodeKeyword("k") == Theme.Brand("k"), "keyword must differ from brand");
    }

    // 青蓝 Accent(Info)与琥珀 Amber 属于品牌三色。
    [Test]
    public void AccentAndAmberAreBrandTriad()
    {
        Theme.Depth = Theme.ColorDepth.TrueColor;
        Theme.Enabled = true;
        Assert.True(Theme.Info("i").Contains("0;215;255"), "accent = #00D7FF");
        Assert.True(Theme.Amber("a").Contains("255;175;0"), "amber = #FFAF00");
        // warn = amber(品牌色,不再推远)。
        Assert.True(Theme.Warn("w").Contains("255;175;0"), "warn must equal amber");
    }

    // Gradient 在非 TrueColor 下降级为 Brand 平色。
    [Test]
    public void GradientFallsBackWhenNotTrueColor()
    {
        Theme.Depth = Theme.ColorDepth.Ansi256;
        Theme.Enabled = true;
        var g = Theme.Gradient("Coder CLI");
        Assert.False(g.Contains("38;2;"), "gradient must not emit rgb at ansi256");
    }

    // 浅色板:Brand 变深紫 #5F5FD7。
    [Test]
    public void LightPaletteUsesDarkerViolet()
    {
        Theme.Apply(ThemeMode.Light);
        Theme.Depth = Theme.ColorDepth.TrueColor;
        Theme.Enabled = true;
        var s = Theme.Brand("x");
        Assert.True(s.Contains("38;2;95;95;215"), $"light brand expected #5F5FD7, got: {Strip(s)}");
        Theme.Apply(ThemeMode.Dark);
    }
}
