using Coder.Tools;
using Coder.Types;

namespace Coder.Tests;

public class FileEditTests
{
    private static string WriteTemp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "coder_test_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, content);
        return path;
    }

    // Kills mutant "edit-truncates-file": a replacement must keep the rest of the file.
    [Test]
    public void FileEditReplacesOnlyTargetKeepsRest()
    {
        var path = WriteTemp("AAA BBB AAA");
        var ctx = new ToolContext { Cwd = Path.GetDirectoryName(path)! };
        var input = new Dictionary<string, object?> { ["path"] = path, ["old_string"] = "BBB", ["new_string"] = "XXX" };
        var r = BuiltinTools.FileEdit(input, ctx).Result;
        Assert.True(r.Ok, "edit should succeed");
        Assert.Equal("AAA XXX AAA", File.ReadAllText(path));
        File.Delete(path);
    }

    // Kills mutant "edit-ignores-missing-target": a missing old_string must error.
    [Test]
    public void FileEditFailsWhenTargetMissing()
    {
        var path = WriteTemp("hello");
        var ctx = new ToolContext { Cwd = Path.GetDirectoryName(path)! };
        var input = new Dictionary<string, object?> { ["path"] = path, ["old_string"] = "zzz", ["new_string"] = "yyy" };
        var r = BuiltinTools.FileEdit(input, ctx).Result;
        Assert.False(r.Ok, "edit must fail when old_string is absent");
        Assert.Equal("hello", File.ReadAllText(path));
        File.Delete(path);
    }
}
