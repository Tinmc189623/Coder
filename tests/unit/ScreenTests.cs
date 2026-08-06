using Coder.Ui;

namespace Coder.Tests;

public class ScreenTests
{
    // 非交互模式:直写,无 ANSI 擦除序列。
    [Test]
    public void NonInteractiveWritesDirectly()
    {
        var sw = new StringWriter();
        var screen = new Screen { Writer = sw, ColsProvider = () => 80, RowsProvider = () => 24 };
        screen.StartTurn(() => "status");
        screen.Emit("hello");
        screen.EndTurn();
        Assert.True(sw.ToString().Contains("hello"), "direct write in non-interactive");
        // 无 ESC 擦除序列。
        Assert.False(sw.ToString().Contains(""), "no ANSI in non-interactive");
    }

    // Emit 提交:非交互时原样直写(流式 delta 自带换行,不追加)。
    [Test]
    public void EmitWritesVerbatimNonInteractive()
    {
        var sw = new StringWriter();
        var screen = new Screen { Writer = sw, ColsProvider = () => 80, RowsProvider = () => 24 };
        screen.Emit("line1\nline2");
        Assert.Equal("line1\nline2", sw.ToString());
    }

    // 交互模式不可直接测(需要真实 TTY),但可通过 StartTurn 的 IsInteractive 分支验证直写兜底。
    [Test]
    public void StartTurnNonInteractiveDoesNotEnableLiveBlock()
    {
        var sw = new StringWriter();
        var screen = new Screen { Writer = sw, ColsProvider = () => 80, RowsProvider = () => 24 };
        screen.StartTurn(() => "status");
        Assert.False(screen.Enabled, "live block must stay disabled in non-interactive");
        screen.EndTurn();
    }

    // 状态行 Compose:包含相位标签与模式。
    [Test]
    public void StatusBarComposeContainsPhaseAndMode()
    {
        Theme.Depth = Theme.ColorDepth.None;
        Theme.Enabled = false;
        var line = StatusBar.Compose(new StatusModel
        {
            Phase = Phase.Streaming,
            ContextUsed = 12000,
            ContextTotal = 200000,
            Mode = Types.PermissionMode.Default,
            InputTokens = 1200,
            OutputTokens = 400,
            Turn = 2,
        });
        Assert.True(line.Contains("streaming"), "phase label present");
        Assert.True(line.Contains("12k/200k"), "context used formatted");
        Assert.True(line.Contains("mode:default"), "mode present");
        Assert.True(line.Contains("turn 2"), "turn present");
    }
}
