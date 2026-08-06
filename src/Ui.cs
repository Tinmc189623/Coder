// Coder CLI (.NET 11 port) — UI: theme, banner, streaming renderer, prompts.
// Ported from src/ui/* (theme.ts, banner.ts, renderer.ts, prompts.ts).

using System.Text;
using Coder.Core;
using Coder.Localization;
using Coder.Types;

namespace Coder.Ui;

/// <summary>
/// Gold terminal palette.
///
/// A single ramp of golds carries the brand (bright gold → classic gold → champagne);
/// semantic inks (error / success / warn) stay clearly distinguishable from it —
/// warn is pushed to amber so it never reads as "brand".
///
/// Every ink degrades on its own: truecolor → xterm-256 → basic 16-color, and the
/// whole palette honors NO_COLOR / CLICOLOR=0 / TERM=dumb.
/// </summary>
public static class Theme
{
    private const string ESC = "\u001b[";
    private const string RESET = "\u001b[0m";

    public enum ColorDepth { None, Basic, Ansi256, TrueColor }

    /// <summary>An ink knows how to render itself at every supported color depth.</summary>
    private readonly record struct Ink(byte R, byte G, byte B, int Xterm, string Basic);

    private sealed record Palette(
        Ink Brand, Ink BrandAlt, Ink Accent, Ink Amber, Ink Muted,
        Ink Error, Ink Success, Ink Warn,
        Ink CodeKeyword, Ink CodeString, Ink CodeNumber);

    // Dark terminals: the full gold ramp can breathe.
    private static readonly Palette DarkPalette = new(
        Brand:       new(175, 135, 255, 141, "95"),  // #AF87FF 电光紫 — xterm 141 精确命中
        BrandAlt:    new(175,  95, 255, 135, "95"),  // #AF5FFF
        Accent:      new(  0, 215, 255,  45, "96"),  // #00D7FF 青蓝 — xterm 45 精确命中
        Amber:       new(255, 175,   0, 214, "93"),  // #FFAF00 琥珀 — xterm 214 精确命中
        Muted:       new(138, 132, 152, 245, "90"),
        Error:       new(255,  95,  95, 203, "91"),
        Success:     new( 95, 215, 135,  78, "92"),
        Warn:        new(255, 175,   0, 214, "93"),  // warn = amber(品牌色,不再推远)
        CodeKeyword: new(215, 135, 255, 177, "95"),  // 亮紫,区别于 Brand 正文紫
        CodeString:  new(135, 215, 135, 114, "92"),  // 绿,区别于 Success 的✓
        CodeNumber:  new(255, 135,  95, 209, "93"));

    // Light terminals: same hues, dropped in luminance so they stay legible on white.
    private static readonly Palette LightPalette = new(
        Brand:       new( 95,  95, 215,  62, "35"),  // #5F5FD7
        BrandAlt:    new(135, 135, 215, 104, "35"),  // #8787D7
        Accent:      new(  0, 135, 135,  30, "36"),  // #008787
        Amber:       new(175,  95,   0, 130, "33"),  // #AF5F00
        Muted:       new(108, 106, 118, 242, "90"),
        Error:       new(176,  35,  24, 160, "31"),
        Success:     new( 46, 125,  50,  28, "32"),
        Warn:        new(175,  95,   0, 130, "33"),
        CodeKeyword: new( 95,   0, 175,  56, "35"),  // #5F00AF
        CodeString:  new(  0, 135,  95,  29, "32"),  // #00875F
        CodeNumber:  new(215,  95,   0, 166, "33")); // #D75F00

    private static Palette _p = DarkPalette;

    public static ColorDepth Depth { get; set; } = DetectDepth();
    public static bool Enabled { get; set; } = DetectDepth() != ColorDepth.None;

    /// <summary>Picks the gold ramp tuned for a light or dark background.</summary>
    public static void Apply(ThemeMode mode)
        => _p = Resolve(mode) == ThemeMode.Light ? LightPalette : DarkPalette;

    private static ThemeMode Resolve(ThemeMode mode)
    {
        if (mode != ThemeMode.Auto) return mode;
        // COLORFGBG is "<fg>;<bg>" — a high background index means a light terminal.
        var fgbg = Environment.GetEnvironmentVariable("COLORFGBG");
        if (!string.IsNullOrEmpty(fgbg))
        {
            var parts = fgbg.Split(';');
            if (parts.Length >= 2 && int.TryParse(parts[^1], out var bg) && bg >= 7)
                return ThemeMode.Light;
        }
        return ThemeMode.Dark;
    }

