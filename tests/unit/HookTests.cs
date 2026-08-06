using Coder.Config;
using Coder.Core;
using Coder.Types;
using Tomlyn;

namespace Coder.Tests;

public class HookTests
{
    private static HookManager MakeManager(string toml, string cwd, SpawnFn spawn)
    {
        var dir = Path.GetTempPath();
        var file = Path.Combine(dir, "hooks-test-" + Guid.NewGuid().ToString("N") + ".toml");
        File.WriteAllText(file, toml);
        try
        {
            var doc = Tomlyn.Toml.Parse(File.ReadAllText(file), file);
            Assert.False(doc.HasErrors, string.Join(";", doc.Diagnostics.Select(d => d.Message)));
            var layers = new List<ConfigLayer>
            {
                new() { Source = file, Data = ConfigLoader.ToDataForTest(doc.ToModel()) },
            };
            return HookManager.FromLayers(layers, cwd, spawn);
        }
        finally { File.Delete(file); }
    }

    private static SpawnFn Returning(int exit, string stdout)
        => (spec, env) => Task.FromResult((exit, stdout));

    // PreToolUse:JSON deny → Blocked。
    [Test]
    public void PreToolUseJsonDenyBlocks()
    {
        var m = MakeManager("""
            [[hooks.PreToolUse]]
            matcher = "bash"
            command = "guard.sh"
            """, "C:\\tmp",
            Returning(0, "{\"permissionDecision\":\"deny\",\"permissionDecisionReason\":\"no shell today\"}"));
        var d = m.PreToolUseAsync(new ToolInvocation("t1", "bash", new())).Result;
        Assert.True(d.Blocked, "deny JSON must block");
        Assert.Equal("no shell today", d.Reason);
    }

    // PreToolUse:exit 2 → Blocked,首行作 reason。
    [Test]
    public void PreToolUseExitTwoBlocks()
    {
        var m = MakeManager("[[hooks.PreToolUse]]\ncommand = \"guard.sh\"", "C:\\tmp", Returning(2, "forbidden by policy"));
        var d = m.PreToolUseAsync(new ToolInvocation("t1", "file_write", new())).Result;
        Assert.True(d.Blocked);
        Assert.Equal("forbidden by policy", d.Reason);
    }

    // PreToolUse:args 覆盖输入。
    [Test]
    public void PreToolUseArgsOverrideInput()
    {
        var m = MakeManager("[[hooks.PreToolUse]]\nmatcher = \"file_edit\"\ncommand = \"x\"", "C:\\tmp",
            Returning(0, "{\"args\":{\"path\":\"safe.txt\"}}"));
        var d = m.PreToolUseAsync(new ToolInvocation("t1", "file_edit", new Dictionary<string, object?> { ["path"] = "evil.txt" })).Result;
        Assert.False(d.Blocked, "args override must not block");
        Assert.True(d.InputOverride != null && d.InputOverride["path"] as string == "safe.txt");
    }

    // PreToolUse:exit 0 空输出 → 放行。
    [Test]
    public void PreToolUseEmptyOutputAllows()
    {
        var m = MakeManager("[[hooks.PreToolUse]]\ncommand = \"x\"", "C:\\tmp", Returning(0, ""));
        var d = m.PreToolUseAsync(new ToolInvocation("t1", "bash", new())).Result;
        Assert.False(d.Blocked);
    }

    // blocked=true 免 spawn 直接拒。
    [Test]
    public void BlockedFlagSkipsSpawn()
    {
        var spawned = 0;
        SpawnFn fn = (spec, env) => { spawned++; return Task.FromResult((0, "")); };
        var m = MakeManager("[[hooks.PreToolUse]]\nblocked = true\ncommand = \"x\"", "C:\\tmp", fn);
        var d = m.PreToolUseAsync(new ToolInvocation("t1", "bash", new())).Result;
        Assert.True(d.Blocked);
        Assert.Equal(0, spawned, "blocked=true must not spawn");
    }

