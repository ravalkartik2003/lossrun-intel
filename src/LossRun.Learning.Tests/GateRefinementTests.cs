using System.Text.Json.Nodes;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Learning;

namespace LossRun.Learning.Tests;

/// <summary>Brief 06b: 06 review Answers 2 (gate per file kind), 3 (printed constants), 4 (shadow → active lifecycle).</summary>
public class GateRefinementTests
{
    private static CarrierFormat Candidate(string proposalJson) =>
        ModelSchema.ToSpec(proposalJson, s => { s.FormatId = "candidate"; s.Version = 1; s.FileKind = "pdf"; });

    // ------------------------------------------------------------------ Answers 2: RequireTotals per file kind

    [Fact]
    public void Pdf_needs_a_reconciling_printed_total()
    {
        var proposal = JsonNode.Parse(Graphs.SecondProposal)!.AsObject();
        proposal["tableLocator"]!["rowKinds"] = new JsonArray(JsonNode.Parse("""{"kind":"skip","regex":"^(Policy|Account) Total\\b"}"""));

        var verification = SpecVerifier.Verify(Graphs.Second(), Candidate(proposal.ToJsonString()), new SubmissionContext("Fictional Casualty Group"));

        Assert.Null(verification.TotalsTie);
        Assert.Null(verification.Gate()); // a PDF without totals is a review signal, even with the carrier from context
        Assert.Equal("pdf-totals", SpecVerifier.Verify(Graphs.Second(), Candidate(Graphs.SecondProposal)).Gate());
    }

    [Fact]
    public void Spreadsheet_with_printed_totals_passes_the_totals_gate()
    {
        var verification = SpecVerifier.Verify(Graphs.Xlsx(), Graphs.Spec("synthetic-mutual-xlsx"));

        Assert.True(verification.TotalsTie, verification.Summary);
        Assert.True(verification.Issues.Count == 0, string.Join(" | ", verification.Issues.Concat(verification.MissingRequired)));
        Assert.Equal("spreadsheet-totals", verification.Gate());
    }

    [Fact]
    public void Spreadsheet_without_totals_passes_on_rules_coverage_and_carrier_from_context()
    {
        var spec = Graphs.Spec("synthetic-mutual-xlsx");
        spec.Periods.Lookup!.Subtotals.Clear(); // the summary's dates still read; no printed totals left

        var withHint = SpecVerifier.Verify(Graphs.Xlsx(), spec, new SubmissionContext("Synthetic Mutual Insurance Co."));
        var withoutHint = SpecVerifier.Verify(Graphs.Xlsx(), spec);
        var otherCarrier = SpecVerifier.Verify(Graphs.Xlsx(), spec, new SubmissionContext("Fictional Casualty Group"));

        Assert.Null(withHint.TotalsTie);
        Assert.Equal(1.0, withHint.RequiredCoverage);
        Assert.Equal("spreadsheet-rules", withHint.Gate());
        Assert.Null(withoutHint.Gate());
        Assert.Null(otherCarrier.Gate());
    }

    [Fact]
    public void Csv_below_80_percent_coverage_goes_to_review_and_the_feedback_says_why()
    {
        var verification = SpecVerifier.Verify(Graphs.Csv(), Graphs.Spec("synthetic-mutual-csv"), new SubmissionContext("Synthetic Mutual Insurance Co."));

        Assert.Equal(8 / 11.0, verification.RequiredCoverage, 3); // no valuation date, no policy dates in the export
        Assert.Null(verification.Gate());
        Assert.Contains("80% required-field coverage (now 73", verification.Feedback());
    }

    [Fact]
    public void Csv_with_carrier_and_dates_from_the_submission_reaches_full_coverage()
    {
        var context = new SubmissionContext("Synthetic Mutual Insurance Co.", ValuationDate: new DateOnly(2024, 9, 30), PolicyPeriods:
        [
            new ContextPeriod("SYN-WC-1001", new DateOnly(2022, 7, 1), new DateOnly(2023, 7, 1)),
            new ContextPeriod("syn wc 1002", new DateOnly(2023, 7, 1), new DateOnly(2024, 7, 1)), // matched after normalization
        ]);

        var verification = SpecVerifier.Verify(Graphs.Csv(), Graphs.Spec("synthetic-mutual-csv"), context);

        Assert.Equal(1.0, verification.RequiredCoverage);
        Assert.Equal("spreadsheet-rules", verification.Gate());
        var lineage = verification.Replay.Document.Lineage!;
        Assert.Equal("context", lineage["valuationDate"].Extractor);
        Assert.Equal("context", lineage["policyPeriods[1].expirationDate"].Extractor);
        Assert.Equal(new DateOnly(2024, 7, 1), verification.Replay.Document.PolicyPeriods[1].ExpirationDate);
    }

    [Fact]
    public void Gate_policy_comes_from_the_data_file()
    {
        var gate = LearnGate.Default;

        Assert.Equal("learn-gate-0.3.0", gate.Version);
        Assert.Equal(11, gate.RequiredFields.Count);
        Assert.True(gate.FileKinds["pdf"].RequirePrintedTotals);
        Assert.Equal(0.8, gate.FileKinds["spreadsheet"].MinCoverage);
        Assert.Contains("document.valuationDate", gate.FileKinds["spreadsheet"].ConditionalFromContext);
    }

