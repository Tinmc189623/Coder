// Coder CLI (.NET 11 port) — main agent loop.
// Drives the conversation: turn → stream → tool calls → execute → re-stream → ... → final text.
// Ported from src/core/agent.ts.

using Coder.Providers;
using Coder.Tools;
using Coder.Types;
using Coder.Utils;

namespace Coder.Core;

public interface IAgentHooks
{
    void OnTextDelta(string text);
    void OnThinkingDelta(string text);
    void OnToolStart(ToolInvocation inv, ToolDefinition def, string? describe);
    void OnToolEnd(ToolInvocation inv, ToolResult result);
    void OnTodoUpdate(List<Dictionary<string, object?>> items);
    void OnIteration(int idx);
}

public sealed class AgentLoop
{
    private List<Message> _messages = new();
    private CoderConfig _cfg;
    private IProvider? _provider;
    private readonly ToolRegistry _registry;
    private readonly PermissionEngine _permEngine;
    private readonly HookManager? _hookManager;
    private IAgentHooks _hooks;
    private bool _aborted;
    private string _systemText = "";
    private string _sessionContext = "";
    private TokenUsage _totalUsage = new(0, 0);

    public AgentLoop(CoderConfig cfg, IProvider? provider, ToolRegistry registry, PermissionEngine permEngine, IAgentHooks hooks,
        string? systemPrompt = null, string? sessionContext = null, HookManager? hookManager = null)
    {
        _cfg = cfg;
        _provider = provider;
        _registry = registry;
        _permEngine = permEngine;
        _hooks = hooks;
        _hookManager = hookManager;
        if (systemPrompt != null) _systemText = systemPrompt;
        if (sessionContext != null) _sessionContext = sessionContext;
    }

    public void SetSystemPrompt(string text) => _systemText = text;
    public void SetSessionContext(string text) => _sessionContext = text;
    public void SetProvider(IProvider? provider) => _provider = provider;
    public void SetConfig(CoderConfig cfg) => _cfg = cfg;
    public void SetMessages(List<Message> messages) => _messages = messages;
    public List<Message> GetMessages() => new(_messages);
    public TokenUsage GetUsage() => _totalUsage;
    public void SetHooks(IAgentHooks hooks) => _hooks = hooks;
    public void Abort() => _aborted = true;

    public async Task<(string finalText, int iterations)> Run(string userInput, string? sessionContext = null, CancellationToken signal = default)
    {
        if (_provider == null) throw new CoderError("NO_API_KEY", "API key not configured");
        _aborted = false;
        if (sessionContext != null) _sessionContext = sessionContext;
        PushUserMessage(userInput);
        int iterations = 0;

        Action onAbort = () => { _aborted = true; };
        if (signal.CanBeCanceled) signal.Register(onAbort);

        try
        {
            while (iterations < _cfg.MaxIterations && !_aborted)
            {
                _hooks.OnIteration(iterations);
                var turn = await RunOneTurn(signal);
                iterations++;
                if (!turn.hasToolUse) return (turn.finalText, iterations);
            }
            if (_aborted) throw new AbortError();
            return ("(max iterations reached)", iterations);
        }
        finally
        {
            // nothing to unregister in this port
        }
    }

    private void PushUserMessage(string input)
    {
        var content = string.IsNullOrEmpty(_sessionContext)
            ? input
            : $"<session>\n{_sessionContext}\n</session>\n\n{input}";
        _messages.Add(new Message(Role.User, content));
    }