    // matcher 不匹配 → 跳过。
    [Test]
    public void MatcherMismatchSkips()
    {
        var spawned = 0;
        SpawnFn fn = (spec, env) => { spawned++; return Task.FromResult((0, "")); };
        var m = MakeManager("[[hooks.PreToolUse]]\nmatcher = \"bash\"\ncommand = \"x\"", "C:\\tmp", fn);
        m.PreToolUseAsync(new ToolInvocation("t1", "glob", new())).Wait();
        Assert.Equal(0, spawned, "matcher mismatch must skip the hook");
    }

    // UserPromptSubmit:block 拦截。
    [Test]
    public void UserPromptSubmitBlockIntercepts()
    {
        var m = MakeManager("[[hooks.UserPromptSubmit]]\ncommand = \"x\"", "C:\\tmp",
            Returning(0, "{\"block\":true,\"reason\":\"quiet hours\"}"));
        var d = m.UserPromptSubmitAsync("run the tests").Result;
        Assert.True(d.Block);
        Assert.Equal("quiet hours", d.Reason);
    }

    // UserPromptSubmit:additionalContext 追加。
    [Test]
    public void UserPromptSubmitAddsContext()
    {
        var m = MakeManager("[[hooks.UserPromptSubmit]]\ncommand = \"x\"", "C:\\tmp",
            Returning(0, "{\"additionalContext\":\"remember to check tests\"}"));
        var d = m.UserPromptSubmitAsync("hello").Result;
        Assert.False(d.Block);
        Assert.Equal("remember to check tests", d.AdditionalContext);
    }

    // SessionStart:输出拼进上下文。
    [Test]
    public void SessionStartReturnsContext()
    {
        var m = MakeManager("[[hooks.SessionStart]]\ncommand = \"x\"", "C:\\tmp", Returning(0, "cwd facts line"));
        var ctx = m.SessionStartAsync().Result;
        Assert.Equal("cwd facts line", ctx);
    }

    // 层合并:user + project 各配同事件 hook → 有序双条目。
    [Test]
    public void LayersMergeInOrder()
    {
        var calls = new List<string>();
        SpawnFn fn = (spec, env) => { calls.Add(spec.Source); return Task.FromResult((0, "")); };
        var userLayer = new ConfigLayer
        {
            Source = "user.toml",
            Data = new Dictionary<string, object?>
            {
                ["hooks"] = new Dictionary<string, object?>
                {
                    ["PreToolUse"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["command"] = "user-hook" },
                    },
                },
            },
        };
        var projectLayer = new ConfigLayer
        {
            Source = "project.toml",
            Data = new Dictionary<string, object?>
            {
                ["hooks"] = new Dictionary<string, object?>
                {
                    ["PreToolUse"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["command"] = "project-hook" },
                    },
                },
            },
        };
        var m = HookManager.FromLayers(new List<ConfigLayer> { userLayer, projectLayer }, "C:\\tmp", fn);
        m.PreToolUseAsync(new ToolInvocation("t1", "bash", new())).Wait();
        Assert.Equal(2, calls.Count);
        Assert.Equal("user.toml", calls[0]);
        Assert.Equal("project.toml", calls[1]);
    }

    // cli 层不含 hooks(Source == "cli" 跳过)。
    [Test]
    public void CliLayerIsSkipped()
    {
        var cliLayer = new ConfigLayer
        {
            Source = "cli",
            Data = new Dictionary<string, object?>
            {
                ["hooks"] = new Dictionary<string, object?>
                {
                    ["PreToolUse"] = new List<object?> { new Dictionary<string, object?> { ["command"] = "x" } },
                },
            },
        };
        var m = HookManager.FromLayers(new List<ConfigLayer> { cliLayer }, "C:\\tmp", Returning(0, ""));
        var d = m.PreToolUseAsync(new ToolInvocation("t1", "bash", new())).Result;
        Assert.False(d.Blocked, "cli layer hooks must be ignored");
    }
}
