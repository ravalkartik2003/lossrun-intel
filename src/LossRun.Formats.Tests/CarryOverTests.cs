using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Formats.Replay;
using LossRun.Rules;

namespace LossRun.Formats.Tests;

/// <summary>Brief 05 Answers carried into brief 06: identity guard, submission context, drift → delta, not-informative, stats.</summary>
public class CarryOverTests
{
    private static async Task<InMemoryFormatRegistry> PdfRegistryAsync()
    {
        var registry = await Graphs.RegistryAsync();
        await FakeEmbeddingService.SeedCentroidAsync(registry, "synthetic-mutual-pdf", Graphs.Native());
        return registry;
    }

    /// <summary>The scanned copy with every mention of the carrier replaced: same layout, different carrier.</summary>
    private static DocumentGraph LayoutTwin()
    {
        var graph = Graphs.Scanned();
        static string Swap(string s) => s.Replace("Synthetic", "Fictional", StringComparison.Ordinal).Replace("Mutual", "Casualty", StringComparison.Ordinal)
            .Replace("Insurance Co.", "Group", StringComparison.Ordinal);
        foreach (var p in graph.Paragraphs)
        {
            p.Text = Swap(p.Text);
        }

        foreach (var w in graph.Words)
        {
            w.Text = Swap(w.Text);
        }

        foreach (var c in graph.Cells)
        {
            c.RawValue = Swap(c.RawValue);
        }

        return graph;
    }

    [Fact]
    public async Task Identity_guard_keeps_the_scanned_copy_of_the_same_carrier()
    {
        var match = await new FormatFingerprinter(await PdfRegistryAsync(), new FakeEmbeddingService()).MatchAsync(Graphs.Scanned(), "run");

        Assert.Equal(("synthetic-mutual-pdf", "embedding"), (match.Format?.FormatId, match.Tier));
        Assert.True(match.IdentityScores!["synthetic-mutual-pdf"] >= FormatFingerprinter.IdentityGuardThreshold);
    }

    [Fact]
    public async Task Identity_guard_rejects_a_layout_twin_from_another_carrier()
    {
        var match = await new FormatFingerprinter(await PdfRegistryAsync(), new FakeEmbeddingService()).MatchAsync(LayoutTwin(), "run");

        Assert.Null(match.Format);
        Assert.Equal("miss", match.Tier);
        Assert.True(match.Similarities["synthetic-mutual-pdf"] >= 0.95, $"cosine {match.Similarities["synthetic-mutual-pdf"]} — the guard, not the cosine, must reject it");
        Assert.True(match.IdentityScores!["synthetic-mutual-pdf"] < FormatFingerprinter.IdentityGuardThreshold);
    }

    [Theory]
    [InlineData("synthetic mutual insurance co", 1.0)]
    [InlineData("synthetic mutual insurance co|loss run", 1.0)]
    [InlineData("fictional casualty group", 0.0)]
    public void Containment_is_the_share_of_identity_tokens_on_the_page(string identity, double expected)
    {
        var page = new HashSet<string>(["synthetic", "mutual", "insurance", "co", "loss", "run", "policy"], StringComparer.Ordinal);

        Assert.Equal(expected, FingerprintText.Containment(identity, page));
    }

    [Fact]
    public async Task A_hint_naming_another_carrier_routes_to_review()
    {
        var memory = new FormatMemory(await PdfRegistryAsync(), new FakeEmbeddingService());

        var (match, replay, decision) = await memory.ProcessAsync(Graphs.Native(), "run", DateTimeOffset.UnixEpoch,
            submission: new SubmissionContext("Fictional Casualty Group", "Q-1", "broker"));

        Assert.True(match.CarrierMismatch);
        Assert.Null(replay);
        Assert.Equal(RoutingTier.Review, decision.Tier);
        Assert.StartsWith("carrier-mismatch", decision.Reasons[0]);
    }

