// Coder CLI (.NET 11 port) — permission engine. Ported from src/core/permissions.ts.

using Coder.Types;

namespace Coder.Core;

public delegate Task<PermissionDecision> AskUserFn(ToolDefinition tool, ToolInvocation inv);

public sealed class PermissionEngine
{
    private readonly AskUserFn _askUser;
    private readonly Func<bool> _isInteractive;
    private PermissionMode _mode = PermissionMode.Default;

    /// <summary>
    /// <paramref name="isInteractive"/> is injectable so the engine can be unit-tested
    /// deterministically without a real console. Defaults to the live console check.
    /// </summary>
    public PermissionEngine(AskUserFn askUser, Func<bool>? isInteractive = null)
    {
        _askUser = askUser;
        _isInteractive = isInteractive ?? (() =>
        {
            try { return Console.IsInputRedirected == false; }
            catch { return false; }
        });
    }

    public void SetMode(PermissionMode mode) => _mode = mode;

    public async Task<PermissionDecision> Check(ToolDefinition tool, ToolInvocation inv)
    {
        if (_mode == PermissionMode.BypassPermissions)
            return new PermissionDecision("allow", "bypass");
        if (_mode == PermissionMode.Plan && tool.Mutates)
            return new PermissionDecision("deny", "plan mode");
        if (_mode == PermissionMode.AcceptEdits &&
            (inv.Name == "file_edit" || inv.Name == "file_write" || inv.Name == "bash"))
            return new PermissionDecision("allow", "acceptEdits");

        // In non-interactive one-shot mode, default-allow read-only tools.
        if (!_isInteractive())
            return tool.ReadOnly ? new PermissionDecision("allow", "non-interactive read-only") : new PermissionDecision("allow", "non-interactive");
        return await _askUser(tool, inv);
    }
}
