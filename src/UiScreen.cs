// Coder CLI (.NET 11 port) — terminal screen coordination.
// Single-writer live-block rendering: committed output scrolls (Emit),
// a small repaintable block (tail line + tool cards + status bar) redraws
// in place (Repaint) via a 100ms ticker. Everything interactive-only;
// piped output bypasses the live block entirely.

using System.Text;
using Coder.Localization;
using Coder.Types;

namespace Coder.Ui;

/// <summary>
/// 单一写者屏幕协调器。回合内 stdout 的一切写入都经过它:
/// Emit = 擦 live → 写提交内容 → 恢复 live;Repaint = 擦 live → 重画 compose 内容。
/// </summary>
public sealed class Screen
{
    public static Screen Instance { get; } = new();

    // 可注入的写者与宽度提供器(单测用)。
    public TextWriter Writer { get; set; } = Console.Out;
    public Func<int> ColsProvider { get; set; } = Terminal.Cols;
    public Func<int> RowsProvider { get; set; } = Terminal.Rows;

    private readonly object _lock = new();
    private readonly List<string> _live = new();
    private Thread? _ticker;
    private volatile bool _running;
    private Func<string>? _compose;
    private bool _enabled;

    public bool Enabled => _enabled;

    /// <summary>回合开始:启动 ticker,进入 live-block 模式。非交互直接返回(直写模式)。</summary>
    public void StartTurn(Func<string> compose)
    {
        if (!Terminal.IsInteractive())
        {
            _enabled = false;
            return;
        }
        lock (_lock)
        {
            _enabled = true;
            _compose = compose;
            _live.Clear();
        }
        _running = true;
        _ticker = new Thread(TickerLoop) { IsBackground = true };
        _ticker.Start();
    }

    /// <summary>回合结束:停 ticker,提交 final 内容,清 live。非交互时 final 直接写出。</summary>
    public void EndTurn(string? final = null)
    {
        _running = false;
        if (_ticker != null) { try { _ticker.Join(300); } catch { } _ticker = null; }
        if (final != null) Emit(final);
        lock (_lock) { _enabled = false; _compose = null; _live.Clear(); }
    }

    /// <summary>主线程主动重绘(按键/事件/内容变化)。</summary>
    public void MarkDirty() => Repaint();

    private void TickerLoop()
    {
        while (_running)
        {
            Thread.Sleep(100);
            try { Repaint(); } catch { /* terminal closed mid-frame */ }
        }
    }

    /// <summary>擦 live → 重画 compose 内容。</summary>
    public void Repaint()
    {
        if (!_enabled) return;
        lock (_lock)
        {
            Erase();
            var text = _compose?.Invoke() ?? "";
            if (text.Length > 0)
            {
                var lines = WrapBlock(text);
                foreach (var l in lines) Writer.Write(l + "\n");
                _live.Clear();
                _live.AddRange(lines);
            }
            Writer.Flush();
        }
    }

    /// <summary>提交输出:擦 live → 写提交内容 → 恢复 live。非交互时直写。</summary>
    public void Emit(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (!_enabled)
        {
            Writer.Write(text);
            Writer.Flush();
            return;
        }
        lock (_lock)
        {
            Erase();
            Writer.Write(text);
            if (!text.EndsWith("\n")) Writer.Write("\n");
            var lines = _live.Count > 0 ? _live : new List<string>();
            if (_compose != null)
            {
                var composed = _compose.Invoke();
                if (composed.Length > 0)
                {
                    lines = WrapBlock(composed);
                    foreach (var l in lines) Writer.Write(l + "\n");
                }
            }
            _live.Clear();
            _live.AddRange(lines);
            Writer.Flush();
        }
    }

    /// <summary>整块提交(不重绘 live,直接写在 live 之前)——用于工具卡片终态等。</summary>
    public void EmitBlock(string text)
    {
        if (string.IsNullOrEmpty(text)) { Emit(text); return; }
        if (!_enabled) { Writer.Write(text + "\n"); Writer.Flush(); return; }
        lock (_lock)
        {
            Erase();
            Writer.Write(text);
            if (!text.EndsWith("\n")) Writer.Write("\n");
            foreach (var l in _live) Writer.Write(l + "\n");
            Writer.Flush();
        }
    }