    /// <summary>Detects the terminal color depth from the environment (NO_COLOR, TERM, COLORTERM, …). Public for unit testing.</summary>
    public static ColorDepth DetectDepth()
    {
        static string Env(string k) => Environment.GetEnvironmentVariable(k) ?? "";

        if (Env("NO_COLOR").Length > 0) return ColorDepth.None;
        if (Env("CLICOLOR") == "0") return ColorDepth.None;

        var term = Env("TERM");
        if (term == "dumb") return ColorDepth.None;

        var colorTerm = Env("COLORTERM");
        if (colorTerm.Contains("truecolor", StringComparison.OrdinalIgnoreCase)
            || colorTerm.Contains("24bit", StringComparison.OrdinalIgnoreCase))
            return ColorDepth.TrueColor;

        // Windows Terminal and the VS Code terminal both speak truecolor.
        if (Env("WT_SESSION").Length > 0 || Env("TERM_PROGRAM") == "vscode")
            return ColorDepth.TrueColor;

        if (term.Contains("256color", StringComparison.Ordinal)) return ColorDepth.Ansi256;
        if (OperatingSystem.IsWindows()) return ColorDepth.Ansi256;
        return term.Length > 0 ? ColorDepth.Basic : ColorDepth.Ansi256;
    }

    private static string Sgr(Ink ink, bool bold)
    {
        var b = bold ? "1;" : "";
        return Depth switch
        {
            ColorDepth.TrueColor => $"{b}38;2;{ink.R};{ink.G};{ink.B}",
            ColorDepth.Ansi256 => $"{b}38;5;{ink.Xterm}",
            _ => b + ink.Basic,
        };
    }

    private static string Wrap(string code, string text)
        => Enabled && Depth != ColorDepth.None ? $"{ESC}{code}m{text}{RESET}" : text;

    private static string Paint(Ink ink, string text, bool bold = false) => Wrap(Sgr(ink, bold), text);

    public static string Bold(string s) => Wrap("1", s);
    public static string Dim(string s) => Wrap("2", s);
    public static string Brand(string s) => Paint(_p.Brand, s, bold: true);
    public static string BrandSecondary(string s) => Paint(_p.BrandAlt, s);
    public static string Info(string s) => Paint(_p.Accent, s, bold: true);
    public static string Amber(string s) => Paint(_p.Amber, s, bold: true);
    public static string Muted(string s) => Paint(_p.Muted, s);
    public static string Error(string s) => Paint(_p.Error, s, bold: true);
    public static string Success(string s) => Paint(_p.Success, s, bold: true);
    public static string Warn(string s) => Paint(_p.Warn, s, bold: true);

    // 代码语法 token 色(与语义色分开命名空间)。
    public static string CodeKeyword(string s) => Paint(_p.CodeKeyword, s);
    public static string CodeString(string s) => Paint(_p.CodeString, s);
    public static string CodeNumber(string s) => Paint(_p.CodeNumber, s);

    /// <summary>
    /// Sweeps the gold ramp across a string, brightest in the middle — the wordmark
    /// catches light like brushed metal. Falls back to flat brand gold when the
    /// terminal can't do truecolor.
    /// </summary>
    public static string Gradient(string text)
    {
        if (!Enabled || Depth != ColorDepth.TrueColor || text.Length == 0) return Brand(text);

        var (from, to) = (_p.BrandAlt, _p.Accent);   // 紫 → 青扫过
        var sb = new StringBuilder();
        var span = Math.Max(text.Length - 1, 1);

        for (var i = 0; i < text.Length; i++)
        {
            // sin() puts the highlight mid-word instead of at one edge.
            var k = Math.Sin((double)i / span * Math.PI);
            sb.Append($"{ESC}1;38;2;{Mix(from.R, to.R, k)};{Mix(from.G, to.G, k)};{Mix(from.B, to.B, k)}m")
              .Append(text[i]);
        }

        return sb.Append(RESET).ToString();
    }

