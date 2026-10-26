using System.Globalization;
using System.Text;
using LossRun.Api.Contracts;

namespace LossRun.Chat;

/// <summary>
/// Brief 24 step 6 (review plan 2026-10-25 §4 changes 2, 3, 5): what a turn remembers of its conversation, with no model
/// call. Two parts:
/// <list type="bullet">
/// <item>A "Conversation facts" block, built from every prior turn's persisted citations: the quote, workspace, documents,
/// claim numbers and metrics cited, with the turns that cited them, newest first, deduplicated and capped. It carries
/// identifiers, never refs: refs are issued fresh each turn.</item>
/// <item>The most recent question/answer pairs verbatim, as many as fit in a token budget (at least the last
/// <see cref="MinVerbatimPairs"/>, at most the configured ceiling).</item>
/// </list>
/// Token counts are estimated at four characters per token (ADR 0013 amendment: the model's tokenizer data is not packaged
/// here). The estimate leans high for English prose and numbers, so a budget is not exceeded in practice.
/// </summary>
public static class ChatMemory
{
    public const int MinVerbatimPairs = 2;

    /// <summary>Default cap of the facts block (plan §4: about 1.5k tokens).</summary>
    public const int FactsTokenBudget = 1500;

    public const string FactsHeader = "Conversation facts (from earlier turns of this conversation; identifiers only).";

    /// <summary>The trace entry a turn writes with its last result set and filter (brief 24 step 7 Answers 2).</summary>
    public const string LastResultTool = "last-result";

    /// <summary>Rows of the last result shown in the block.</summary>
    public const int MaxLastResultRows = 10;

    public const string FactsRule = "Refs such as [S1] or [C2] from earlier turns are not valid in this turn. To use one of these facts, call a tool with its identifier "
        + "and cite the ref that tool returns.";

    public static int EstimateTokens(string? text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    /// <summary>
    /// The prior turns to replay verbatim: newest first while they fit in <paramref name="tokenBudget"/> (always the last
    /// <see cref="MinVerbatimPairs"/>), never more than <paramref name="ceiling"/>; returned oldest first with their 1-based turn numbers.
    /// </summary>
    public static IReadOnlyList<(int Turn, ChatTurnResult Result)> Verbatim(IReadOnlyList<ChatTurnResult> history, int tokenBudget, int ceiling)
    {
        var picked = new List<(int, ChatTurnResult)>();
        var used = 0;
        for (var i = history.Count - 1; i >= 0 && picked.Count < Math.Max(ceiling, 0); i--)
        {
            var cost = EstimateTokens(history[i].Question) + EstimateTokens(history[i].Text);
            if (picked.Count >= MinVerbatimPairs && used + cost > tokenBudget)
            {
                break;
            }

            picked.Add((i + 1, history[i]));
            used += cost;
        }

        picked.Reverse();
        return picked;
    }

    /// <summary>
    /// The facts block, or null when no prior turn cited anything. Citations outside <paramref name="scope"/> (a quote's
    /// conversation citing another quote: impossible by construction, checked anyway) are left out.
    /// </summary>
    public static string? Facts(IReadOnlyList<ChatTurnResult> history, ChatScope scope, int tokenBudget = FactsTokenBudget)
    {
        // key → (line, turns that cited it); newest turn first.
        var facts = new Dictionary<string, (string Line, SortedSet<int> Turns, int Newest)>(StringComparer.Ordinal);
        void Add(string key, string line, int turn)
        {
            if (facts.TryGetValue(key, out var f))
            {
                f.Turns.Add(turn);
                facts[key] = (f.Line, f.Turns, Math.Max(f.Newest, turn));
            }
            else
            {
                facts[key] = (line, [turn], turn);
            }
        }

        for (var i = 0; i < history.Count; i++)
        {
            var turn = i + 1;
            foreach (var c in history[i].Citations)
            {
                if (scope.QuoteId is { } q && c.QuoteId is { } cq && cq != q)
                {
                    continue;
                }

                if (c.DocumentId is { } d)
                {
                    Add($"doc:{d}", $"document {d}{(c.FileName is { } f ? $" ({f})" : "")}", turn);
                }

                if (c.Kind == "claim" && ClaimNumber(c) is { } claim)
                {
                    Add($"claim:{claim}:{c.DocumentId}", $"claim {claim}{(c.DocumentId is { } cd ? $" in {cd}" : "")}{(c.FieldPath is { } fp && fp.EndsWith(".total.incurred", StringComparison.Ordinal) ? "" : c.FieldPath is { } p ? $", field {p}" : "")}", turn);
                }

                if (c.Metric is { } m)
                {
                    Add($"metric:{m}", $"metric {m}", turn);
                }
            }
        }

        var last = LastResult(history);
        if (facts.Count == 0 && last is null)
        {
            return null;
        }

        var sb = new StringBuilder().Append(FactsHeader).Append('\n'); // "\n" on every platform: the prompt is the same bytes everywhere
        sb.Append(CultureInfo.InvariantCulture, $"Scope: {scope}.").Append('\n');
        if (last is not null)
        {
            sb.Append(last).Append('\n');
        }

        var lines = facts.Values.OrderByDescending(f => f.Newest).ThenBy(f => f.Line, StringComparer.Ordinal)
            .Select(f => $"- {f.Line} (turn {string.Join(", ", f.Turns.Reverse())})");
        var budget = tokenBudget - EstimateTokens(sb.ToString()) - EstimateTokens(FactsRule);
        var kept = 0;
        foreach (var line in lines)
        {
            var cost = EstimateTokens(line) + 1;
            if (cost > budget)
            {
                break;
            }

            sb.Append(line).Append('\n');
            budget -= cost;
            kept++;
        }

        if (kept < facts.Count)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- … {facts.Count - kept} older fact(s) left out (size cap).").Append('\n');
        }

        return sb.Append(FactsRule).ToString();
    }

