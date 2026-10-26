using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Ingestion.Metering;
using LossRun.Learning;
using LossRun.Rules;

namespace LossRun.Learning.Tests;

public class ModelSchemaTests
{
    /// <summary>Every schema node (not property-name maps): root, properties' values, items, $defs, anyOf members.</summary>
    private static IEnumerable<JsonObject> Objects(JsonNode? node)
    {
        if (node is not JsonObject schema)
        {
            yield break;
        }

        yield return schema;
        var children = new List<JsonNode?>();
        foreach (var key in new[] { "properties", "$defs" })
        {
            if (schema[key] is JsonObject map)
            {
                children.AddRange(map.Select(p => p.Value));
            }
        }

        children.Add(schema["items"]);
        if (schema["anyOf"] is JsonArray any)
        {
            children.AddRange(any);
        }

        foreach (var child in children.SelectMany(Objects))
        {
            yield return child;
        }
    }

    [Theory]
    [InlineData("proposal")]
    [InlineData("delta")]
    public void Response_schemas_meet_the_strict_structured_output_rules(string which)
    {
        var schema = which == "proposal" ? ModelSchema.Proposal : ModelSchema.Delta;
        string[] unsupported = ["pattern", "format", "minimum", "maximum", "exclusiveMinimum", "minItems", "maxItems", "const", "$schema", "$id"];

        foreach (var obj in Objects(schema))
        {
            Assert.DoesNotContain(obj.Select(p => p.Key), unsupported.Contains);
            if (obj["properties"] is JsonObject props)
            {
                Assert.Equal(false, obj["additionalProperties"]?.GetValue<bool>());
                Assert.Equal(props.Select(p => p.Key).Order(), obj["required"]!.AsArray().Select(r => r!.GetValue<string>()).Order());
            }

            Assert.False(obj["additionalProperties"] is JsonObject, "map-valued objects must become entry arrays");
        }

        Assert.True(Objects(schema).Sum(o => (o["properties"] as JsonObject)?.Count ?? 0) <= 5000); // service limit on total properties
    }

    [Fact]
    public void Pipeline_owned_fields_are_not_in_the_proposal_schema()
    {
        var properties = ModelSchema.Proposal["properties"]!.AsObject().Select(p => p.Key).ToList();

        Assert.DoesNotContain(properties, ModelSchema.PipelineOwned.Contains);
        Assert.Contains("columnMap", properties);
        Assert.Contains("documentFields", properties);
    }

    [Fact]
    public void Lookup_subtotal_entries_round_trip_to_the_spec_map()
    {
        var proposal = """{"carrierName":"X","tableLocator":{},"columnMap":[{"source":"A","target":"claim.claimNumber","normalizer":{"type":"text"}}],"periods":{"source":"column","column":"Policy","lookup":{"sheet":"S","keyHeader":"Policy","rangeHeader":null,"rangeRegex":null,"subtotals":[{"key":"Claims","value":"claimCount"}]}}}""";

        var spec = ModelSchema.ToSpec(proposal, _ => { });

        Assert.Equal("claimCount", spec.Periods.Lookup!.Subtotals["Claims"]);
        Assert.Null(spec.Periods.Lookup.RangeHeader);
    }

    [Fact]
    public void Static_prefix_is_cacheable_and_stable()
    {
        Assert.True(PromptBuilder.StaticPrefix.Length / 4 >= 1024, $"prefix ≈ {PromptBuilder.StaticPrefix.Length / 4} tokens");
        Assert.Same(PromptBuilder.StaticPrefix, PromptBuilder.StaticPrefix);
        Assert.Contains("carrier-format-0.5.0", PromptBuilder.StaticPrefix);
        Assert.DoesNotContain(Graphs.Second().DocumentId, PromptBuilder.StaticPrefix);
    }
}

public class LearnLoopTests
{
    private static FormatLearner Learner(ILearnModel model, IFormatRegistry registry, LearnOptions? options = null) =>
        new(model, registry, options ?? Graphs.Options(), clock: new FixedClock());

    [Fact]
    public async Task Verified_spec_is_published_after_feedback()
    {
        var registry = new InMemoryFormatRegistry();
        var model = FakeLearnModel.Sequence(Graphs.SecondProposalWithout("O/S"), Graphs.SecondProposal);

        var outcome = await Learner(model, registry).LearnAsync(new LearnRequest(Graphs.Second(), "run-1"));

        Assert.True(outcome.Published, string.Join(" | ", outcome.Reasons));
        Assert.Equal(2, outcome.ModelCalls);
        Assert.Contains("unmapped header \"O/S\"", model.Requests[1].Turns[^1].Text);
        var spec = outcome.Spec!;
        Assert.Equal(("fictional-casualty-group-pdf", 1, "learn:learn-deployment@test-version"), (spec.FormatId, spec.Version, spec.CreatedBy));
        Assert.Equal(2, spec.Learning!.Iterations);
        Assert.Equal(outcome.InputTokens, spec.Learning.InputTokens);
        Assert.Equal("pdf-totals", outcome.Verification!.Gate());
        Assert.Equal(true, outcome.Verification.TotalsTie);
        Assert.Single(await registry.GetIndexAsync());
        var fp = FingerprintText.Compute(Graphs.Second());
        Assert.Equal((fp.IdentityKey, fp.HeaderKey), (spec.Fingerprint.IdentityKey, spec.Fingerprint.HeaderKey));
    }