    /// <summary>擦除 live block(视口钳制:滚动出视口的部分放弃擦除)。</summary>
    private void Erase()
    {
        if (_live.Count == 0) return;
        var n = Math.Min(_live.Count, Math.Max(0, RowsProvider() - 2));
        if (n <= 0) { _live.Clear(); return; }
        Writer.Write($"[{n}A");
        for (var i = 0; i < n - 1; i++) Writer.Write("[2K[1B");
        Writer.Write("[2K\r");
        _live.Clear();
    }

    private List<string> WrapBlock(string text)
    {
        var cols = Math.Max(20, ColsProvider());
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            foreach (var wrapped in TextFmt.Wrap(raw, cols))
                lines.Add(wrapped);
        return lines;
    }
}

// ── 流式 markdown ───────────────────────────────────────────────────────────
/// <summary>
/// 增量 markdown 解析:完整行按块状态渲染提交,只有未闭合尾行/表格区留在 live block。
/// fence 行语法高亮;表格整块缓存(列宽依赖)闭合时提交;不平衡行滞留尾行。
/// </summary>
public sealed class StreamingMarkdown
{
    private readonly StringBuilder _tail = new();
    private readonly List<string> _pendingTable = new();
    private bool _inFence;
    private string _fenceLang = "";
    private bool _thinkingMode;

    /// <summary>追加 delta;产出已闭合的完整行(渲染后)供调用方提交。</summary>
    public void Append(string delta, out List<string> committed)
    {
        committed = new List<string>();
        _tail.Append(delta);
        var text = _tail.ToString();
        int nl;
        while ((nl = text.IndexOf('\n')) >= 0)
        {
            var raw = text[..nl];
            text = text[(nl + 1)..];
            if (!_inFence && Markdown.IsTableRow(raw))
            {
                _pendingTable.Add(raw);
                if (_pendingTable.Count >= 20)   // 表格护栏:超限强制提交
                {
                    committed.Add(Markdown.RenderTable(_pendingTable));
                    _pendingTable.Clear();
                }
                continue;
            }
            if (_pendingTable.Count > 0)
            {
                committed.Add(Markdown.RenderTable(_pendingTable));
                _pendingTable.Clear();
            }
            if (!_inFence && raw.TrimStart().StartsWith("```"))
            {
                _fenceLang = System.Text.RegularExpressions.Regex.Match(raw.TrimStart(), @"^```([a-zA-Z0-9_+-]*)").Groups[1].Value;
                _inFence = true;
                committed.Add(Theme.Dim(_fenceLang.Length > 0 ? $"```{_fenceLang}" : "```"));
                continue;
            }
            if (_inFence && raw.TrimStart().StartsWith("```"))
            {
                _inFence = false;
                committed.Add(Theme.Dim("```"));
                continue;
            }
            committed.Add(_inFence ? Syntax.Highlight(raw, _fenceLang) : Markdown.RenderLine(raw));
        }
        _tail.Clear();
        _tail.Append(text);
        if (_tail.Length > 2000)   // 容量护栏:恶意长行强制提交
        {
            committed.Add(_tail.ToString());
            _tail.Clear();
        }
    }

    /// <summary>渲染未闭合尾行(live block 内容)。</summary>
    public string RenderTail()
    {
        if (_tail.Length == 0 && _pendingTable.Count == 0) return "";
        var sb = new StringBuilder();
        if (_pendingTable.Count > 0) sb.Append(Markdown.RenderTable(_pendingTable)).Append('\n');
        if (_tail.Length > 0)
            sb.Append(_inFence ? Syntax.Highlight(_tail.ToString(), _fenceLang) : Markdown.RenderLine(_tail.ToString()));
        return sb.ToString();
    }

    /// <summary>thinking 模式:暗淡渲染(正文接管时由调用方切换)。</summary>
    public void SetThinking(bool on) => _thinkingMode = on;

    public void Reset()
    {
        _tail.Clear();
        _pendingTable.Clear();
        _inFence = false;
        _fenceLang = "";
        _thinkingMode = false;
    }
}

// ── 回合状态机 ──────────────────────────────────────────────────────────────
public enum Phase { Idle, Requesting, Streaming, Thinking, ToolRun, Done, Interrupted }

/// <summary>回合状态机:驱动点全部来自现有 IAgentHooks 时序,Agent 层零改动。</summary>
public sealed class TurnStateMachine
{
    public Phase Phase { get; private set; } = Phase.Idle;
    public int Iteration { get; private set; }
    public long TurnOutputChars { get; private set; }

    public void BeginTurn()
    {
        Phase = Phase.Requesting;
        Iteration = 0;
        TurnOutputChars = 0;
    }

