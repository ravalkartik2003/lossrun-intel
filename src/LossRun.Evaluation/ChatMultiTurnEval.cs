using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LossRun.Analytics;
using LossRun.Api.Contracts;
using LossRun.Contracts;

namespace LossRun.Evaluation;

/// <summary>
/// <c>lossrun eval chat-multiturn</c> (brief 24 step 6, review plan 2026-10-25 §4): five conversations of eight turns on one
/// quote. Each refers back to what was said earlier: "it", "the second one", "go back to the largest claim". Expected facts
/// come from the system of record (<c>GET /quotes/{id}/claims</c>, the input the tools read).
/// A turn passes when every expected fact is in the answer, it has no invalid citation, the fact guard flagged nothing,
/// and every citation is inside the quote.
/// The gate is ≥ 90 % of turns passing, with 0 leaks and 0 invalid citations; cost per turn is recorded.
/// Written in brief 24; first run in step 7.
/// Brief 31 Answers 8: one run is noisy (34/40, then 37/40, on the same code), so <c>--runs 3</c> (the default) runs it three times and gates on the
/// median of turns passing, with 0 leaks and 0 invalid citations in every run. The model call cannot be pinned instead: gpt-5.4-mini is a reasoning
/// model, which accepts no <c>temperature</c> other than 1 and no <c>seed</c> on the Responses API (Azure OpenAI reasoning-models documentation).
/// </summary>
public static class ChatMultiTurnEval
{
    /// <param name="Text">The expected text; "a|b" accepts any alternative (a reopened claim is open: step 7 Answers 2).</param>
    public sealed record Fact(string Label, string Text, bool Money = false)
    {
        public bool In(string answer) => Text.Split('|').Any(t => answer.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    public sealed record Step(string Question, IReadOnlyList<Fact> Facts);

    public sealed record Conversation(string Id, IReadOnlyList<Step> Steps);

    public sealed record Scored(string Conversation, int Turn, string Question, string? Answer, IReadOnlyList<string> Missing, int InvalidCitations, int Unverified,
        bool Leak, decimal CostUsd, long TotalMs, int VerbatimTurns)
    {
        public bool Pass => Answer is not null && Missing.Count == 0 && InvalidCitations == 0 && Unverified == 0 && !Leak;
    }

    /// <summary>The gate over several runs: the median of turns passing reaches 90 % of the turns, and no run leaked or cited invalidly.</summary>
    public sealed record Verdict(IReadOnlyList<int> Passing, int Turns, double Median, bool Pass);

    public static Verdict Judge(IReadOnlyList<IReadOnlyList<Scored>> runs)
    {
        var passing = runs.Select(r => r.Count(s => s.Pass)).ToList();
        var sorted = passing.Order().ToList();
        var median = sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
        var turns = runs.Count == 0 ? 0 : runs[0].Count;
        var clean = runs.All(r => r.All(s => !s.Leak && s.InvalidCitations == 0));
        return new Verdict(passing, turns, median, turns > 0 && median >= Math.Ceiling(0.9 * turns) && clean);
    }

    public static async Task<int> RunAsync(string outDir, string? baseOption, string quote, int runs = 3)
    {
        if (runs <= 1)
        {
            return Judge([await RunOnceAsync(outDir, baseOption, quote).ConfigureAwait(false)]).Pass ? 0 : 1;
        }

        var all = new List<IReadOnlyList<Scored>>();
        for (var n = 1; n <= runs; n++)
        {
            Console.WriteLine($"chat-multiturn: run {n} of {runs}");
            all.Add(await RunOnceAsync(Path.Combine(outDir, $"run-{n}"), baseOption, quote).ConfigureAwait(false));
        }

        var verdict = Judge(all);
        var summary = new StringBuilder();
        summary.AppendLine("# Chat multi-turn eval, median of runs (brief 31 Answers 8)").AppendLine();
        summary.AppendLine(CultureInfo.InvariantCulture, $"Quote `{quote}` · {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC · {runs} runs of 5 conversations × 8 turns, same code; each run in `run-N/`").AppendLine();
        summary.AppendLine("| Measure | Result | Target |").AppendLine("|---|---|---|");
        summary.AppendLine(CultureInfo.InvariantCulture, $"| Turns passing, per run | {string.Join(", ", verdict.Passing.Select(p => $"{p}/{verdict.Turns}"))} | — |");
        summary.AppendLine(CultureInfo.InvariantCulture, $"| **Median turns passing** | **{verdict.Median:0.#}/{verdict.Turns}** | ≥ 90 % |");
        summary.AppendLine(CultureInfo.InvariantCulture, $"| Leaks, all runs | {all.Sum(r => r.Count(s => s.Leak))} | 0 |");
        summary.AppendLine(CultureInfo.InvariantCulture, $"| Invalid citations, all runs | {all.Sum(r => r.Sum(s => s.InvalidCitations))} | 0 |");
        summary.AppendLine(CultureInfo.InvariantCulture, $"| Gate | {(verdict.Pass ? "pass" : "fail")} | — |");
        summary.AppendLine(CultureInfo.InvariantCulture, $"| Cost per turn | mean ${all.SelectMany(r => r).Average(s => s.CostUsd):0.0000} | — |");
        await File.WriteAllTextAsync(Path.Combine(outDir, "chat-multiturn-eval.md"), summary.ToString()).ConfigureAwait(false);
        Console.WriteLine(summary);
        return verdict.Pass ? 0 : 1;
    }

    private static async Task<IReadOnlyList<Scored>> RunOnceAsync(string outDir, string? baseOption, string quote)
    {
        Directory.CreateDirectory(outDir);
        var api = Api.Base(baseOption);
        using var http = Api.Client();
        var input = AnalyticsJson.ReadInput(JsonNode.Parse(await http.GetStringAsync($"{api}/quotes/{Uri.EscapeDataString(quote)}/claims"))!["input"]!.ToJsonString());
        var conversations = Conversations(input);
        var scored = new List<Scored>();
        foreach (var c in conversations)
        {
            string? conversationId = null;
            for (var i = 0; i < c.Steps.Count; i++)
            {
                var step = c.Steps[i];
                var turn = await Api.AskAsync(api, new ChatTurnRequest(ChatScopes.Quote, step.Question, quote, ConversationId: conversationId));
                conversationId ??= turn.Result?.ConversationId;
                var answer = turn.Result?.Text;
                var numbers = answer is null ? [] : Numbers(answer);
                var missing = step.Facts.Where(f => answer is null || (f.Money
                    ? !numbers.Any(n => Math.Abs(n - decimal.Parse(f.Text, CultureInfo.InvariantCulture)) <= 1m)
                    : !f.In(answer))).Select(f => f.Label).ToList();
                var history = turn.Result?.ToolTrace.FirstOrDefault(t => t.Tool == "history");
                var s = new Scored(c.Id, i + 1, step.Question, answer, missing, turn.Result?.InvalidCitations?.Count ?? 0, turn.Result?.Unverified?.Count ?? 0,
                    turn.Result?.Citations.Any(x => x.QuoteId is { } q && q != quote) ?? false, turn.Result?.Usage.CostUsd ?? 0, turn.TotalMs, history?.Results ?? 0);
                scored.Add(s);
                Console.WriteLine($"{c.Id} t{i + 1} {(s.Pass ? "pass" : "FAIL")} {s.TotalMs} ms ${s.CostUsd:0.0000}" + (missing.Count > 0 ? $" | missing {string.Join(", ", missing)}" : "")
                    + (s.Leak ? " | LEAK" : "") + (turn.Error is null ? "" : $" | {turn.Error}"));
            }
        }

        var report = Report(api, quote, scored);
        await File.WriteAllTextAsync(Path.Combine(outDir, "chat-multiturn-eval.json"),
            JsonSerializer.Serialize(new { quote, conversations, turns = scored }, new JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = true }));
        await File.WriteAllTextAsync(Path.Combine(outDir, "chat-multiturn-eval.md"), report);
        Console.WriteLine(report);
        return scored;
    }

