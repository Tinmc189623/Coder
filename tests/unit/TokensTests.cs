using Coder.Core;

namespace Coder.Tests;

public class TokensTests
{
    [Test]
    public void EstimateNullIsZero()
    {
        Assert.Equal(0, Tokens.Estimate(null));
    }

    // Kills mutant "tokens-budget-zeroed": non-empty input must estimate > 0.
    [Test]
    public void EstimateNonEmptyStringPositive()
    {
        Assert.True(Tokens.Estimate("hello world") > 0, "non-empty prose must estimate > 0 tokens");
    }

    [Test]
    public void EstimateCodeStringPositive()
    {
        Assert.True(Tokens.Estimate("function foo() { return 1; }") > 0);
    }

    [Test]
    public void EstimateDictionarySumsEntries()
    {
        var dict = new Dictionary<string, object?> { ["a"] = "hello", ["b"] = "world" };
        Assert.True(Tokens.Estimate(dict) > 0);
    }

    [Test]
    public void BudgetComputesRemaining()
    {
        var b = Tokens.Budget(100, 1000);
        Assert.Equal(900, b.Remaining);
        Assert.True(Math.Abs(b.PctUsed - 0.1) < 1e-9, $"pct used should be ~0.1, was {b.PctUsed}");
    }
}
