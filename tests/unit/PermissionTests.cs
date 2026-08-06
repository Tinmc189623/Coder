using Coder.Core;
using Coder.Types;

namespace Coder.Tests;

public class PermissionTests
{
    private static ToolDefinition Mutating(string name) => new(name, "desc", new(), Mutates: true);
    private static ToolDefinition ReadOnlyTool(string name) => new(name, "desc", new(), ReadOnly: true);

    private static PermissionEngine Engine(PermissionMode mode, Func<bool> interactive, string decision = "deny")
    {
        var eng = new PermissionEngine((_, _) => Task.FromResult(new PermissionDecision(decision)), interactive);
        eng.SetMode(mode);
        return eng;
    }

    [Test]
    public void PlanModeDeniesMutatingTool()
    {
        var eng = Engine(PermissionMode.Plan, () => false);
        var d = eng.Check(Mutating("file_write"), new("id", "file_write", new())).Result;
        Assert.Equal("deny", d.Behavior);
    }

    [Test]
    public void PlanModeDeniesBash()
    {
        var eng = Engine(PermissionMode.Plan, () => false);
        var d = eng.Check(Mutating("bash"), new("id", "bash", new())).Result;
        Assert.Equal("deny", d.Behavior);
    }

    // Kills mutant "perm-acceptedits-covers-all": acceptEdits must NOT blanket-
    // approve tools outside {file_edit, file_write, bash}.
    [Test]
    public void AcceptEditsDeniesReadOnlyWebFetch()
    {
        var eng = Engine(PermissionMode.AcceptEdits, () => true, "deny");
        var d = eng.Check(ReadOnlyTool("web_fetch"), new("id", "web_fetch", new())).Result;
        Assert.Equal("deny", d.Behavior);
    }

    [Test]
    public void AcceptEditsAllowsFileEdit()
    {
        var eng = Engine(PermissionMode.AcceptEdits, () => true, "deny");
        var d = eng.Check(Mutating("file_edit"), new("id", "file_edit", new())).Result;
        Assert.Equal("allow", d.Behavior);
    }

    [Test]
    public void BypassAllowsEverything()
    {
        var eng = Engine(PermissionMode.BypassPermissions, () => false);
        var d = eng.Check(Mutating("file_write"), new("id", "file_write", new())).Result;
        Assert.Equal("allow", d.Behavior);
    }

    [Test]
    public void NonInteractiveAllowsReadOnly()
    {
        var eng = Engine(PermissionMode.Default, () => false);
        var d = eng.Check(ReadOnlyTool("file_read"), new("id", "file_read", new())).Result;
        Assert.Equal("allow", d.Behavior);
    }

    [Test]
    public void DefaultInteractiveAsksUser()
    {
        var eng = Engine(PermissionMode.Default, () => true, "deny");
        var d = eng.Check(Mutating("bash"), new("id", "bash", new())).Result;
        Assert.Equal("deny", d.Behavior);
    }
}