    private static byte Mix(byte a, byte b, double t)
        => (byte)Math.Round(a + (b - a) * Math.Clamp(t, 0, 1));
}

public static class Brand
{
    public const string Glyph = "◆";

    /// <summary>程序集发布版本。单一来源为 Coder.csproj 的 &lt;Version&gt;，运行时从元数据读取，禁止硬编码。</summary>
    public static string Version { get; } = ReadInformationalVersion();

    // 从 AssemblyInformationalVersion 读取版本字符串；
    // SourceLink/GitInfo 可能附加 +hash 后缀，只保留主版本段。
    private static string ReadInformationalVersion()
    {
        var attr = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(Brand).Assembly);
        var iv = attr?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(iv)) return "0.0.0";
        var plus = iv.IndexOf('+');
        return plus > 0 ? iv[..plus] : iv;
    }
}

public static class Banner
{
    public static string Render(CoderConfig cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Theme.Gradient($"{Brand.Glyph} Coder CLI") + "  " + Theme.Dim($"v{Brand.Version}  ·  {cfg.Provider}/{cfg.Model}"));
        sb.AppendLine(Theme.Dim("production-grade AI coding agent for the terminal"));
        sb.AppendLine();
        return sb.ToString();
    }
}

public sealed class StreamRenderer : IAgentHooks
{
    private bool _inTurn;
    private readonly TurnStateMachine _state = new();
    private readonly StreamingMarkdown _md = new();
    private readonly StringBuilder _plainBuf = new();   // 非交互路径的文本缓冲
    private Func<StatusModel>? _statusSource;
    private readonly List<ToolCard> _cards = new();
    private bool _inThinking;
    private bool _thinkingPrefixPending;
    private static readonly string[] SpinnerFrames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

    public StreamRenderer() { }

    /// <summary>注入状态行数据源(REPL/一次性路径各设一次)。</summary>
    public void SetStatusSource(Func<StatusModel>? source) => _statusSource = source;

    public void BeginTurn()
    {
        _inTurn = true;
        _state.BeginTurn();
        _plainBuf.Clear();
        _md.Reset();
        _inThinking = false;
        _cards.Clear();
        Screen.Instance.StartTurn(() =>
        {
            var sb = new StringBuilder();
            // 工具卡片区(运行中的卡片闪烁 + 耗时实时)。
            var frame = SpinnerFrames[(int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond / 100) % SpinnerFrames.Length];
            foreach (var card in _cards)
            {
                if (card.Phase == CardPhase.Done || card.Phase == CardPhase.Failed) continue;  // 已提交
                sb.Append(card.Header(frame)).Append('\n');
            }
            var tail = _md.RenderTail();
            if (tail.Length > 0) sb.Append(tail).Append('\n');
            var status = _statusSource == null ? "" : StatusBar.Compose(_statusSource());
            if (status.Length > 0) sb.Append(status);
            return sb.ToString().TrimEnd('\n');
        });
    }

    public void EndTurn(bool aborted = false)
    {
        if (!_inTurn) return;
        _inTurn = false;
        _state.EndTurn(aborted);
        // 非交互:flush 缓冲 + 换行;交互:提交残留尾行后结束。
        if (!Terminal.IsInteractive())
        {
            if (_plainBuf.Length > 0) Console.Out.Write(_plainBuf.ToString());
            Console.Out.Write("\n");
        }
        else
        {
            var tail = _md.RenderTail();
            if (tail.Length > 0) Screen.Instance.Emit(tail);
        }
        _md.Reset();
        Screen.Instance.EndTurn();
    }

    // Streaming feeds this one delta at a time; the non-streaming path replays
    // the whole message through the same hook, so output is identical either way.
    public void OnTextDelta(string text)
    {
        _state.OnTextDelta(text);
        if (_inThinking)
        {
            // thinking → 正文切换:提交 thinking 残留,正文接管 live block。
            var tail = _md.RenderTail();
            if (tail.Length > 0) Screen.Instance.Emit(tail);
            _md.Reset();
            _inThinking = false;
        }
        if (!Terminal.IsInteractive()) { _plainBuf.Append(text); return; }
        _md.Append(text, out var committed);
        foreach (var line in committed) Screen.Instance.Emit(line);
    }

