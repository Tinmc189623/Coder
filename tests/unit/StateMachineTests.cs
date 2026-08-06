using Coder.Ui;

namespace Coder.Tests;

public class StateMachineTests
{
    // BeginTurn → Requesting。
    [Test]
    public void BeginTurnStartsRequesting()
    {
        var sm = new TurnStateMachine();
        sm.BeginTurn();
        Assert.Equal(Phase.Requesting, sm.Phase);
    }

    // 文本 delta → Streaming。
    [Test]
    public void TextDeltaStreams()
    {
        var sm = new TurnStateMachine();
        sm.BeginTurn();
        sm.OnTextDelta("hello");
        Assert.Equal(Phase.Streaming, sm.Phase);
    }

    // thinking delta → Thinking。
    [Test]
    public void ThinkingDeltaSetsThinking()
    {
        var sm = new TurnStateMachine();
        sm.BeginTurn();
        sm.OnThinkingDelta("hmm");
        Assert.Equal(Phase.Thinking, sm.Phase);
    }

    // OnIteration → Requesting(下一轮)。
    [Test]
    public void IterationResetsToRequesting()
    {
        var sm = new TurnStateMachine();
        sm.BeginTurn();
        sm.OnTextDelta("x");
        Assert.Equal(Phase.Streaming, sm.Phase);
        sm.OnIteration(1);
        Assert.Equal(Phase.Requesting, sm.Phase);
        Assert.Equal(1, sm.Iteration);
    }

    // OnToolStart → ToolRun。
    [Test]
    public void ToolStartSetsToolRun()
    {
        var sm = new TurnStateMachine();
        sm.BeginTurn();
        sm.OnToolStart();
        Assert.Equal(Phase.ToolRun, sm.Phase);
    }

    // EndTurn → Done / Interrupted。
    [Test]
    public void EndTurnDoneOrInterrupted()
    {
        var sm = new TurnStateMachine();
        sm.BeginTurn();
        sm.EndTurn();
        Assert.Equal(Phase.Done, sm.Phase);

        var sm2 = new TurnStateMachine();
        sm2.BeginTurn();
        sm2.EndTurn(aborted: true);
        Assert.Equal(Phase.Interrupted, sm2.Phase);
    }

    // 字符计数累计。
    [Test]
    public void OutputCharsAccumulate()
    {
        var sm = new TurnStateMachine();
        sm.BeginTurn();
        sm.OnTextDelta("abc");
        sm.OnThinkingDelta("de");
        Assert.Equal(5, sm.TurnOutputChars);
    }
}
