// Coder CLI (.NET 11 port) — skill discovery, loading and community install.
// Ported from src/skills/{paths,loader,install}.ts.

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Coder.Utils;

namespace Coder.Skills;

public enum SkillOrigin { Coder, Global, Project }

/// <summary>Skill metadata parsed from a SKILL.md file.</summary>
public sealed record Skill(
    string Name,
    string Description,
    string Body,
    string Source,
    List<string>? AllowedTools = null,
    SkillOrigin Origin = SkillOrigin.Project,
    bool Plugin = false);

// ── Search paths ────────────────────────────────────────────────────────────
public static class SkillPaths
{
    /// <summary>Deduplicated, existing skill scan directories (community layouts included).</summary>
    public static List<string> Resolve(string cwd, string userConfigDir, IEnumerable<string>? extraPaths = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        cwd = Path.GetFullPath(cwd);

        var candidates = new List<string>
        {
            Path.Combine(cwd, "skills"),
            Path.Combine(cwd, ".coder", "skills"),
            Path.Combine(cwd, ".claude", "skills"),
            Path.Combine(cwd, ".cursor", "skills"),
            Path.Combine(cwd, ".codex", "skills"),
            Path.Combine(cwd, ".agents", "skills"),
            Path.Combine(userConfigDir, "skills"),
            Path.Combine(home, ".coder", "skills"),
            Path.Combine(home, ".cursor", "skills"),
            Path.Combine(home, ".cursor", "skills-cursor"),
            Path.Combine(home, ".claude", "skills"),
            Path.Combine(home, ".codex", "skills"),
            Path.Combine(home, ".agents", "skills"),
            Path.Combine(home, ".config", "coder", "skills"),
        };
        if (extraPaths != null) candidates.AddRange(extraPaths);

        var envPaths = Environment.GetEnvironmentVariable("CODER_SKILL_PATHS");
        if (!string.IsNullOrEmpty(envPaths))
            candidates.AddRange(envPaths!.Split(';', ',').Select(p => p.Trim()).Where(p => p.Length > 0));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outList = new List<string>();
        foreach (var p in candidates)
        {
            string abs;
            try { abs = Path.GetFullPath(p); } catch { continue; }
            if (!seen.Add(abs)) continue;
            if (!Directory.Exists(abs) && !File.Exists(abs)) { seen.Remove(abs); continue; }
            outList.Add(abs);
        }
        return outList;
    }

    /// <summary>Install target directory for community skills.</summary>
    public static string DefaultInstallDir(string userConfigDir) => Path.Combine(userConfigDir, "skills");
}

// ── Loader ──────────────────────────────────────────────────────────────────
public sealed class SkillLoader
{
    private readonly List<string> _paths;
    private readonly Dictionary<string, Skill> _cache = new(StringComparer.Ordinal);

    public SkillLoader(IEnumerable<string> paths) => _paths = paths.ToList();

    public List<string> GetPaths() => new(_paths);
    public List<string> Names() => _cache.Keys.ToList();

    /// <summary>Scan all configured roots and discover skills.</summary>
    public async Task<List<Skill>> DiscoverAsync()
    {
        var outList = new List<Skill>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in _paths)
        {
            if (!Directory.Exists(root) && !File.Exists(root)) continue;
            try
            {
                var found = await ScanRootAsync(root, LabelForPath(root));
                foreach (var skill in found)
                {
                    if (!seen.Add(skill.Name.ToLowerInvariant())) continue;
                    _cache[skill.Name] = skill;
                    outList.Add(skill);
                }
            }
            catch (Exception e)
            {
                Log.Instance.Warn("Failed to scan skill path:", $"{root} {e.Message}");
            }
        }