    // The model's reasoning is shown live as a dim, quiet stream — it was
    // previously discarded entirely. In interactive mode it scrolls like a
    // thought; in redirected mode it is still emitted so logs capture it.
    public void OnThinkingDelta(string text)
    {
        _state.OnThinkingDelta(text);
        if (!Terminal.IsInteractive()) { _plainBuf.Append(text); return; }
        if (!_inThinking)
        {
            // 正文 → thinking 切换:提交正文残留。
            var tail = _md.RenderTail();
            if (tail.Length > 0) Screen.Instance.Emit(tail);
            _md.Reset();
            _inThinking = true;
            _thinkingPrefixPending = true;
        }
        // 暗淡 + ▍ 前缀与正文区分;仅首个片段带前缀。
        var prefix = _thinkingPrefixPending ? "▍ " : "";
        _thinkingPrefixPending = false;
        Screen.Instance.Emit(prefix + Theme.Dim(text));
    }

    public void OnToolStart(ToolInvocation inv, ToolDefinition def, string? describe)
    {
        _state.OnToolStart();
        // 非交互:保持历史文本行格式(管道/日志友好)。
        if (!Terminal.IsInteractive())
        {
            var (tier, label) = RiskLevel.Assess(def, inv);
            var icon = inv.Name == "bash" ? "❯" : "⛯";
            var line = Theme.Brand(icon + " ") + Theme.Bold(inv.Name) + " " +
                       RiskLevel.Badge(tier, label);
            if (!string.IsNullOrEmpty(describe)) line += Theme.Dim($"  {describe}");
            Console.Out.WriteLine(line);
            return;
        }
        var (t, l) = RiskLevel.Assess(def, inv);
        var card = new ToolCard
        {
            Inv = inv,
            Def = def,
            Describe = describe,
            Tier = t,
            RiskLabel = l,
            Phase = CardPhase.Running,
            StartedMs = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds,
        };
        _cards.Add(card);
        Screen.Instance.MarkDirty();
    }

    public void OnToolEnd(ToolInvocation inv, ToolResult result)
    {
        // 非交互:保持历史文本行格式。
        if (!Terminal.IsInteractive())
        {
            var mark = result.Ok ? Theme.Success("✓") : Theme.Error("✗");
            var tail = result.Ok
                ? Theme.Dim($"{result.ElapsedMs}ms")
                : Theme.Error(TextFmt.TruncateVisible(result.Error ?? "failed", 60));
            Console.Out.WriteLine($"  {mark} {tail}");
            if (result.Ok && !string.IsNullOrWhiteSpace(result.Output))
            {
                var first = result.Output.Replace("\r\n", "\n").Split('\n', 2)[0];
                var preview = TextFmt.TruncateVisible(first, Math.Max(20, Terminal.Cols() - 6));
                if (preview.Length > 0) Console.Out.WriteLine("    " + Theme.Dim(preview));
            }
            return;
        }
        var card = _cards.LastOrDefault(c => c.Inv.Id == inv.Id);
        if (card == null) return;
        card.Phase = result.Ok ? CardPhase.Done : CardPhase.Failed;
        card.Result = result;
        card.EndedMs = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;

        // 终态:整体提交卡片(状态行 + 输出盒)进 scrollback。
        var sb = new StringBuilder();
        sb.Append(card.Header()).Append('\n');
        var cols = Math.Max(20, Terminal.Cols() - 4);
        foreach (var line in card.Body(cols))
            sb.Append("  ").Append(TextFmt.TruncateVisible(line, cols)).Append('\n');
        Screen.Instance.EmitBlock(sb.ToString().TrimEnd('\n'));
        _cards.Remove(card);
    }

    public void OnTodoUpdate(List<Dictionary<string, object?>> items)
    {
        Screen.Instance.Emit(Theme.Bold(I18n.T("todos.title")));
        foreach (var it in items)
        {
            var status = it.TryGetValue("status", out var s) ? s?.ToString() : "?";
            var content = it.TryGetValue("content", out var c) ? c?.ToString() : "";
            var (glyph, paint) = TodoIcons.For(status);
            var line = Theme.Enabled ? $"  {paint(glyph)} {Markdown.Inline(content ?? "")}"
                                     : $"  [{status}] {content}";
            Screen.Instance.Emit(line);
        }
    }

    public void OnIteration(int idx) => _state.OnIteration(idx);

