// Coder CLI (.NET 11 port) — event hooks.
// Five events, defined in settings.toml [hooks] (array-of-tables):
//   PreToolUse / PostToolUse / UserPromptSubmit / SessionStart / Stop.
// Each hook spawns a process; stdout follows the Claude Code hook JSON protocol.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Coder.Config;
using Coder.Types;
using Coder.Utils;

namespace Coder.Core;

/// <summary>单条 hook 定义,来自 settings.toml 的 [[hooks.X]]。</summary>
public sealed record HookSpec(
    string Event,
    string Command,
    string? Matcher,
    int TimeoutSeconds,
    bool Blocked,
    string Source);

/// <summary>PreToolUse 判定结果:拒绝或改参。</summary>
public sealed record PreToolUseDecision(
    bool Blocked,
    string? Reason = null,
    Dictionary<string, object?>? InputOverride = null);

/// <summary>UserPromptSubmit 判定结果:拦截或追加上下文。</summary>
public sealed record PromptDecision(
    bool Block,
    string? Reason = null,
    string? AdditionalContext = null);

/// <summary>进程执行委托;测试注入假实现避免真实 spawn。</summary>
public delegate Task<(int ExitCode, string Stdout)> SpawnFn(HookSpec spec, Dictionary<string, string> env);

/// <summary>
/// 五事件 hook 管理器。构造时把 HookSpec 分派到对应事件列表;
/// 执行时逐个 spawn,协议与 Claude Code hooks 一致。
/// </summary>
public sealed class HookManager
{
    private readonly List<HookSpec> _pre = new();
    private readonly List<HookSpec> _post = new();
    private readonly List<HookSpec> _prompt = new();
    private readonly List<HookSpec> _session = new();
    private readonly List<HookSpec> _stop = new();
    private readonly SpawnFn _spawn;
    private readonly string _cwd;

    public HookManager(List<HookSpec> all, string cwd, SpawnFn? spawn = null)
    {
        _cwd = cwd;
        _spawn = spawn ?? SpawnProcessAsync;
        foreach (var h in all)
        {
            (h.Event switch
            {
                "PreToolUse" => _pre,
                "PostToolUse" => _post,
                "UserPromptSubmit" => _prompt,
                "SessionStart" => _session,
                _ => _stop,
            }).Add(h);
        }
    }

    /// <summary>从配置层读取 [hooks](跳过 cli 层),构建管理器。用户层在前、项目层在后。</summary>
    public static HookManager FromLayers(List<ConfigLayer> layers, string cwd, SpawnFn? spawn = null)
    {
        var all = new List<HookSpec>();
        foreach (var layer in layers)
        {
            if (layer.Source == "cli") continue;
            if (!layer.Data.TryGetValue("hooks", out var hooks) || hooks is not Dictionary<string, object?> hookDict) continue;
            foreach (var (eventName, entries) in hookDict)
            {
                // TOML [[hooks.X]] 深转为 List<Dictionary<...>>。
                if (entries is not List<object?> list) continue;
                foreach (var entry in list)
                {
                    if (entry is not Dictionary<string, object?> e) continue;
                    var command = e.TryGetValue("command", out var c) ? c?.ToString() : null;
                    if (string.IsNullOrWhiteSpace(command))
                    {
                        Log.Instance.Warn("hook missing command:", eventName);
                        continue;
                    }
                    var matcher = e.TryGetValue("matcher", out var m) ? m?.ToString() : null;
                    int timeout = 60;
                    if (e.TryGetValue("timeout", out var t) && t is long tl && tl > 0) timeout = (int)Math.Min(tl, int.MaxValue);
                    bool blocked = e.TryGetValue("blocked", out var b) && (b is bool bb ? bb : b?.ToString() == "true");
                    all.Add(new HookSpec(eventName, command!, string.IsNullOrWhiteSpace(matcher) ? null : matcher,
                        timeout, blocked, layer.Source));
                }
            }
        }
        return new HookManager(all, cwd, spawn);
    }

