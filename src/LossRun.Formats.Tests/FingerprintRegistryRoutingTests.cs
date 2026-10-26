using System.Text.Json.Nodes;
using Json.Schema;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Formats.Replay;
using LossRun.Ingestion.Metering;
using LossRun.Rules;

namespace LossRun.Formats.Tests;

public class FingerprintTests
{
    [Fact]
    public void Deterministic_keys_are_stable_and_match_the_checked_in_specs()
    {
        var native = FingerprintText.Compute(Graphs.Native());

        Assert.Equal(native, FingerprintText.Compute(Graphs.Native()));
        Assert.Equal(native, FingerprintText.Compute(Graphs.Copy(Graphs.Native())));
        var spec = Graphs.Spec("synthetic-mutual-pdf").Fingerprint;
        Assert.Equal((spec.IdentityKey, spec.HeaderKey), (native.IdentityKey, native.HeaderKey));
        var xlsx = Graphs.Spec("synthetic-mutual-xlsx").Fingerprint;
        Assert.Equal((xlsx.IdentityKey, xlsx.HeaderKey), (FingerprintText.Compute(Graphs.Xlsx()).IdentityKey, FingerprintText.Compute(Graphs.Xlsx()).HeaderKey));
    }

    [Fact]
    public void Digits_never_enter_the_keys_or_the_embedding_text()
    {
        var graph = Graphs.Native();
        var fp = FingerprintText.Compute(graph);

        Assert.DoesNotMatch(@"\d", fp.IdentityText + fp.HeaderText);
        Assert.DoesNotMatch(@"[1-9]", FingerprintText.ForEmbedding(graph)); // digits masked to 0
        Assert.Contains("valued as of 00 00 0000", FingerprintText.ForEmbedding(graph));
    }

    [Theory]
    [InlineData("native", "synthetic-mutual-pdf")]
    [InlineData("bundle", "synthetic-mutual-pdf")]
    [InlineData("xlsx", "synthetic-mutual-xlsx")]
    [InlineData("csv", "synthetic-mutual-csv")]
    public async Task Exact_repeats_hit_the_deterministic_tier_without_an_embedding_call(string family, string formatId)
    {
        var registry = await Graphs.RegistryAsync();
        var embeddings = new FakeEmbeddingService();
        var graph = family switch { "native" => Graphs.Native(), "bundle" => Graphs.BundleLossRun(), "xlsx" => Graphs.Xlsx(), _ => Graphs.Csv() };

        var match = await new FormatFingerprinter(registry, embeddings).MatchAsync(graph, "run");

        Assert.Equal((formatId, "deterministic", false, 0), (match.Format?.FormatId, match.Tier, match.Drift, match.EmbeddingCalls));
        Assert.Equal(0, embeddings.Calls);
    }

    [Fact]
    public async Task Scanned_copy_misses_the_deterministic_tier_and_matches_by_embedding()
    {
        var registry = await Graphs.RegistryAsync();
        await FakeEmbeddingService.SeedCentroidAsync(registry, "synthetic-mutual-pdf", Graphs.Native());
        var embeddings = new FakeEmbeddingService();

        var match = await new FormatFingerprinter(registry, embeddings).MatchAsync(Graphs.Scanned(), "run");

        Assert.Equal(("synthetic-mutual-pdf", "embedding", 1), (match.Format?.FormatId, match.Tier, match.EmbeddingCalls));
        Assert.True(match.Score >= 0.95, $"cosine {match.Score}");
        Assert.Single(match.Usage);
        Assert.Equal("fingerprint", match.Usage[0].Stage);
    }

    [Fact]
    public async Task Payroll_page_does_not_match_the_loss_run_format()
    {
        var registry = await Graphs.RegistryAsync();
        await FakeEmbeddingService.SeedCentroidAsync(registry, "synthetic-mutual-pdf", Graphs.Native());

        var match = await new FormatFingerprinter(registry, new FakeEmbeddingService()).MatchAsync(Graphs.BundlePayroll(), "run");

        Assert.Null(match.Format);
        Assert.Equal("miss", match.Tier);
        Assert.True(match.Similarities["synthetic-mutual-pdf"] < 0.95);
    }

    [Fact]
    public async Task Header_rename_on_a_known_carrier_is_drift()
    {
        var registry = await Graphs.RegistryAsync();
        var graph = Graphs.Xlsx();
        graph.FingerprintInputs.FirstTableHeaderCells[2] = "Injured Worker"; // carrier renamed "Claimant"

        var match = await new FormatFingerprinter(registry, new FakeEmbeddingService()).MatchAsync(graph, "run");

        Assert.True(match.Drift);
        Assert.Equal("synthetic-mutual-xlsx", match.Format?.FormatId);
        Assert.False(match.Hit);
        var decision = new FormatRouter().Decide(match, null, null);
        Assert.Equal(RoutingTier.Learn, decision.Tier);
        Assert.Contains("drift", decision.Reasons[0]);
    }