    [Fact]
    public async Task Published_learned_spec_validates_against_the_spec_schema()
    {
        var registry = new InMemoryFormatRegistry();
        var outcome = await Learner(FakeLearnModel.Sequence(Graphs.SecondProposal), registry).LearnAsync(new LearnRequest(Graphs.Second(), "run"));

        var schema = JsonSchema.FromText(File.ReadAllText(Graphs.File("schema", "carrier-format.schema.json")));
        var result = schema.Evaluate(JsonNode.Parse(FormatJson.Write(outcome.Spec!)), new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, string.Join("; ", result.Details.Where(d => d.HasErrors).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))));
        Assert.Equal("carrier-format-0.5.0", outcome.Spec!.SpecSchema);
    }

    [Fact]
    public async Task Exhaustion_routes_to_review_with_the_best_attempt_and_publishes_nothing()
    {
        var registry = new InMemoryFormatRegistry();
        var worse = Graphs.SecondProposalWithout("O/S").Replace("^Policy Total\\\\b", "^Never\\\\b", StringComparison.Ordinal);
        var model = FakeLearnModel.Sequence(worse, Graphs.SecondProposalWithout("O/S"), worse);

        var outcome = await Learner(model, registry).LearnAsync(new LearnRequest(Graphs.Second(), "run"));

        Assert.False(outcome.Published);
        Assert.Equal(3, outcome.ModelCalls);
        Assert.Empty(await registry.GetIndexAsync());
        Assert.Contains("review", outcome.Reasons[0]);
        Assert.Same(outcome.Attempts[1].Spec, outcome.Spec); // the attempt with the lowest penalty
        Assert.Empty((await registry.GetLearnedGlossaryAsync()).Mappings);
    }

    [Fact]
    public async Task An_answer_that_is_not_a_spec_costs_an_iteration_and_is_fed_back()
    {
        var model = FakeLearnModel.Sequence("""{"carrierName": 5}""", Graphs.SecondProposal);

        var outcome = await Learner(model, new InMemoryFormatRegistry()).LearnAsync(new LearnRequest(Graphs.Second(), "run"));

        Assert.True(outcome.Published);
        Assert.NotNull(outcome.Attempts[0].Error);
        Assert.StartsWith("# Your answer is not a valid spec", model.Requests[1].Turns[^1].Text);
    }

    [Fact]
    public async Task Every_call_shares_the_static_prefix_and_cache_key()
    {
        var model = FakeLearnModel.Sequence(Graphs.SecondProposalWithout("O/S"), Graphs.SecondProposal);

        await Learner(model, new InMemoryFormatRegistry()).LearnAsync(new LearnRequest(Graphs.Second(), "run"));

        Assert.All(model.Requests, r => Assert.Same(PromptBuilder.StaticPrefix, r.Instructions));
        Assert.All(model.Requests, r => Assert.Equal(FormatLearner.CacheKey, r.CacheKey));
        Assert.Equal(model.Requests[0].Turns[0], model.Requests[1].Turns[0]); // document part repeated verbatim → cached too
        Assert.All(model.Requests, r => Assert.Same(ModelSchema.Proposal, r.Schema));
    }

    [Fact]
    public async Task Meter_fires_per_call_with_cached_tokens_billed_as_a_subset()
    {
        var model = FakeLearnModel.Sequence(Graphs.SecondProposalWithout("O/S"), Graphs.SecondProposal);
        var outcome = await Learner(model, new InMemoryFormatRegistry()).LearnAsync(new LearnRequest(Graphs.Second(), "run"));

        Assert.Equal(6, outcome.Usage.Count); // input, cached input, output × 2 calls
        Assert.All(outcome.Usage, e => Assert.Equal(("learn", "learn-deployment", "test-version"), (e.Stage, e.ModelOrAnalyzer, e.ModelVersion)));
        Assert.Equal(0, outcome.Usage.Where(e => e.CorrelationId == "fake-0" && e.Meter == "cachedInputTokens").Sum(e => e.Quantity));
        Assert.True(outcome.CachedInputTokens > 0);

        var priced = outcome.Usage.Select(e => e with { ModelOrAnalyzer = "gpt-5.4-mini" }).ToList(); // price rows are keyed by model
        var cost = CostCalculator.Compute("doc-second", "run", "learn", priced, PriceTable.Embedded);
        Assert.True(cost.CostUsd > 0);
        Assert.Equal(2, cost.LlmCalls);
        Assert.Equal(outcome.OutputTokens, cost.TokensOut);
    }

    [Fact]
    public async Task Learned_mappings_become_candidate_aliases_that_resolve_only_once_active()
    {
        // ADR 0019: a verified learn records a candidate (never used); a second format adds evidence; a reviewer activates it.
        var registry = new InMemoryFormatRegistry();
        await Learner(FakeLearnModel.Sequence(Graphs.SecondProposal), registry).LearnAsync(new LearnRequest(Graphs.Second(), "run"));

        var learned = (await registry.GetLearnedGlossaryAsync()).Mappings;
        var accident = Assert.Single(learned, m => m.Header == "Accident Dt" && m.CanonicalField == "claim.dateOfInjury" && m.FormatId == "fictional-casualty-group-pdf");
        Assert.Equal((AliasStatus.Candidate, 1, "learn"), (accident.Status, accident.Evidence.Count, accident.Evidence[0].Route));
        Assert.DoesNotContain(learned, m => m.Header == "Claim No"); // the seed already resolves it
        Assert.Null(CanonicalData.Glossary.WithLearned(learned).ResolveHeader("Accident Dt")); // a candidate is never used

        await Learner(FakeLearnModel.Sequence(Graphs.SecondProposal), registry).LearnAsync(new LearnRequest(Graphs.SecondScanned(), "run-2"));
        var after = (await registry.GetLearnedGlossaryAsync()).Mappings;
        Assert.Equal(learned.Count, after.Count); // one alias per header and field: the second format adds evidence
        Assert.Equal((AliasStatus.Candidate, 2, 2), (after.Single(m => m.Header == "Accident Dt").Status, after.Single(m => m.Header == "Accident Dt").DistinctDocuments,
            after.Single(m => m.Header == "Accident Dt").DistinctFormats));

        await registry.UpdateLearnedGlossaryAsync(g => AliasLifecycle.Review(g, [new AliasDecision("Accident Dt", "claim.dateOfInjury", AliasDecision.Accept)], "uw-1", DateTimeOffset.UtcNow));
        var glossary = await registry.GlossaryAsync();
        Assert.Equal(("claim.dateOfInjury", "learned"), (glossary.ResolveHeader("accident dt")?.Field, glossary.ResolveHeader("accident dt")?.Method));
        Assert.Equal("alias", glossary.ResolveHeader("Claim No")?.Method); // seed first
    }

    [Fact]
    public async Task Drift_learn_with_a_prior_publishes_the_next_version_with_supersedes()
    {
        var registry = await Graphs.RegistryAsync();
        var prior = Graphs.Spec("synthetic-mutual-pdf");
        var fixedSpec = JsonNode.Parse(FormatJson.Write(prior))!.AsObject();
        foreach (var owned in ModelSchema.PipelineOwned)
        {
            fixedSpec.Remove(owned);
        }

        var map = fixedSpec["columnMap"]!.AsArray();
        map.Single(c => c!["source"]!.GetValue<string>() == "Reported")!["source"] = "Date Rptd";
        map.Add(JsonNode.Parse("""{"source":"Lit","target":"claim.litigationFlag","normalizer":{"type":"boolean"}}"""));
        var model = FakeLearnModel.Sequence(fixedSpec.ToJsonString());

        var outcome = await Learner(model, registry).LearnAsync(new LearnRequest(Graphs.Drifted(), "run", Prior: prior, Reason: "drift"));

        Assert.True(outcome.Published, string.Join(" | ", outcome.Reasons));
        Assert.Equal(("synthetic-mutual-pdf", 2, "synthetic-mutual-pdf@1"), (outcome.Spec!.FormatId, outcome.Spec.Version, outcome.Spec.Supersedes));
        Assert.Contains("Prior spec", model.Requests[0].Turns[0].Text);
        Assert.Equal(prior.Fingerprint.Embedding?.Model, outcome.Spec.Fingerprint.Embedding?.Model);
        Assert.NotNull(await registry.GetAsync("synthetic-mutual-pdf", 1)); // old version stays for old documents
    }

    [Fact]
    public async Task Page_image_is_attached_only_when_the_flag_is_on()
    {
        var pdf = File.ReadAllBytes(Graphs.File("fixtures", "lossrun2-native.pdf"));
        var off = FakeLearnModel.Sequence(Graphs.SecondProposal);
        var on = FakeLearnModel.Sequence(Graphs.SecondProposal);

        await Learner(off, new InMemoryFormatRegistry()).LearnAsync(new LearnRequest(Graphs.Second(), "run", SourcePdf: pdf));
        var outcome = await Learner(on, new InMemoryFormatRegistry(), Graphs.Options(image: true)).LearnAsync(new LearnRequest(Graphs.Second(), "run", SourcePdf: pdf));

        Assert.Null(off.Requests[0].PageImagePng);
        Assert.NotNull(on.Requests[0].PageImagePng);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], on.Requests[0].PageImagePng![..4]);
        Assert.True(outcome.Spec!.Learning!.PageImage);
    }

    [Fact]
    public void New_format_ids_never_collide()
    {
        var graph = Graphs.Second();
        FormatIndexEntry Entry(string id) => new(id, 1, "x", "pdf", "", "", DateTimeOffset.UnixEpoch);

        Assert.Equal("fictional-casualty-group-pdf", FormatLearner.NewFormatId("Fictional Casualty Group", graph, []));
        Assert.Equal("fictional-casualty-group-pdf-2", FormatLearner.NewFormatId("FICTIONAL CASUALTY GROUP", graph, [Entry("fictional-casualty-group-pdf")]));
    }
}