    // ── 五个事件 ─────────────────────────────────────────────────────────

    /// <summary>工具调用前。首个 deny 即停;args 覆盖取首个返回的 hook;其余放行。</summary>
    public async Task<PreToolUseDecision> PreToolUseAsync(ToolInvocation inv)
    {
        foreach (var h in _pre)
        {
            if (h.Blocked) return new PreToolUseDecision(true, "Blocked by hook configuration");
            if (h.Matcher != null && !System.Text.RegularExpressions.Regex.IsMatch(inv.Name, h.Matcher)) continue;
            var env = new Dictionary<string, string>
            {
                ["CODER_HOOK_EVENT"] = "PreToolUse",
                ["CODER_HOOK_CWD"] = _cwd,
                ["CODER_HOOK_TOOL_NAME"] = inv.Name,
                ["CODER_HOOK_TOOL_INPUT"] = JsonSerializer.Serialize(inv.Input),
                ["CODER_HOOK_TOOL_ID"] = inv.Id,
            };
            var (exit, stdout) = await _spawn(h, env);
            if (exit == 2)
                return new PreToolUseDecision(true, FirstLine(stdout));
            var decision = ParsePreJson(stdout);
            if (decision != null) return decision;
        }
        return new PreToolUseDecision(false);
    }

    /// <summary>工具调用后(含被拒绝的调用),无判定,仅记录输出。</summary>
    public async Task PostToolUseAsync(ToolInvocation inv, ToolResult result)
    {
        foreach (var h in _post)
        {
            if (h.Matcher != null && !System.Text.RegularExpressions.Regex.IsMatch(inv.Name, h.Matcher)) continue;
            var env = new Dictionary<string, string>
            {
                ["CODER_HOOK_EVENT"] = "PostToolUse",
                ["CODER_HOOK_CWD"] = _cwd,
                ["CODER_HOOK_TOOL_NAME"] = inv.Name,
                ["CODER_HOOK_TOOL_INPUT"] = JsonSerializer.Serialize(inv.Input),
                ["CODER_HOOK_TOOL_ID"] = inv.Id,
                ["CODER_HOOK_TOOL_RESULT"] = result.Ok ? "ok" : "error",
            };
            var (exit, stdout) = await _spawn(h, env);
            if (exit != 0 || !string.IsNullOrWhiteSpace(stdout)) Log.Instance.Debug($"hook post: {inv.Name}");
        }
    }

    /// <summary>用户输入提交前。block 拦截本轮;additionalContext 追加到 prompt 末尾。</summary>
    public async Task<PromptDecision> UserPromptSubmitAsync(string prompt)
    {
        foreach (var h in _prompt)
        {
            if (h.Matcher != null && !System.Text.RegularExpressions.Regex.IsMatch(prompt, h.Matcher)) continue;
            var env = new Dictionary<string, string>
            {
                ["CODER_HOOK_EVENT"] = "UserPromptSubmit",
                ["CODER_HOOK_CWD"] = _cwd,
                ["CODER_HOOK_PROMPT"] = prompt,
            };
            var (_, stdout) = await _spawn(h, env);
            var block = ParseBlock(stdout);
            if (block != null && block.Value.block)
                return new PromptDecision(true, block.Value.reason);
            var ctx = ParseAdditionalContext(stdout);
            if (!string.IsNullOrEmpty(ctx)) return new PromptDecision(false, AdditionalContext: ctx);
        }
        return new PromptDecision(false);
    }

    /// <summary>会话开始;stdout(截断 4000)作为附加上下文。</summary>
    public async Task<string> SessionStartAsync()
    {
        var sb = new StringBuilder();
        foreach (var h in _session)
        {
            var env = new Dictionary<string, string>
            {
                ["CODER_HOOK_EVENT"] = "SessionStart",
                ["CODER_HOOK_CWD"] = _cwd,
            };
            var (_, stdout) = await _spawn(h, env);
            if (!string.IsNullOrWhiteSpace(stdout))
                sb.AppendLine(stdout.Trim());
        }
        var text = sb.ToString().Trim();
        return text.Length > 4000 ? text[..4000] : text;
    }

