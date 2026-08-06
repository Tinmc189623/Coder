// Coder CLI (.NET 11 port) — tool risk classification for the permission prompt.
// The agent can ask to run anything; the permission dialog must make the *stakes*
// obvious before the user approves. We grade every proposed tool call into a
// small set of tiers so destructive operations never look like harmless ones.

using Coder.Types;
using Coder.Ui;

namespace Coder.Core;

public enum RiskTier
{
    /// Safe, read-only, or purely local inspection.
    Low,
    /// Mutates files but is routine and reversible (write/edit/install).
    Caution,
    /// Network- or identity-affecting, hard to reverse, or runs arbitrary code.
    Dangerous,
    /// Can destroy data, burn credentials, or exfiltrate the machine.
    Critical,
}

public static class RiskLevel
{
    // Patterns that escalate a command to Critical regardless of tool.
    private static readonly (string Label, string Pattern)[] CriticalPatterns =
    {
        ("删除全部文件", @"\brm\s+(-rf?|--force)\s+/|rm\s+-rf?\s+--no-preserve-root|del\s+/[sq]\s+[a-z]:\\?\\?"),
        ("清空磁盘/分区", @"mkfs|dd\s+if=/dev/(zero|urandom)|shred\s+-[a-z]*\s"),
        ("格式化/覆盖引导", @"format\s+[a-z]:|diskpart"),
        ("curl 管道到 shell", @"curl\s+.*\|\s*(sudo\s+)?(ba)?sh|wget\s+.*\|\s*(ba)?sh"),
        ("改写系统凭据", @"git\s+push\s+.*--force|npm\s+publish|--force.*publish"),
        ("提权", @"sudo\s+(su|passwd|visudo|-i)"),
    };

    private static readonly (string Label, string Pattern)[] DangerousPatterns =
    {
        ("执行 shell 命令", "(bash|shell|exec|run_command)"),
        ("推送远程", @"git\s+push|git\s+clone\s+http|rsync\s+.*\s+(ssh|remote|rsync://)"),
        ("上传/外发", @"curl\s+-X\s*(POST|PUT|DELETE)|scp\s|ftp\s+put|requests\.(post|put)"),
        ("安装软件", @"npm\s+install|pip\s+install|apt\s+install|brew\s+install|dotnet\s+tool"),
        ("网络访问", @"(web_fetch|web_search|fetch_url|http_request)"),
        ("改写代码仓库", @"(git|hg)\s+(reset|checkout|rebase|amend|clean\s+-[a-z]*[fd])"),
    };

    private static bool MatchAny(string? haystack, params (string, string)[] pairs)
    {
        if (string.IsNullOrEmpty(haystack)) return false;
        foreach (var (_, pat) in pairs)
            if (System.Text.RegularExpressions.Regex.IsMatch(haystack, pat,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return true;
        return false;
    }

    /// <summary>Grade a proposed tool call. <paramref name="cwd"/> lets file tools
    /// detect writes outside the project sandbox.</summary>
    public static (RiskTier Tier, string Label) Assess(ToolDefinition tool, ToolInvocation inv, string cwd = "")
    {
        var name = inv.Name;
        var cmd = inv.Input.TryGetValue("command", out var c) ? c?.ToString() : null;
        var url = inv.Input.TryGetValue("url", out var u) ? u?.ToString() : "";
        var haystack = $"{name} {cmd} {url}";

        if (MatchAny(haystack, CriticalPatterns))
            return (RiskTier.Critical, "危险操作");
        if (MatchAny(haystack, DangerousPatterns))
            return (RiskTier.Dangerous, "敏感操作");

        if (name is "file_write" or "file_edit" or "file_delete")
            return (RiskTier.Caution, "将修改文件");

        if (tool.Mutates)
            return (RiskTier.Caution, "将修改环境");

        if (tool.ReadOnly)
            return (RiskTier.Low, "只读");

        return (RiskTier.Low, "低风险");
    }

    // ── presentation ────────────────────────────────────────────────────────
    public static string Badge(RiskTier tier, string label)
    {
        var body = tier switch
        {
            RiskTier.Critical => "⚠ " + label,
            RiskTier.Dangerous => "◆ " + label,
            RiskTier.Caution => "· " + label,
            _ => "· " + label,
        };
        return tier switch
        {
            RiskTier.Critical => Theme.Error(body),
            RiskTier.Dangerous => Theme.Warn(body),
            RiskTier.Caution => Theme.Dim(body),
            _ => Theme.Muted(body),
        };
    }
}
