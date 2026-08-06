// Coder CLI (.NET 11 port) — terminal chrome: width math, boxes, panels,
// markdown rendering, status line, permission dialog.
// Ported from src/ui/{terminal,terminal_fmt,ansi_terminal,box,chrome,markdown,status_line,permission_dialog}.ts.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Coder.Core;
using Coder.Localization;
using Coder.Types;

namespace Coder.Ui;

// ── Terminal metrics ────────────────────────────────────────────────────────
public static class Terminal
{
    public static int Cols()
    {
        try
        {
            var w = Console.WindowWidth;
            return w > 0 ? w : 80;
        }
        catch { return 80; }
    }

    public static bool IsInteractive()
        => !Console.IsInputRedirected && !Console.IsOutputRedirected;
}

/// <summary>Display-width helpers that account for CJK wide characters and ANSI codes.</summary>
public static class TextFmt
{
    private static readonly Regex AnsiPattern = new(@"\u001b\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    public static string Strip(string s) => AnsiPattern.Replace(s, "");

    public static int CharWidth(char c)
    {
        if (char.IsControl(c)) return 0;
        var cat = CharUnicodeInfo.GetUnicodeCategory(c);
        if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format) return 0;
        return IsWide(c) ? 2 : 1;
    }

    private static bool IsWide(char c)
    {
        int cp = c;
        return (cp >= 0x1100 && cp <= 0x115F)   // Hangul Jamo
            || (cp >= 0x2E80 && cp <= 0xA4CF)   // CJK radicals … Yi
            || (cp >= 0xAC00 && cp <= 0xD7A3)   // Hangul syllables
            || (cp >= 0xF900 && cp <= 0xFAFF)   // CJK compatibility ideographs
            || (cp >= 0xFE30 && cp <= 0xFE6F)   // CJK compatibility forms
            || (cp >= 0xFF00 && cp <= 0xFF60)   // Fullwidth forms
            || (cp >= 0xFFE0 && cp <= 0xFFE6);
    }

    public static int DisplayWidth(string s)
    {
        var total = 0;
        foreach (var c in Strip(s)) total += CharWidth(c);
        return total;
    }

    /// <summary>Truncate to a display width, appending an ellipsis when cut.</summary>
    public static string TruncateVisible(string s, int max)
    {
        if (max <= 0) return "";
        if (DisplayWidth(s) <= max) return s;
        var sb = new StringBuilder();
        var w = 0;
        foreach (var c in Strip(s))
        {
            var cw = CharWidth(c);
            if (w + cw > max - 1) break;
            sb.Append(c);
            w += cw;
        }
        return sb.ToString() + "…";
    }

    public static string PadVisible(string s, int width)
    {
        var pad = width - DisplayWidth(s);
        return pad > 0 ? s + new string(' ', pad) : s;
    }

    /// <summary>
    /// Hard-wrap text at a display width. ANSI escape sequences are never split
    /// and the active SGR state is carried over to continuation lines.
    /// </summary>
    public static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        if (width <= 0) return lines;

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (DisplayWidth(raw) <= width) { lines.Add(raw); continue; }

            var sb = new StringBuilder();
            var active = new StringBuilder();   // SGR codes currently in effect
            var w = 0;
            var i = 0;

            void Flush()
            {
                if (active.Length > 0) sb.Append("\u001b[0m");
                lines.Add(sb.ToString());
                sb.Clear();
                sb.Append(active);
                w = 0;
            }

            while (i < raw.Length)
            {
                // Pass escape sequences through untouched, tracking SGR state.
                if (raw[i] == '\u001b' && i + 1 < raw.Length && raw[i + 1] == '[')
                {
                    var j = i + 2;
                    while (j < raw.Length && !char.IsLetter(raw[j])) j++;
                    if (j < raw.Length) j++;
                    var seq = raw[i..j];
                    sb.Append(seq);
                    if (seq.EndsWith('m'))
                    {
                        if (seq is "\u001b[0m" or "\u001b[m") active.Clear();
                        else active.Append(seq);
                    }
                    i = j;
                    continue;
                }

                var cw = CharWidth(raw[i]);
                if (w + cw > width) Flush();
                sb.Append(raw[i]);
                w += cw;
                i++;
            }

            if (sb.Length > 0) lines.Add(sb.ToString());
        }
        return lines;
    }
}

