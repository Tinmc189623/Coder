using Coder.Core;
using Coder.Types;
using Coder.Ui;

namespace Coder.Tests;

public class PermissionDialogTests
{
    private static ToolDefinition ReadTool() => new("file_read", "read a file", new Dictionary<string, object?>());

    private static ToolInvocation ReadInv() => new("t1", "file_read", new Dictionary<string, object?> { ["path"] = "a.txt" });

    private sealed class FakeClock : TimeProvider
    {
        public long Ticks;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => 10_000;   // 1 tick = 0.1ms
    }

    private static int RunPrompt(FakeClock clock, params ConsoleKey[] keys)
    {
        // 每个按键前推进 300ms(10kHz → 3000 ticks),确保超过 200ms gate。
        var q = new Queue<ConsoleKey>(keys);
        return PermissionDialog.Prompt(ReadTool(), ReadInv(), "",
            clock: clock, keySource: () =>
            {
                clock.Ticks += 3_000;
                return new ConsoleKeyInfo(' ', q.Dequeue(), false, false, false);
            });
    }

    // 200ms 防误触:gate 内按键被吞,时间推进后同一按键生效。
    [Test]
    public void AntiMisclickGateSwallowsEarlyKeys()
    {
        var clock = new FakeClock();   // Ticks=0:首键在 gate 内被吞
        var presses = 0;
        var result = PermissionDialog.Prompt(ReadTool(), ReadInv(), "",
            clock: clock,
            keySource: () =>
            {
                presses++;
                if (presses == 1) return new ConsoleKeyInfo('y', ConsoleKey.Y, false, false, false);   // 被吞
                clock.Ticks += 3_000;   // 第二次按键前推进 300ms
                return new ConsoleKeyInfo('y', ConsoleKey.Y, false, false, false);
            });
        Assert.Equal(2, presses, "first key swallowed by gate, second key accepted");
        Assert.Equal(0, result, "y after gate must allow once");
    }

    // y → 本次允许(gate 已过)。
    [Test]
    public void YAllowsOnce()
    {
        var clock = new FakeClock { Ticks = 20_000 };   // 频率 10kHz → 200ms = 2000 ticks;20k 远超
        Assert.Equal(0, RunPrompt(clock, ConsoleKey.Y));
    }

    // a → 始终允许。
    [Test]
    public void AAlwaysAllows()
    {
        var clock = new FakeClock { Ticks = 20_000 };
        Assert.Equal(1, RunPrompt(clock, ConsoleKey.A));
    }

    // n / Esc → 拒绝。
    [Test]
    public void NAndEscDeny()
    {
        var clock = new FakeClock { Ticks = 20_000 };
        Assert.Equal(2, RunPrompt(clock, ConsoleKey.N));
        Assert.Equal(2, RunPrompt(clock, ConsoleKey.Escape));
    }

    // 数字键 1/2/3。
    [Test]
    public void DigitKeysSelect()
    {
        var clock = new FakeClock { Ticks = 20_000 };
        Assert.Equal(0, RunPrompt(clock, ConsoleKey.D1));
        Assert.Equal(1, RunPrompt(clock, ConsoleKey.D2));
        Assert.Equal(2, RunPrompt(clock, ConsoleKey.D3));
    }

    // 方向键移动 + Enter 确认。
    [Test]
    public void ArrowKeysMoveAndEnterConfirms()
    {
        var clock = new FakeClock { Ticks = 20_000 };
        Assert.Equal(1, RunPrompt(clock, ConsoleKey.DownArrow, ConsoleKey.Enter));
        Assert.Equal(2, RunPrompt(clock, ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.Enter));
        Assert.Equal(0, RunPrompt(clock, ConsoleKey.DownArrow, ConsoleKey.UpArrow, ConsoleKey.Enter));
    }
}