    /// <summary>会话结束清理;fire 后等待各自 timeout。</summary>
    public async Task StopAsync()
    {
        foreach (var h in _stop)
        {
            var env = new Dictionary<string, string>
            {
                ["CODER_HOOK_EVENT"] = "Stop",
                ["CODER_HOOK_CWD"] = _cwd,
            };
            await _spawn(h, env);
        }
    }

    // ── 协议解析 ──────────────────────────────────────────────────────────

    private static PreToolUseDecision? ParsePreJson(string stdout)
    {
        var line = FirstLine(stdout);
        if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("permissionDecision", out var pd)
                && pd.ValueKind == JsonValueKind.String && pd.GetString() == "deny")
            {
                var reason = root.TryGetProperty("permissionDecisionReason", out var r)
                    ? r.GetString() : null;
                return new PreToolUseDecision(true, string.IsNullOrEmpty(reason) ? "Blocked by hook" : reason);
            }
            if (root.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Object)
            {
                // JsonElement 展开为纯 CLR 值(string/long/bool/嵌套字典),与配置深转一致。
                var dict = JsonElementToClr(args) as Dictionary<string, object?>;
                if (dict != null) return new PreToolUseDecision(false, InputOverride: dict);
            }
        }
        catch { /* 非 JSON 输出按放行处理 */ }
        return null;
    }

    private static (bool block, string? reason)? ParseBlock(string stdout)
    {
        var line = FirstLine(stdout);
        if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("block", out var b) && b.ValueKind == JsonValueKind.True)
            {
                var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
                return (true, string.IsNullOrEmpty(reason) ? "Blocked by hook" : reason);
            }
        }
        catch { }
        return null;
    }

    private static string? ParseAdditionalContext(string stdout)
    {
        var line = FirstLine(stdout);
        if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("additionalContext", out var ctx)
                && ctx.ValueKind == JsonValueKind.String)
                return ctx.GetString();
        }
        catch { }
        return null;
    }

    private static string FirstLine(string s)
    {
        var nl = s.IndexOf('\n');
        return nl >= 0 ? s[..nl].Trim() : s.Trim();
    }

    /// <summary>JsonElement → 纯 CLR 值(字符串/整数/布尔/数组/字典递归)。</summary>
    private static object? JsonElementToClr(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number when el.TryGetInt64(out var l) => l,
        JsonValueKind.Number => el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToClr).ToList(),
        JsonValueKind.Object => el.EnumerateObject()
            .ToDictionary(p => p.Name, p => JsonElementToClr(p.Value), StringComparer.Ordinal),
        _ => el.GetRawText(),
    };

    // ── 进程执行 ──────────────────────────────────────────────────────────

    /// <summary>默认实现:与 bash 工具同款 spawn 模式,捕获 stdout+stderr,超时杀进程树。</summary>
    private static async Task<(int, string)> SpawnProcessAsync(HookSpec spec, Dictionary<string, string> env)
    {
        var isWin = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWin ? "cmd.exe" : "/bin/sh",
            Arguments = isWin ? $"/c \"{spec.Command}\"" : $"-c \"{spec.Command}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var (k, v) in env) psi.Environment[k] = v;
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("hook spawn failed");
        // 先起读取任务再等待,避免管道满死锁。
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(spec.TimeoutSeconds));
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            Log.Instance.Warn("hook timeout:", spec.Command);
            return (1, "");
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (proc.ExitCode != 0) Log.Instance.Warn($"hook exit={proc.ExitCode}:", spec.Command);
        var combined = stdout + (stderr.Length > 0 ? "\n" + stderr : "");
        return (proc.ExitCode, combined.Length > 64_000 ? combined[..64_000] : combined);
    }
}