public class DeltaTests
{
    private static (FormatLearner Learner, FormatRepairer Repairer) Build(ILearnModel model, IFormatRegistry registry)
    {
        var learner = new FormatLearner(model, registry, Graphs.Options(), clock: new FixedClock());
        return (learner, new FormatRepairer(model, learner, Graphs.Options()));
    }

    private const string Delta = """
        {"additions":[
          {"source":"Date Rptd","target":"claim.dateReported","normalizer":{"type":"date","glossaryField":null,"pattern":null,"format":null,"codeSystem":null},"nullWhen":null},
          {"source":"Lit","target":"claim.litigationFlag","normalizer":{"type":"boolean"},"nullWhen":null},
          {"source":"Claim #","target":"claim.claimantName","normalizer":{"type":"text"},"nullWhen":null}
        ],"ignoredHeaders":[]}
        """;

    [Fact]
    public async Task Drifted_layout_is_repaired_by_a_delta_and_published_as_the_next_version()
    {
        var registry = await Graphs.RegistryAsync();
        var model = new FakeLearnModel((r, _) => r.Stage == "delta" ? Delta : throw new InvalidOperationException("learn must not run"));
        var (learner, repairer) = Build(model, registry);
        var pipeline = new LearningPipeline(registry, null, learner, repairer);

        var result = await pipeline.ProcessAsync(Graphs.Drifted(), "run", DateTimeOffset.UnixEpoch);

        Assert.Equal(RoutingTier.ReplayWithDelta, result.Decision.Tier);
        Assert.True(result.Decision.Drift);
        Assert.Equal("Delta", result.FinalTier);
        var spec = result.Learn!.Spec!;
        Assert.Equal(("synthetic-mutual-pdf", 2, "synthetic-mutual-pdf@1", "delta:delta-deployment@test-version"), (spec.FormatId, spec.Version, spec.Supersedes, spec.CreatedBy));
        Assert.Contains(spec.ColumnMap, c => c.Source == "Date Rptd" && c.Target == "claim.dateReported");
        Assert.DoesNotContain(spec.ColumnMap, c => c.Source == "Reported");                          // renamed column replaced
        Assert.DoesNotContain(spec.ColumnMap, c => c.Source == "Claim #" && c.Target == "claim.claimantName"); // only unmapped headers
        Assert.Equal(Graphs.Spec("synthetic-mutual-pdf").ColumnMap.Count + 1, spec.ColumnMap.Count);
        Assert.Equal(1, model.CallsFor("delta"));
        var prompt = model.Requests[0].Turns.Single().Text;
        Assert.Contains("\"Date Rptd\": \"09/16/2022\"", prompt);
        Assert.DoesNotContain("Synthetic Claimant A", prompt); // only the unmapped columns' values are sent

        var again = await pipeline.ProcessAsync(Graphs.Drifted(), "run-2", DateTimeOffset.UnixEpoch);
        Assert.Equal(RoutingTier.ReplayAccepted, again.Decision.Tier);
        Assert.Equal(1, model.Calls); // the second document replays with zero model calls
    }

