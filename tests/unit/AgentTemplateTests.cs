using Coder.Agents;
using Coder.Core;

namespace Coder.Tests;

public class AgentTemplateTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "coder-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // frontmatter 全字段解析 + 正文即 Body。
    [Test]
    public void ParsesFullFrontmatter()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "review.md");
            File.WriteAllText(file, """
                ---
                name: "security-review"
                description: "Reviews code for security issues"
                tools: file_read, glob, grep, bash
                model: "claude-sonnet-4-5"
                ---
                You are a security reviewer.
                """);
            var t = AgentTemplateLoader.ParseAsync(file).Result;
            Assert.True(t != null);
            Assert.Equal("security-review", t!.Name);
            Assert.Equal("Reviews code for security issues", t.Description);
            Assert.Equal("claude-sonnet-4-5", t.Model);
            Assert.Equal("You are a security reviewer.", t.Body);
            Assert.Equal(4, t.Tools?.Length ?? -1);
            Assert.True(t.Tools!.Contains("glob"), "tools must split on commas");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // 无 frontmatter → 文件名作 name,正文即 Body。
    [Test]
    public void NoFrontmatterUsesFileName()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "helper.md");
            File.WriteAllText(file, "Just some text.");
            var t = AgentTemplateLoader.ParseAsync(file).Result;
            Assert.True(t != null);
            Assert.Equal("helper", t!.Name);
            Assert.Equal("Just some text.", t.Body);
            Assert.True(t.Tools == null);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // 两级扫描:同名项目覆盖用户。
    [Test]
    public void ProjectOverridesUserOnSameName()
    {
        var dir = TempDir();
        try
        {
            var proj = Path.Combine(dir, "proj", ".coder", "agents"); Directory.CreateDirectory(proj);
            var user = Path.Combine(dir, "user", "agents"); Directory.CreateDirectory(user);
            File.WriteAllText(Path.Combine(proj, "dup.md"), "---\nname: \"dup\"\n---\nproject body");
            File.WriteAllText(Path.Combine(user, "dup.md"), "---\nname: \"dup\"\n---\nuser body");
            var list = AgentTemplateLoader.LoadAsync(Path.Combine(dir, "proj"), Path.Combine(dir, "user")).Result;
            Assert.Equal(1, list.Count);
            Assert.Equal("project body", list[0].Body, "project must win on same name");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // tools 逗号/空白两种写法。
    [Test]
    public void ToolsSplitBothCommaAndWhitespace()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "a.md");
            File.WriteAllText(file, "---\ntools: file_read glob grep\n---\nx");
            var t = AgentTemplateLoader.ParseAsync(file).Result;
            Assert.Equal(3, t!.Tools?.Length ?? -1);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // 非法文件(不可读/损坏)跳过。
    [Test]
    public void BadFilesAreSkipped()
    {
        var dir = TempDir();
        try
        {
            var proj = Path.Combine(dir, ".coder", "agents"); Directory.CreateDirectory(proj);
            // 空文件也是合法模板(无 frontmatter),所以用不存在目录验证跳过路径:
            var list = AgentTemplateLoader.LoadAsync(dir, Path.Combine(dir, "nonexistent")).Result;
            Assert.Equal(0, list.Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // AllTypes:内置 + 模板名合并去重。
    [Test]
    public void AllTypesMergesBuiltinsAndTemplates()
    {
        var templates = new List<AgentTemplate>
        {
            new("custom-a", "", "body", null, null, "x"),
            new("Explore", "", "dup", null, null, "y"),   // 与内置重名
        };
        var types = SubAgent.AllTypes(templates);
        Assert.True(types.Contains("general-purpose"));
        Assert.True(types.Contains("custom-a"));
        Assert.Equal(types.Length, types.Distinct().Count(), "no duplicates");
        // 无模板时退回纯内置。
        var plain = SubAgent.AllTypes(null);
        Assert.True(plain.Contains("Plan"));
        Assert.False(plain.Contains("custom-a"));
    }
}
