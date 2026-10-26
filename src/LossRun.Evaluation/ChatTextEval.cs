using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LossRun.Analytics;
using LossRun.Api.Contracts;
using LossRun.Contracts;

namespace LossRun.Evaluation;

/// <summary>
/// <c>lossrun eval chat-text</c> (brief 13 §4, 12 Answers): text answers scored with a rubric. Two corpus-v3 quotes go
/// through the deployed pipeline: a CT-rich account (one c4 workbook and one c5 export, which print WCIO codes,
/// litigation, last day worked and recoveries) and a c1 bundle whose payroll report, application and broker letter are
/// kept (not processed) and indexed. Ten questions want prose; each has expected facts from the system of record (the
/// feature engine over <c>GET /quotes/{id}/claims</c>, the same input the tools read) or from the documents' printed text.
/// A turn passes when every expected fact is in the answer and no number in it is unexplained (a fabrication: not in the
/// record, the documents or the question). Then <c>--large-turns</c> claim questions on the large account measure chat
/// latency there (brief 13 §4: p50 ≤ 4 s).
/// </summary>
public static partial class ChatTextEval
{
    public sealed record Fact(string Label, string? Text = null, decimal? Number = null, string Kind = "number");

    public sealed record Question(string Id, string QuoteId, string Text, IReadOnlyList<Fact> Facts);

    public sealed record Scored(Question Question, string? Answer, IReadOnlyList<string> Missing, IReadOnlyList<string> Unexplained, bool NotFound,
        decimal CostUsd, long TotalMs, IReadOnlyList<string> Tools)
    {
        public bool Pass => Answer is not null && Missing.Count == 0 && Unexplained.Count == 0;
    }

    /// <summary>The payroll report, application and letter printed in every c1 bundle (Renderers.Bundle).</summary>
    private static readonly decimal[] BundleNumbers = [8810, 3632, 12, 27, 842_000, 1_615_500, 43, 100, 95814, 2024, 12, 1, 2026];

    public static async Task<int> RunAsync(string corpusDir, string outDir, string? baseOption, string? runKeyOption, bool skipUpload, string largeQuote, int largeTurns)
    {
        Directory.CreateDirectory(outDir);
        var api = Api.Base(baseOption);
        var runKey = runKeyOption ?? DateTime.UtcNow.ToString("MMddHHmm", CultureInfo.InvariantCulture);
        var (ctQuote, bundleQuote) = ($"EVAL13-{runKey}-CT", $"EVAL13-{runKey}-BUNDLE");
        var manifest = CorpusManifest.Load(corpusDir);
        string[] ctDocs = [Richest(corpusDir, manifest, "c4"), Richest(corpusDir, manifest, "c5")];
        string[] bundleDocs = ["c1-030-native-pdf-bundle"];
        using var http = Api.Client();

        if (!skipUpload)
        {
            // One upload per document with its carrier as the submission's hint: the CSV export prints no carrier name, and a
            // spreadsheet without printed totals verifies only with the carrier from the submission (as a broker's upload would carry).
            var uploads = new List<string>();
            foreach (var (quote, id) in ctDocs.Select(d => (ctQuote, d)).Concat(bundleDocs.Select(d => (bundleQuote, d))))
            {
                var entry = manifest.Entries.Single(e => e.Id == id);
                using var form = new MultipartFormDataContent();
                form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(CorpusManifest.DocumentPath(corpusDir, entry.File))), "file", entry.File);
                form.Add(new StringContent(quote), "quoteId");
                form.Add(new StringContent(EvalRunner.CarrierName(entry.Carrier) ?? ""), "carrierHint");
                using var post = new HttpRequestMessage(HttpMethod.Post, $"{api}/uploads") { Content = form };
                post.Headers.Add("Idempotency-Key", $"eval13-{runKey}-{id}");
                using var response = await http.SendAsync(post);
                var accepted = JsonSerializer.Deserialize<UploadAccepted>(await response.Content.ReadAsStringAsync(), CanonicalJson.Options)!;
                uploads.Add(accepted.UploadId);
                Console.WriteLine($"{quote}: upload {accepted.UploadId} ({id})");
            }