        outList.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return outList;
    }

    /// <summary>Load a skill by name (supports the ui-ux-pro-max alias family).</summary>
    public async Task<Skill> LoadAsync(string name)
    {
        if (_cache.TryGetValue(name, out var direct)) return direct;
        if (_cache.TryGetValue(NormalizeAlias(name), out var aliased)) return aliased;

        foreach (var p in _paths)
        {
            var candidates = new[]
            {
                Path.Combine(p, name, "SKILL.md"),
                Path.Combine(p, NormalizeAlias(name), "SKILL.md"),
                Path.Combine(p, name, "skill.md"),
            };
            foreach (var file in candidates)
            {
                if (!File.Exists(file)) continue;
                var skill = await ParseSkillFileAsync(file, Path.GetFileName(Path.GetDirectoryName(file)!) ?? name, LabelForPath(p));
                if (skill != null) { _cache[skill.Name] = skill; return skill; }
            }
        }
        throw new CoderError("SKILL_NOT_FOUND", $"Skill not found: {name}");
    }

    private static async Task<List<Skill>> ScanRootAsync(string root, SkillOrigin origin)
    {
        var outList = new List<Skill>();

        if (File.Exists(root))
        {
            if (Regex.IsMatch(Path.GetFileName(root), @"skill\.md$", RegexOptions.IgnoreCase))
            {
                var s = await ParseSkillFileAsync(root, Path.GetFileName(Path.GetDirectoryName(root)!) ?? "skill", origin);
                if (s != null) outList.Add(s);
            }
            return outList;
        }
        if (!Directory.Exists(root)) return outList;

        // Root itself is a skill package.
        var rootSkill = Path.Combine(root, "SKILL.md");
        if (File.Exists(rootSkill))
        {
            var s = await ParseSkillFileAsync(rootSkill, Path.GetFileName(root), origin);
            if (s != null) outList.Add(s);
        }

        // plugin.json declared sub-skills.
        var manifest = ReadPluginManifest(Path.Combine(root, "plugin.json"));
        if (manifest?.Skills is { Count: > 0 })
        {
            foreach (var rel in manifest.Skills)
            {
                var dir = Path.GetFullPath(Path.Combine(root, rel));
                var file = Path.Combine(dir, "SKILL.md");
                if (!File.Exists(file)) continue;
                var s = await ParseSkillFileAsync(file, Path.GetFileName(dir), origin);
                if (s != null) outList.Add(s);
            }
        }

        // First-level subdirectories.
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var file = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(file)) continue;
            var s = await ParseSkillFileAsync(file, Path.GetFileName(dir), origin);
            if (s != null) outList.Add(s);
        }

        // Community layout: <root>/skills/<name>/SKILL.md
        var nested = Path.Combine(root, "skills");
        if (Directory.Exists(nested)) outList.AddRange(await ScanRootAsync(nested, origin));

        return outList;
    }

    /// <summary>Parse a SKILL.md file with YAML frontmatter.</summary>
    private static async Task<Skill?> ParseSkillFileAsync(string file, string fallbackName, SkillOrigin origin)
    {
        try
        {
            var raw = await File.ReadAllTextAsync(file);
            var m = Regex.Match(raw, @"^---\r?\n([\s\S]*?)\r?\n---\r?\n([\s\S]*)$");
            var name = fallbackName;
            var description = "";
            List<string>? allowedTools = null;
            var body = raw;

            if (m.Success)
            {
                var fm = ParseFrontmatter(m.Groups[1].Value);
                if (fm.TryGetValue("name", out var n) && !string.IsNullOrWhiteSpace(n)) name = n;
                if (fm.TryGetValue("description", out var d)) description = d;
                if (fm.TryGetValue("allowed-tools", out var at) && !string.IsNullOrWhiteSpace(at))
                    allowedTools = at.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                body = m.Groups[2].Value.Trim();
            }

            var dir = Path.GetDirectoryName(file)!;
            var plugin = Directory.Exists(Path.Combine(dir, "scripts")) || Directory.Exists(Path.Combine(dir, "data"));

            return new Skill(name, description, body, file, allowedTools, origin, plugin);
        }
        catch (Exception e)
        {
            Log.Instance.Warn("Failed to parse skill file:", $"{file} {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parse simple key/value YAML frontmatter. Supports folded continuations,
    /// block scalars (<c>|</c>, <c>&gt;</c>, with chomping indicators) and quoted values.
    /// </summary>
    public static Dictionary<string, string> ParseFrontmatter(string block)
    {
        var fm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var key = "";
        var buf = "";
        foreach (var line in block.Split('\n'))
        {
            var kv = Regex.Match(line, @"^([a-z0-9_-]+):\s*(.*)$", RegexOptions.IgnoreCase);
            if (kv.Success)
            {
                if (key.Length > 0) fm[key] = CleanScalar(buf);
                key = kv.Groups[1].Value.ToLowerInvariant();
                buf = kv.Groups[2].Value;
            }
            else if (key.Length > 0 && line.Length > 0 && char.IsWhiteSpace(line[0]))
            {
                buf += " " + line.Trim();
            }
        }
        if (key.Length > 0) fm[key] = CleanScalar(buf);
        return fm;
    }

    /// <summary>Strip YAML block-scalar markers, list dashes and surrounding quotes.</summary>
    private static string CleanScalar(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return s;

        // Block scalar header: '|', '>', optionally with '-' / '+' and an indent digit.
        var block = Regex.Match(s, @"^[|>][-+]?\d?\s*");
        if (block.Success) s = s[block.Length..].Trim();

        // Leading list dash left over from folded continuations ("- item").
        s = Regex.Replace(s, @"^-\s+", "");

        // Surrounding quotes (YAML single/double quoted scalars).
        if (s.Length >= 2 &&
            ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
        {
            var quote = s[0];
            s = s[1..^1];
            s = quote == '"' ? s.Replace("\\\"", "\"") : s.Replace("''", "'");
        }
        else if (s.Length >= 1 && (s[0] == '"' || s[0] == '\''))
        {
            // Unterminated quote (truncated multi-line scalar) — drop the opener.
            s = s[1..];
        }

        return s.Trim();
    }

    private sealed class PluginManifest
    {
        public string? Name { get; set; }
        public List<string>? Skills { get; set; }
    }

    private static PluginManifest? ReadPluginManifest(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            return JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    private static SkillOrigin LabelForPath(string p)
    {
        var norm = p.Replace('\\', '/').ToLowerInvariant();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/').ToLowerInvariant();
        if (norm.Contains("/.coder") || norm.Contains("/.config/coder")) return SkillOrigin.Coder;
        if (norm.StartsWith(home)) return SkillOrigin.Global;
        return SkillOrigin.Project;
    }

    public static string NormalizeAlias(string name)
    {
        var n = name.ToLowerInvariant().TrimStart('/');
        return n is "ui-ux-pro-max-skill" or "uiuxpromax" ? "ui-ux-pro-max" : name;
    }

    /// <summary>Copy a skill directory into the user skills directory.</summary>
    public static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(src, dest));
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(src, dest), overwrite: true);
    }
}

// ── Community install ───────────────────────────────────────────────────────
public sealed record SkillInstallResult(bool Ok, string Name, string Path, string Message);

public static class SkillInstaller
{
    private sealed record CommunitySkill(string Repo, string[] Paths);

    private static readonly Dictionary<string, CommunitySkill> Community = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ui-ux-pro-max"] = new(
            "https://github.com/nextlevelbuilder/ui-ux-pro-max-skill.git",
            new[] { "skills/ui-ux-pro-max", "ui-ux-pro-max", ".claude/skills/ui-ux-pro-max" }),
    };

    public static List<string> ListInstallable() => Community.Keys.ToList();

    public static async Task<SkillInstallResult> InstallAsync(string name, string userConfigDir)
    {
        var key = SkillLoader.NormalizeAlias(name).ToLowerInvariant();
        var dest = Path.Combine(SkillPaths.DefaultInstallDir(userConfigDir), key);

        if (File.Exists(Path.Combine(dest, "SKILL.md")))
            return new SkillInstallResult(true, key, dest, "already_installed");

        if (!Community.TryGetValue(key, out var spec))
            return new SkillInstallResult(false, key, dest, $"unknown_skill:{key}");

        var tmp = Path.Combine(userConfigDir, ".tmp-skill-install");
        SafeDelete(tmp);
        Directory.CreateDirectory(tmp);

        var cloned = await GitCloneShallowAsync(spec.Repo, tmp);
        if (!cloned)
        {
            SafeDelete(tmp);
            return new SkillInstallResult(false, key, dest, "git_clone_failed");
        }

        string? src = null;
        foreach (var rel in spec.Paths)
        {
            var candidate = Path.Combine(tmp, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(Path.Combine(candidate, "SKILL.md"))) { src = candidate; break; }
        }
        src ??= FindSkillDirRecursive(tmp, key);

        if (src == null)
        {
            SafeDelete(tmp);
            return new SkillInstallResult(false, key, dest, "skill_not_found_in_repo");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        SkillLoader.CopyDirectory(src, dest);
        SafeDelete(tmp);

        return new SkillInstallResult(true, key, dest, "installed");
    }

    private static async Task<bool> GitCloneShallowAsync(string repo, string dir)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "clone", "--depth", "1", repo, dir }) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        catch (Exception e)
        {
            Log.Instance.Warn("git clone failed:", e.Message);
            return false;
        }
    }

    private static string? FindSkillDirRecursive(string root, string key)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories))
            {
                var dir = Path.GetDirectoryName(file)!;
                if (Path.GetFileName(dir).Equals(key, StringComparison.OrdinalIgnoreCase)) return dir;
            }
            // Fall back to the first SKILL.md found.
            return Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName).FirstOrDefault();
        }
        catch { return null; }
    }

    private static void SafeDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
    }
}
