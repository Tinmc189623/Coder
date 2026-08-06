// Coder CLI (.NET 11 port) — message helpers. Ported from src/core/messages.ts.

using Coder.Types;

namespace Coder.Core;

public static class Messages
{
    public static Message AssistantMessage(List<AssistantContent> content) => new(Role.Assistant, content);

    public static Message ToolResultsToUserMessage(List<ToolResult> results) => new(Role.Tool, "", ToolResults: results);

    public static string AssistantText(Message msg)
    {
        if (msg.Content is List<AssistantContent> ac)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in ac)
                if (c is AssistantText t) sb.Append(t.Text);
            return sb.ToString();
        }
        return msg.Content as string ?? "";
    }

    public static List<AssistantToolUse> AssistantToolCalls(Message msg)
    {
        if (msg.Content is List<AssistantContent> ac)
            return ac.OfType<AssistantToolUse>().ToList();
        return new();
    }
}