    [Fact]
    public async Task A_hint_spelling_the_same_carrier_differently_still_replays()
    {
        var memory = new FormatMemory(await PdfRegistryAsync(), new FakeEmbeddingService());

        var (_, _, decision) = await memory.ProcessAsync(Graphs.Native(), "run", DateTimeOffset.UnixEpoch,
            submission: new SubmissionContext("Synthetic Mutual Insurance Company"));

        Assert.Equal(RoutingTier.ReplayAccepted, decision.Tier);
    }

    [Fact]
    public async Task The_hint_replaces_the_constant_carrier_of_a_letterhead_less_export()
    {
        var memory = new FormatMemory(await Graphs.RegistryAsync(), null);

        var (_, withHint, _) = await memory.ProcessAsync(Graphs.Csv(), "run", DateTimeOffset.UnixEpoch, submission: new SubmissionContext("Synthetic Mutual Insurance Company"));
        var (_, without, _) = await memory.ProcessAsync(Graphs.Csv(), "run", DateTimeOffset.UnixEpoch);

        Assert.Equal("Synthetic Mutual Insurance Company", withHint!.Document.CarrierName);
        Assert.Equal("context", withHint.Document.Lineage!["carrierName"].Extractor);
        Assert.Equal("Synthetic Mutual Insurance Co.", without!.Document.CarrierName);
        Assert.Equal("spec", without.Document.Lineage!["carrierName"].Extractor);
    }

    [Fact]
    public async Task Csv_with_a_hint_for_another_carrier_is_a_mismatch()
    {
        var memory = new FormatMemory(await Graphs.RegistryAsync(), null);

        var (_, _, decision) = await memory.ProcessAsync(Graphs.Csv(), "run", DateTimeOffset.UnixEpoch, submission: new SubmissionContext("Acme Casualty"));

        Assert.Equal(RoutingTier.Review, decision.Tier);
        Assert.StartsWith("carrier-mismatch", decision.Reasons[0]);
    }

    [Fact]
    public async Task Header_level_drift_replays_the_prior_version_and_routes_to_delta()
    {
        var graph = Graphs.Xlsx();
        var index = graph.FingerprintInputs.FirstTableHeaderCells.IndexOf("Claimant");
        graph.FingerprintInputs.FirstTableHeaderCells[index] = "Injured Worker";
        foreach (var cell in graph.Cells.Where(c => c.RawValue == "Claimant"))
        {
            cell.RawValue = "Injured Worker";
        }

        var (match, replay, decision) = await new FormatMemory(await Graphs.RegistryAsync(), null).ProcessAsync(graph, "run", DateTimeOffset.UnixEpoch);

        Assert.True(match.Drift);
        Assert.Contains("Injured Worker", replay!.UnmappedHeaders);
        Assert.Equal(RoutingTier.ReplayWithDelta, decision.Tier);
        Assert.StartsWith("format changed", decision.Badge);
    }

    [Fact]
    public void Checks_on_derived_values_are_not_informative()
    {
        var replay = ReplayEngine.Execute(Graphs.Native(), Graphs.Spec("synthetic-mutual-pdf"));

        var r02 = replay.RuleOutcomes.Where(o => o.RuleId == "R-02").ToList();
        Assert.Contains(r02, o => o.Status == RuleStatus.NotInformative);
        Assert.DoesNotContain(r02, o => o.Status == RuleStatus.Fail);
    }

    [Fact]
    public async Task Concurrent_stats_updates_are_not_lost()
    {
        var registry = new InMemoryFormatRegistry();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => registry.UpdateStatsAsync("f", s =>
        {
            var stat = s.Fields.TryGetValue("claim.claimNumber", out var x) ? x : s.Fields["claim.claimNumber"] = new FieldStat();
            stat.SuccessCount++;
        }))));

        Assert.Equal(20, (await registry.GetStatsAsync("f")).Fields["claim.claimNumber"].SuccessCount);
    }
}
