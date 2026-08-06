// Coder CLI (.NET 11 port) — file-defined subagent templates.
// Scans .coder/agents/*.md (project) then ~/.coder/agents/*.md (user);
// same-name templates are first-seen-wins (project overrides user).

using System.Text.RegularExpressions;
using Coder.Skills;
using Coder.Utils;

namespace Coder.Agents;

/// <summary>文件定义的自定义子代理。Body 即该子代理的系统提示。</summary>
public sealed record AgentTemplate(
    string Name,
    string Description,
    string Body,
    string[]? Tools,
    string? Model,
    string Source);

public static class AgentTemplateLoader
{
    /// <summary>两级扫描:项目 .coder/agents 优先,用户 ~/.coder/agents 兜底;同名首见生效。</summary>
    public static async Task<List<AgentTemplate>> LoadAsync(string cwd, string userConfigDir)
    {
        var result = new List<AgentTemplate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { System.IO.Path.Combine(cwd, ".coder", "agents"),
                                     System.IO.Path.Combine(userConfigDir, "agents") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.md")
                         .OrderBy(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal))
            {
                try
                {
                    var t = await ParseAsync(file);
                    if (t != null && seen.Add(t.Name)) result.Add(t);
                }
                catch (Exception e) { Log.Instance.Warn("agent template failed:", $"{file} {e.Message}"); }
            }
        }
        return result;
    }

    /// <summary>解析单个模板文件:frontmatter(name/description/tools/model)+ 正文即 Body;无 frontmatter 时文件名作 name。</summary>
    public static async Task<AgentTemplate?> ParseAsync(string file)
    {
        var raw = await File.ReadAllTextAsync(file);
        var m = Regex.Match(raw, @"^---\r?\n([\s\S]*?)\r?\n---\r?\n([\s\S]*)$");
        if (!m.Success)
            return new AgentTemplate(System.IO.Path.GetFileNameWithoutExtension(file), "", raw.Trim(), null, null, file);
        var fm = SkillLoader.ParseFrontmatter(m.Groups[1].Value);
        fm.TryGetValue("name", out var name);
        fm.TryGetValue("description", out var desc);
        string[]? tools = null;
        if (fm.TryGetValue("tools", out var t) && !string.IsNullOrWhiteSpace(t))
            tools = t.Split(',', ' ').Where(s => s.Length > 0).Distinct().ToArray();
        fm.TryGetValue("model", out var model);
        return new AgentTemplate(
            string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileNameWithoutExtension(file) : name,
            desc ?? "",
            m.Groups[2].Value.Trim(),
            tools,
            string.IsNullOrWhiteSpace(model) ? null : model,
            file);
    }
}