/// <summary>Raw ANSI escape helpers.</summary>
public static class Ansi
{
    public const string ClearLine = "\u001b[2K\r";
    public static string CursorUp(int n = 1) => $"\u001b[{n}A";
    public static string RefreshPreviousLine() => CursorUp() + ClearLine;
    public const string HideCursor = "\u001b[?25l";
    public const string ShowCursor = "\u001b[?25h";
}

// ── Boxes and panels ────────────────────────────────────────────────────────
public static class Chrome
{
    public const string Indent = "  ";
    public static int BoxInner => Math.Max(32, Math.Min(Terminal.Cols() - 4, 100));

    private const char TL = '╭', TR = '╮', BL = '╰', BR = '╯', H = '─', V = '│';

    /// <summary>Render a rounded box panel around the given lines.</summary>
    public static string BoxPanel(IEnumerable<string> lines, string? title = null)
    {
        var inner = BoxInner;
        var sb = new StringBuilder();
        var top = new string(H, inner);
        if (!string.IsNullOrEmpty(title))
        {
            var t = $" {title} ";
            var tw = TextFmt.DisplayWidth(t);
            if (tw < inner) top = t + new string(H, inner - tw);
        }
        sb.Append(Theme.Dim($"{TL}{top}{TR}")).Append('\n');
        foreach (var raw in lines)
        {
            foreach (var line in TextFmt.Wrap(raw, inner - 2))
                sb.Append(Theme.Dim(V.ToString()))
                  .Append(' ')
                  .Append(TextFmt.PadVisible(line, inner - 2))
                  .Append(' ')
                  .Append(Theme.Dim(V.ToString()))
                  .Append('\n');
        }
        sb.Append(Theme.Dim($"{BL}{new string(H, inner)}{BR}"));
        return sb.ToString();
    }

    public static string Divider()
        => Theme.Dim(new string('─', Math.Max(8, Math.Min(Terminal.Cols(), 100))));
}

// ── Markdown rendering ──────────────────────────────────────────────────────
/// <summary>Lightweight Markdown → ANSI renderer (headings, lists, code, emphasis).</summary>
public static class Markdown
{
    private static readonly Regex Bold = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex Italic = new(@"(?<!\*)\*([^*]+)\*(?!\*)", RegexOptions.Compiled);
    private static readonly Regex Code = new(@"`([^`]+)`", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);