    [Fact]
    public async Task Spreadsheets_never_call_the_embedding_model()
    {
        var registry = new InMemoryFormatRegistry(); // empty: guaranteed miss
        var embeddings = new FakeEmbeddingService();

        var match = await new FormatFingerprinter(registry, embeddings).MatchAsync(Graphs.Csv(), "run");

        Assert.Equal("miss", match.Tier);
        Assert.Equal(0, embeddings.Calls);
    }
}

public class RegistryTests
{
    [Fact]
    public async Task Publishing_the_same_version_twice_is_refused()
    {
        var registry = new InMemoryFormatRegistry();
        var spec = Graphs.Spec("synthetic-mutual-pdf");
        await registry.PublishAsync(spec);

        await Assert.ThrowsAsync<FormatAlreadyPublishedException>(() => registry.PublishAsync(Graphs.Spec("synthetic-mutual-pdf")));
    }

    [Fact]
    public async Task New_versions_are_new_entries_and_latest_wins()
    {
        var registry = await Graphs.RegistryAsync();
        var v2 = Graphs.Spec("synthetic-mutual-pdf");
        v2.Version = 2;
        v2.Supersedes = "synthetic-mutual-pdf@1";
        await registry.PublishAsync(v2);

        Assert.Equal(2, (await registry.GetAsync("synthetic-mutual-pdf"))!.Version);
        Assert.Equal(1, (await registry.GetAsync("synthetic-mutual-pdf", 1))!.Version);
        Assert.Equal(5, (await registry.GetIndexAsync()).Count); // + csv-dated (brief 25 Answers 3)
    }

    [Fact]
    public async Task Published_specs_cannot_be_mutated_through_a_returned_instance()
    {
        var registry = await Graphs.RegistryAsync();
        var copy = await registry.GetAsync("synthetic-mutual-pdf", 1);
        copy!.ColumnMap.Clear();

        Assert.NotEmpty((await registry.GetAsync("synthetic-mutual-pdf", 1))!.ColumnMap);
    }

    [Fact]
    public async Task Accepted_replays_increment_per_field_success_counters()
    {
        var registry = await Graphs.RegistryAsync();
        var memory = new FormatMemory(registry, new FakeEmbeddingService());
        var at = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

        await memory.ProcessAsync(Graphs.Native(), "run-1", at);
        await memory.ProcessAsync(Graphs.BundleLossRun(), "run-2", at.AddHours(1));

        var stats = await registry.GetStatsAsync("synthetic-mutual-pdf");
        Assert.Equal(2, stats.Fields["claim.indemnity.paid"].SuccessCount);
        Assert.Equal(at.AddHours(1), stats.Fields["claim.indemnity.paid"].LastSeen);
        Assert.Equal("synthetic-mutual-pdf@1", stats.Fields["document.valuationDate"].LastFormatVersion);
        Assert.False(stats.Fields.ContainsKey("claim.indemnity.incurred")); // derived values are not "reads"
    }

    [Fact]
    public async Task Checked_in_specs_validate_against_the_carrier_format_schema()
    {
        var schema = JsonSchema.FromText(File.ReadAllText(Graphs.File("schema", "carrier-format.schema.json")));
        foreach (var file in Directory.GetFiles(Graphs.File("formats"), "*.json", SearchOption.AllDirectories).Where(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out _)))
        {
            var results = schema.Evaluate(JsonNode.Parse(File.ReadAllText(file)), new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
            Assert.True(results.IsValid, file + ": " + string.Join("; ", results.Details.Where(d => d.HasErrors).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation} {e.Value}"))));
            var written = JsonNode.Parse(FormatJson.Write(FormatJson.Parse(File.ReadAllText(file))));
            Assert.True(schema.Evaluate(written).IsValid, file + " (re-serialized)");
        }

        Assert.Equal(7, (await (await Graphs.RegistryAsync(maxVersion: null)).GetIndexAsync()).Count); // synthetic-mutual-pdf v1–v3 and v5, csv, csv-dated, xlsx
    }
}

public class RoutingTests
{
    private static async Task<RoutingDecision> Route(DocumentGraph graph, CarrierFormat? spec = null, RoutingPolicy? policy = null)
    {
        var registry = new InMemoryFormatRegistry();
        await registry.PublishAsync(spec ?? Graphs.Spec("synthetic-mutual-pdf"));
        var match = await new FormatFingerprinter(registry, new FakeEmbeddingService()).MatchAsync(graph, "run");
        var replay = match.Hit ? ReplayEngine.Execute(graph, match.Format!) : null;
        return new FormatRouter(policy).Decide(match, replay, FormatRouter.MeanOcrConfidence(graph));
    }

