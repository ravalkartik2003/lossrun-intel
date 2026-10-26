using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LossRun.Api.Contracts;
using LossRun.Contracts;

namespace LossRun.Evaluation;

/// <summary>
/// <c>lossrun eval chat-corrections</c> (brief 24 step 5, review plan 2026-10-25 §5): ten questions about corrections the
/// eval plants itself. One source-consistent native PDF of the synthetic corpus goes through the deployed pipeline into a
/// fresh quote. Overrides are applied through <c>POST /documents/{id}/overrides</c>: three fields, one of them corrected
/// twice so the chain has a superseded link, each with its own rationale. The questions ask why a value differs, who
/// changed it, when, what the reviewer wrote, and what the loss run printed.
/// A turn passes when:
/// <list type="bullet">
/// <item>every expected fact is in the answer: the old and new value, the reviewer, the date, the rationale verbatim;</item>
/// <item>the turn's fact guard flagged nothing (no invented name, date or quotation);</item>
/// <item>it did not answer "not found".</item>
/// </list>
/// Written in brief 24; first run in step 7.
/// </summary>
public static class ChatCorrectionsEval
{
    /// <param name="Text">The expected text; "a|b" accepts any of the alternatives (brief 24 step 7 Answers 1: a right answer worded differently).</param>
    public sealed record Fact(string Label, string Text)
    {
        /// <summary>
        /// Brief 24 step 7: the eval's own tag on the rationales it plants is stripped from both sides before comparison (a
        /// rubric defect: the tag marks the eval's data, it is not the reviewer's words); typographic apostrophes are made plain.
        /// </summary>
        public bool In(string answer) => Text.Split('|').Select(Plain).Any(t => Plain(answer).Contains(t, StringComparison.OrdinalIgnoreCase));

        private static string Plain(string s) => s.Replace(Tag, "", StringComparison.Ordinal).Replace('’', '\'').Replace('‘', '\'');
    }

    /// <summary>The prefix every planted rationale carries, so the eval's corrections are recognisable in the ledger.</summary>
    public const string Tag = "eval24: ";

    public sealed record Question(string Id, string Text, IReadOnlyList<Fact> Facts);

    public sealed record Scored(Question Question, string? Answer, IReadOnlyList<string> Missing, IReadOnlyList<string> Unverified, bool NotFound, decimal CostUsd,
        long TotalMs, IReadOnlyList<string> Tools)
    {
        public bool Pass => Answer is not null && Missing.Count == 0 && Unverified.Count == 0 && !NotFound;
    }

    private sealed record Planted(string ClaimNumber, string Field, string Label, string Printed, string Final, IReadOnlyList<(string Value, string Rationale)> Steps);