    [Fact]
    public async Task Multi_exemplar_learning_publishes_only_a_spec_that_verifies_on_every_exemplar()
    {
        var bad = Graphs.Second();
        bad.Cells.First(c => c.RawValue == "11,600.00").RawValue = "11,700.00"; // this exemplar's claim total contradicts its subtotal

        var ok = await new FormatLearner(FakeLearnModel.Sequence(Graphs.SecondProposal), new InMemoryFormatRegistry(), Graphs.Options(), clock: new FixedClock())
            .LearnAsync(new LearnRequest(Graphs.Second(), "run", Exemplars: [Graphs.SecondScanned()]));
        var model = FakeLearnModel.Sequence(Graphs.SecondProposal);
        var blocked = await new FormatLearner(model, new InMemoryFormatRegistry(), Graphs.Options(), clock: new FixedClock())
            .LearnAsync(new LearnRequest(Graphs.Second(), "run", Exemplars: [Graphs.SecondScanned(), bad]));

        Assert.True(ok.Published);
        Assert.False(blocked.Published);
        Assert.Equal(3, blocked.ModelCalls);
        Assert.Contains("fails on 1 of 2 other documents", model.Requests[1].Turns[^1].Text);
        Assert.All(blocked.Attempts, a => Assert.Equal("verified on the learning document; fails 1/2 exemplar(s)", a.Error));
    }

    // ------------------------------------------------------------------ Answers 3: printed constants

    [Fact]
    public void A_constant_whose_value_or_alias_is_printed_needs_a_regex()
    {
        var proposal = JsonNode.Parse(Graphs.SecondProposal)!.AsObject();
        var lob = proposal["documentFields"]!.AsArray().Single(f => f!["target"]!.GetValue<string>() == "document.lineOfBusiness")!;
        lob["locator"] = JsonNode.Parse("""{"type":"const","value":"WC"}""");
        lob["reason"] = "WC-only carrier";

        var issues = SpecVerifier.Verify(Graphs.Second(), Candidate(proposal.ToJsonString())).Issues;

        Assert.Contains(issues, i => i.Contains("document.lineOfBusiness is declared as the constant \"WC\" but \"workers compensation\" is printed", StringComparison.Ordinal));
        Assert.DoesNotContain(issues, i => i.Contains("expenseTreatment", StringComparison.Ordinal)); // "separate" is not printed
    }

