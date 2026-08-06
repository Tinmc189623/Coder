// Coder CLI (.NET 11 port) — error types. Ported from src/utils/errors.ts.

namespace Coder.Utils;

public class CoderError : Exception
{
    public string Code { get; }
    public CoderError(string message) : base(message) => Code = "CODER_ERROR";
    public CoderError(string code, string message) : base(message) => Code = code;
}

public class ConfigError : CoderError
{
    public ConfigError(string message) : base("CONFIG_ERROR", message) { }
}

public class ProviderError : CoderError
{
    public ProviderError(string message) : base("PROVIDER_ERROR", message) { }
}

public class ToolNotFoundError : CoderError
{
    public ToolNotFoundError(string name) : base("TOOL_NOT_FOUND", $"Tool not found: {name}") { }
}

public class PermissionDeniedError : CoderError
{
    public PermissionDeniedError(string message) : base("PERMISSION_DENIED", message) { }
}

public class AbortError : Exception
{
    public AbortError() : base("Aborted") { Code = "ABORTED"; }
    public string Code { get; } = "ABORTED";
}