    [Fact]
    public async Task A_replay_that_reads_no_claim_rows_goes_to_review()
    {
        // Eval run (c5): an empty extraction passed every claim rule vacuously and was accepted.
        var registry = new InMemoryFormatRegistry();
        var spec = Graphs.Spec("synthetic-mutual-csv");
        await registry.PublishAsync(spec);
        var graph = Graphs.Csv();
        var match = await new FormatFingerprinter(registry, null).MatchAsync(graph, "run");
        var empty = Graphs.Copy(graph);
        empty.Tables.Clear();

        var decision = new FormatRouter().Decide(match, ReplayEngine.Execute(empty, match.Format!), null);

        Assert.True(match.Hit);
        Assert.Equal(RoutingTier.Review, decision.Tier);
        Assert.Contains(decision.Reasons, r => r.StartsWith("replay read no claim rows", StringComparison.Ordinal));
    }

    [Fact]
    public void A_csv_spec_learned_from_another_export_still_finds_its_table()
    {
        // A CSV's only sheet is named after its file; a learned spec pinned the first file's name (eval run, c5).
        var spec = Graphs.Spec("synthetic-mutual-csv");
        spec.TableLocator.Sheet = "some-other-export";

        Assert.True(ReplayEngine.Execute(Graphs.Csv(), spec).Stats.ClaimRows > 0);
    }

    [Fact]
    public async Task Clean_repeat_is_replay_accepted_with_the_badge()
    {
        var decision = await Route(Graphs.Native());

        Assert.Equal(RoutingTier.ReplayAccepted, decision.Tier);
        Assert.Equal("format memory hit: Synthetic Mutual Insurance Co. synthetic-mutual-pdf v1 · 0 LLM calls", decision.Badge);
        Assert.Equal((0, 0, 0, "deterministic"), (decision.ModelCalls, decision.HardFailures, decision.UnmappedHeaders, decision.FingerprintTier));
    }

    [Fact]
    public async Task Unmapped_columns_route_to_delta()
    {
        var spec = Graphs.Spec("synthetic-mutual-pdf");
        spec.ColumnMap.RemoveAll(c => c.Source == "Exp Paid");
        var graph = Graphs.Native();

        var decision = await Route(graph, spec);

        Assert.Equal(RoutingTier.ReplayWithDelta, decision.Tier);
        Assert.Contains("Exp Paid", decision.Reasons[0]);
        Assert.Contains("delta needed for 1 column", decision.Badge);
    }

    [Fact]
    public async Task Unknown_format_routes_to_learn()
    {
        var registry = new InMemoryFormatRegistry();
        var match = await new FormatFingerprinter(registry, new FakeEmbeddingService()).MatchAsync(Graphs.Scanned(), "run");

        var decision = new FormatRouter().Decide(match, null, FormatRouter.MeanOcrConfidence(Graphs.Scanned()));

        Assert.Equal(RoutingTier.Learn, decision.Tier);
        Assert.Equal("new format → learn path", decision.Badge);
        Assert.Equal(1, decision.EmbeddingCalls);
    }

    [Fact]
    public async Task Low_ocr_confidence_is_noted_but_not_relearned()
    {
        // 08b: a learn cannot fix OCR, so low confidence no longer sends a matching document to learn; the rules check its values.
        var decision = await Route(Graphs.Native(), policy: new RoutingPolicy { MinOcrConfidence = 0.999 });

        Assert.Equal(RoutingTier.ReplayAccepted, decision.Tier);
        Assert.Contains(decision.Reasons, r => r.StartsWith("low OCR confidence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hard_failures_with_everything_mapped_route_to_review()
    {
        var graph = Graphs.Native();
        var cell = graph.Cells.First(c => c.TableId == "t0" && c.RawValue == "9,200.00"); // period 1 medical paid subtotal
        cell.RawValue = "9,300.00";

        var decision = await Route(graph);

        Assert.Equal(RoutingTier.Review, decision.Tier);
        Assert.True(decision.HardFailures > 0);
        Assert.StartsWith("needs review", decision.Badge);
    }

    [Fact]
    public void Replay_cost_carries_ingestion_only_and_counts_the_embedding_separately()
    {
        UsageEvent Event(string stage, string service, string meter, decimal quantity, string unit, string model, string op) =>
            new("doc-1", "run", stage, service, meter, quantity, unit, model, service == "AzureOpenAI" ? "DataZoneStandard" : null, "eastus2", DateTimeOffset.UnixEpoch, op);

        var cost = CostCalculator.Compute("doc-1", "run", "cu:prebuilt-layout",
            [Event("layout", "ContentUnderstanding", "documentPagesStandard", 1, "page", "prebuilt-layout", "op-1"),
             Event("fingerprint", "AzureOpenAI", "inputTokens", 700, "token", "text-embedding-3-large", "emb-1")], PriceTable.Embedded);

        Assert.Equal((0, 1), (cost.LlmCalls, cost.EmbeddingCalls));
        Assert.Equal(0.005m + 700 * 0.000000143m, cost.CostUsd);
    }
}