    [Fact]
    public void Delta_application_keeps_only_additions_for_unmapped_headers()
    {
        var spec = Graphs.Spec("synthetic-mutual-pdf");
        ColumnMapping Map(string source, string target) => new() { Source = source, Target = target, Normalizer = new NormalizerSpec { Type = "text" } };

        var repaired = FormatRepairer.Apply(spec, Graphs.Drifted(), ["Date Rptd", "Lit"],
            [Map("Date Rptd", "claim.dateReported"), Map("Status", "claim.claimType"), Map("Lit", "claim.litigationFlag")], ["Lit", "Type"]);

        Assert.Equal(spec.ColumnMap.Count + 1, repaired.ColumnMap.Count);
        Assert.Single(repaired.ColumnMap, c => c.Source == "Status" && c.Target == "claim.status");
        Assert.Empty(repaired.IgnoredHeaders); // "Lit" was mapped; "Type" was never unmapped
        Assert.Equal(spec.ColumnMap.Count, Graphs.Spec("synthetic-mutual-pdf").ColumnMap.Count); // the published spec is untouched
    }

    [Fact]
    public async Task A_failed_delta_escalates_to_learn_once_with_the_old_spec_as_prior()
    {
        var registry = await Graphs.RegistryAsync();
        var model = new FakeLearnModel((r, _) => r.Stage == "delta" ? """{"additions":[],"ignoredHeaders":[]}""" : "{}");
        var (learner, repairer) = Build(model, registry);

        var outcome = await repairer.RepairAsync(Graphs.Drifted(), Graphs.Spec("synthetic-mutual-pdf"), ["Date Rptd", "Lit"], "run");

        Assert.False(outcome.Published);
        Assert.Equal((1, 3), (model.CallsFor("delta"), model.CallsFor("learn")));
        Assert.Contains("Prior spec", model.Requests[1].Turns[0].Text);
        Assert.StartsWith("delta failed → escalated to learn", outcome.Reasons[0]);
        Assert.Single(await registry.GetIndexAsync(), e => e.FormatId == "synthetic-mutual-pdf"); // nothing unverified published
    }