    /// <summary>
    /// "Last result (turn N): …" from the newest prior turn that recorded one: the tool, the filter that produced it (nulls left
    /// out), and its first rows (claim number, carrier, status, policy year, incurred). Null when no prior turn has one.
    /// </summary>
    internal static string? LastResult(IReadOnlyList<ChatTurnResult> history)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var entry = history[i].ToolTrace.LastOrDefault(t => t.Tool == LastResultTool);
            if (entry is null)
            {
                continue;
            }

            var node = System.Text.Json.Nodes.JsonNode.Parse(entry.Arguments) as System.Text.Json.Nodes.JsonObject;
            if (node is null)
            {
                return null;
            }

            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"Last result (turn {i + 1}): {node["tool"]}");
            foreach (var key in new[] { "metric", "claimNumber", "field" })
            {
                if (node[key] is { } v && v.ToString().Length > 0)
                {
                    sb.Append(CultureInfo.InvariantCulture, $", {key} {v}");
                }
            }

            if (node["filter"] is System.Text.Json.Nodes.JsonObject filter && filter.Where(p => p.Value is not null).ToList() is { Count: > 0 } set)
            {
                sb.Append(", filter ").Append(string.Join(", ", set.Select(p => $"{p.Key}={p.Value}")));
            }

            if (node["value"] is { } value)
            {
                sb.Append(CultureInfo.InvariantCulture, $", value {value}");
            }

            if (node["matches"] is { } matches)
            {
                sb.Append(CultureInfo.InvariantCulture, $", {matches} match(es)");
            }

            if (node["rows"] is System.Text.Json.Nodes.JsonArray rows && rows.Count > 0)
            {
                sb.Append(": ").Append(string.Join("; ", rows.Take(MaxLastResultRows).Select(r =>
                    $"{r?["claimNumber"]} ({string.Join(", ", new[] { r?["carrier"]?.ToString(), r?["status"]?.ToString(), r?["policyYear"]?.ToString(), r?["incurred"] is { } inc ? $"incurred {inc}" : null }.Where(x => !string.IsNullOrEmpty(x)))})")));
            }

            return sb.Append('.').ToString();
        }

        return null;
    }

    /// <summary>The claim number from a claim citation's label ("WC-22-0001 · file.pdf · p.3").</summary>
    private static string? ClaimNumber(ChatCitation c)
    {
        var first = c.Label.Split(" · ")[0].Trim();
        return first.Length == 0 || first == "claim" ? null : first;
    }
}
