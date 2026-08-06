using Coder.Tools;

namespace Coder.Tests;

public class GlobTests
{
    [Test]
    public void GlobMatchRecursiveFindsNested()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "coder_glob_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tmp, "sub"));
        File.WriteAllText(Path.Combine(tmp, "a.cs"), "x");
        File.WriteAllText(Path.Combine(tmp, "sub", "b.cs"), "x");
        var found = BuiltinTools.GlobMatch(tmp, "**/*.cs").ToList();
        Assert.Equal(2, found.Count);
        Directory.Delete(tmp, true);
    }

    [Test]
    public void GlobMatchTopLevelOnly()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "coder_glob_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tmp, "sub"));
        File.WriteAllText(Path.Combine(tmp, "a.cs"), "x");
        File.WriteAllText(Path.Combine(tmp, "sub", "b.cs"), "x");
        var found = BuiltinTools.GlobMatch(tmp, "*.cs").ToList();
        Assert.Equal(1, found.Count);
        Directory.Delete(tmp, true);
    }
}
