// Coder CLI (.NET 11 port) — approximate token counter + context compaction.
// Ported from src/core/tokens.ts and src/core/compaction.ts.

using System.Text.RegularExpressions;
using Coder.Types;

namespace Coder.Core;

public sealed record TokenBudget(int Total, int Used, int Remaining, double PctUsed);

/// <summary>
/// Heuristic token estimation (~4 chars/token for prose, ~3.5 for code).
/// Good enough to drive compaction decisions without a BPE dependency.
/// </summary>
public static class Tokens
{
    private const double CodeCharsPerToken = 3.5;
    private const double NaturalCharsPerToken = 4.0;

    private static readonly Regex CodePunct = new(@"[{};()<>]", RegexOptions.Compiled);
    private static readonly Regex CodeKeyword =
        new(@"\b(function|const|let|var|class|import|export|return)\b", RegexOptions.Compiled);

    public static int Estimate(object? input)
    {
        switch (input)
        {
            case null:
                return 0;
            case bool:
                return 1;
            case string s:
            {
                var looksLikeCode = CodePunct.IsMatch(s) && CodeKeyword.IsMatch(s);
                var ratio = looksLikeCode ? CodeCharsPerToken : NaturalCharsPerToken;
                return (int)Math.Ceiling(s.Length / ratio);
            }
            case int or long or double or float or decimal:
                return (int)Math.Ceiling((input.ToString() ?? "").Length / NaturalCharsPerToken);
            case System.Collections.IDictionary dict:
            {
                var total = 0;
                foreach (System.Collections.DictionaryEntry e in dict)
                {
                    total += (int)Math.Ceiling(((e.Key?.ToString()) ?? "").Length / NaturalCharsPerToken);
                    total += Estimate(e.Value);
                }
                return total;
            }
            case System.Collections.IEnumerable seq:
            {
                var total = 0;
                foreach (var x in seq) total += Estimate(x);
                return total;
            }
            default:
                // Records and POCOs: fall back on their string form.
                return (int)Math.Ceiling((input.ToString() ?? "").Length / NaturalCharsPerToken);
        }
    }

    public static int EstimateMessage(Message msg)
    {
        var total = 4; // role framing overhead
        total += Estimate(msg.Content);
        if (msg.ToolResults != null) total += Estimate(msg.ToolResults.Select(r => r.Output).ToList());
        if (msg.Role is Role.Tool or Role.Assistant) total += 4;
        return total;
    }

    public static TokenBudget Budget(int used, int total) =>
        new(total, used, Math.Max(0, total - used), total > 0 ? (double)used / total : 0);
}

public sealed record CompactionConfig(int MaxContextTokens, double TrimThreshold, int MinKeepTurns);

/// <summary>
/// Trims old messages when the budget runs low, preserving the most recent N turns.
/// </summary>
public static class Compaction
{
    public static readonly CompactionConfig Default = new(180_000, 0.85, 6);

    public static List<Message> Compact(List<Message> messages, int systemTokens, CompactionConfig? config = null)
    {
        var cfg = config ?? Default;
        var used = systemTokens + messages.Sum(Tokens.EstimateMessage);
        var threshold = cfg.MaxContextTokens * cfg.TrimThreshold;
        if (used <= threshold) return messages;

        var recentStart = TrailingTurnsStartIndex(messages, cfg.MinKeepTurns);
        var head = messages.Take(recentStart).ToList();
        var tail = messages.Skip(recentStart).ToList();

        double cur = used;
        while (head.Count > 0 && cur > threshold)
        {
            cur -= Tokens.EstimateMessage(head[0]);
            head.RemoveAt(0);
        }
        var outList = new List<Message>(head.Count + tail.Count);
        outList.AddRange(head);
        outList.AddRange(tail);
        return outList;
    }

    /// <summary>Index of the first message belonging to the last <paramref name="turns"/> user turns.</summary>
    private static int TrailingTurnsStartIndex(List<Message> messages, int turns)
    {
        var userCount = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == Role.User && HasTextContent(messages[i]))
            {
                userCount++;
                if (userCount >= turns) return i;
            }
        }
        return 0;
    }

    private static bool HasTextContent(Message m) => m.Content switch
    {
        string => true,
        List<UserContentBlock> blocks => blocks.Any(b => b.Type == "text"),
        _ => false,
    };
}