    public void OnIteration(int i)
    {
        Iteration = i;
        Phase = Phase.Requesting;
    }

    public void OnTextDelta(string s)
    {
        TurnOutputChars += s.Length;
        Phase = Phase.Streaming;
    }

    public void OnThinkingDelta(string s)
    {
        TurnOutputChars += s.Length;
        Phase = Phase.Thinking;
    }

    public void OnToolStart() => Phase = Phase.ToolRun;

    public void EndTurn(bool aborted = false) => Phase = aborted ? Phase.Interrupted : Phase.Done;
}

// ── 状态行 ──────────────────────────────────────────────────────────────────
public sealed class StatusModel
{
    public Phase Phase;
    public int ContextUsed;
    public int ContextTotal;
    public PermissionMode Mode;
    public int InputTokens;
    public int OutputTokens;
    public int Turn;
    public string Model = "";
    public string Cwd = "";
}

public static class StatusBar
{
    /// <summary>渲染状态栏(两行):上 = 模型/相位/目录 左对齐 + 权限模式右对齐;
    /// 下 = token 用量左对齐 + context 用量右对齐。窄终端整行截断。</summary>
    public static string Compose(StatusModel m, Func<string>? phaseGlyph = null)
    {
        var cols = Math.Max(40, Terminal.Cols() - 1);
        var glyph = phaseGlyph?.Invoke() ?? PhaseGlyph(m.Phase);
        var label = PhaseLabel(m.Phase);

        var left1 = $"{Theme.Bold(m.Model)} {glyph} {label}  {Theme.Dim(m.Cwd)}";
        var left2 = Theme.Dim($"{FormatK(m.InputTokens)}↑ {FormatK(m.OutputTokens)}↓") +
                    Theme.Dim($"  turn {m.Turn}");
        return PadBoth(left1, ModeText(m.Mode), cols) + "\n" +
               PadBoth(left2, ContextText(m.ContextUsed, m.ContextTotal), cols);
    }

    /// <summary>左右两端对齐成一行,过宽时截断并保留右侧。</summary>
    private static string PadBoth(string left, string right, int width)
    {
        var lw = TextFmt.DisplayWidth(left);
        var rw = TextFmt.DisplayWidth(right);
        if (lw + rw >= width) return TextFmt.TruncateVisible(left + " " + right, width);
        return left + new string(' ', width - lw - rw) + right;
    }

    private static string PhaseGlyph(Phase p) => p switch
    {
        Phase.Requesting => Theme.Brand("⠙"),
        Phase.Streaming => Theme.Info("▌"),
        Phase.Thinking => Theme.Dim("⠋"),
        Phase.ToolRun => Theme.Amber("⚙"),
        Phase.Done => Theme.Success("✓"),
        Phase.Interrupted => Theme.Error("✗"),
        _ => Theme.Muted("·"),
    };

    private static string PhaseLabel(Phase p) => p switch
    {
        Phase.Requesting => Theme.Muted(I18n.T("status.phase.requesting")),
        Phase.Streaming => Theme.Info(I18n.T("status.phase.streaming")),
        Phase.Thinking => Theme.Dim(I18n.T("status.phase.thinking")),
        Phase.ToolRun => Theme.Amber(I18n.T("status.phase.toolRun")),
        Phase.Done => Theme.Success(I18n.T("status.phase.done")),
        Phase.Interrupted => Theme.Error(I18n.T("status.phase.interrupted")),
        _ => Theme.Muted(I18n.T("status.phase.idle")),
    };

    private static string ContextText(int used, int total)
    {
        if (total <= 0) return Theme.Muted("context: ?");
        var pct = (double)used / total;
        var text = $"context: {pct * 100:0}% ({FormatK(used)}/{FormatK(total)})";
        return pct > 0.9 ? Theme.Error(text) : pct > 0.7 ? Theme.Warn(text) : Theme.Dim(text);
    }

    private static string ModeText(PermissionMode m) => m switch
    {
        PermissionMode.BypassPermissions => Theme.Error($"mode:{m.ToString().ToLowerInvariant()}"),
        PermissionMode.Plan => Theme.Warn("mode:plan"),
        PermissionMode.AcceptEdits => Theme.Success("mode:acceptEdits"),
        _ => Theme.Muted("mode:default"),
    };

    private static string FormatK(int n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 1000 => $"{n / 1000.0:0.#}k",
        _ => n.ToString(),
    };
}
