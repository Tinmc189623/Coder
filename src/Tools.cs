// Coder CLI (.NET 11 port) — tool registry & built-in tools.
// Ported from src/core/tools.ts + src/tools/*.ts.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Coder.Types;
using Coder.Ui;
using Coder.Utils;

namespace Coder.Tools;

public sealed class ToolContext
{
    public string Cwd { get; init; } = ".";
    public string UserConfigDir { get; init; } = "";
    public Func<ToolDefinition, ToolInvocation, Task<PermissionDecision>> RequestPermission { get; init; } = (_, _) => Task.FromResult(new PermissionDecision("allow"));
    public Action<string> OnProgress { get; init; } = _ => { };
    public Action OnExecuting { get; init; } = () => { };
}

public sealed record ToolEntry(ToolDefinition Def, Func<Dictionary<string, object?>, ToolContext, Task<ToolResult>> Handler, Func<Dictionary<string, object?>, string>? Describe);

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolEntry> _tools = new();

    public void Register(ToolDefinition def, Func<Dictionary<string, object?>, ToolContext, Task<ToolResult>> handler, Func<Dictionary<string, object?>, string>? describe = null)
        => _tools[def.Name] = new ToolEntry(def, handler, describe);

    public List<ToolDefinition> List() => _tools.Values.Select(t => t.Def).ToList();
    public ToolEntry Get(string name) => _tools.TryGetValue(name, out var e) ? e : throw new ToolNotFoundError(name);

    public bool Has(string name) => _tools.ContainsKey(name);

    public async Task<ToolResult> InvokeAsync(ToolInvocation inv, ToolContext ctx)
    {
        var entry = Get(inv.Name);
        var decision = await ctx.RequestPermission(entry.Def, inv);
        if (decision.Behavior != "allow")
            return new ToolResult(inv.Id, false, decision.Reason ?? "Permission denied", Error: decision.Reason ?? "denied");
        var sw = Stopwatch.StartNew();
        try
        {
            ctx.OnExecuting();
            var r = await entry.Handler(inv.Input, ctx);
            return r with { ElapsedMs = sw.ElapsedMilliseconds, InvocationId = inv.Id };
        }
        catch (Exception e)
        {
            return new ToolResult(inv.Id, false, $"Error: {e.Message}", Error: e.Message, ElapsedMs: sw.ElapsedMilliseconds);
        }
    }
}