    [Fact]
    public async Task A_failed_delta_on_a_low_resolution_scan_is_not_escalated_to_learn()
    {
        var registry = await Graphs.RegistryAsync();
        var model = new FakeLearnModel((r, _) => r.Stage == "delta" ? """{"additions":[],"ignoredHeaders":[]}""" : "{}");
        var (_, repairer) = Build(model, registry);
        var fax = Graphs.Drifted();
        (fax.SourceFile.TextLayer, fax.SourceFile.ScanDpi) = (false, 150);

        var outcome = await repairer.RepairAsync(fax, Graphs.Spec("synthetic-mutual-pdf"), ["Date Rptd", "Lit"], "run");

        Assert.False(outcome.Published);
        Assert.Equal((1, 0), (model.CallsFor("delta"), model.CallsFor("learn")));
        Assert.Contains("150 dpi scan is not learned from", outcome.Reasons[0]);
    }
}

public class PipelineTests
{
    [Fact]
    public async Task Unseen_format_is_learned_then_replayed_with_zero_model_calls()
    {
        var registry = await Graphs.RegistryAsync();
        var model = FakeLearnModel.Sequence(Graphs.SecondProposal);
        var learner = new FormatLearner(model, registry, Graphs.Options(), clock: new FixedClock());
        var pipeline = new LearningPipeline(registry, null, learner, new FormatRepairer(model, learner, Graphs.Options()));

        var first = await pipeline.ProcessAsync(Graphs.Second(), "run-1", DateTimeOffset.UnixEpoch);
        var second = await pipeline.ProcessAsync(Graphs.Second(), "run-2", DateTimeOffset.UnixEpoch);

        Assert.Equal((RoutingTier.Learn, "Learned"), (first.Decision.Tier, first.FinalTier));
        Assert.Equal(RoutingTier.ReplayAccepted, second.Decision.Tier);
        Assert.Equal("fictional-casualty-group-pdf@1", second.Decision.FormatRef);
        Assert.Equal(1, model.Calls);
        Assert.Equal(0, second.Replay!.ModelCalls);
        Assert.Equal(first.Replay!.ToJson(), second.Replay.ToJson()); // the learning document's output cites the published spec
    }

    [Fact]
    public async Task Carrier_mismatch_goes_to_review_without_a_model_call()
    {
        var registry = await Graphs.RegistryAsync();
        var model = FakeLearnModel.Sequence(Graphs.SecondProposal);
        var learner = new FormatLearner(model, registry, Graphs.Options());
        var pipeline = new LearningPipeline(registry, null, learner, new FormatRepairer(model, learner, Graphs.Options()));

        var result = await pipeline.ProcessAsync(Graphs.Native(), "run", DateTimeOffset.UnixEpoch, new SubmissionContext("Fictional Casualty Group"));

        Assert.Equal("Review", result.FinalTier);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public void Second_opinion_tables_merge_into_the_graph()
    {
        var graph = Graphs.Scanned();
        graph.Tables.Clear();
        graph.Cells.RemoveAll(c => c.TableId is not null);
        Assert.Equal([1], SecondOpinionOcr.PagesNeedingSecondOpinion(graph, null));

        var analyze = JsonDocument.Parse("""
            {"tables":[{"rowCount":2,"columnCount":2,"cells":[
              {"kind":"columnHeader","rowIndex":0,"columnIndex":0,"content":"Claim #"},{"kind":"columnHeader","rowIndex":0,"columnIndex":1,"content":"Ind Paid"},
              {"rowIndex":1,"columnIndex":0,"content":"WC-22-0001"},{"rowIndex":1,"columnIndex":1,"content":"12,000.00"}]}]}
            """).RootElement;
        var added = SecondOpinionOcr.Merge(graph, analyze, 1, CanonicalData.Glossary);

        Assert.Equal(1, added);
        Assert.Equal(["Claim #", "Ind Paid"], graph.Tables.Single().HeaderTexts);
        Assert.Equal(["claim.claimNumber", "claim.indemnity.paid"], graph.Tables.Single().HeaderFields);
        Assert.Contains(graph.Warnings, w => w.Contains("second-opinion", StringComparison.Ordinal));
    }

    [Fact]
    public void Second_opinion_skips_tables_cu_already_read()
    {
        var graph = Graphs.Scanned(); // CU returned one table (the second period) on this scan
        var cu = graph.Tables.Single();
        var top = cu.Polygon!.Where((_, i) => i % 2 == 1).Min();
        string Box(double y0, double y1) => $"[{{\"pageNumber\":1,\"polygon\":[0.5,{y0},10,{y0},10,{y1},0.5,{y1}]}}]";
        var analyze = JsonDocument.Parse($$"""
            {"tables":[
              {"rowCount":1,"columnCount":1,"boundingRegions":{{Box(top, top + 0.8)}},"cells":[{"kind":"columnHeader","rowIndex":0,"columnIndex":0,"content":"Claim #"}]},
              {"rowCount":1,"columnCount":1,"boundingRegions":{{Box(top - 1.2, top - 0.5)}},"cells":[{"kind":"columnHeader","rowIndex":0,"columnIndex":0,"content":"Claim #"}]}]}
            """).RootElement;

        Assert.Equal(1, SecondOpinionOcr.Merge(graph, analyze, 1, CanonicalData.Glossary));
        Assert.Equal(2, graph.Tables.Count);
        Assert.Contains("1 skipped as already read by CU", graph.Warnings[^1]);
    }
}

internal sealed class FixedClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
}

