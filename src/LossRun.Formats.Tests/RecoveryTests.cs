using System.Text.Json;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Formats.Replay;
using Xunit.Abstractions;

namespace LossRun.Formats.Tests;

/// <summary>Brief 08b: format recovery, OCR-misread headers, OCR-level failures, narrative routing, adaptive threshold.</summary>
public class RecoveryTests(ITestOutputHelper output)
{
    private static DocumentGraph RunGraph(string name) =>
        JsonSerializer.Deserialize<DocumentGraph>(File.ReadAllText(Graphs.File("graphs", name)), CanonicalJson.Options)!;

    private static string Join(IEnumerable<string?> keys) => string.Join("|", keys.Select(k => k ?? "-"));

    private void Show(RoutingDecision d) => output.WriteLine($"{d.Tier} {d.FormatRef} recovered={d.RecoveredFrom} kind={d.HardFailureKind} | {string.Join(" | ", d.Reasons)}");

    [Fact]
    public async Task A_fax_scan_that_misses_the_fingerprint_is_recovered_from_its_carriers_format_not_learned()
    {
        var registry = await Graphs.RegistryAsync();
        var memory = new FormatMemory(registry, null);

        var (match, replay, decision) = await memory.ProcessAsync(RunGraph("c1-fax.graph.json"), "run", DateTimeOffset.UtcNow,
            submission: new SubmissionContext("Synthetic Mutual Insurance Co."));
        Show(decision);

        Assert.Equal("recovered", match.Tier);
        Assert.Equal("synthetic-mutual-pdf@1", decision.FormatRef);
        Assert.NotNull(decision.RecoveredFrom);
        Assert.NotEqual(RoutingTier.Learn, decision.Tier);
        Assert.StartsWith("format-recovered", decision.Reasons[0], StringComparison.Ordinal);
        Assert.True(replay!.Stats.ClaimRows > 0);
    }

    [Fact]
    public async Task Recovery_works_from_identity_tokens_without_a_carrier_hint()
    {
        var registry = new InMemoryFormatRegistry();
        await registry.PublishAsync(FormatJson.Parse(File.ReadAllText(Graphs.File("graphs", "fictional-casualty-group-pdf.1.json"))));

        var spec = (await registry.GetAsync("fictional-casualty-group-pdf"))!;
        var graph = RunGraph("c2-fax.graph.json");
        var replayed = ReplayEngine.Execute(graph, spec);
        output.WriteLine($"identity '{spec.Fingerprint.IdentityText}' containment {FingerprintText.Containment(spec.Fingerprint.IdentityText, FingerprintText.PageTokens(graph)):0.00}; rows {replayed.Stats.ClaimRows}, unmapped {replayed.UnmappedHeaders.Count} [{string.Join(", ", replayed.UnmappedHeaders)}], hard {replayed.RuleOutcomes.Count(o => o.Status == LossRun.Rules.RuleStatus.Fail && o.Severity == LossRun.Rules.RuleSeverity.Hard)}");
        var (match, _, decision) = await new FormatMemory(registry, null).ProcessAsync(graph, "run", DateTimeOffset.UtcNow);
        Show(decision);

        Assert.Equal("fictional-casualty-group-pdf@1", decision.FormatRef);
        Assert.StartsWith("identity tokens", match.RecoveredFrom, StringComparison.Ordinal);
        Assert.NotEqual(RoutingTier.Learn, decision.Tier);
    }

    [Fact]
    public async Task Another_carriers_format_is_not_a_candidate()
    {
        var registry = await Graphs.RegistryAsync(); // only Synthetic Mutual formats

        var (match, _, decision) = await new FormatMemory(registry, null).ProcessAsync(RunGraph("c2-fax.graph.json"), "run", DateTimeOffset.UtcNow,
            submission: new SubmissionContext("Fictional Casualty Group"));

        Assert.Null(match.RecoveredFrom);
        Assert.Equal(RoutingTier.Learn, decision.Tier);
    }

    [Fact]
    public async Task A_fax_whose_period_headers_ocr_broke_is_reviewed_from_its_format_not_learned()
    {
        // Corpus v2 seed: OCR garbled a period header line, so claims fell into the wrong period (structural-looking failures).
        var (match, _, decision) = await new FormatMemory(await Graphs.RegistryAsync(), null).ProcessAsync(RunGraph("c1-fax-periods.graph.json"), "run",
            DateTimeOffset.UtcNow, submission: new SubmissionContext("Synthetic Mutual Insurance Co."));
        Show(decision);

        Assert.Equal("recovered", match.Tier);
        Assert.Equal(RoutingTier.Review, decision.Tier);
    }

