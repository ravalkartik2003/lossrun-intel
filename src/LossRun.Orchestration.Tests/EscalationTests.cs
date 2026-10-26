using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;
using LossRun.Formats;
using LossRun.Rules;

namespace LossRun.Orchestration.Tests;

/// <summary>Brief 26: escalation triggers, dedupe and count, the hourly pattern detector, and no document value in a trace.</summary>
public class EscalationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly EscalationConfig Config = EscalationConfig.Embedded;

    private static LossRunRuntime Runtime() =>
        new(new InMemoryObjectStore(), new InMemoryFormatRegistry(), null!, new LossRun.Ingestion.IngestionOptions(), new LossRun.Learning.LearnOptions { LearnDeployment = "learn", DeltaDeployment = "delta" }, _ => null, _ => null!,
            null!, TimeProvider.System);

    private static DocumentResult Result(string id = "doc-1", decimal cost = 0.01m, string formatStatus = FormatLifecycle.Active, int hard = 0, string uploadId = "up-1") => new()
    {
        DocumentId = id, UploadId = uploadId, FormatRef = "fmt-a@1",
        Document = new LossRunDocument
        {
            DocumentId = id, CarrierName = "Synthetic Mutual Insurance Co.",
            PolicyPeriods = [new PolicyPeriod { PolicyNumber = "SYN-WC-1001", Claims = [new Claim { ClaimNumber = "CLM-778812", ClaimantName = "Jordan Example" }] }],
        },
        RuleOutcomes = [new RuleOutcome("R-10", RuleSeverity.Hard, RuleStatus.Fail, "incurred 23,500.00 ≠ paid 20,000.00 + reserve 3,000.00 for CLM-778812", ["x"], "CLM-778812")],
        Routing = new RoutingDecision(RoutingTier.Review, ["1 hard rule failure(s) with every header mapped (\"Jordan Example\": 23,500.00)"], "fmt-a@1",
            "Synthetic Mutual Insurance Co.", "exact", 1, false, 0, hard, 0, 0, null, 0, 0, "review", formatStatus, FingerprintKey: "fp:aaaaaaaaaaaa:bbbbbbbbbbbb"),
        Cost = new DocumentCost(id, "run", "Review", 1, 0, 0, 0, cost, [], 0),
    };

    private static RouteResult Route(string tier = "ReplayAccepted") => new("doc-1", tier, "fmt-a@1", false, [], [], "b", true, 0);

    // ------------------------------------------------------------------ single-event triggers

    [Fact]
    public void Learn_exhausted_raises_with_the_learn_step()
    {
        var signals = EscalationTriggers.AfterPersist(Result(), Route("Learn"), new LearnResult("doc-1", false, null, null, ["verification failed: totals"], 3, 0.02m), "Review", Config, Now);
        var s = Assert.Single(signals);
        Assert.Equal((EscalationReasons.LearnExhausted, "learn", "fp:aaaaaaaaaaaa:bbbbbbbbbbbb", 3m), (s.ReasonCode, s.Trace.Step, s.FormatFingerprint, s.Trace.Counts["learnModelCalls"]));

        // Brief 26 Answers 4: a delta repair that does not publish has its own code (a drift signal, counted apart).
        var delta = Assert.Single(EscalationTriggers.AfterPersist(Result(), Route("ReplayWithDelta"), new LearnResult("doc-1", false, null, null, ["repair failed"], 1, 0.01m), "Review", Config, Now));
        Assert.Equal((EscalationReasons.DeltaExhausted, "delta"), (delta.ReasonCode, delta.Trace.Step));
    }

    [Fact]
    public void Replay_failure_on_an_active_format_raises_but_not_on_a_shadow_format()
    {
        Assert.Contains(EscalationTriggers.AfterPersist(Result(hard: 1), Route(), null, "Review", Config, Now), s => s.ReasonCode == EscalationReasons.ReplayFailedActiveFormat);
        Assert.Empty(EscalationTriggers.AfterPersist(Result(hard: 1, formatStatus: FormatLifecycle.Shadow), Route(), null, "Review", Config, Now));
        Assert.Empty(EscalationTriggers.AfterPersist(Result(hard: 0), Route(), null, "ReplayAccepted", Config, Now));
    }

    [Fact]
    public void Item30_22_a_metered_call_without_a_price_raises_missing_price_keyed_by_the_meter()
    {
        var result = Result();
        result.Cost = new DocumentCost("doc-1", "run", "Review", 1, 0, 0, 0, 0.01m,
            [new CostLine("AzureOpenAI", "inputTokens (gpt-9-preview)", 500, "token", 0, 0, DocumentCost.MissingPrice)], 0);

        var signal = Assert.Single(EscalationTriggers.AfterPersist(result, Route(), null, "ReplayAccepted", Config, Now), s => s.ReasonCode == EscalationReasons.MissingPrice);
        Assert.Equal("price:AzureOpenAI inputTokens (gpt-9-preview)", signal.FormatFingerprint);
        Assert.Equal(1m, signal.Trace.Counts["unpricedLines"]);
    }

    [Fact]
    public void Item30_31_a_retried_upload_failure_is_one_occurrence_not_one_per_retry()
    {
        var first = EscalationTriggers.Exception("up-9", "ParseFile", "SqlException", Now);
        var (created, _) = EscalationLedger.Merge(null, first, "system");
        var (again, outcome) = EscalationLedger.Merge(created, first with { At = Now.AddMinutes(1) }, "system"); // Durable retried the activity

        Assert.Equal((EscalationLedger.Outcome.Unchanged, 1), (outcome, again.Count)); // before brief 30: Counted, 2
        var (other, counted) = EscalationLedger.Merge(again, EscalationTriggers.Exception("up-10", "ParseFile", "SqlException", Now), "system");
        Assert.Equal((EscalationLedger.Outcome.Counted, 2), (counted, other.Count));
    }

    [Fact]
    public void Cost_above_the_configured_limit_raises()
    {
        Assert.Empty(EscalationTriggers.AfterPersist(Result(cost: Config.MaxCostUsd), Route(), null, "ReplayAccepted", Config, Now));
        var s = Assert.Single(EscalationTriggers.AfterPersist(Result(cost: Config.MaxCostUsd + 0.01m), Route(), null, "ReplayAccepted", Config, Now));
        Assert.Equal((EscalationReasons.CostOverLimit, Config.MaxCostUsd), (s.ReasonCode, s.Trace.Counts["limitUsd"]));
    }

    [Fact]
    public void More_segments_than_the_limit_and_an_unhandled_exception_raise()
    {
        Assert.Null(EscalationTriggers.Segments("up-1", "d", "fp:x", Config.MaxSegments, 12, Config, Now));
        var seg = EscalationTriggers.Segments("up-1", "d", "fp:x", Config.MaxSegments + 1, 12, Config, Now)!;
        Assert.Equal((EscalationReasons.TooManySegments, Config.MaxSegments + 1m), (seg.ReasonCode, seg.Trace.Counts["segments"]));
        var ex = EscalationTriggers.Exception("up-1", "ParseFile", "System.InvalidOperationException", Now);
        Assert.Equal("exception:ParseFile:System.InvalidOperationException", ex.FormatFingerprint);
    }

    // ------------------------------------------------------------------ dedupe and count

    [Fact]
    public async Task Dedupe_counts_new_documents_once_reopens_resolved_cases_and_keeps_wont_fix_quiet()
    {
        var runtime = Runtime();
        var service = new EscalationService(runtime);
        EscalationSignal Doc(string id) => EscalationTriggers.ForDocument(EscalationReasons.LearnExhausted, Result(id), Now, [], "learn");
        async Task<List<string>> Events() => [.. (await runtime.RecordStore.OutboxAsync(null, 100)).Select(o => o.Kind).Order(StringComparer.Ordinal)]; // same timestamp: compare as a set

        var first = await service.RaiseAsync(Doc("doc-1"), Actor.System);
        Assert.Equal((1, 1), (first!.Count, first.Version));
        Assert.Equal(1, (await service.RaiseAsync(Doc("doc-1"), Actor.System))!.Count); // same document: unchanged
        var second = await service.RaiseAsync(Doc("doc-2"), Actor.System);
        Assert.Equal((2, 2), (second!.Count, second.Version));
        Assert.Equal(["doc-1", "doc-2"], second.DocumentIds);
        Assert.Single(await runtime.RecordStore.ListEscalationsAsync()); // one record per (reason, fingerprint)
        Assert.Equal([OutboxKinds.EscalationRaised, OutboxKinds.EscalationUpdated], await Events());

        // Signals without documents (an exception) count every time.
        await service.RaiseAsync(EscalationTriggers.Exception("up-1", "Route", "X", Now), Actor.System);
        Assert.Equal(2, (await service.RaiseAsync(EscalationTriggers.Exception("up-2", "Route", "X", Now), Actor.System))!.Count);

        await service.SetStatusAsync(first.Id, EscalationStatuses.Resolved, "spec fixed", new Actor("eng", Roles.Admin));
        var regressed = await service.RaiseAsync(Doc("doc-3"), Actor.System);
        Assert.Equal((EscalationStatuses.New, 3), (regressed!.Status, regressed.Count));
        Assert.StartsWith("reopened", regressed.ResolutionNote, StringComparison.Ordinal);

        await service.SetStatusAsync(first.Id, EscalationStatuses.WontFix, "fax quality; nothing to fix", new Actor("eng", Roles.Admin));
        var before = (await Events()).Count;
        var quiet = await service.RaiseAsync(Doc("doc-4"), Actor.System);
        Assert.Equal((EscalationStatuses.WontFix, 4), (quiet!.Status, quiet.Count));
        Assert.Equal(before, (await Events()).Count); // counted, no alert
    }

    [Fact]
    public async Task A_stale_version_is_refused_by_the_store()
    {
        var runtime = Runtime();
        var e = (await new EscalationService(runtime).RaiseAsync(EscalationTriggers.ForDocument(EscalationReasons.ManualFlag, Result(), Now, [], "review"), Actor.System))!;
        var audit = new AuditEvent(Now, "x", "x", "escalation.updated", "", null);
        Assert.False(await runtime.RecordStore.SaveEscalationAsync(e with { Count = 9 }, 0, audit, Actor.System, []));
        Assert.True(await runtime.RecordStore.SaveEscalationAsync(e with { Count = 9 }, 1, audit, Actor.System, []));
        var stored = (await runtime.RecordStore.GetEscalationAsync(e.Id))!;
        Assert.Equal((9, 2), (stored.Count, stored.Version));
    }

    // ------------------------------------------------------------------ pattern detector

    private static EscalationObservation Obs(string id, int daysAgo, bool reviewed = false, string? fp = "fp:1", string carrier = "Carrier A", int reopens = 0, params string[] rules) =>
        new(id, "up-" + id, carrier, fp, "fmt@1", Now.AddDays(-daysAgo), reviewed, rules, reopens);

    [Fact]
    public void Repeated_review_of_one_fingerprint_within_the_window()
    {
        var two = new[] { Obs("a", 1, true), Obs("b", 2, true), Obs("c", 9, true) }; // c is outside the 7-day window
        Assert.DoesNotContain(EscalationPatterns.Detect(two, Config, Now), s => s.ReasonCode == EscalationReasons.RepeatedReview);
        var three = two.Append(Obs("d", 3, true)).ToList();
        var s = Assert.Single(EscalationPatterns.Detect(three, Config, Now), s => s.ReasonCode == EscalationReasons.RepeatedReview);
        Assert.Equal(("fp:1", 3), (s.FormatFingerprint, s.DocumentIds.Count));
    }

    [Fact]
    public void One_rule_on_most_of_a_carriers_documents_with_a_minimum_count()
    {
        var four = Enumerable.Range(0, 4).Select(i => Obs($"d{i}", i, rules: "R-10")).ToList();
        Assert.DoesNotContain(EscalationPatterns.Detect(four, Config, Now), s => s.ReasonCode == EscalationReasons.RuleDominant); // under the minimum of 5
        var five = four.Append(Obs("d4", 4)).ToList(); // 4 of 5 = 80 %
        var s = Assert.Single(EscalationPatterns.Detect(five, Config, Now), s => s.ReasonCode == EscalationReasons.RuleDominant);
        Assert.Equal(["R-10"], s.Trace.RuleIds);
        Assert.Equal(0.8m, s.Trace.Counts["share"]);
        Assert.EndsWith("/R-10", s.FormatFingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain("Carrier A", s.FormatFingerprint, StringComparison.Ordinal); // the key hashes the carrier
    }

    [Fact]
    public void Review_rate_above_the_trailing_baseline()
    {
        var baseline = Enumerable.Range(0, 10).Select(i => Obs($"b{i}", 10 + i, reviewed: i == 0, fp: null)).ToList(); // 10 % before the window
        var calm = Enumerable.Range(0, 5).Select(i => Obs($"r{i}", i, reviewed: i == 0, fp: null)).ToList(); // 20 %: +10 points
        Assert.DoesNotContain(EscalationPatterns.Detect([.. baseline, .. calm], Config, Now), s => s.ReasonCode == EscalationReasons.ReviewRateSpike);
        var spike = Enumerable.Range(0, 5).Select(i => Obs($"r{i}", i, reviewed: i < 3, fp: null)).ToList(); // 60 %: +50 points
        var s = Assert.Single(EscalationPatterns.Detect([.. baseline, .. spike], Config, Now), s => s.ReasonCode == EscalationReasons.ReviewRateSpike);
        Assert.Equal((0.6m, 0.1m, 3), (s.Trace.Counts["reviewRate"], s.Trace.Counts["baselineRate"], s.DocumentIds.Count));
    }

    [Fact]
    public void A_document_reopened_more_than_the_limit()
    {
        Assert.Empty(EscalationPatterns.Detect([Obs("a", 1, reopens: Config.MaxReopens)], Config, Now));
        Assert.Equal(EscalationReasons.RepeatedReopen, Assert.Single(EscalationPatterns.Detect([Obs("a", 1, reopens: Config.MaxReopens + 1)], Config, Now)).ReasonCode);
    }

    [Fact]
    public async Task The_sweep_reads_the_store_raises_once_and_reports_the_open_count()
    {
        var telemetry = new Telemetry();
        var runtime = Runtime() with { EscalationTelemetry = telemetry };
        var records = runtime.RecordStore;
        foreach (var (id, i) in new[] { "a", "b", "c" }.Select((id, i) => (id, i)))
        {
            var upload = $"up-{id}";
            await records.UpdateManifestAsync(upload, _ => new UploadManifest { UploadId = upload, CreatedAt = DateTimeOffset.UtcNow.AddDays(-1 - i) }, Actor.System);
            await records.IndexDocumentAsync(id, upload);
            await records.SaveResultAsync(Result(id, uploadId: upload), Actor.System);
            await records.OpenReviewAsync(new ReviewItem { DocumentId = id, UploadId = upload, Kind = ReviewKinds.DocumentReview, OpenedAt = DateTimeOffset.UtcNow });
        }

        var swept = await new EscalationService(runtime).SweepAsync();
        Assert.Equal((3, 1, 1), (swept.Documents, swept.Changed, swept.Open)); // 3 reviews of one fingerprint in 7 days
        Assert.Contains(await records.ListEscalationsAsync(), e => e.ReasonCode == EscalationReasons.RepeatedReview && e.Count == 3);
        var again = await new EscalationService(runtime).SweepAsync();
        Assert.Equal((0, 1), (again.Changed, again.Open)); // the same pattern found again is not a new occurrence
        Assert.Equal([1, 1], telemetry.OpenCounts);
        Assert.Equal("raised", Assert.Single(telemetry.Changes));
    }

    // ------------------------------------------------------------------ traces carry no document values

    [Fact]
    public void A_trace_summary_never_carries_a_document_value()
    {
        var result = Result(hard: 1);
        List<EscalationSignal> signals =
        [
            EscalationTriggers.ForDocument(EscalationReasons.ManualFlag, result, Now,
                [.. result.Routing!.Reasons, "header(s): Clmt Name, Jordan Example", "claim CLM-778812 'Jordan Example' at $23,500.00 on 2025-03-14"], "review"),
            .. EscalationTriggers.AfterPersist(result, Route("Learn"),
                new LearnResult("doc-1", false, null, null, ["mismatch: \"Jordan Example\" 23,500.00 vs 20,000.00 (row 4: CLM-778812)"], 2, 0), "Review", Config, Now),
        ];
        string[] values = ["Jordan Example", "CLM-778812", "23,500", "20,000", "3,000", "2025-03-14", "SYN-WC-1001", "778812"];
        Assert.NotEmpty(signals);
        foreach (var s in signals)
        {
            var json = JsonSerializer.Serialize(s.Trace, CanonicalJson.Options);
            Assert.All(values, v => Assert.DoesNotContain(v, json, StringComparison.Ordinal));
            Assert.Contains("R-10", json, StringComparison.Ordinal); // rule ids survive the scrub
        }

        Assert.Equal("header(s): …", EscalationTraceText.Sanitize("header(s): Clmt Name, Jordan Example"));
        Assert.Equal("the carrier's R-10 total differs by #", EscalationTraceText.Sanitize("the carrier's R-10 total differs by 1,234.50"));
    }

    private sealed class Telemetry : IEscalationTelemetry
    {
        public List<string> Changes { get; } = [];
        public List<int> OpenCounts { get; } = [];
        public void Changed(Escalation escalation, string change) => Changes.Add(change);
        public void Open(int count) => OpenCounts.Add(count);
    }
}