    public static async Task<int> RunAsync(string corpusDir, string outDir, string? baseOption, string? runKeyOption)
    {
        Directory.CreateDirectory(outDir);
        var api = Api.Base(baseOption);
        var runKey = runKeyOption ?? DateTime.UtcNow.ToString("MMddHHmm", CultureInfo.InvariantCulture);
        var quote = $"EVAL24-{runKey}-FIX";
        var manifest = CorpusManifest.Load(corpusDir);
        var entry = manifest.Entries.First(e => e.File.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && e.Id.Contains("native", StringComparison.Ordinal)
            && CorpusManifest.Truth(corpusDir, e.Id) is { SourceConsistent: true } t && t.Document.PolicyPeriods.SelectMany(p => p.Claims).Count() >= 3);
        using var http = Api.Client();
        var me = JsonNode.Parse(await http.GetStringAsync($"{api}/me"))!["userId"]!.GetValue<string>();

        // Upload, wait, approve any document review (overrides apply to processed documents), then plant the corrections.
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(CorpusManifest.DocumentPath(corpusDir, entry.File))), "file", entry.File);
        form.Add(new StringContent(quote), "quoteId");
        form.Add(new StringContent(EvalRunner.CarrierName(entry.Carrier) ?? ""), "carrierHint");
        using var post = new HttpRequestMessage(HttpMethod.Post, $"{api}/uploads") { Content = form };
        post.Headers.Add("Idempotency-Key", $"eval24-{runKey}-{entry.Id}");
        using var accepted = await http.SendAsync(post);
        var uploadId = JsonSerializer.Deserialize<UploadAccepted>(await accepted.Content.ReadAsStringAsync(), CanonicalJson.Options)!.UploadId;
        await ChatEval.WaitForUploadAsync(http, api, uploadId);
        var item = JsonSerializer.Deserialize<UploadManifest>(await http.GetStringAsync($"{api}/uploads/{uploadId}"), CanonicalJson.Options)!.Items.First(i => i.DocumentId is not null);
        var documentId = item.DocumentId!;
        if (item.Status == ItemStatuses.AwaitingReview)
        {
            (await http.PostAsJsonAsync($"{api}/reviews/{documentId}/decision", new ReviewDecisionRequest(ReviewActions.Accept, Rationale: "eval24: approve before planting corrections"),
                CanonicalJson.Options)).EnsureSuccessStatusCode();
            await Task.Delay(5000);
        }

        var document = JsonNode.Parse(await http.GetStringAsync($"{api}/documents/{documentId}"))!["document"]!;
        var claims = document["policyPeriods"]!.AsArray().SelectMany((p, pi) => p!["claims"]!.AsArray().Select((c, ci) => (Path: $"policyPeriods[{pi}].claims[{ci}]", Claim: c!))).ToList();
        string Value(JsonNode claim, string field) => field.Split('.').Aggregate((JsonNode?)claim, (n, part) => n?[part]) is JsonValue v ? v.ToJsonString().Trim('"') : "0";
        var planted = new List<Planted>
        {
            Plant(claims[0], "medical.paid", "medical paid", [("+1234.56", "eval24: medical bill dated 2024-08-14 received after the run was printed")]),
            Plant(claims[1], "indemnity.reserve", "indemnity reserve", [("+2500", "eval24: adjuster raised the reserve for a second surgery")]),
            Plant(claims[2], "total.incurred", "total incurred", [("+1000", "eval24: first correction from the broker's spreadsheet"),
                ("+1750", "eval24: carrier letter supersedes the broker's figure")]),
        };

        Planted Plant((string Path, JsonNode Claim) c, string field, string label, (string Delta, string Rationale)[] steps)
        {
            var printed = decimal.Parse(Value(c.Claim, field), NumberStyles.Number, CultureInfo.InvariantCulture);
            var applied = steps.Select(s => ((printed + decimal.Parse(s.Delta, CultureInfo.InvariantCulture)).ToString("0.00", CultureInfo.InvariantCulture), s.Rationale)).ToList();
            return new Planted(c.Claim["claimNumber"]!.GetValue<string>(), $"{c.Path}.{field}", label, printed.ToString("0.00", CultureInfo.InvariantCulture), applied[^1].Item1, applied);
        }

        foreach (var p in planted)
        {
            foreach (var (value, rationale) in p.Steps)
            {
                var body = new OverrideRequest([new FieldOverride(p.Field, JsonDocument.Parse(value).RootElement, rationale)], rationale);
                (await http.PostAsJsonAsync($"{api}/documents/{documentId}/overrides", body, CanonicalJson.Options)).EnsureSuccessStatusCode();
            }
        }

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var (a, b, c) = (planted[0], planted[1], planted[2]);
        Fact Money(string label, string v) => new(label, decimal.Parse(v, CultureInfo.InvariantCulture).ToString("#,##0.00", CultureInfo.InvariantCulture));
        var questions = new List<Question>
        {
            new("why-differs", $"Why does claim {a.ClaimNumber} show medical paid of ${decimal.Parse(a.Final, CultureInfo.InvariantCulture):#,##0.00} when the loss run says ${decimal.Parse(a.Printed, CultureInfo.InvariantCulture):#,##0.00}?",
                [Money("printed", a.Printed), Money("current", a.Final), new("rationale", a.Steps[0].Rationale), new("reviewer", me)]),
            // Step 7 Answers 1: the rubric asks for what the question asks (who and when), not the value it did not ask about.
            new("who-when", $"Who changed the indemnity reserve on claim {b.ClaimNumber}, and when?", [new("reviewer", me), new("date", today)]),
            new("what-written", $"What did the reviewer write when correcting claim {b.ClaimNumber}'s indemnity reserve?", [new("rationale", b.Steps[0].Rationale)]),
            new("chain-latest", $"Has claim {c.ClaimNumber}'s total incurred been corrected? What does the loss run print?",
                [Money("printed", c.Printed), Money("current", c.Final)]),
            new("chain-history", $"Show every correction of claim {c.ClaimNumber}'s total incurred, in order.",
                [Money("printed", c.Printed), Money("first", c.Steps[0].Value), Money("current", c.Final), new("first rationale", c.Steps[0].Rationale), new("second rationale", c.Steps[1].Rationale)]),
            new("unchanged", $"Was claim {a.ClaimNumber}'s indemnity paid corrected?", [new("as printed", "not corrected|no corrections|unchanged|was not|wasn't|has not|hasn't|no correction")]),
            new("which-fields", $"Which values on claim {a.ClaimNumber} did a reviewer correct?", [new("field", "medical"), Money("current", a.Final)]),
            new("original", $"What was the original printed indemnity reserve of claim {b.ClaimNumber}?", [Money("printed", b.Printed)]),
            new("reported-adjusted", $"Explain the difference between the reported and the adjusted medical paid on claim {a.ClaimNumber}.",
                [Money("printed", a.Printed), Money("current", a.Final), new("rationale", a.Steps[0].Rationale)]),
            new("who-approved", "Who corrected values on this quote, and what reasons did they give?", [new("reviewer", me), new("rationale a", a.Steps[0].Rationale), new("rationale b", b.Steps[0].Rationale)]),
        };

        var scored = new List<Scored>();
        foreach (var q in questions)
        {
            var turn = await Api.AskAsync(api, new ChatTurnRequest(ChatScopes.Quote, q.Text, quote));
            var answer = turn.Result?.Text;
            var missing = q.Facts.Where(f => answer is null || !f.In(answer)
                && !(f.Label == "date" && Dates(answer).Contains(today))).Select(f => f.Label).ToList();
            var s = new Scored(q, answer, missing, turn.Result?.Unverified ?? [], turn.Result?.NotFound ?? false, turn.Result?.Usage.CostUsd ?? 0, turn.TotalMs,
                [.. turn.Result?.ToolTrace.Select(t => t.Tool) ?? []]);
            scored.Add(s);
            Console.WriteLine($"{q.Id,-18} {(s.Pass ? "pass" : "FAIL")} {s.TotalMs} ms ${s.CostUsd:0.0000}" + (missing.Count > 0 ? $" | missing {string.Join(", ", missing)}" : "")
                + (s.Unverified.Count > 0 ? $" | unverified {string.Join("; ", s.Unverified)}" : ""));
        }

        var report = Report(runKey, api, quote, entry.Id, scored);
        await File.WriteAllTextAsync(Path.Combine(outDir, "chat-corrections-eval.json"),
            JsonSerializer.Serialize(new { runKey, quote, document = entry.Id, documentId, planted, turns = scored }, new JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = true }));
        await File.WriteAllTextAsync(Path.Combine(outDir, "chat-corrections-eval.md"), report);
        Console.WriteLine(report);
        return scored.Count(s => s.Pass) >= 9 && scored.All(s => s.Unverified.Count == 0) ? 0 : 1;
    }

    /// <summary>Dates written as words or numbers, normalised to yyyy-MM-dd (the "when" fact accepts any form).</summary>
    private static HashSet<string> Dates(string? text) =>
        text is null ? [] : [.. new[] { "yyyy-MM-dd", "d MMM yyyy", "d MMMM yyyy", "MMMM d, yyyy", "MMM d, yyyy", "M/d/yyyy" }
            .SelectMany(fmt => System.Text.RegularExpressions.Regex.Matches(text, @"\b[\w,/ -]{8,18}\b").Select(m => m.Value.Trim())
                .Select(v => DateTime.TryParseExact(v, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null))
            .OfType<string>()];

    private static string Report(string runKey, string api, string quote, string document, IReadOnlyList<Scored> scored)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Chat corrections eval (brief 24 step 5) — run {runKey}").AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"API `{api}` · quote `{quote}` · document `{document}` (synthetic corpus) · {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC").AppendLine();
        sb.AppendLine("Rubric: a turn passes when the answer has every expected fact (printed and current values, the reviewer, the date, the rationale verbatim), the fact guard flagged nothing (no invented name, date or quotation) and it did not answer \"not found\".").AppendLine();
        sb.AppendLine("| Measure | Result | Target |").AppendLine("|---|---|---|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Rubric pass | {scored.Count(s => s.Pass)}/{scored.Count} | ≥ 9/10 |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Unverified names, dates or quotations | {scored.Sum(s => s.Unverified.Count)} | 0 |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Cost per turn | mean ${scored.Average(s => s.CostUsd):0.0000} | — |").AppendLine();
        sb.AppendLine("| Question | Pass | Missing | Unverified | Tools | ms | Answer |").AppendLine("|---|---|---|---|---|---:|---|");
        foreach (var s in scored)
        {
            var answer = s.Answer is null ? "(no answer)" : (s.Answer.Length <= 220 ? s.Answer : s.Answer[..220] + "…").ReplaceLineEndings(" ").Replace("|", "/", StringComparison.Ordinal);
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {s.Question.Id} | {(s.Pass ? "✓" : "✗")} | {string.Join(", ", s.Missing)} | {string.Join("; ", s.Unverified)} | {string.Join(", ", s.Tools)} | {s.TotalMs} | {answer} |");
        }

        return sb.ToString();
    }
}
