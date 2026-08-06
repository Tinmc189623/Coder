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
                            ClearLine(prompt, sb.Length, cursor);
                            sb.Clear(); sb.Append(completed); cursor = sb.Length;
                            Console.Out.Write(sb.ToString());
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
                    if (cursor > 0) { sb.Remove(cursor - 1, 1); cursor--; Refresh(prompt, sb, cursor); }
                    continue;
                }
                if (key.Key == ConsoleKey.LeftArrow) { if (cursor > 0) { cursor--; Console.Out.Write("\x1b[D"); } continue; }
                if (key.Key == ConsoleKey.RightArrow) { if (cursor < sb.Length) { cursor++; Console.Out.Write("\x1b[C"); } continue; }
                if (key.Key == ConsoleKey.Home) { cursor = 0; Console.Out.Write("\x1b[H"); continue; }
                if (key.Key == ConsoleKey.End) { cursor = sb.Length; Console.Out.Write("\x1b[F"); continue; }
                if (key.Key == ConsoleKey.Delete)
                {
                    if (cursor < sb.Length) { sb.Remove(cursor, 1); Refresh(prompt, sb, cursor); }
                    continue;
                }
                if (key.KeyChar == '\u0000' || key.KeyChar == '\u0003') { cancelled = true; break; } // Ctrl+C already handled by handler; ignore NUL
                if (!char.IsControl(key.KeyChar))
                {
                    sb.Insert(cursor, key.KeyChar);
                    cursor++;
                    Refresh(prompt, sb, cursor);
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
    private static void Refresh(string prompt, StringBuilder sb, int cursor)
    {
        // Redraw the whole current line from the prompt.
        ClearLine(prompt, sb.Length, cursor);
        Console.Out.Write(sb.ToString());
        // Move cursor back to its logical position.
        var back = sb.Length - cursor;
        if (back > 0) Console.Out.Write($"\x1b[{back}D");
    }

    private static void ClearLine(string prompt, int len, int cursor)
    {
        var back = len - cursor;
        if (back > 0) Console.Out.Write($"\x1b[{back}D");
        Console.Out.Write("\x1b[K");
    }

    private static void ReplaceLine(string prompt, StringBuilder old, int oldCursor, string replacement, ref StringBuilder sb, ref int cursor)
    {
        ClearLine(prompt, old.Length, oldCursor);
        sb.Clear(); sb.Append(replacement); cursor = sb.Length;
        Console.Out.Write(sb.ToString());
    }
}