    public static string Render(string md)
    {
        var sb = new StringBuilder();
        var inFence = false;
        foreach (var line in md.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```"))
            {
                inFence = !inFence;
                sb.AppendLine(Theme.Dim(line));
                continue;
            }
            if (inFence) { sb.AppendLine(Theme.Info(line)); continue; }

            var m = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            if (m.Success)
            {
                var level = m.Groups[1].Value.Length;
                var text = Inline(m.Groups[2].Value);
                sb.AppendLine(level <= 2 ? Theme.Brand(Theme.Bold(text)) : Theme.Bold(text));
                continue;
            }

            var bullet = Regex.Match(line, @"^(\s*)[-*+]\s+(.*)$");
            if (bullet.Success)
            {
                sb.AppendLine($"{bullet.Groups[1].Value}{Theme.BrandSecondary("•")} {Inline(bullet.Groups[2].Value)}");
                continue;
            }

            var numbered = Regex.Match(line, @"^(\s*)(\d+)\.\s+(.*)$");
            if (numbered.Success)
            {
                sb.AppendLine($"{numbered.Groups[1].Value}{Theme.BrandSecondary(numbered.Groups[2].Value + ".")} {Inline(numbered.Groups[3].Value)}");
                continue;
            }

            if (line.TrimStart().StartsWith("> "))
            {
                sb.AppendLine(Theme.Dim("┃ ") + Theme.Dim(Inline(line.TrimStart()[2..])));
                continue;
            }

            if (Regex.IsMatch(line, @"^\s*([-*_])\s*\1\s*\1[\s\-*_]*$"))
            {
                sb.AppendLine(Chrome.Divider());
                continue;
            }

            sb.AppendLine(Inline(line));
        }
        return sb.ToString().TrimEnd();
    }

    private static string Inline(string s)
    {
        s = Code.Replace(s, m => Theme.Info(m.Groups[1].Value));
        s = Bold.Replace(s, m => Theme.Bold(m.Groups[1].Value));
        s = Italic.Replace(s, m => Theme.Dim(m.Groups[1].Value));
        s = Link.Replace(s, m => $"{Theme.Bold(m.Groups[1].Value)} {Theme.Dim("(" + m.Groups[2].Value + ")")}");
        return s;
    }
}

// ── Status line ─────────────────────────────────────────────────────────────
public static class StatusLine
{
    public static readonly PermissionMode[] Cycle =
    {
        PermissionMode.Default,
        PermissionMode.AcceptEdits,
        PermissionMode.Plan,
        PermissionMode.BypassPermissions,
    };

    public static PermissionMode Next(PermissionMode current)
    {
        var idx = Array.IndexOf(Cycle, current);
        return Cycle[(idx + 1) % Cycle.Length];
    }

    public static string ModeLabel(PermissionMode mode) => mode switch
    {
        PermissionMode.Default => "default",
        PermissionMode.AcceptEdits => "acceptEdits",
        PermissionMode.Plan => "plan",
        PermissionMode.BypassPermissions => "bypassPermissions",
        _ => "default",
    };

    public static string Render(PermissionMode mode, int? cols = null)
    {
        var zh = I18n.Current == "zh-CN";
        var raw =
            Theme.Dim(zh ? "/ 命令  ·  Ctrl+C 中断" : "/ for commands  ·  Ctrl+C to interrupt") +
            Theme.Dim("  ·  ") +
            Theme.Dim(zh ? "权限" : "permission") +
            " " + Theme.Bold(ModeLabel(mode)) +
            Theme.Dim("  ·  ") +
            Theme.Dim(zh ? "Shift+Tab 切换" : "Shift+Tab to cycle");
        var max = Math.Max(24, cols ?? Terminal.Cols());
        return TextFmt.DisplayWidth(raw) <= max ? raw : TextFmt.TruncateVisible(raw, max);
    }

    public static void Print(PermissionMode mode) => Console.Out.Write(Render(mode) + "\n");

    public static string ShortcutsPanel()
    {
        var zh = I18n.Current == "zh-CN";
        var rows = zh
            ? new[] { "/help      查看命令列表", "Shift+Tab  切换权限模式", "Ctrl+C     中断当前回合", "Ctrl+D     退出" }
            : new[] { "/help      list commands", "Shift+Tab  cycle permission mode", "Ctrl+C     interrupt turn", "Ctrl+D     exit" };
        var lines = new List<string> { Theme.Bold(zh ? "快捷键" : "Shortcuts") };
        lines.AddRange(rows.Select(r => "  " + r));
        return "\n" + Chrome.BoxPanel(lines) + "\n";
    }
}

// ── Permission dialog ───────────────────────────────────────────────────────
public static class PermissionDialog
{
    /// <summary>
    /// Render the tool permission panel and return the selected option index
    /// (0 = allow once, 1 = always allow, 2 = deny). Surfaces the *stakes* before
    /// the user approves: a risk tier badge and, for file tools, a real diff of
    /// what will hit disk — so the user is never approving blind.
    /// </summary>
    public static int Prompt(ToolDefinition tool, ToolInvocation inv, string cwd = "")
    {
        var zh = I18n.Current == "zh-CN";
        var (tier, label) = RiskLevel.Assess(tool, inv, cwd);

        var rows = new List<string>
        {
            Theme.Bold(zh ? $"请求执行工具：{tool.Name}" : $"Tool requests permission: {tool.Name}") +
            "  " + RiskLevel.Badge(tier, label),
        };

        if (tier == RiskTier.Critical || tier == RiskTier.Dangerous)
            rows.Add(Theme.Warn(zh ? "⚠ 此操作可能不可逆，请确认你是故意的。" :
                                      "⚠ This may be irreversible — make sure it is intended."));

        foreach (var (k, v) in inv.Input)
        {
            if (k.StartsWith('_')) continue;
            var s = v is string str ? str : System.Text.Json.JsonSerializer.Serialize(v);
            var maxVal = Chrome.BoxInner - Chrome.Indent.Length - TextFmt.DisplayWidth(k) - 6;
            rows.Add($"{Chrome.Indent}{Theme.Dim(k + ":")} {TextFmt.TruncateVisible(s, Math.Max(12, maxVal))}");
        }

        var preview = FileDiffPreview(tool.Name, inv.Input, cwd);
        if (preview != null) rows.AddRange(preview);

        Console.Out.Write(Chrome.BoxPanel(rows) + "\n");

        var options = zh
            ? new List<string> { "本次允许", "始终允许该工具", tier is RiskTier.Critical or RiskTier.Dangerous ? "拒绝（推荐）" : "拒绝" }
            : new List<string> { "Allow once", "Always allow this tool", tier is RiskTier.Critical or RiskTier.Dangerous ? "Deny (recommended)" : "Deny" };
        return Prompter.Choose(zh ? $"是否执行 {tool.Name}?" : $"Run {tool.Name}?", options);
    }

    /// <summary>Build a diff preview for file_write / file_edit, read from disk
    /// (the file is still in its pre-execution state here). Null when N/A.</summary>
    private static List<string>? FileDiffPreview(string name, Dictionary<string, object?> input, string cwd)
    {
        if (name is not ("file_write" or "file_edit")) return null;
        if (!input.TryGetValue("path", out var p) || p is not string path || string.IsNullOrEmpty(path)) return null;

        var full = string.IsNullOrEmpty(cwd) ? path : Path.GetFullPath(path, cwd);
        var newText = name == "file_write"
            ? (input.TryGetValue("content", out var c) ? c?.ToString() : "")
            : (input.TryGetValue("new_string", out var n) ? n?.ToString() : "");
        var oldText = name == "file_edit"
            ? (input.TryGetValue("old_string", out var o) ? o?.ToString() : "")
            : null;

        var exists = File.Exists(full);
        var head = Theme.Dim("  ── ") + (exists ? Theme.Muted("磁盘当前状态 → 将变为") : Theme.Muted("新文件")) + Theme.Dim(" ──");
        var body = new List<string>();

        if (name == "file_edit" && exists && !string.IsNullOrEmpty(oldText))
        {
            var disk = File.ReadAllText(full);
            body.AddRange(RenderLineDiff(disk, oldText ?? "", newText ?? ""));
        }
        else
        {
            // Whole-file preview (new file, or file_write overwrite).
            var lines = (newText ?? "").Replace("\r\n", "\n").Split('\n').ToList();
            const int cap = 40;
            if (lines.Count > cap)
            {
                body.AddRange(lines.Take(cap).Select(ln => "  " + Theme.Success("+ ") + ln));
                body.Add(Theme.Dim($"  … (+{lines.Count - cap} 行)"));
            }
            else
            {
                body.AddRange(lines.Select(ln => "  " + Theme.Success("+ ") + ln));
            }
            if (exists) body.Insert(0, Theme.Dim($"  (将覆盖现有文件，{lines.Count} 行)"));
        }

        var outLines = new List<string> { head };
        outLines.AddRange(body);
        return outLines;
    }

    /// <summary>Line-level diff: show the old_string region with `-` and the
    /// new_string region with `+`, plus a little surrounding context.</summary>
    private static List<string> RenderLineDiff(string disk, string oldText, string newText)
    {
        var diskLines = disk.Replace("\r\n", "\n").Split('\n');
        var oldLines = (oldText ?? "").Replace("\r\n", "\n").Split('\n');
        var newLines = (newText ?? "").Replace("\r\n", "\n").Split('\n');

        // Locate oldText's first line in the file (best-effort).
        int start = -1;
        for (int i = 0; i + oldLines.Length <= diskLines.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < oldLines.Length; j++)
                if (diskLines[i + j] != oldLines[j]) { match = false; break; }
            if (match) { start = i; break; }
        }

        var outLines = new List<string>();
        int ctx = 2;
        int a = start < 0 ? 0 : Math.Max(0, start - ctx);
        int b = start < 0 ? diskLines.Length : Math.Min(diskLines.Length, start + oldLines.Length + ctx);
        for (int i = a; i < b; i++)
            outLines.Add(Theme.Error("  - ") + diskLines[i]);
        foreach (var ln in newLines)
            outLines.Add(Theme.Success("  + ") + ln);

        const int cap = 40;
        if (outLines.Count > cap)
            outLines = outLines.Take(ctx + 2).Concat(new[] { Theme.Dim($"  … (+{outLines.Count - cap} 行)") }).ToList();
        return outLines;
    }
}