    /// <summary>Five conversations from the quote's claims: largest, smallest, open, oldest injury, and a metric thread.</summary>
    public static IReadOnlyList<Conversation> Conversations(AnalyticsInput input)
    {
        var claims = input.Claims.Where(c => c.ClaimNumber is not null).ToList();
        if (claims.Count < 4)
        {
            throw new InvalidOperationException("The quote needs at least four claims with claim numbers.");
        }

        static long Incurred(AnalyticsClaim c) => c.IndemnityPaidCents + c.IndemnityReserveCents + c.MedicalPaidCents + c.MedicalReserveCents + c.ExpensePaidCents + c.ExpenseReserveCents;
        static long Paid(AnalyticsClaim c) => c.IndemnityPaidCents + c.MedicalPaidCents + c.ExpensePaidCents;
        static string Dollars(long cents) => (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
        var byIncurred = claims.OrderByDescending(Incurred).ToList();
        var open = claims.Where(c => c.Status is "open" or "reopened").OrderByDescending(Incurred).ToList();
        var byInjury = claims.Where(c => c.DateOfInjury is not null).OrderBy(c => c.DateOfInjury, StringComparer.Ordinal).ToList();

        Conversation Pair(string id, string first, AnalyticsClaim a, string next, AnalyticsClaim b) => new(id,
        [
            new(first, [new("claim", a.ClaimNumber!)]),
            new("Is it open or closed?", [new("status", a.Status == "reopened" ? "reopened|open" : a.Status ?? "")]),
            new("What is its date of injury?", [new("date of injury", a.DateOfInjury ?? "")]),
            new(next, [new("second claim", b.ClaimNumber!)]),
            new("How do those two compare in total incurred?", [new("first incurred", Dollars(Incurred(a)), true), new("second incurred", Dollars(Incurred(b)), true)]),
            new("Which policy year is the second one in?", [new("policy year", b.PolicyYear?.ToString(CultureInfo.InvariantCulture) ?? "")]),
            new("Go back to the first claim we discussed: how much has been paid on it?", [new("first paid", Dollars(Paid(a)), true)]),
            new("Summarise the claims we discussed, with their claim numbers.", [new("first claim", a.ClaimNumber!), new("second claim", b.ClaimNumber!)]),
        ]);

        var total = claims.Sum(Incurred);
        var metric = new Conversation("metrics",
        [
            new("What is the total incurred across all claims?", [new("total incurred", Dollars(total), true)]),
            new("How many claims are open or reopened?", [new("open count", open.Count.ToString(CultureInfo.InvariantCulture))]),
            new("Of those, which has the largest total incurred?", open.Count > 0 ? [new("largest open", open[0].ClaimNumber!)] : [new("none open", "no")]),
            new("What is its reserve?", open.Count > 0 ? [new("reserve", Dollars(Incurred(open[0]) - Paid(open[0])), true)] : []),
            new("And the largest claim overall?", [new("largest", byIncurred[0].ClaimNumber!)]),
            new("Is that the same claim as the one before?", [new("same or not", open.Count > 0 && open[0].ClaimNumber == byIncurred[0].ClaimNumber ? "same|yes" : "not|different|no,")]),
            new("What share of the total incurred is that largest claim?", [new("largest incurred", Dollars(Incurred(byIncurred[0])), true)]),
            new("Remind me of the total we started with.", [new("total incurred", Dollars(total), true)]),
        ]);

        return
        [
            Pair("largest", "Which claim has the largest total incurred?", byIncurred[0], "And the second largest?", byIncurred[1]),
            Pair("smallest", "Which claim has the smallest total incurred?", byIncurred[^1], "And the next smallest?", byIncurred[^2]),
            Pair("open", "Which open claim has the largest total incurred?", open.Count > 0 ? open[0] : byIncurred[2], "And the open claim with the smallest?", open.Count > 1 ? open[^1] : byIncurred[3]),
            Pair("oldest", "Which claim has the earliest date of injury?", byInjury.Count > 0 ? byInjury[0] : byIncurred[0], "And the claim with the latest date of injury?", byInjury.Count > 1 ? byInjury[^1] : byIncurred[1]),
            metric,
        ];
    }

    private static List<decimal> Numbers(string text) =>
        [.. System.Text.RegularExpressions.Regex.Matches(text, @"\$?\d{1,3}(?:,\d{3})+(?:\.\d+)?|\$?\d+(?:\.\d+)?")
            .Select(m => decimal.TryParse(m.Value.Replace(",", "", StringComparison.Ordinal).TrimStart('$'), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : (decimal?)null)
            .OfType<decimal>()];

    private static string Report(string api, string quote, IReadOnlyList<Scored> scored)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Chat multi-turn eval (brief 24 step 6)").AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"API `{api}` · quote `{quote}` · {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC · 5 conversations × 8 turns").AppendLine();
        sb.AppendLine("| Measure | Result | Target |").AppendLine("|---|---|---|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Turns passing | {scored.Count(s => s.Pass)}/{scored.Count} | ≥ 90 % |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Leaks (citations outside the quote) | {scored.Count(s => s.Leak)} | 0 |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Invalid citations | {scored.Sum(s => s.InvalidCitations)} | 0 |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Unverified names, dates or quotations | {scored.Sum(s => s.Unverified)} | 0 |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Cost per turn | mean ${scored.Average(s => s.CostUsd):0.0000} · turn 8 mean ${scored.Where(s => s.Turn == 8).DefaultIfEmpty().Average(s => s?.CostUsd ?? 0):0.0000} | — |").AppendLine();
        sb.AppendLine("| Conversation | Turn | Pass | Missing | Verbatim prior turns | ms | Answer |").AppendLine("|---|---:|---|---|---:|---:|---|");
        foreach (var s in scored)
        {
            var answer = s.Answer is null ? "(no answer)" : (s.Answer.Length <= 160 ? s.Answer : s.Answer[..160] + "…").ReplaceLineEndings(" ").Replace("|", "/", StringComparison.Ordinal);
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {s.Conversation} | {s.Turn} | {(s.Pass ? "✓" : "✗")} | {string.Join(", ", s.Missing)} | {s.VerbatimTurns} | {s.TotalMs} | {answer} |");
        }

        return sb.ToString();
    }
}
