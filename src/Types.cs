// Coder CLI (.NET 11 port) — root type definitions.
// Common types shared across providers, tools, UI, and core loops.
// Ported from src/types.ts.

namespace Coder.Types;

public enum Role
{
    System,
    User,
    Assistant,
    Tool,
}

public enum PermissionMode
{
    Default,
    AcceptEdits,
    BypassPermissions,
    Plan,
}

public enum ThemeMode
{
    Auto,
    Dark,
    Light,
}

public enum EffortLevel
{
    Low,
    Medium,
    High,
    XHigh,
    Max,
}

public readonly record struct ToolChoiceAuto
{
    public string Type => "auto";
}

public interface IToolChoice
{
    string Type { get; }
}

public sealed record ToolChoiceAutoImpl : IToolChoice
{
    public string Type => "auto";
}

public sealed record ToolChoiceAny : IToolChoice
{
    public string Type => "any";
}

public sealed record ToolChoiceNone : IToolChoice
{
    public string Type => "none";
}

public sealed record ToolChoiceTool(string Name) : IToolChoice
{
    public string Type => "tool";
}

public sealed record ProviderCapabilities(
    bool Tools,
    bool Streaming,
    bool SystemChannel,
    bool Images,
    bool PromptCaching,
    bool Thinking,
    bool RequiresApiKey,
    string DisplayName,
    string? ApiKeyEnvVar = null,
    string? DefaultModel = null,
    int? DefaultContextWindow = null,
    int? MaxContextWindow = null);

public sealed record ToolDefinition(
    string Name,
    string Description,
    Dictionary<string, object?> InputSchema,
    bool Mutates = false,
    bool ReadOnly = false);

public sealed record ToolInvocation(string Id, string Name, Dictionary<string, object?> Input);

public sealed record ToolResult(
    string InvocationId,
    bool Ok,
    string Output,
    object? Details = null,
    string? Error = null,
    long ElapsedMs = 0);

// ── Assistant content blocks ────────────────────────────────────────────────
public abstract record AssistantContent;

public sealed record AssistantText(string Text) : AssistantContent;

public sealed record AssistantToolUse(string Id, string Name, Dictionary<string, object?> Input) : AssistantContent;

public sealed record AssistantThinking(string Text) : AssistantContent;

// ── User content blocks ────────────────────────────────────────────────────
public sealed record UserContentBlock(
    string Type, // "text" | "image" | "tool_result"
    string? Text = null,
    ImageSource? Source = null,
    string? ToolUseId = null,
    object? Content = null,
    bool? IsError = null);

public sealed record ImageSource(string MediaType, string Data);

public sealed record Message(
    Role Role,
    object Content, // string | List<AssistantContent> | List<UserContentBlock>
    List<ToolInvocation>? ToolCalls = null,
    List<ToolResult>? ToolResults = null,
    Dictionary<string, object?>? Meta = null);

// ── Stream events ──────────────────────────────────────────────────────────
public interface IStreamEvent
{
    string Type { get; }
}

public sealed record StreamEventText(string Text) : IStreamEvent
{
    public string Type => "text_delta";
}

public sealed record StreamEventThinking(string Text) : IStreamEvent
{
    public string Type => "thinking_delta";
}

public sealed record StreamEventToolUseStart(string Id, string Name) : IStreamEvent
{
    public string Type => "tool_use_start";
}

public sealed record StreamEventToolUseDelta(string Id, string InputDelta) : IStreamEvent
{
    public string Type => "tool_use_delta";
}

public sealed record StreamEventToolUseEnd(string Id, Dictionary<string, object?> Input) : IStreamEvent
{
    public string Type => "tool_use_end";
}

public sealed record StreamEventDone(string? StopReason = null, TokenUsage? Usage = null) : IStreamEvent
{
    public string Type => "done";
}

public sealed record StreamEventError(string Error) : IStreamEvent
{
    public string Type => "error";
}

public sealed record TokenUsage(
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens = 0,
    int CacheWriteTokens = 0);

public sealed record CompletionRequest(
    string Model,
    List<Message> Messages,
    string? System = null,
    List<ToolDefinition>? Tools = null,
    IToolChoice? ToolChoice = null,
    int? MaxTokens = null,
    double? Temperature = null,
    Dictionary<string, object?>? Extra = null,
    CancellationToken Signal = default);

public sealed record CompletionResult(
    List<AssistantContent> Content,
    string? StopReason = null,
    TokenUsage? Usage = null);

public interface IProvider
{
    string Id { get; }
    ProviderCapabilities Capabilities { get; }
    Task<CompletionResult> CompleteAsync(CompletionRequest req);
    Task<CompletionResult> StreamAsync(CompletionRequest req, Action<IStreamEvent> onEvent);
}

public sealed record CoderConfig(
    string Provider,
    string Model,
    string Cwd,
    string UserConfigDir,
    List<string> SkillPaths,
    int MaxTokens,
    double Temperature,
    int MaxIterations,
    PermissionMode PermissionMode,
    ThemeMode Theme,
    EffortLevel EffortLevel,
    string SubagentModel,
    int ContextWindow,
    string? ApiKey = null,
    string? BaseUrl = null,
    string? AgentsFile = null,
    string? Lang = null,
    bool CnMode = false,
    bool DeepOptimize = false,
    bool StreamOutput = true);

public sealed record PermissionDecision(
    string Behavior, // "allow" | "deny" | "ask"
    string? Reason = null,
    List<PermissionRule>? UpdatedRules = null);

public sealed record PermissionRule(
    string Tool,
    string? Pattern = null,
    string Decision = "allow", // "allow" | "deny" | "ask"
    string Origin = "session"); // "user" | "project" | "session"

public sealed record SessionState(
    string Id,
    string Cwd,
    long StartedAt,
    List<Message> Messages,
    TokenUsage Usage,
    List<PermissionRule> PermissionRules,
    string Model,
    string Provider);
