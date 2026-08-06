// Coder CLI (.NET 11 port) — UI: theme, banner, streaming renderer, prompts.
// Ported from src/ui/* (theme.ts, banner.ts, renderer.ts, prompts.ts).

using System.Text;
using Coder.Core;
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
        Ink Brand, Ink BrandAlt, Ink Accent, Ink Muted, Ink Error, Ink Success, Ink Warn);

    // Dark terminals: the full gold ramp can breathe.
    private static readonly Palette DarkPalette = new(
        Brand:    new(255, 209,  74, 220, "33"),   // #FFD14A  bright gold
        BrandAlt: new(201, 162,  39, 178, "33"),   // #C9A227  classic gold
        Accent:   new(233, 202, 141, 223, "33"),   // #E9CA8D  champagne
        Muted:    new(128, 122, 106, 245, "90"),   // warm grey, not blue-grey
        Error:    new(226,  98,  86, 203, "31"),
        Success:  new(154, 191, 106, 149, "32"),
        Warn:     new(240, 158,  61, 214, "33"));  // amber — distinct from gold

    // Light terminals: same hues, dropped in luminance so they stay legible on white.
    private static readonly Palette LightPalette = new(
        Brand:    new(140, 102,   0, 136, "33"),
        BrandAlt: new(122,  90,  12, 130, "33"),
        Accent:   new(150, 112,  24, 136, "33"),
        Muted:    new(117, 117, 117, 244, "90"),
        Error:    new(176,  35,  24, 160, "31"),
        Success:  new( 56, 118,  44,  28, "32"),
        Warn:     new(166,  86,   0, 130, "33"));

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
    public static string Muted(string s) => Paint(_p.Muted, s);
    public static string Error(string s) => Paint(_p.Error, s, bold: true);
    public static string Success(string s) => Paint(_p.Success, s, bold: true);
    public static string Warn(string s) => Paint(_p.Warn, s, bold: true);

    /// <summary>
    /// Sweeps the gold ramp across a string, brightest in the middle — the wordmark
    /// catches light like brushed metal. Falls back to flat brand gold when the
    /// terminal can't do truecolor.
    /// </summary>
    public static string Gradient(string text)
    {
        if (!Enabled || Depth != ColorDepth.TrueColor || text.Length == 0) return Brand(text);

        var (from, to) = (_p.BrandAlt, _p.Brand);
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
    private readonly object _spinLock = new();
    private CancellationTokenSource? _spinCts;
    private Thread? _spinThread;

    public StreamRenderer() { }

    public void BeginTurn() { _inTurn = true; }
    public void EndTurn() { if (_inTurn) { Console.Out.Write("\n"); _inTurn = false; } }

    // Streaming feeds this one delta at a time; the non-streaming path replays
    // the whole message through the same hook, so output is identical either way.
    public void OnTextDelta(string text) { Console.Out.Write(text); }

    // The model's reasoning is shown live as a dim, quiet stream — it was
    // previously discarded entirely. In interactive mode it scrolls like a
    // thought; in redirected mode it is still emitted so logs capture it.
    public void OnThinkingDelta(string text) { Console.Out.Write(Theme.Dim(text)); }

    public void OnToolStart(ToolInvocation inv, ToolDefinition def, string? describe)
    {
        var (tier, label) = RiskLevel.Assess(def, inv);
        var icon = inv.Name == "bash" ? "❯" : "⛯";
        Console.Out.Write(Theme.Brand(icon + " ") + Theme.Bold(inv.Name) + " " +
                          RiskLevel.Badge(tier, label));
        if (!string.IsNullOrEmpty(describe)) Console.Out.Write(Theme.Dim($"  {describe}"));
        Console.Out.Write("\n");

        // Long-running tools would otherwise freeze the UI with no feedback.
        // Spin only on a real TTY, and only after a beat so fast tools stay quiet.
        if (!Terminal.IsInteractive()) return;
        _spinCts = new CancellationTokenSource();
        var token = _spinCts.Token;
        var started = DateTime.UtcNow;
        _spinThread = new Thread(() =>
        {
            try
            {
                Thread.Sleep(800);
                if (token.IsCancellationRequested) return;
                var frames = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
                int i = 0;
                while (!token.IsCancellationRequested)
                {
                    var elapsed = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                    Console.Out.Write($"\r{Theme.Brand("⠋"[0].ToString())}{frames[i % frames.Length]} {Theme.Dim($"{elapsed}ms")}   \r");
                    Console.Out.Flush();
                    Thread.Sleep(90);
                    i++;
                }
            }
            catch { /* thread aborted mid-flush — harmless */ }
        });
        _spinThread.IsBackground = true;
        _spinThread.Start();
    }

    public void OnToolEnd(ToolInvocation inv, ToolResult result)
    {
        // Stop and fully join the spinner so its last frame is gone before we print.
        if (_spinThread != null)
        {
            try { _spinCts?.Cancel(); _spinThread.Join(400); } catch { }
            _spinThread = null; _spinCts = null;
            // Clear the spinner line.
            Console.Out.Write("\r" + new string(' ', Math.Max(0, Terminal.Cols() - 1)) + "\r");
        }

        var mark = result.Ok ? Theme.Success("✓") : Theme.Error("✗");
        var tail = result.Ok
            ? Theme.Dim($"{result.ElapsedMs}ms")
            : Theme.Error(TextFmt.TruncateVisible(result.Error ?? "failed", 60));
        Console.Out.Write($"  {mark} {tail}\n");

        // Surface a short slice of the tool's output so a tool call is self-explanatory.
        if (result.Ok && !string.IsNullOrWhiteSpace(result.Output))
        {
            var first = result.Output.Replace("\r\n", "\n").Split('\n', 2)[0];
            var preview = TextFmt.TruncateVisible(first, Math.Max(20, Terminal.Cols() - 6));
            if (preview.Length > 0) Console.Out.Write("    " + Theme.Dim(preview) + "\n");
        }
    }

    public void OnTodoUpdate(List<Dictionary<string, object?>> items)
    {
        Console.Out.Write(Theme.Bold("Todos:") + "\n");
        foreach (var it in items)
        {
            var status = it.TryGetValue("status", out var s) ? s?.ToString() : "?";
            var content = it.TryGetValue("content", out var c) ? c?.ToString() : "";
            Console.Out.Write($"  [{status}] {content}\n");
        }
    }

    public void OnIteration(int idx) { }

    public static void PrintError(string msg) => Console.Error.WriteLine(Theme.Error("✗ " + msg));
    public static void PrintInfo(string msg) => Console.Out.WriteLine(Theme.Info("ℹ " + msg));
}

public static class Prompter
{
    /// <summary>
    /// Minimal blocking choice prompt used for permission dialogs and the
    /// ask-user tool. Returns the selected option index.
    /// </summary>
    public static int Choose(string prompt, List<string> options, int defaultIndex = 0)
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
