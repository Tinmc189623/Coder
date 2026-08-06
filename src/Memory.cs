// Coder CLI (.NET 11 port) — persistent memory store.
// Three scopes: user (all projects), agent (this agent), project (this repo).
// Stored as Markdown files with YAML-ish frontmatter.
// Ported from src/memory/store.ts.

using System.Text.RegularExpressions;
using Coder.Utils;

namespace Coder.Memory;

public enum MemoryScope { User, Agent, Project }

public sealed record MemoryEntry(
    string Scope,
    string Topic,
    string Content,
    string UpdatedAt,
    string Path,
    string? Type = null);

public sealed class MemoryStore
{
    private readonly string _userDir;
    private readonly string _agentDir;
    private readonly string _projectDir;

    public MemoryStore(string userConfigDir, string projectRoot)
    {
        _userDir = Path.Combine(userConfigDir, "memory");
        _agentDir = Path.Combine(userConfigDir, "agents", "coder", "memory");
        _projectDir = Path.Combine(projectRoot, ".coder", "memory");
    }

    public string DirFor(MemoryScope scope) => scope switch
    {
        MemoryScope.User => _userDir,
        MemoryScope.Agent => _agentDir,
        _ => _projectDir,
    };

    public static MemoryScope ParseScope(string? s) => s?.ToLowerInvariant() switch
    {
        "user" => MemoryScope.User,
        "agent" => MemoryScope.Agent,
        _ => MemoryScope.Project,
    };

    public async Task<string> WriteAsync(MemoryScope scope, string topic, string content, string? type = null)
    {
        var dir = DirFor(scope);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{SanitizeTopic(topic)}.md");
        var now = DateTime.UtcNow.ToString("o");
        var lines = new List<string>
        {
            "---",
            $"scope: {scope.ToString().ToLowerInvariant()}",
            $"topic: {topic}",
        };
        if (!string.IsNullOrEmpty(type)) lines.Add($"type: {type}");
        lines.Add($"updated_at: {now}");
        lines.Add("---");
        lines.Add("");
        await File.WriteAllTextAsync(file, string.Join("\n", lines) + content.Trim() + "\n");
        return file;
    }

    public async Task<List<MemoryEntry>> ListAsync(MemoryScope? scope = null, string? topic = null)
    {
        var scopes = scope.HasValue
            ? new[] { scope.Value }
            : new[] { MemoryScope.User, MemoryScope.Agent, MemoryScope.Project };

        var outList = new List<MemoryEntry>();
        foreach (var s in scopes)
        {
            var dir = DirFor(s);
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.md"); } catch { continue; }
            foreach (var full in files)
            {
                var entry = await ParseEntryAsync(full, s.ToString().ToLowerInvariant());
                if (entry == null) continue;
                if (!string.IsNullOrEmpty(topic) && entry.Topic != topic) continue;
                outList.Add(entry);
            }
        }
        return outList.OrderByDescending(e => e.UpdatedAt, StringComparer.Ordinal).ToList();
    }

    public async Task<MemoryEntry?> ReadAsync(MemoryScope scope, string topic)
    {
        var file = Path.Combine(DirFor(scope), $"{SanitizeTopic(topic)}.md");
        if (!File.Exists(file)) return null;
        return await ParseEntryAsync(file, scope.ToString().ToLowerInvariant());
    }

    private static string SanitizeTopic(string t)
    {
        var s = Regex.Replace(t.ToLowerInvariant(), @"[^a-z0-9_-]+", "-").Trim('-');
        if (s.Length > 64) s = s[..64];
        return s.Length == 0 ? "untitled" : s;
    }

    private static async Task<MemoryEntry?> ParseEntryAsync(string file, string scope)
    {
        try
        {
            var raw = await File.ReadAllTextAsync(file);
            var m = Regex.Match(raw, @"^---\r?\n([\s\S]*?)\r?\n---\r?\n([\s\S]*)$");
            if (!m.Success)
                return new MemoryEntry(scope, Path.GetFileNameWithoutExtension(file), raw.Trim(), "", file);

            var fm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in m.Groups[1].Value.Split('\n'))
            {
                var kv = Regex.Match(line, @"^([a-z_]+):\s*(.*)$");
                if (kv.Success) fm[kv.Groups[1].Value] = kv.Groups[2].Value.Trim();
            }

            return new MemoryEntry(
                scope,
                fm.TryGetValue("topic", out var tp) ? tp : Path.GetFileNameWithoutExtension(file),
                m.Groups[2].Value.Trim(),
                fm.TryGetValue("updated_at", out var ua) ? ua : "",
                file,
                fm.TryGetValue("type", out var ty) ? ty : null);
        }
        catch (Exception e)
        {
            Log.Instance.Warn("Failed to parse memory entry:", $"{file} {e.Message}");
            return null;
        }
    }
}
