using Coder.Ui;

namespace Coder.Tests;

public class ThemeTests
{
    // Kills mutant "theme-colour-always-off": with NO_COLOR unset and a color
    // terminal advertised, depth must be non-None.
    [Test]
    public void DetectDepthNotNoneWhenColorAllowed()
    {
        var prevNo = Environment.GetEnvironmentVariable("NO_COLOR");
        var prevCli = Environment.GetEnvironmentVariable("CLICOLOR");
        var prevTerm = Environment.GetEnvironmentVariable("TERM");
        var prevCt = Environment.GetEnvironmentVariable("COLORTERM");
        try
        {
            Environment.SetEnvironmentVariable("NO_COLOR", null);
            Environment.SetEnvironmentVariable("CLICOLOR", null);
            Environment.SetEnvironmentVariable("TERM", "xterm-256color");
            Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
            Assert.NotEqual(Theme.ColorDepth.None, Theme.DetectDepth());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NO_COLOR", prevNo);
            Environment.SetEnvironmentVariable("CLICOLOR", prevCli);
            Environment.SetEnvironmentVariable("TERM", prevTerm);
            Environment.SetEnvironmentVariable("COLORTERM", prevCt);
        }
    }

    [Test]
    public void DetectDepthNoneWhenNoColorSet()
    {
        var prevNo = Environment.GetEnvironmentVariable("NO_COLOR");
        try
        {
            Environment.SetEnvironmentVariable("NO_COLOR", "1");
            Assert.Equal(Theme.ColorDepth.None, Theme.DetectDepth());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NO_COLOR", prevNo);
        }
    }
}