            foreach (var u in uploads)
            {
                await ChatEval.WaitForUploadAsync(http, api, u);
            }

            await ChatEval.WaitForIndexAsync([ctQuote, bundleQuote], [ctDocs.Length, bundleDocs.Length]); // files (a bundle's kept segments share its file)
        }

        // Expected facts from the system of record, through the same engine the compute_metric tool uses.
        var input = new Dictionary<string, AnalyticsInput>();
        foreach (var q in new[] { ctQuote, bundleQuote })
        {
            input[q] = AnalyticsJson.ReadInput(JsonNode.Parse(await http.GetStringAsync($"{api}/quotes/{q}/claims"))!["input"]!.ToJsonString());
        }

        var questions = Questions(ctQuote, bundleQuote, input);
        var scored = new List<Scored>();
        foreach (var question in questions)
        {
            var turn = await Api.AskAsync(api, new ChatTurnRequest(ChatScopes.Quote, question.Text, question.QuoteId));
            var s = Score(question, turn, Allowed(input[question.QuoteId], question.QuoteId == bundleQuote), ClaimNumbers(input[question.QuoteId]));
            scored.Add(s);
            Console.WriteLine($"{question.Id,-16} {(s.Pass ? "pass" : "FAIL")} {s.TotalMs} ms ${s.CostUsd:0.0000} | {Trim(s.Answer)}" +
                (s.Missing.Count > 0 ? $" | missing {string.Join(", ", s.Missing)}" : "") + (s.Unexplained.Count > 0 ? $" | unexplained {string.Join(", ", s.Unexplained)}" : ""));
        }

        // Latency on the large account: claim questions answered by query_claims over ~1,000 claims (the cached result load).
        var large = new List<long>();
        string[] largeQuestions =
        [
            "How many claims are open or reopened?", "What is the total incurred across all claims?", "Which claim has the largest total incurred?",
            "How many claims have total incurred of $25,000 or more?", "What is the total outstanding reserve?",
        ];
        for (var i = 0; i < largeTurns; i++)
        {
            var turn = await Api.AskAsync(api, new ChatTurnRequest(ChatScopes.Quote, largeQuestions[i % largeQuestions.Length], largeQuote));
            large.Add(turn.Result?.Usage.TotalMs is > 0 and var ms ? Math.Max(ms, turn.TotalMs) : turn.TotalMs);
            Console.WriteLine($"large {i + 1}/{largeTurns}: {large[^1]} ms{(turn.Error is null ? "" : " " + turn.Error)}");
        }

        var report = Report(runKey, api, ctDocs, bundleDocs, scored, largeQuote, large);
        await File.WriteAllTextAsync(Path.Combine(outDir, "chat-text-eval.json"),
            JsonSerializer.Serialize(new { runKey, quotes = new[] { ctQuote, bundleQuote }, ctDocs, bundleDocs, turns = scored, largeQuote, largeMs = large }, Pretty));
        await File.WriteAllTextAsync(Path.Combine(outDir, "chat-text-eval.md"), report);
        Console.WriteLine(report);
        return scored.Count(s => s.Pass) >= Math.Ceiling(0.9 * scored.Count) && scored.All(s => s.Unexplained.Count == 0) ? 0 : 1;
    }

    /// <summary>The carrier's source-consistent document with the most v3 signal: litigated, CT and recovered claims.</summary>
    private static string Richest(string corpusDir, CorpusManifest manifest, string carrier) =>
        manifest.Entries.Where(e => e.Carrier == carrier && !e.Strata.Contains(Strata.Password) && CorpusManifest.Truth(corpusDir, e.Id).SourceConsistent)
            .Select(e => (e.Id, Claims: CorpusManifest.Truth(corpusDir, e.Id).Document.PolicyPeriods.SelectMany(p => p.Claims).ToList()))
            .OrderByDescending(x => x.Claims.Count(c => c.LitigationFlag == true) + x.Claims.Count(c => c.CumulativeTrauma == true) + x.Claims.Count(c => c.RecoverySubrogation > 0))
            .ThenBy(x => x.Id, StringComparer.Ordinal).First().Id;

    public static IReadOnlyList<Question> Questions(string ctQuote, string bundleQuote, IReadOnlyDictionary<string, AnalyticsInput> input)
    {
        var ct = FeatureEngine.Compute(input[ctQuote]);
        var bundle = FeatureEngine.Compute(input[bundleQuote]);
        var byId = input[ctQuote].Claims.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var flagged = (string flag) => ct.PerClaimFlags.Where(f => f.Flags.Contains(flag)).Select(f => byId[f.ClaimId].ClaimNumber!).Order(StringComparer.Ordinal).ToList();
        decimal Value(FeatureSnapshot s, string metric) => Scaled(s[metric].Value, s[metric].Scale) ?? throw new InvalidOperationException($"{metric} is not measurable on this quote");
        Fact Pct(string label, FeatureSnapshot s, string metric) => new(label, Number: Value(s, metric) * 100, Kind: "percent");
        var litigated = flagged("litigated");
        var cause = ct["loss_drivers_cause_incurred"].Breakdown![0];
        var ctCount = flagged("ct").Count;

        return
        [
            new("ct-summary", ctQuote, "Summarize the cumulative trauma exposure on this account: how many CT claims are there and what share of all claims is that?",
                [new("CT claims", Number: ctCount, Kind: "count"), Pct("CT share", ct, "ct_share")]),
            new("litigated", ctQuote, "Which claims are litigated? Give their claim numbers.",
                [.. litigated.Select(n => new Fact(n, Text: n))]),
            new("post-term-ct", ctQuote, "Are there post-termination cumulative trauma claims (injury reported after the last day worked)? How many?",
                [new("post-termination CT", Number: Value(ct, "post_termination_ct_count"), Kind: "count")]),
            new("litigation-rate", ctQuote, "What is the litigation rate on lost-time claims?", [Pct("litigation rate", ct, "litigation_rate")]),
            new("cause-driver", ctQuote, "Which cause of injury drives the most incurred loss, and how much?",
                [new("cause code", Text: cause.Key), new("cause incurred", Number: Scaled(cause.Value, 2), Kind: "money")]),
            new("subrogation", ctQuote, "What is the subrogation recovery rate on this account?", [Pct("recovery rate", ct, "subrogation_recovery_rate")]),
            new("reporting-lag", ctQuote, "What is the median reporting lag in days, and what share of claims were reported more than 30 days after injury?",
                [new("median lag", Number: Value(ct, "reporting_lag_median_days"), Kind: "days"), Pct("over 30 days", ct, "reporting_lag_over_30_share")]),
            new("payroll", bundleQuote, "What annual payroll does the payroll report show for class code 3632, and what is that class?",
                [new("payroll", Number: 1_615_500, Kind: "money"), new("class", Text: "Machine Shop")]),
            new("application", bundleQuote, "According to the application, how many employees does the insured have and what is the nature of the business?",
                [new("employees", Number: 43, Kind: "count"), new("business", Text: "light manufacturing")]),
            new("totals", bundleQuote, "What is the total incurred on the loss run, and how many claims are open or reopened?",
                [new("total incurred", Number: Value(bundle, "incurred_total"), Kind: "money"), new("open", Number: Value(bundle, "open_claim_count"), Kind: "count")]),
        ];
    }

    private static decimal? Scaled(long? value, int scale) => value is { } v ? v / (decimal)Math.Pow(10, scale) : null;

    /// <summary>Every number the record and the documents can explain: claim amounts and dates, sums, metric values and breakdowns.</summary>
    public static IReadOnlyList<decimal> Allowed(AnalyticsInput input, bool bundle)
    {
        var values = new List<decimal>();
        var claims = input.Claims;
        foreach (var c in claims)
        {
            long[] cents = [c.IndemnityPaidCents, c.IndemnityReserveCents, c.MedicalPaidCents, c.MedicalReserveCents, c.ExpensePaidCents, c.ExpenseReserveCents,
                c.IndemnityPaidCents + c.IndemnityReserveCents, c.MedicalPaidCents + c.MedicalReserveCents, c.ExpensePaidCents + c.ExpenseReserveCents,
                c.IndemnityPaidCents + c.MedicalPaidCents + c.ExpensePaidCents,
                c.IndemnityPaidCents + c.IndemnityReserveCents + c.MedicalPaidCents + c.MedicalReserveCents + c.ExpensePaidCents + c.ExpenseReserveCents, c.RecoveryCents ?? 0];
            values.AddRange(cents.Select(v => v / 100m));
            foreach (var code in new[] { c.NatureCode, c.CauseCode, c.BodyPartCode })
            {
                if (decimal.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    values.Add(n);
                }
            }
        }

        foreach (var view in new[] { "adjusted", AccountBuilder.AsReported })
        {
            var snapshot = FeatureEngine.Compute(input, view: view);
            foreach (var m in snapshot.Metrics)
            {
                var factor = m.Unit is "ratio" ? 100m : 1m;
                values.AddRange(new[] { Scaled(m.Value, m.Scale) }.Concat(m.Breakdown?.Select(b => Scaled(b.Value, m.Scale)) ?? []).OfType<decimal>().SelectMany(v => new[] { v, v * factor }));
            }

            foreach (var p in snapshot.PerPeriod)
            {
                values.AddRange([p.ClaimCount, p.OpenCount, .. p.Indemnity.Concat(p.Medical).Concat(p.Expense).Concat(p.Total).Select(v => v / 100m)]);
            }
        }

        values.AddRange(Enumerable.Range(0, Math.Max(claims.Count, 31) + 1).Select(i => (decimal)i)); // counts and day-of-month
        values.AddRange(Enumerable.Range(2000, 31).Select(y => (decimal)y)); // years
        values.Add(30m); // the 30-day threshold the questions name
        if (bundle)
        {
            values.AddRange(BundleNumbers);
        }

        return values;
    }

    private static IReadOnlyList<string> ClaimNumbers(AnalyticsInput input) => [.. input.Claims.Select(c => c.ClaimNumber).OfType<string>().Where(n => n.Length >= 4)];

    public static Scored Score(Question question, Api.Turn turn, IReadOnlyList<decimal> allowed, IReadOnlyList<string> claimNumbers)
    {
        if (turn.Result is not { } r)
        {
            return new Scored(question, null, [.. question.Facts.Select(f => f.Label)], [], false, 0, turn.TotalMs, [.. turn.ToolsStreamed.Select(t => t.Tool)]);
        }

        // Refs ([M1]), claim numbers and ISO / US dates are not free-standing numbers.
        var text = Refs().Replace(r.Text, " ");
        text = claimNumbers.OrderByDescending(n => n.Length).Aggregate(text, (t, n) => t.Replace(n, " ", StringComparison.OrdinalIgnoreCase));
        var numbers = Numbers(Dates().Replace(text, " "));
        var missing = question.Facts.Where(f => !Present(f, r.Text, numbers)).Select(f => f.Label).ToList();
        var unexplained = numbers.Where(n => !allowed.Any(a => Close(a, n)) && !Numbers(question.Text).Contains(n))
            .Select(n => n.ToString("#,##0.##", CultureInfo.InvariantCulture)).Distinct().ToList();
        return new Scored(question, r.Text, missing, unexplained, r.NotFound, r.Usage.CostUsd, r.Usage.TotalMs > 0 ? Math.Max(r.Usage.TotalMs, turn.TotalMs) : turn.TotalMs,
            [.. r.ToolTrace.Select(t => t.Tool)]);
    }

    private static bool Present(Fact fact, string answer, IReadOnlyList<decimal> numbers) => fact switch
    {
        { Text: { } t } => answer.Contains(t, StringComparison.OrdinalIgnoreCase),
        { Number: { } n, Kind: "percent" } => numbers.Any(x => Math.Abs(x - n) <= 0.51m) || numbers.Any(x => Math.Abs(x * 100 - n) <= 0.51m),
        { Number: { } n, Kind: "days" } => numbers.Any(x => Math.Abs(x - n) <= 0.5m),
        { Number: { } n, Kind: "money" } => numbers.Any(x => Math.Abs(x - n) <= 1m),
        { Number: { } n } => numbers.Contains(n),
        _ => false,
    };

    /// <summary>Explained: equal to the cent, within a dollar for money-sized values, or a percentage rounded to one decimal.</summary>
    private static bool Close(decimal allowed, decimal n) =>
        allowed == n || Math.Abs(allowed) >= 100 && Math.Abs(allowed - n) <= 1m || Math.Abs(allowed) < 100 && Math.Abs(allowed - n) <= 0.051m;

    private static List<decimal> Numbers(string text) =>
        [.. Number().Matches(text).Select(m => decimal.TryParse(m.Value.Replace(",", "", StringComparison.Ordinal).TrimStart('$'), NumberStyles.Number,
            CultureInfo.InvariantCulture, out var v) ? v : (decimal?)null).OfType<decimal>()];

    [GeneratedRegex(@"\$?\d{1,3}(?:,\d{3})+(?:\.\d+)?|\$?\d+(?:\.\d+)?")]
    private static partial Regex Number();

    [GeneratedRegex(@"\[[A-Z]+\d+\]")]
    private static partial Regex Refs();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b|\b\d{1,2}/\d{1,2}/\d{2,4}\b")]
    private static partial Regex Dates();

    private static string Report(string runKey, string api, IReadOnlyList<string> ctDocs, IReadOnlyList<string> bundleDocs, IReadOnlyList<Scored> scored,
        string largeQuote, IReadOnlyList<long> large)
    {
        static long Pct(IReadOnlyList<long> values, double p) => values.Count == 0 ? 0 : values.Order().ElementAt(Math.Min(values.Count - 1, (int)Math.Ceiling(p * values.Count) - 1));
        var totals = scored.Select(s => s.TotalMs).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Chat text-answer eval (`docs/poc-plan/closeout/README.md`) — run {runKey}").AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"API `{api}` · corpus v3 · CT quote: {string.Join(", ", ctDocs)} · bundle quote: {string.Join(", ", bundleDocs)} · {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC").AppendLine();
        sb.AppendLine("Rubric: a turn passes when every expected fact is in the answer (numbers within rounding: money ±$1, percentages ±0.5 pt, days ±0.5) and no number in it is unexplained by the system of record, the documents' printed text or the question (a fabrication).").AppendLine();
        sb.AppendLine("| Measure | Result | Target |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Rubric pass | {scored.Count(s => s.Pass)}/{scored.Count} ({100.0 * scored.Count(s => s.Pass) / scored.Count:0} %) | ≥ 90 % |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Fabrications (unexplained numbers) | {scored.Sum(s => s.Unexplained.Count)} | 0 |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Full answer, these turns | p50 {Pct(totals, 0.5):N0} ms · p95 {Pct(totals, 0.95):N0} ms | — |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Full answer, large account `{largeQuote}` ({large.Count} claim questions) | p50 {Pct(large, 0.5):N0} ms · p95 {Pct(large, 0.95):N0} ms | p50 ≤ 4 s |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Cost per turn | mean ${scored.Average(s => s.CostUsd):0.0000} · max ${scored.Max(s => s.CostUsd):0.0000} | — |").AppendLine();
        sb.AppendLine("| Question | Pass | Missing facts | Unexplained numbers | Tools | ms | Answer |");
        sb.AppendLine("|---|---|---|---|---|---:|---|");
        foreach (var s in scored)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {s.Question.Id} | {(s.Pass ? "✓" : "✗")} | {string.Join(", ", s.Missing)} | {string.Join(", ", s.Unexplained)} | {string.Join(", ", s.Tools)} | {s.TotalMs} | {Trim(s.Answer)?.Replace("|", "/", StringComparison.Ordinal)} |");
        }

        return sb.ToString();
    }

    private static string? Trim(string? text) => text is null ? "(no answer)" : (text.Length <= 220 ? text : text[..220] + "…").ReplaceLineEndings(" ");

    private static readonly JsonSerializerOptions Pretty = new(CanonicalJson.Options) { WriteIndented = true };
}
