// Coder CLI (.NET 11 port) — line-level syntax highlighting for code fences.
// Tokenizes a single line: keywords / strings / numbers / comments. No
// cross-line state (multi-line strings or /* */ comments span lines untracked —
// documented limitation). Strings win over keywords inside quotes.

using System.Text;
using System.Text.RegularExpressions;
using Coder.Ui;

namespace Coder.Ui;

public static partial class Syntax
{
    // 常见语言关键字集合(覆盖主流编程语言的通用子集)。
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        // C-family & general
        "if", "else", "for", "while", "do", "switch", "case", "break", "continue", "return",
        "void", "int", "float", "double", "char", "bool", "long", "short", "unsigned", "signed",
        "class", "struct", "interface", "enum", "namespace", "using", "public", "private",
        "protected", "internal", "static", "const", "readonly", "virtual", "override", "abstract",
        "sealed", "new", "this", "base", "null", "true", "false", "try", "catch", "finally", "throw",
        "async", "await", "var", "dynamic", "ref", "out", "in", "is", "as", "typeof", "sizeof",
        "default", "delegate", "event", "extern", "get", "set", "init", "record", "partial",
        // Python
        "def", "lambda", "import", "from", "with", "yield", "global", "nonlocal", "pass", "elif", "raise",
        "and", "or", "not", "None", "True", "False", "self", "cls",
        // JS/TS
        "function", "const", "let", "export", "import", "extends", "super", "typeof", "instanceof",
        "undefined", "NaN", "this",
        // Shell
        "echo", "then", "fi", "esac", "done", "local",
    };

    // 单行注释起始符(按语言;`//` 与 `#` 覆盖主流)。
    private static readonly Regex CommentStart = new(@"//|#", RegexOptions.Compiled);

    // 关键字/数字 token(数字要求前后非字母数字,避免 v2 / int64 误判)。
    private static readonly Regex WordToken = new(@"[A-Za-z_][A-Za-z0-9_]*|\d[\d.]*", RegexOptions.Compiled);

    /// <summary>按行渲染语法高亮。空行/纯空白原样返回。</summary>
    public static string Highlight(string line, string lang)
    {
        if (string.IsNullOrWhiteSpace(line)) return line;
        var sb = new StringBuilder(line.Length + 32);
        int i = 0;

        while (i < line.Length)
        {
            var c = line[i];

            // 注释:直接染到行尾。
            if (CommentStart.IsMatch(line.Substring(i, Math.Min(2, line.Length - i))))
            {
                sb.Append(Theme.Muted(line[i..]));
                break;
            }

            // 字符串:'...' 或 "..."。
            if (c is '"' or '\'')
            {
                var end = line.IndexOf(c, i + 1);
                var len = end < 0 ? line.Length - i : end - i + 1;
                sb.Append(Theme.CodeString(line.Substring(i, len)));
                i += len;
                continue;
            }

            // 关键字/数字。
            var m = WordToken.Match(line, i);
            if (m.Success && m.Index == i)
            {
                var tok = m.Value;
                if (Keywords.Contains(tok))
                    sb.Append(Theme.CodeKeyword(tok));
                else if (char.IsDigit(tok[0]))
                    sb.Append(Theme.CodeNumber(tok));
                else
                    sb.Append(tok);
                i += tok.Length;
                continue;
            }

            // 普通字符。
            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
