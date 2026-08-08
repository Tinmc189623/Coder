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

    /// <summary>Read a (possibly multi-line) prompt from the user, drawn inside a
    /// rounded full-width box (Kimi 风格); long content scrolls by its tail.</summary>
    /// <param name="prompt">Rendered prompt prefix, e.g. "&gt; ".</param>
    /// <param name="commandNames">Slash-command names for Tab completion.</param>
    /// <returns>The joined input, or null on EOF / cancel.</returns>
    public static string? Read(string prompt, List<string> commandNames)
    {
        if (!Terminal.IsInteractive())
            return ReadPlain();

        var inner = BoxInnerWidth();
        var lines = new List<string>();
        var historyIdx = History.Count;   // points "past the end" = fresh line

        for (int n = 0; ; n++)
        {
            if (n > 0) Console.Out.Write(Theme.Dim($"… (line {n + 1}, empty line to finish)") + "\n");
            DrawBox(inner, top: true);
            DrawContentLine(prompt, "", 0, inner);

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
                    if (NeedsContinuation(sb.ToString())) { break; }
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
                            Redraw(prompt, sb, cursor, inner);
                        }
                    }
                    continue;
                }
                if (key.Key == ConsoleKey.UpArrow)
                {
                    if (History.Count == 0) continue;
                    historyIdx = Math.Max(0, historyIdx - 1);
                    ReplaceLine(prompt, sb, cursor, History[historyIdx], ref sb, ref cursor, inner);
                    continue;
                }
                if (key.Key == ConsoleKey.DownArrow)
                {
                    if (historyIdx >= History.Count - 1) { historyIdx = History.Count; ReplaceLine(prompt, sb, cursor, "", ref sb, ref cursor, inner); }
                    else { historyIdx++; ReplaceLine(prompt, sb, cursor, History[historyIdx], ref sb, ref cursor, inner); }
                    continue;
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    // 代理对(emoji 等)一次删完整:光标前是高代理+低代理则整个删。
                    if (cursor > 0)
                    {
                        var i = cursor - 1;
                        if (i > 0 && char.IsLowSurrogate(sb[i]) && char.IsHighSurrogate(sb[i - 1])) i--;
                        sb.Remove(i, cursor - i);
                        cursor = i;
                        Redraw(prompt, sb, cursor, inner);
                    }
                    continue;
                }
                if (key.Key == ConsoleKey.LeftArrow)
                {
                    // 按字符显示宽移动:中文/emoji 占 2 列,逐 1 列会停在半字符上。
                    if (cursor > 0)
                    {
                        var i = cursor - 1;
                        if (i > 0 && char.IsLowSurrogate(sb[i]) && char.IsHighSurrogate(sb[i - 1])) i--;
                        Console.Out.Write($"\x1b[{TextFmt.DisplayWidth(sb.ToString(i, cursor - i))}D");
                        cursor = i;
                    }
                    continue;
                }
                if (key.Key == ConsoleKey.RightArrow)
                {
                    if (cursor < sb.Length)
                    {
                        var i = cursor + 1;
                        if (cursor + 1 < sb.Length && char.IsHighSurrogate(sb[cursor]) && char.IsLowSurrogate(sb[cursor + 1])) i++;
                        Console.Out.Write($"\x1b[{TextFmt.DisplayWidth(sb.ToString(cursor, i - cursor))}C");
                        cursor = i;
                    }
                    continue;
                }
                if (key.Key == ConsoleKey.Home)
                {
                    // 光标回输入起点:整行重绘放置。\x1b[H 是屏幕归位不是行首,不能用。
                    cursor = 0;
                    Redraw(prompt, sb, cursor, inner);
                    continue;
                }
                if (key.Key == ConsoleKey.End)
                {
                    cursor = sb.Length;
                    Redraw(prompt, sb, cursor, inner);
                    continue;
                }
                if (key.Key == ConsoleKey.Delete)
                {
                    // 代理对一次删完整(与 Backspace 对称)。
                    if (cursor < sb.Length)
                    {
                        var len = 1;
                        if (char.IsHighSurrogate(sb[cursor]) && cursor + 1 < sb.Length && char.IsLowSurrogate(sb[cursor + 1])) len = 2;
                        sb.Remove(cursor, len);
                        Redraw(prompt, sb, cursor, inner);
                    }
                    continue;
                }
                if (key.KeyChar == '\u0000' || key.KeyChar == '\u0003') { cancelled = true; break; } // Ctrl+C already handled by handler; ignore NUL
                if (!char.IsControl(key.KeyChar))
                {
                    sb.Insert(cursor, key.KeyChar);
                    cursor++;
                    Redraw(prompt, sb, cursor, inner);
                }
            }

            DrawBox(inner, top: false);
            if (cancelled) { Console.Out.Write('\n'); return null; }
            if (done)
            {
                var text = sb.ToString();
                if (n == 0 && text.Length == 0) { Console.Out.Write('\n'); return ""; }
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

    /// <summary>输入框内容宽:整宽减左右边框与缩进,窄终端保底 20。</summary>
    private static int BoxInnerWidth() => Math.Max(20, Terminal.Cols() - 4);

    /// <summary>画输入框横边(上 ╭─╮ / 下 ╰─╯),前面一个空格缩进。</summary>
    private static void DrawBox(int inner, bool top)
    {
        var corner = top ? "╭" : "╰";
        var end = top ? "╮" : "╯";
        Console.Out.Write(" " + Theme.Dim(corner + new string('─', inner) + end) + "\n");
    }

    /// <summary>
    /// 画输入内容行:"│ " + prompt + 内容 + 填充 + " │"。内容超长取尾部滚动,
    /// 截断与光标回退都按列宽计算——中文等宽字符占 2 列,按字符数截会撑破右边框。
    /// </summary>
    private static void DrawContentLine(string prompt, string content, int cursor, int inner)
    {
        var pw = TextFmt.DisplayWidth(prompt);
        var maxLen = Math.Max(1, inner - 2 - pw);           // 内容可视宽(列)
        var (shown, offset) = TailVisible(content, maxLen); // 超长取尾部(列宽感知)
        var visCursor = Math.Clamp(cursor - offset, 0, shown.Length);
        var shownCols = TextFmt.DisplayWidth(shown);
        var pad = maxLen - shownCols;
        var cursorCol = TextFmt.DisplayWidth(shown[..visCursor]);

        Console.Out.Write(Theme.Dim("│ ") + prompt + shown + new string(' ', pad) + Theme.Dim(" │"));
        var back = pad + 1 + (shownCols - cursorCol);       // 光标从行尾回退到逻辑列
        if (back > 0) Console.Out.Write($"\x1b[{back}D");
        Console.Out.Flush();
    }

    /// <summary>取字符串尾部,显示宽不超过 max 列;返回 (子串, 被截掉的头部字符数)。</summary>
    private static (string, int) TailVisible(string s, int max)
    {
        var w = 0;
        var start = s.Length;
        for (var i = s.Length - 1; i >= 0; i--)
        {
            var cw = TextFmt.CharWidth(s[i]);
            if (w + cw > max) break;
            w += cw;
            start = i;
        }
        // 代理对不能被截断:起点若落在低代理(孤立的尾单元),回退补上高代理,否则渲染乱码。
        if (start < s.Length && start > 0 && char.IsLowSurrogate(s[start]) && char.IsHighSurrogate(s[start - 1]))
            start--;
        return (s[start..], start);
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
        // 先清输入行,避免列表与旧输入内容重叠。
        var common = LongestCommonPrefix(matches);
        Console.Out.Write("\r\x1b[2K" + Theme.Dim(string.Join("  ", matches)) + "\n");
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
    /// 整行重绘:回行首 → 清整行 → 重画框行(prompt + 内容 + 填充 + 右边框)。
    /// 用 \x1b[2K 清整行而非 \x1b[K 清到行尾——插入/删除字符后终端光标不在行尾,
    /// 后者会残留前半段字符导致重复输入(ls → lls)。
    /// </summary>
    private static void Redraw(string prompt, StringBuilder sb, int cursor, int inner)
    {
        Console.Out.Write("\r\x1b[2K");
        DrawContentLine(prompt, sb.ToString(), cursor, inner);
    }

    private static void ReplaceLine(string prompt, StringBuilder old, int oldCursor, string replacement, ref StringBuilder sb, ref int cursor, int inner)
    {
        sb.Clear(); sb.Append(replacement); cursor = sb.Length;
        Redraw(prompt, sb, cursor, inner);
    }
}