    [Fact]
    public void Constants_for_values_the_document_does_not_print_stay_allowed()
    {
        var verification = SpecVerifier.Verify(Graphs.Csv(), Graphs.Spec("synthetic-mutual-csv"));

        Assert.DoesNotContain(verification.Issues, i => i.Contains("constant", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ Answers 4: shadow → active

    private static LearningPipeline Pipeline(InMemoryFormatRegistry registry, ILearnModel? model = null)
    {
        model ??= FakeLearnModel.Sequence(Graphs.SecondProposal);
        var learner = new FormatLearner(model, registry, Graphs.Options(), clock: new FixedClock());
        return new LearningPipeline(registry, null, learner, new FormatRepairer(model, learner, Graphs.Options()));
    }

    [Fact]
    public async Task New_carrier_is_learned_in_shadow_and_three_clean_replays_activate_it()
    {
        var registry = await Graphs.RegistryAsync();
        var pipeline = Pipeline(registry);

        var learned = await pipeline.ProcessAsync(Graphs.Second(), "run-0", DateTimeOffset.UnixEpoch);
        Assert.Equal(FormatLifecycle.Shadow, learned.Learn!.Spec!.Status);
        Assert.True(learned.ConfirmNewFormat);
        Assert.Equal("pdf-totals", learned.Learn.Spec.Learning!.VerificationGate);

        var badges = new List<string>();
        for (var i = 1; i <= 4; i++)
        {
            var r = await pipeline.ProcessAsync(Graphs.Second(), $"run-{i}", DateTimeOffset.UnixEpoch.AddHours(i));
            Assert.Equal(RoutingTier.ReplayAccepted, r.Decision.Tier); // shadow replays are processed, not held
            badges.Add(r.Decision.Badge);
            Assert.Equal(i <= 3, r.ConfirmNewFormat);
            Assert.Equal(i <= 3 ? i : null, r.Decision.ShadowReplay);
        }

        Assert.StartsWith("format learned · shadow 1/3", badges[0]);
        Assert.StartsWith("format learned · shadow 3/3", badges[2]);
        Assert.StartsWith("format memory hit", badges[3]);
        var live = (await registry.GetStatsAsync("fictional-casualty-group-pdf")).Lifecycle["1"];
        Assert.Equal((FormatLifecycle.Active, "clean-replays"), (live.Status, live.ActivatedBy));
    }

    [Fact]
    public async Task A_hard_failure_resets_the_shadow_count()
    {
        var registry = await Graphs.RegistryAsync();
        var pipeline = Pipeline(registry);
        await pipeline.ProcessAsync(Graphs.Second(), "run-0", DateTimeOffset.UnixEpoch);
        await pipeline.ProcessAsync(Graphs.Second(), "run-1", DateTimeOffset.UnixEpoch);
        await pipeline.ProcessAsync(Graphs.Second(), "run-2", DateTimeOffset.UnixEpoch);
        Assert.Equal(2, (await registry.GetStatsAsync("fictional-casualty-group-pdf")).Lifecycle["1"].CleanReplays);

        var bad = Graphs.Second();
        bad.Cells.First(c => c.RawValue == "11,600.00").RawValue = "11,700.00"; // claim total no longer reconciles
        var review = await pipeline.ProcessAsync(bad, "run-bad", DateTimeOffset.UnixEpoch);
        Assert.Equal(RoutingTier.Review, review.Decision.Tier);
        Assert.Equal(0, (await registry.GetStatsAsync("fictional-casualty-group-pdf")).Lifecycle["1"].CleanReplays);

        var next = await pipeline.ProcessAsync(Graphs.Second(), "run-3", DateTimeOffset.UnixEpoch);
        Assert.Equal(1, next.Decision.ShadowReplay); // soft warnings and flags would not have reset it; the hard failure did
    }

    [Fact]
    public async Task A_retired_version_takes_no_part_in_matching()
    {
        var registry = await Graphs.RegistryAsync();
        var memory = new FormatMemory(registry, null);
        Assert.Equal(RoutingTier.ReplayAccepted, (await memory.ProcessAsync(Graphs.Native(), "run", DateTimeOffset.UnixEpoch)).Decision.Tier);

        await FormatLifecycle.RetireAsync(registry, "synthetic-mutual-pdf", 1, "admin", "superseded test copy", DateTimeOffset.UnixEpoch);
        var after = await memory.ProcessAsync(Graphs.Native(), "run", DateTimeOffset.UnixEpoch);

        Assert.Null(after.Match.Format);
        Assert.Equal(RoutingTier.Learn, after.Decision.Tier);
        Assert.NotNull(await registry.GetAsync("synthetic-mutual-pdf", 1)); // never deleted: lineage of old documents still resolves
        Assert.Equal(FormatLifecycle.Retired, await FormatLifecycle.StatusAsync(registry, (await registry.GetAsync("synthetic-mutual-pdf", 1))!));
    }

    [Fact]
    public async Task Underwriter_approval_activates_a_shadow_format_immediately()
    {
        var registry = await Graphs.RegistryAsync();
        var pipeline = Pipeline(registry);
        await pipeline.ProcessAsync(Graphs.Second(), "run-0", DateTimeOffset.UnixEpoch);

        await FormatLifecycle.ApproveAsync(registry, "fictional-casualty-group-pdf", 1, "uw-1", DateTimeOffset.UnixEpoch);
        var next = await pipeline.ProcessAsync(Graphs.Second(), "run-1", DateTimeOffset.UnixEpoch);

        Assert.Equal((FormatLifecycle.Active, false, (int?)null), (next.Decision.FormatStatus, next.ConfirmNewFormat, next.Decision.ShadowReplay));
        Assert.Equal("approval:uw-1", (await registry.GetStatsAsync("fictional-casualty-group-pdf")).Lifecycle["1"].ActivatedBy);
    }

    [Fact]
    public async Task Drift_and_delta_versions_inherit_the_live_status()
    {
        // Delta on an active hand spec → active v2.
        var registry = await Graphs.RegistryAsync();
        const string delta = """{"additions":[{"source":"Date Rptd","target":"claim.dateReported","normalizer":{"type":"date"}},{"source":"Lit","target":"claim.litigationFlag","normalizer":{"type":"boolean"}}],"ignoredHeaders":[]}""";
        var repaired = await Pipeline(registry, new FakeLearnModel((_, _) => delta)).ProcessAsync(Graphs.Drifted(), "run", DateTimeOffset.UnixEpoch);
        Assert.Equal(FormatLifecycle.Active, repaired.Learn!.Spec!.Status);
        Assert.False(repaired.ConfirmNewFormat);
        Assert.Equal("inherited:synthetic-mutual-pdf@1", (await registry.GetStatsAsync("synthetic-mutual-pdf")).Lifecycle["2"].ActivatedBy);

        // Drift learn on a shadow format → shadow v2.
        var shadow = (await Pipeline(registry).ProcessAsync(Graphs.Second(), "run-s", DateTimeOffset.UnixEpoch)).Learn!.Spec!;
        var relearn = await new FormatLearner(FakeLearnModel.Sequence(Graphs.SecondProposal), registry, Graphs.Options(), clock: new FixedClock())
            .LearnAsync(new LearnRequest(Graphs.Second(), "run-d", Prior: shadow, Reason: "drift"));
        Assert.Equal((2, FormatLifecycle.Shadow), (relearn.Spec!.Version, relearn.Spec.Status));
    }
}
