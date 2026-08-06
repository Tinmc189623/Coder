// Coder CLI (.NET 11 port) — a small interactive line editor.
// Replaces the bare Console.ReadLine used by the REPL with something that
// earns its place: multi-line input (auto-continuation on unbalanced
// brackets / trailing backtick), "/" slash-command completion, and
// up/down history recall. Falls back to plain ReadLine when there is no TTY.

using System.Text;

namespace Coder.Ui;

public static class LineInput
{
    private static readonly List<string> History = new();

    /// <summary>Read a (possibly multi-line) prompt from the user.</summary>
    /// <param name="prompt">Rendered prompt prefix, e.g. "◆ › ".</param>
    /// <param name="commandNames">Slash-command names for Tab completion.</param>
    /// <returns>The joined input, or null on EOF / cancel.</returns>
    public static string? Read(string prompt, List<string> commandNames)
    {
        if (!Terminal.IsInteractive())
            return ReadPlain();

        var lines = new List<string>();
        var historyIdx = History.Count;   // points "past the end" = fresh line

        for (int n = 0; ; n++)
        {
            Console.Out.Write(prompt);
            if (n > 0) Console.Out.Write(Theme.Dim("… ") + Theme.Dim($"(line {n + 1}, empty line to finish)") + "\n" + prompt);

            var sb = new StringBuilder();
            var cursor = 0;
            bool done = false, cancelled = false;

            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    // Empty line ends a multi-line block; on the first line it ends the turn.
                    if (sb.Length == 0) { done = true; break; }
                    if (NeedsContinuation(sb.ToString())) { Console.Out.Write('\n'); break; }
                    done = true; break;
                }
                if (key.Key == ConsoleKey.Escape) { cancelled = true; break; }
                if (key.Key == ConsoleKey.Tab)
                {
                    if (n == 0 && sb.ToString().StartsWith('/'))
                    {
                        var completed = Complete(sb.ToString(), commandNames);
                        if (completed != null)
                        {
                            sb.Clear(); sb.Append(completed); cursor = sb.Length;
                            Redraw(prompt, sb, cursor);
                        }
                    }
                    continue;
                }
                if (key.Key == ConsoleKey.UpArrow)
                {
                    if (History.Count == 0) continue;
                    historyIdx = Math.Max(0, historyIdx - 1);
                    ReplaceLine(prompt, sb, cursor, History[historyIdx], ref sb, ref cursor);
                    continue;
                }
                if (key.Key == ConsoleKey.DownArrow)
                {
                    if (historyIdx >= History.Count - 1) { historyIdx = History.Count; ReplaceLine(prompt, sb, cursor, "", ref sb, ref cursor); }
                    else { historyIdx++; ReplaceLine(prompt, sb, cursor, History[historyIdx], ref sb, ref cursor); }
                    continue;
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (cursor > 0) { sb.Remove(cursor - 1, 1); cursor--; Redraw(prompt, sb, cursor); }
                    continue;
                }
                if (key.Key == ConsoleKey.LeftArrow) { if (cursor > 0) { cursor--; Console.Out.Write("\x1b[D"); } continue; }
                if (key.Key == ConsoleKey.RightArrow) { if (cursor < sb.Length) { cursor++; Console.Out.Write("\x1b[C"); } continue; }
                if (key.Key == ConsoleKey.Home) { cursor = 0; Console.Out.Write("\x1b[H"); continue; }
                if (key.Key == ConsoleKey.End) { cursor = sb.Length; Console.Out.Write("\x1b[F"); continue; }
                if (key.Key == ConsoleKey.Delete)
                {
                    if (cursor < sb.Length) { sb.Remove(cursor, 1); Redraw(prompt, sb, cursor); }
                    continue;
                }
                if (key.KeyChar == '\u0000' || key.KeyChar == '\u0003') { cancelled = true; break; } // Ctrl+C already handled by handler; ignore NUL
                if (!char.IsControl(key.KeyChar))
                {
                    sb.Insert(cursor, key.KeyChar);
                    cursor++;
                    Redraw(prompt, sb, cursor);
                }
            }

            if (cancelled) { Console.Out.Write('\n'); return null; }
            if (done)
            {
                var text = sb.ToString();
                if (n == 0 && text.Length == 0) return "";
                lines.Add(text);
                // A block ends when the last (non-empty) line is balanced & not continuing.
                if (n == 0 || !NeedsContinuation(sb.ToString()))
                {
                    var joined = string.Join("\n", lines).TrimEnd();
                    if (joined.Length > 0) { History.Add(joined); if (History.Count > 500) History.RemoveAt(0); }
                    Console.Out.Write('\n');
                    return joined;
                }
            }
        }
    }

    private static string? ReadPlain()
    {
        try { return Console.ReadLine(); }
        catch (IOException) { return null; }
    }

    /// <summary>True when the line should keep going: unbalanced brackets, or a
    /// trailing backtick / opening fence.</summary>
    private static bool NeedsContinuation(string line)
    {
        if (line.EndsWith('`') || line.EndsWith("```")) return true;
        var open = 0;
        foreach (var ch in line)
        {
            if (ch is '(' or '[' or '{') open++;
            else if (ch is ')' or ']' or '}') open--;
        }
        return open > 0;
    }

    private static string? Complete(string text, List<string> names)
    {
        var prefix = text.Substring(1); // drop '/'
        var matches = names.Where(n => n.Length > 1 && n.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return null;
        if (matches.Count == 1) return matches[0];
        // Multiple: complete to the longest common prefix, then list the rest.
        var common = LongestCommonPrefix(matches);
        Console.Out.Write('\n' + Theme.Dim(string.Join("  ", matches)) + '\n');
        return common.Length > text.Length ? common : text;
    }

    private static string LongestCommonPrefix(List<string> s)
    {
        if (s.Count == 0) return "";
        var p = s[0];
        foreach (var x in s.Skip(1))
        {
            int i = 0;
            while (i < p.Length && i < x.Length && p[i] == x[i]) i++;
            p = p[..i];
        }
        return p;
    }

    // ── cursor helpers ───────────────────────────────────────────────────────
    /// <summary>
    /// 整行重绘:回行首 → 清整行 → 重画 prompt + 内容 → 光标定位回逻辑位置。
    /// 用 \x1b[2K 清整行而非 \x1b[K 清到行尾——插入/删除字符后终端光标不在行尾,
    /// 后者会残留前半段字符导致重复输入(ls → lls)。
    /// </summary>
    private static void Redraw(string prompt, StringBuilder sb, int cursor)
    {
        Console.Out.Write("\r\x1b[2K");
        Console.Out.Write(prompt);
        Console.Out.Write(sb.ToString());
        var back = sb.Length - cursor;
        if (back > 0) Console.Out.Write($"\x1b[{back}D");
        Console.Out.Flush();
    }

    private static void ReplaceLine(string prompt, StringBuilder old, int oldCursor, string replacement, ref StringBuilder sb, ref int cursor)
    {
        sb.Clear(); sb.Append(replacement); cursor = sb.Length;
        Redraw(prompt, sb, cursor);
    }
}