    private async Task<(bool hasToolUse, string finalText)> RunOneTurn(CancellationToken signal)
    {
        if (_provider == null) throw new CoderError("NO_API_KEY", "API key not configured");
        if (_aborted || signal.IsCancellationRequested) throw new AbortError();

        var req = new CompletionRequest(
            Model: _cfg.Model,
            Messages: _messages,
            System: _systemText,
            Tools: _registry.List(),
            ToolChoice: new ToolChoiceAutoImpl(),
            MaxTokens: _cfg.MaxTokens,
            Temperature: _cfg.Temperature,
            Signal: signal);

        var text = "";
        var toolCalls = new Dictionary<string, ToolInvocation>();
        var toolInputBuffers = new Dictionary<string, string>();

        CompletionResult result;
        if (!_cfg.StreamOutput)
        {
            // Non-streaming turn: request the whole completion, then replay it
            // through the same hooks so rendering stays identical.
            result = await _provider.CompleteAsync(req);
            foreach (var block in result.Content)
            {
                switch (block)
                {
                    case AssistantText t:
                        text += t.Text;
                        _hooks.OnTextDelta(t.Text);
                        break;
                    case AssistantThinking th:
                        _hooks.OnThinkingDelta(th.Text);
                        break;
                    case AssistantToolUse tu:
                        toolCalls[tu.Id] = new ToolInvocation(tu.Id, tu.Name, tu.Input);
                        break;
                }
            }
        }
        else
        {
        result = await _provider.StreamAsync(req, ev =>
        {
            if (_aborted || signal.IsCancellationRequested) return;
            switch (ev)
            {
                case StreamEventText e: text += e.Text; _hooks.OnTextDelta(e.Text); break;
                case StreamEventThinking e: _hooks.OnThinkingDelta(e.Text); break;
                case StreamEventToolUseStart e:
                    toolCalls[e.Id] = new ToolInvocation(e.Id, e.Name, new());
                    toolInputBuffers[e.Id] = "";
                    break;
                case StreamEventToolUseDelta e:
                    toolInputBuffers[e.Id] = (toolInputBuffers.TryGetValue(e.Id, out var b) ? b : "") + e.InputDelta;
                    break;
                case StreamEventToolUseEnd e:
                    if (toolCalls.ContainsKey(e.Id)) toolCalls[e.Id] = toolCalls[e.Id] with { Input = e.Input };
                    break;
                case StreamEventError e:
                    Log.Instance.Error("Provider stream error:", e.Error);
                    break;
            }
        });

            // Safety net: a provider may surface tool calls only in the final
            // result instead of as stream events. Never drop them silently.
            foreach (var block in result.Content)
            {
                if (block is AssistantToolUse tu && !string.IsNullOrEmpty(tu.Id) && !toolCalls.ContainsKey(tu.Id))
                    toolCalls[tu.Id] = new ToolInvocation(tu.Id, tu.Name, tu.Input);
            }
        }

        // Finalize any tool inputs still buffered.
        foreach (var id in toolCalls.Keys.ToList())
        {
            var inv = toolCalls[id];
            if (inv.Input.Count == 0 && toolInputBuffers.TryGetValue(id, out var buf))
            {
                Dictionary<string, object?> newInput;
                try { newInput = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(buf) ?? new(); }
                catch { newInput = new() { ["_raw"] = buf }; }
                toolCalls[id] = inv with { Input = newInput };
            }
        }
        Log.Instance.Debug($"TURN_PARSED textLen={text.Length} tools={toolCalls.Count}");

        if (result.Usage != null)
        {
            _totalUsage = new TokenUsage(
                _totalUsage.InputTokens + result.Usage.InputTokens,
                _totalUsage.OutputTokens + result.Usage.OutputTokens,
                _totalUsage.CacheReadTokens + result.Usage.CacheReadTokens,
                _totalUsage.CacheWriteTokens + result.Usage.CacheWriteTokens);
        }

        if (_aborted || signal.IsCancellationRequested) throw new AbortError();

        _messages.Add(Messages.AssistantMessage(result.Content));

        if (toolCalls.Count == 0) return (false, text);

        var results = new List<ToolResult>();
        foreach (var inv0 in toolCalls.Values)
        {
            if (_aborted || signal.IsCancellationRequested) throw new AbortError();
            var inv = inv0;
            ToolResult r;

            // PreToolUse hook:先于权限引擎。deny → 直接构造失败结果,模型可见原因;args → 覆盖输入。
            var pre = _hookManager == null ? null : await _hookManager.PreToolUseAsync(inv);
            if (pre != null && pre.Blocked)
            {
                r = new ToolResult(inv.Id, false, pre.Reason ?? "Blocked by hook", Error: "HOOK_BLOCKED");
                Log.Instance.Warn($"HOOK_PRE_BLOCK tool={inv.Name} reason={pre.Reason}");
            }
            else
            {
                if (pre?.InputOverride != null) inv = inv with { Input = pre.InputOverride };
                r = await InvokeToolAsync(inv, signal);
            }

            // PostToolUse hook:被拒绝的调用不再触发。
            if (_hookManager != null && r.Error != "HOOK_BLOCKED")
                await _hookManager.PostToolUseAsync(inv, r);

            results.Add(r);
            _hooks.OnToolEnd(inv, r);

            if (inv.Name == "todo_write" && inv.Input.TryGetValue("items", out var items) && items is List<object?> list)
            {
                var dicts = list.OfType<Dictionary<string, object?>>().ToList();
                _hooks.OnTodoUpdate(dicts);
            }
        }

        _messages.Add(Messages.ToolResultsToUserMessage(results));
        return (true, text);
    }

    /// <summary>执行单个工具调用(权限检查 + handler + 异常转 ToolResult)。</summary>
    private async Task<ToolResult> InvokeToolAsync(ToolInvocation inv, CancellationToken signal)
    {
        try
        {
            Log.Instance.Debug($"TOOL_START name={inv.Name} input={System.Text.Json.JsonSerializer.Serialize(inv.Input)}");
            var r = await _registry.InvokeAsync(inv, new ToolContext
            {
                Cwd = _cfg.Cwd,
                UserConfigDir = _cfg.UserConfigDir,
                RequestPermission = (td, ti) => _permEngine.Check(td, ti),
                OnProgress = m => Log.Instance.Debug($"[{inv.Name}] {m}"),
                OnExecuting = () => _hooks.OnToolStart(inv, _registry.Get(inv.Name).Def, DescribeTool(inv)),
            });
            Log.Instance.Debug($"TOOL_END name={inv.Name} ok={r.Ok} out={r.Output[..Math.Min(80, r.Output.Length)]}");
            return r;
        }
        catch (Exception e) when (e is PermissionDeniedError pde)
        {
            return new ToolResult(inv.Id, false, pde.Message, Error: pde.Message);
        }
        catch (Exception e)
        {
            return new ToolResult(inv.Id, false, $"Error: {e.Message}", Error: e.Message);
        }
    }

    private string? DescribeTool(ToolInvocation inv)
    {
        try { return _registry.Get(inv.Name).Describe?.Invoke(inv.Input); }
        catch { return null; }
    }
}