    [Fact]
    public async Task Drift_that_only_ocr_made_is_a_hit_not_a_learn()
    {
        var registry = new InMemoryFormatRegistry();
        await registry.PublishAsync(FormatJson.Parse(File.ReadAllText(Graphs.File("graphs", "fictional-casualty-group-pdf.v2.1.json"))));

        var (match, replay, decision) = await new FormatMemory(registry, null).ProcessAsync(RunGraph("c2-fax-drift.graph.json"), "run", DateTimeOffset.UtcNow);
        Show(decision);

        Assert.True(match.Drift);
        Assert.NotEmpty(replay!.OcrHeaderMatches);
        Assert.NotEqual(RoutingTier.Learn, decision.Tier);
        Assert.Contains(decision.Reasons, r => r.StartsWith("header tuple differs only by OCR misreads", StringComparison.Ordinal));
    }

    [Fact]
    public void Ocr_misread_headers_are_read_as_the_spec_column_on_scans_only()
    {
        var keys = OcrHeaders.Resolve(["clalm no", "cis dt", "doj", "εχρ pd", "ind paid"], new HashSet<string> { "claim no", "cls dt", "doi", "exp pd", "ind paid", "med paid" }, scanned: true);
        var native = OcrHeaders.Resolve(["clalm no", "ind paid"], new HashSet<string> { "claim no", "ind paid" }, scanned: false);

        Assert.Equal("claim no|cls dt|doi|exp pd|ind paid", Join(keys));
        Assert.Equal("-|ind paid", Join(native)); // a native renamed column stays drift
    }

    [Fact]
    public void A_similar_header_does_not_take_a_column_the_table_already_prints()
    {
        // "mcd paid" may only become "med paid" because "med paid" is absent; "ind paid" is printed, so it stays itself.
        var keys = OcrHeaders.Resolve(["ind paid", "mcd paid"], new HashSet<string> { "ind paid", "med paid" }, scanned: true);

        Assert.Equal("ind paid|med paid", Join(keys));
    }

    [Fact]
    public void Fax_totals_failures_are_ocr_level_and_a_round_printing_error_is_not()
    {
        var fax = ReplayEngine.Execute(RunGraph("c1-fax.graph.json"), Graphs.Spec("synthetic-mutual-pdf"));
        var graph = Graphs.Native();
        graph.Cells.First(c => c.TableId == "t0" && c.RawValue == "9,200.00").RawValue = "9,300.00"; // a printing error: exactly 100.00 off
        var native = ReplayEngine.Execute(graph, Graphs.Spec("synthetic-mutual-pdf"));

        var faxKind = OcrFailures.Classify(fax, OcrHeaders.IsScanned(RunGraph("c1-fax.graph.json")));
        output.WriteLine($"fax: {faxKind.Ocr} OCR / {faxKind.Structural} structural: {string.Join("; ", faxKind.Examples)}");

        Assert.True(faxKind.AllOcr);
        Assert.False(OcrFailures.Classify(native, scanned: false).AllOcr);
        Assert.False(OcrFailures.Classify(native, scanned: true).AllOcr); // round delta, full confidence: the source, not OCR
    }

    [Fact]
    public async Task A_pdf_without_tables_and_without_a_format_goes_to_review_not_learn()
    {
        var graph = Graphs.Copy(Graphs.Native());
        graph.Tables.Clear();
        graph.Cells.Clear();

        var (_, _, decision) = await new FormatMemory(new InMemoryFormatRegistry(), null).ProcessAsync(graph, "run", DateTimeOffset.UtcNow);

        Assert.Equal(RoutingTier.Review, decision.Tier);
        Assert.StartsWith("narrative statement", decision.Reasons[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_adaptive_threshold_is_mean_minus_two_sigma_with_a_floor_after_five_hits()
    {
        var hits = new EmbeddingHits();
        foreach (var c in new[] { 0.99, 0.98, 0.97, 0.99 })
        {
            hits.Add(c);
        }

        Assert.Null(hits.Threshold());
        hits.Add(0.96);
        Assert.Equal(Math.Round(0.978 - 2 * Math.Sqrt(0.00017), 4), hits.Threshold()!.Value, 4);

        var noisy = new EmbeddingHits();
        foreach (var c in new[] { 0.99, 0.80, 0.97, 0.85, 0.96 })
        {
            noisy.Add(c);
        }

        Assert.Equal(EmbeddingHits.Floor, noisy.Threshold());
    }
}