// ── Built-in tool implementations ──────────────────────────────────────────
public static class BuiltinTools
{
    private static string? Str(Dictionary<string, object?> input, string key)
        => input.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static Dictionary<string, object?> Schema(params (string name, string type, string desc, bool req)[] props)
    {
        var p = new Dictionary<string, object?>();
        foreach (var (n, t, d, r) in props)
            p[n] = new Dictionary<string, object?> { ["type"] = t, ["description"] = d, ["required"] = r };
        return new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = p,
            ["required"] = props.Where(x => x.req).Select(x => x.name).ToList(),
        };
    }

    public static void RegisterAll(ToolRegistry reg)
    {
        reg.Register(
            new ToolDefinition("bash", "Run a shell command and return its output.", Schema(("command", "string", "Shell command to run", true)), Mutates: true),
            Bash,
            i => $"$ {Str(i, "command")}");

        reg.Register(
            new ToolDefinition("file_read", "Read a file from disk.", Schema(("path", "string", "File path", true), ("offset", "number", "Line offset", false), ("limit", "number", "Max lines", false)), ReadOnly: true),
            FileRead,
            i => $"read {Str(i, "path")}");

        reg.Register(
            new ToolDefinition("file_write", "Write content to a file (creating or overwriting).", Schema(("path", "string", "File path", true), ("content", "string", "File content", true)), Mutates: true),
            FileWrite,
            i => $"write {Str(i, "path")}");

        reg.Register(
            new ToolDefinition("file_edit", "Replace an exact string in a file.", Schema(("path", "string", "File path", true), ("old_string", "string", "Text to replace", true), ("new_string", "string", "Replacement text", true)), Mutates: true),
            FileEdit,
            i => $"edit {Str(i, "path")}");

        reg.Register(
            new ToolDefinition("glob", "Find files matching a glob pattern.", Schema(("pattern", "string", "Glob pattern (e.g. **/*.ts)", true), ("path", "string", "Base directory", false)), ReadOnly: true),
            Glob,
            i => $"glob {Str(i, "pattern")}");

        reg.Register(
            new ToolDefinition("grep", "Search file contents for a pattern.", Schema(("pattern", "string", "Regex pattern", true), ("path", "string", "Directory or file", false), ("glob", "string", "File glob filter", false)), ReadOnly: true),
            Grep,
            i => $"grep {Str(i, "pattern")}");

        reg.Register(
            new ToolDefinition("web_fetch", "Fetch a URL and return its text content.", Schema(("url", "string", "URL to fetch", true)), ReadOnly: true),
            WebFetch,
            i => $"fetch {Str(i, "url")}");

        reg.Register(
            new ToolDefinition("web_search", "Search the web (requires a search backend).", Schema(("query", "string", "Search query", true)), ReadOnly: true),
            WebSearch,
            i => $"search {Str(i, "query")}");

        reg.Register(
            new ToolDefinition("todo_write", "Update the session todo list.", Schema(("items", "array", "Todo items", true)), ReadOnly: true),
            TodoWrite,
            i => "update todos");
    }

    // ── bash ──────────────────────────────────────────────────────────────
    private static async Task<ToolResult> Bash(Dictionary<string, object?> input, ToolContext ctx)
    {
        var cmd = Str(input, "command") ?? "";
        var isWin = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWin ? "cmd.exe" : "/bin/sh",
            Arguments = isWin ? $"/c {cmd}" : $"-c \"{cmd.Replace("\"", "\\\"")}\"",
            WorkingDirectory = ctx.Cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi) ?? throw new Exception("Failed to start process");
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var outText = stdout;
        if (!string.IsNullOrEmpty(stderr)) outText += (outText.Length > 0 ? "\n" : "") + stderr;
        return new ToolResult(Id(input), true, outText.TrimEnd(), Details: new { exitCode = proc.ExitCode });
    }

    // ── file_read ────────────────────────────────────────────────────────
    private static Task<ToolResult> FileRead(Dictionary<string, object?> input, ToolContext ctx)
    {
        var path = Resolve(ctx.Cwd, Str(input, "path") ?? "");
        if (!File.Exists(path)) return Task.FromResult(Err(Id(input), $"File not found: {path}"));
        var lines = File.ReadAllLines(path);
        var offset = input.TryGetValue("offset", out var o) && o is JsonElement oe ? oe.GetInt32() - 1 : 0;
        var limit = input.TryGetValue("limit", out var l) && l is JsonElement le ? le.GetInt32() : lines.Length;
        offset = Math.Max(0, offset);
        var slice = lines.Skip(offset).Take(limit).ToArray();
        var sb = new StringBuilder();
        for (int i = 0; i < slice.Length; i++)
            sb.AppendLine($"{offset + i + 1}\t{slice[i]}");
        return Task.FromResult(Ok(Id(input), sb.ToString().TrimEnd()));
    }

    // ── file_write ───────────────────────────────────────────────────────
    private static Task<ToolResult> FileWrite(Dictionary<string, object?> input, ToolContext ctx)
    {
        var path = Resolve(ctx.Cwd, Str(input, "path") ?? "");
        var content = Str(input, "content") ?? "";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return Task.FromResult(Ok(Id(input), $"Wrote {content.Length} bytes to {path}"));
    }

    // ── file_edit ────────────────────────────────────────────────────────
    internal static Task<ToolResult> FileEdit(Dictionary<string, object?> input, ToolContext ctx)
    {
        var path = Resolve(ctx.Cwd, Str(input, "path") ?? "");
        var oldS = Str(input, "old_string") ?? "";
        var newS = Str(input, "new_string") ?? "";
        if (!File.Exists(path)) return Task.FromResult(Err(Id(input), $"File not found: {path}"));
        var text = File.ReadAllText(path);
        if (!text.Contains(oldS)) return Task.FromResult(Err(Id(input), "old_string not found in file"));
        var replaced = text.Replace(oldS, newS);
        File.WriteAllText(path, replaced);
        var count = (text.Length - replaced.Length) / Math.Max(1, oldS.Length - newS.Length);
        return Task.FromResult(Ok(Id(input), $"Edited {path}"));
    }

    // ── glob ─────────────────────────────────────────────────────────────
    private static Task<ToolResult> Glob(Dictionary<string, object?> input, ToolContext ctx)
    {
        var pattern = Str(input, "pattern") ?? "**/*";
        var baseDir = Resolve(ctx.Cwd, Str(input, "path") ?? ".");
        var matches = GlobMatch(baseDir, pattern).Take(500).OrderBy(p => p).ToList();
        var outText = matches.Count == 0 ? "(no matches)" : string.Join("\n", matches);
        return Task.FromResult(Ok(Id(input), outText));
    }

    // ── grep ─────────────────────────────────────────────────────────────
    private static Task<ToolResult> Grep(Dictionary<string, object?> input, ToolContext ctx)
    {
        var pattern = Str(input, "pattern") ?? "";
        var path = Resolve(ctx.Cwd, Str(input, "path") ?? ".");
        var globFilter = Str(input, "glob");
        if (string.IsNullOrEmpty(pattern)) return Task.FromResult(Err(Id(input), "pattern required"));
        Regex re;
        try { re = new Regex(pattern, RegexOptions.IgnoreCase); }
        catch (Exception e) { return Task.FromResult(Err(Id(input), $"Invalid regex: {e.Message}")); }

        var files = Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) : new[] { path };
        var sb = new StringBuilder();
        foreach (var f in files)
        {
            if (globFilter != null && !MatchGlob(Path.GetFileName(f), globFilter)) continue;
            if (IsBinary(f)) continue;
            try
            {
                var lines = File.ReadLines(f);
                int n = 0;
                foreach (var line in lines)
                {
                    n++;
                    if (re.IsMatch(line)) sb.AppendLine($"{f}:{n}: {line}");
                }
            }
            catch { /* skip unreadable */ }
        }
        var outText = sb.Length == 0 ? "(no matches)" : sb.ToString().TrimEnd();
        return Task.FromResult(Ok(Id(input), outText));
    }

    // ── web_fetch ────────────────────────────────────────────────────────
    private static async Task<ToolResult> WebFetch(Dictionary<string, object?> input, ToolContext ctx)
    {
        var url = Str(input, "url") ?? "";
        if (string.IsNullOrEmpty(url)) return Err(Id(input), "url required");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Add("User-Agent", $"Coder-CLI/{Brand.Version}");
            var html = await client.GetStringAsync(url);
            var text = StripHtml(html);
            return Ok(Id(input), text.Length > 20000 ? text[..20000] : text);
        }
        catch (Exception e) { return Err(Id(input), $"Fetch failed: {e.Message}"); }
    }

    // ── web_search ───────────────────────────────────────────────────────
    private static Task<ToolResult> WebSearch(Dictionary<string, object?> input, ToolContext ctx)
    {
        var q = Str(input, "query") ?? "";
        // TODO(.NET port): wire a real search backend (e.g. an OpenAI-compatible
        // web search tool, Bing API, or a provider's native search). For now we
        // surface guidance instead of failing silently.
        return Task.FromResult(Ok(Id(input),
            $"[web_search] query=\"{q}\"\nSearch backend not configured in this .NET port yet. " +
            $"Set up a search provider or implement BuiltinTools.WebSearch. You can still use web_fetch for known URLs."));
    }

    // ── todo_write ───────────────────────────────────────────────────────
    private static Task<ToolResult> TodoWrite(Dictionary<string, object?> input, ToolContext ctx)
    {
        var items = input.TryGetValue("items", out var it) && it is List<object?> list
            ? list.OfType<Dictionary<string, object?>>().ToList()
            : new List<Dictionary<string, object?>>();
        var sb = new StringBuilder();
        sb.AppendLine($"Todos ({items.Count}):");
        foreach (var item in items)
        {
            var status = item.TryGetValue("status", out var s) ? s?.ToString() : "?";
            var content = item.TryGetValue("content", out var c) ? c?.ToString() : "";
            sb.AppendLine($"  [{status}] {content}");
        }
        return Task.FromResult(Ok(Id(input), sb.ToString().TrimEnd()));
    }

    // ── helpers ──────────────────────────────────────────────────────────
    private static string Id(Dictionary<string, object?> input)
        => input.TryGetValue("_invocationId", out var v) ? v?.ToString() ?? "" : "";

    private static ToolResult Ok(string id, string output) => new(id, true, output);
    private static ToolResult Err(string id, string error) => new(id, false, error, Error: error);

    private static string Resolve(string cwd, string p)
    {
        if (string.IsNullOrEmpty(p)) return cwd;
        if (Path.IsPathRooted(p)) return p;
        return Path.GetFullPath(Path.Combine(cwd, p));
    }

    private static bool IsBinary(string f)
    {
        try
        {
            using var fs = File.OpenRead(f);
            var buf = new byte[256];
            int n = fs.Read(buf, 0, buf.Length);
            for (int i = 0; i < n; i++) if (buf[i] == 0) return true;
        }
        catch { return true; }
        return false;
    }

    private static bool MatchGlob(string name, string glob)
    {
        var pattern = "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase);
    }

    internal static IEnumerable<string> GlobMatch(string baseDir, string pattern)
    {
        if (!Directory.Exists(baseDir)) yield break;
        // Normalize "**/*.ts" style patterns into a recursive walk with a suffix filter.
        var isRecursive = pattern.Contains("**");
        var searchOption = isRecursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var simple = pattern.Replace("**/", "").Replace("**", "*");
        var namePattern = Path.GetFileName(simple);
        var subDir = Path.GetDirectoryName(simple) ?? "";
        var root = string.IsNullOrEmpty(subDir) ? baseDir : Path.Combine(baseDir, subDir);
        if (!Directory.Exists(root)) yield break;
        foreach (var f in Directory.EnumerateFiles(root, namePattern == "" ? "*" : namePattern, searchOption))
            yield return f;
    }

    private static string StripHtml(string html)
    {
        var withoutScripts = Regex.Replace(html, "<(script|style)[^>]*>.*?</\\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var text = Regex.Replace(withoutScripts, "<[^>]+>", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }
}
