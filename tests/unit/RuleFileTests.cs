using Coder.Config;

namespace Coder.Tests;

public class RuleFileTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "coder-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // 优先级:CODER.md > CLAUDE.md > AGENTS.md > AGENT.md。
    [Test]
    public void CoderMdWinsOverClaudeMd()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "CODER.md"), "coder rules");
            File.WriteAllText(Path.Combine(dir, "CLAUDE.md"), "claude rules");
            var ctx = Coder.Config.ProjectContextLoader.LoadAsync(dir, dir).Result;
            Assert.True(ctx.AgentsFile!.EndsWith("CODER.md"), "CODER.md must win over CLAUDE.md");
            Assert.Equal("coder rules", ctx.AgentsBody);
            Assert.True(ctx.FilesFound.Contains("CODER.md") && ctx.FilesFound.Contains("CLAUDE.md"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public void ClaudeMdWinsOverAgentsMd()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "CLAUDE.md"), "claude rules");
            File.WriteAllText(Path.Combine(dir, "AGENTS.md"), "agents rules");
            var ctx = Coder.Config.ProjectContextLoader.LoadAsync(dir, dir).Result;
            Assert.True(ctx.AgentsFile!.EndsWith("CLAUDE.md"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public void AgentMdSingularIsLastFallback()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "AGENT.md"), "singular rules");
            var ctx = Coder.Config.ProjectContextLoader.LoadAsync(dir, dir).Result;
            Assert.True(ctx.AgentsFile!.EndsWith("AGENT.md"), "AGENT.md must be picked up");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // 无规则文件 → README 兜底。
    [Test]
    public void ReadmeIsFallback()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "README.md"), "readme content");
            var ctx = Coder.Config.ProjectContextLoader.LoadAsync(dir, dir).Result;
            Assert.True(ctx.AgentsFile == null, "README must not count as rules file");
            var rendered = Coder.Config.ProjectContextLoader.Render(ctx);
            Assert.True(rendered.Contains("# README.md"), "README must render as fallback");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // 全局规则加载,Render 输出全局在前、项目在后。
    [Test]
    public void GlobalRulesRenderFirstThenProject()
    {
        var dir = TempDir();
        try
        {
            var proj = Path.Combine(dir, "proj"); Directory.CreateDirectory(proj);
            var user = Path.Combine(dir, "user"); Directory.CreateDirectory(user);
            File.WriteAllText(Path.Combine(proj, "CODER.md"), "project rules");
            File.WriteAllText(Path.Combine(user, "CODER.md"), "global rules");
            var ctx = Coder.Config.ProjectContextLoader.LoadAsync(proj, user).Result;
            Assert.Equal("global rules", ctx.GlobalBody);
            var rendered = Coder.Config.ProjectContextLoader.Render(ctx);
            var gIdx = rendered.IndexOf("global rules", StringComparison.Ordinal);
            var pIdx = rendered.IndexOf("project rules", StringComparison.Ordinal);
            Assert.True(gIdx >= 0 && pIdx > gIdx, "global rules must come before project rules");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // 截断边界:全局 4000、项目 8000。
    [Test]
    public void TruncationLimitsApply()
    {
        var dir = TempDir();
        try
        {
            var proj = Path.Combine(dir, "proj"); Directory.CreateDirectory(proj);
            var user = Path.Combine(dir, "user"); Directory.CreateDirectory(user);
            File.WriteAllText(Path.Combine(proj, "CODER.md"), new string('p', 9000));
            File.WriteAllText(Path.Combine(user, "CODER.md"), new string('g', 5000));
            var ctx = Coder.Config.ProjectContextLoader.LoadAsync(proj, user).Result;
            var rendered = Coder.Config.ProjectContextLoader.Render(ctx);
            Assert.True(rendered.Contains("… (truncated)"), "long rules must be truncated");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