public class SpecLintTests
{
    [Theory]
    [InlineData("synthetic-mutual-pdf", "pdf-totals")]
    [InlineData("synthetic-mutual-xlsx", "spreadsheet-totals")]
    [InlineData("synthetic-mutual-csv", null)] // lint-clean, but 73% coverage and no totals → review (GateRefinementTests)
    public void Hand_written_specs_are_lint_clean(string formatId, string? gate)
    {
        var graph = formatId switch { "synthetic-mutual-xlsx" => Graphs.Xlsx(), "synthetic-mutual-csv" => Graphs.Csv(), _ => Graphs.Native() };
        var verification = SpecVerifier.Verify(graph, Graphs.Spec(formatId));

        Assert.Empty(verification.Issues);
        Assert.Equal(gate, verification.Gate());
    }

    [Fact]
    public void Row_level_columns_on_claim_paths_get_a_canonical_suggestion()
    {
        // Eval run (c5 CSV): the model mapped repeated columns to claim.valuationDate / claim.policyNumber, and an
        // "unknown path" lint gave it nothing to correct towards in three iterations.
        var spec = Graphs.Spec("synthetic-mutual-csv");
        spec.ColumnMap.Add(new ColumnMapping { Source = "Valued", Target = "claim.valuationDate" });
        spec.ColumnMap.Add(new ColumnMapping { Source = "Policy", Target = "claim.policyNumber" });

        var issues = SpecVerifier.Verify(Graphs.Csv(), spec).Issues;

        Assert.Contains(issues, i => i.Contains("claim.valuationDate is not a canonical path; a document value repeated on every row maps to document.valuationDate", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Contains("claim.policyNumber is not a canonical path; a policy value repeated on every row is period.policyNumber", StringComparison.Ordinal));
    }

    [Fact]
    public void Summary_subtotal_targets_must_be_subtotal_relative()
    {
        var spec = Graphs.Spec("synthetic-mutual-xlsx");
        spec.Periods.Lookup!.Subtotals["Claims"] = "document.reportTotals.claimCount";

        var issues = SpecVerifier.Verify(Graphs.Xlsx(), spec).Issues;

        Assert.Contains(issues, i => i.Contains("periods.lookup.subtotals \"Claims\" → document.reportTotals.claimCount", StringComparison.Ordinal) && i.EndsWith("use claimCount", StringComparison.Ordinal));
    }

    [Fact]
    public void Total_row_separators_cu_drops_are_made_optional_without_a_model_call()
    {
        // 08c corpus v2: the learned c2 spec required "Account Total - 57 Claims"; CU read "Account Total" | "57 Claims".
        var spec = Graphs.Spec("synthetic-mutual-pdf");
        spec.TableLocator.RowKinds[1].Regex = @"^Account Total\s*-\s*\d+\s+Claims\b";
        spec.TableLocator.RowKinds[1].CountsRegex = @"^Account Total\s*-\s*(?<claimCount>\d+)\s+Claims\b";

        var notes = SpecLint.RelaxRowKindPunctuation(spec);

        Assert.Equal(3, notes.Count); // subtotal regex has no separator; its counts regex and both grand-total regexes are relaxed
        Assert.Matches(spec.TableLocator.RowKinds[1].Regex, "Account Total 57 Claims");
        Assert.Matches(spec.TableLocator.RowKinds[1].Regex, "Account Total - 57 Claims");
        Assert.Matches(spec.TableLocator.RowKinds[0].CountsRegex!, "Subtotal 14 claims: 5 open / 9 closed");
        Assert.Equal("14", System.Text.RegularExpressions.Regex.Match("Subtotal 14 claims: 5 open / 9 closed", spec.TableLocator.RowKinds[0].CountsRegex!).Groups["claimCount"].Value);
        Assert.Empty(SpecLint.RelaxRowKindPunctuation(spec)); // idempotent
        Assert.Empty(SpecLint.RelaxRowKindPunctuation(Graphs.Spec("synthetic-mutual-csv")));
    }

    [Fact]
    public void A_date_constant_is_located_by_its_label_never_by_its_value()
    {
        var graph = Graphs.Xlsx();
        var dated = graph.Cells.First(c => c.Column > 0 && CanonicalComparer.NormalizeDate(c.RawValue) is not null
            && graph.Cells.Any(l => l.Page == c.Page && l.Row == c.Row && l.Column == c.Column - 1 && CanonicalComparer.NormalizeDate(l.RawValue) is null && !string.IsNullOrWhiteSpace(l.RawValue)));
        var label = graph.Cells.Single(l => l.Page == dated.Page && l.Row == dated.Row && l.Column == dated.Column - 1).RawValue!.Trim();
        var spec = Graphs.Spec("synthetic-mutual-xlsx");
        spec.DocumentFields.RemoveAll(f => f.Target == "document.valuationDate");
        spec.DocumentFields.Add(new DocumentFieldSpec
        {
            Target = "document.valuationDate",
            Locator = new FieldLocator { Type = "const", Value = System.Text.Json.JsonDocument.Parse($"\"{CanonicalComparer.NormalizeDate(dated.RawValue):yyyy-MM-dd}\"").RootElement },
        });

        SpecLint.LocatePrintedConstants(spec, graph, CanonicalData.Glossary);

        var field = spec.DocumentFields.Single(f => f.Target == "document.valuationDate");
        Assert.Equal(("labelRight", label, "date"), (field.Locator.Type, field.Locator.Label, field.Normalizer.Type)); // not a regex for the literal date
    }

    [Fact]
    public void A_summary_sheets_printed_totals_are_mapped_when_the_learned_lookup_left_them_out()
    {
        var spec = Graphs.Spec("synthetic-mutual-xlsx");
        var expected = spec.Periods.Lookup!.Subtotals.ToDictionary();
        spec.Periods.Lookup.Subtotals.Clear();

        var notes = SpecLint.MapLookupSubtotals(spec, Graphs.Xlsx(), CanonicalData.Glossary);

        Assert.Equal(expected, spec.Periods.Lookup.Subtotals); // Claims, Open, Closed, Total Incurred: the hand spec's own mapping
        Assert.Equal(4, notes.Count);
        Assert.Empty(SpecLint.MapLookupSubtotals(spec, Graphs.Xlsx(), CanonicalData.Glossary)); // idempotent, and never overrides a mapping
        Assert.NotNull(SpecVerifier.Verify(Graphs.Xlsx(), spec).Gate()); // the totals tie, so the spec still verifies
    }

    [Fact]
    public void Coded_columns_mapped_whole_are_retargeted_to_code_or_text()
    {
        var spec = Graphs.Spec("synthetic-mutual-csv");
        spec.ColumnMap.Add(new ColumnMapping { Source = "Nature", Target = "claim.natureOfInjury", Normalizer = new NormalizerSpec { Type = "integer" } });
        spec.ColumnMap.Add(new ColumnMapping { Source = "Body", Target = "claim.partOfBody", Normalizer = new NormalizerSpec { Type = "text" } });

        var notes = SpecLint.RetargetCodedColumns(spec);

        Assert.Equal(["columnMap \"Nature\": claim.natureOfInjury (integer) → claim.natureOfInjury.code (code)", "columnMap \"Body\": claim.partOfBody (text) → claim.partOfBody.text"], notes);
        Assert.Equal(("code", "WCIO-Nature"), (spec.ColumnMap[^2].Normalizer.Type, spec.ColumnMap[^2].Normalizer.CodeSystem));
        Assert.Empty(SpecLint.RetargetCodedColumns(spec)); // idempotent
    }

    [Fact]
    public void Row_level_columns_on_claim_paths_are_retargeted_without_a_model_call()
    {
        var spec = Graphs.Spec("synthetic-mutual-csv");
        spec.ColumnMap.Add(new ColumnMapping { Source = "Valued", Target = "claim.valuationDate", Normalizer = new NormalizerSpec { Type = "date" } });
        spec.ColumnMap.Add(new ColumnMapping { Source = "Carrier", Target = "claim.rawCarrier" });

        var notes = SpecLint.RetargetRowLevelColumns(spec);

        Assert.Equal(["columnMap \"Valued\": claim.valuationDate → document.valuationDate", "columnMap \"Carrier\": claim.rawCarrier → document.carrierName"], notes.Take(2));
        Assert.Contains("documentFields document.carrierName: dropped (const locator); the column map reads it on every row", notes); // the column now supplies it
        Assert.Empty(SpecLint.RetargetRowLevelColumns(Graphs.Spec("synthetic-mutual-pdf"))); // PDFs are left to the model

        var more = Graphs.Spec("synthetic-mutual-csv");
        more.ColumnMap.Add(new ColumnMapping { Source = "LOB", Target = "document.lineOfBusiness" });
        more.ColumnMap.Add(new ColumnMapping { Source = "Pol Eff", Target = "claim.periodEffectiveDate", Normalizer = new NormalizerSpec { Type = "date" } });
        Assert.Equal(["columnMap \"LOB\": document.lineOfBusiness → claim.lineOfBusiness", "columnMap \"Pol Eff\": claim.periodEffectiveDate → period.effectiveDate"],
            SpecLint.RetargetRowLevelColumns(more));

        var duplicate = Graphs.Spec("synthetic-mutual-csv");
        duplicate.ColumnMap.Add(new ColumnMapping { Source = "Valued", Target = "document.valuationDate", Normalizer = new NormalizerSpec { Type = "date" } });
        duplicate.DocumentFields.Add(new DocumentFieldSpec { Target = "document.valuationDate", Locator = new FieldLocator { Type = "labelRight", Label = "Valued" } });
        Assert.Contains(SpecLint.RetargetRowLevelColumns(duplicate), n => n.StartsWith("documentFields document.valuationDate: dropped", StringComparison.Ordinal));
        Assert.DoesNotContain(duplicate.DocumentFields, f => f.Target == "document.valuationDate");
    }

    [Fact]
    public void A_printed_constant_is_rewritten_as_a_locator_without_a_model_call()
    {
        // 08b learn-reliability study: 15 of 20 failed learns ended on this lint, often with an otherwise verified spec.
        var spec = Graphs.Spec("synthetic-mutual-pdf");
        var lob = spec.DocumentFields.Single(f => f.Target == "document.lineOfBusiness");
        lob.Locator = new FieldLocator { Type = "const", Value = System.Text.Json.JsonSerializer.SerializeToElement("Workers' Compensation") };
        Assert.Contains(SpecVerifier.Verify(Graphs.Native(), spec).Issues, i => i.Contains("lineOfBusiness is declared as the constant", StringComparison.Ordinal));

        var notes = SpecLint.LocatePrintedConstants(spec, Graphs.Native(), CanonicalData.Glossary);
        var verification = SpecVerifier.Verify(Graphs.Native(), spec);

        Assert.Single(notes);
        Assert.Equal("regex", lob.Locator.Type);
        Assert.Empty(verification.Issues);
        Assert.False(string.IsNullOrEmpty(verification.Replay.Document.LineOfBusiness));
        Assert.Equal("replay", verification.Replay.Document.Lineage!["lineOfBusiness"].Extractor); // located, with lineage
    }

    [Fact]
    public void Defects_seen_on_the_first_live_learn_are_issues()
    {
        var proposal = JsonNode.Parse(Graphs.SecondProposal)!.AsObject();
        var fields = proposal["documentFields"]!.AsArray();
        fields.Add(JsonNode.Parse("""{"target":"document.jurisdictionState","locator":{"type":"const","value":"California"},"reason":"assumed"}"""));
        fields.Single(f => f!["target"]!.GetValue<string>() == "document.expenseTreatment")!["locator"]!["value"] = "mergedIntoMedical";
        fields.Single(f => f!["target"]!.GetValue<string>() == "document.insuredName")!["locator"]!["pattern"] = @"Named Insured:\s*(?<value>.+?)(?:\s*$)";
        // period.subtotals.claimCount is a valid total-row target since ADR 0017; a bare period count is still not a path.
        proposal["columnMap"]!.AsArray().Add(JsonNode.Parse("""{"source":"Claims","target":"period.claimCount","normalizer":{"type":"integer"}}"""));
        proposal["derivedRules"]!.AsArray().Add(JsonNode.Parse("""{"target":"claim.total.incurred","sum":["indemnity.paid"],"when":"absent","appliesTo":["claim"]}"""));
        var spec = ModelSchema.ToSpec(proposal.ToJsonString(), s => { s.FormatId = "candidate"; s.Version = 1; s.FileKind = "pdf"; });

        var issues = SpecVerifier.Verify(Graphs.Second(), spec).Issues;

        Assert.Contains(issues, i => i.Contains("document.jurisdictionState is declared as a constant", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Contains("expenseTreatment is \"mergedIntoMedical\"", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Contains("document.insuredName captured", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Contains("period.claimCount", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Contains("derivedRules path \"claim.total.incurred\"", StringComparison.Ordinal));
        Assert.Empty(SpecVerifier.Verify(Graphs.Second(), ModelSchema.ToSpec(Graphs.SecondProposal, s => { s.FormatId = "candidate"; s.Version = 1; s.FileKind = "pdf"; })).Issues);
    }
}