    public static void PrintError(string msg) => Console.Error.WriteLine(Theme.Error("✗ " + msg));
    public static void PrintInfo(string msg) => Console.Out.WriteLine(Theme.Info("ℹ " + msg));
}

public static class Prompter
{
    /// <summary>
    /// Blocking choice prompt with arrow-key navigation (Enter confirm,
    /// Esc default, digits select). Non-interactive falls back to the
    /// legacy numbered ReadLine path.
    /// </summary>
    public static int Choose(string prompt, List<string> options, int defaultIndex = 0,
        Func<ConsoleKeyInfo>? keySource = null)
    {
        if (!Terminal.IsInteractive())
            return ChoosePlain(prompt, options, defaultIndex);

        keySource ??= () => Console.ReadKey(true);
        var sel = Math.Clamp(defaultIndex, 0, options.Count - 1);
        RenderMenu(prompt, options, sel, keySource);
        while (true)
        {
            var key = keySource();
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    sel = Math.Max(0, sel - 1);
                    RenderMenu(prompt, options, sel, keySource);
                    break;
                case ConsoleKey.DownArrow:
                    sel = Math.Min(options.Count - 1, sel + 1);
                    RenderMenu(prompt, options, sel, keySource);
                    break;
                case ConsoleKey.Enter:
                    return sel;
                case ConsoleKey.Escape:
                    return defaultIndex;
                case ConsoleKey.D1:
                    if (options.Count >= 1) return 0;
                    break;
                case ConsoleKey.D2:
                    if (options.Count >= 2) return 1;
                    break;
                case ConsoleKey.D3:
                    if (options.Count >= 3) return 2;
                    break;
            }
        }
    }

    private static int _lastMenuLines;

    private static void RenderMenu(string prompt, List<string> options, int sel, Func<ConsoleKeyInfo> keySource)
    {
        // 重绘:擦掉上一帧菜单行数。
        if (_lastMenuLines > 0)
        {
            Console.Out.Write($"[{Math.Min(_lastMenuLines, Terminal.Rows() - 2)}A");
            for (var i = 0; i < _lastMenuLines - 1; i++) Console.Out.Write("[2K[1B");
            Console.Out.Write("[2K\r");
        }
        var sb = new StringBuilder();
        sb.Append(Theme.Bold(prompt)).Append('\n');
        for (var i = 0; i < options.Count; i++)
            sb.Append(i == sel ? Theme.Brand("▶ ") + Theme.Bold(options[i])
                               : "   " + options[i]).Append('\n');
        sb.Append(Theme.Dim("↑↓ 选择 · Enter 确认 · Esc 默认")).Append('\n');
        Console.Out.Write(sb.ToString());
        Console.Out.Flush();
        _lastMenuLines = options.Count + 2;
    }

    /// <summary>非交互回退:编号 + ReadLine。</summary>
    private static int ChoosePlain(string prompt, List<string> options, int defaultIndex)
    {
        Console.Out.Write(Theme.Bold(prompt) + "\n");
        for (int i = 0; i < options.Count; i++)
            Console.Out.Write($"  {Theme.Info((i + 1).ToString())}. {options[i]}\n");
        Console.Out.Write(Theme.Dim($"Select [1-{options.Count}, default {defaultIndex + 1}]: "));
        var line = Console.ReadLine();
        if (int.TryParse(line, out var n) && n >= 1 && n <= options.Count) return n - 1;
        return defaultIndex;
    }

    /// <summary>Yes/no confirmation prompt.</summary>
    public static bool Confirm(string prompt, bool defaultValue = false)
    {
        var hint = defaultValue ? "Y/n" : "y/N";
        Console.Out.Write(Theme.Bold(prompt) + Theme.Dim($" [{hint}]: "));
        var line = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(line)) return defaultValue;
        return line is "y" or "yes" or "1" or "true";
    }

    /// <summary>Free-text prompt.</summary>
    public static string Ask(string prompt, string defaultValue = "")
    {
        Console.Out.Write(Theme.Bold(prompt) + (defaultValue.Length > 0 ? Theme.Dim($" [{defaultValue}]") : "") + Theme.Dim(": "));
        var line = Console.ReadLine();
        return string.IsNullOrWhiteSpace(line) ? defaultValue : line!.Trim();
    }
}
