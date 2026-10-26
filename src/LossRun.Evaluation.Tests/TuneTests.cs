using LossRun.Data;

namespace LossRun.Evaluation.Tests;

/// <summary>Brief 27: lossrun tune's deterministic pieces — cause classification, the ranked fix list, the stopping verdict, batch folders.</summary>
public class TuneTests
{
    private static readonly TuningConfig Config = TuningConfig.Load(Path.Combine(Root(), "config", "tuning-config.json"));

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "config", "tuning-config.json")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("config/tuning-config.json");
    }

    private static DocOutcome Doc(string id, string input = "native-pdf", string tier = "ReplayAccepted", bool consistent = true, int truthClaims = 3) =>
        new(id, "Carrier A", input, "b", "t", [], consistent, tier, tier != "Review", 0, true, true, true, true, 1, 0.01m, 0, truthClaims, truthClaims, truthClaims, true, null);

    private static RunResult Run(string id, string? error = null, string tier = "ReplayAccepted", bool learnPublished = true, int calls = 0, string[]? reasons = null,
        string[]? hard = null, string? formatRef = "fmt-a@1", bool noDocuments = false) => new()
    {
        Id = id, Error = error,
        Documents = noDocuments ? [] : [new ExtractedDocument
        {
            FinalTier = tier, FormatRef = formatRef, LearnPublished = learnPublished, ModelCalls = calls, Reasons = [.. reasons ?? []], HardFailureRules = [.. hard ?? []],
            HardFailures = hard?.Length ?? 0,
        }],
    };

    private static FieldObservation Miss(string doc, string field, string error) => new(doc, "Carrier A", "native-pdf", "b", "t", "claim", field, false, error, "1", "2");

    [Fact]
    public void Each_cause_has_its_rule_and_the_first_rule_that_applies_wins()
    {
        string Cause(DocOutcome d, RunResult r, params FieldObservation[] f) => Assert.Single(Tune.Classify(d, r, f)).Category;

        Assert.Empty(Tune.Classify(Doc("ok"), Run("ok"), []));
        Assert.Equal("g", Cause(Doc("e"), Run("e", error: "NullReferenceException: boom")));
        Assert.Equal("f", Cause(Doc("f", consistent: false, tier: "Review"), Run("f", tier: "Review")));
        Assert.Equal("d", Cause(Doc("d", input: "scan-150dpi-fax", tier: "Review"), Run("d", tier: "Review")));
        Assert.Equal("d", Cause(Doc("d2", tier: "Review"), Run("d2", tier: "Review", reasons: ["200 dpi scan is below 280 dpi: not learned from"])));
        Assert.Equal("c", Cause(Doc("c", tier: "Review"), Run("c", noDocuments: true)));
        Assert.Equal("a", Cause(Doc("a", tier: "Review"), Run("a", tier: "Review", reasons: ["3 unmapped header(s): Clmt, Ind Pd, Med Pd"])));
        Assert.Equal("b", Cause(Doc("b", tier: "Review"), Run("b", tier: "Review", learnPublished: false, calls: 3, reasons: ["learn exhausted 3 iteration(s)"])));
        Assert.Equal("e", Cause(Doc("e2", tier: "Review"), Run("e2", tier: "Review", hard: ["R-10"])));
        Assert.Equal("a", Cause(Doc("w"), Run("w"), Miss("w", "claim.medical.paid", "wrong"), Miss("w", "claim.medical.reserve", "wrong-but-plausible")));
        Assert.Equal("b", Cause(Doc("m"), Run("m"), Miss("m", "claim.dateOfInjury", "missing"), Miss("m", "claim.claimNumber", "missing"), Miss("m", "claim.medical.paid", "wrong")));
        Assert.Equal("g", Cause(Doc("u", tier: "Review"), Run("u", tier: "Review")));
    }

    [Fact]
    public void Evidence_carries_rule_ids_and_scrubbed_reasons_never_values()
    {
        var f = Assert.Single(Tune.Classify(Doc("x", tier: "Review"), Run("x", tier: "Review", hard: ["R-10"], reasons: ["total 12,345.00 differs from \"Jordan Example\" (row 4: CLM-778812)"]), []));
        var evidence = string.Join(" ", f.Evidence);
        Assert.Contains("R-10", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("12,345", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("Jordan", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("778812", evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fix_list_groups_by_cause_and_key_ranks_by_documents_and_leaves_source_inconsistency_out()
    {
        TuneFailure F(string doc, string cause, string key) => new(doc, "native-pdf", "Carrier A", cause, "why", key, ["R-10"]);
        var fixes = Tune.Rank([F("1", "a", "fmt-a@1"), F("2", "a", "fmt-a@1"), F("3", "b", "fmt-b@1"), F("4", "g", "fmt-c@1"), F("5", "f", "fmt-a@1"), F("6", "b", "fmt-b@1"), F("7", "b", "fmt-b@1")]);
        Assert.Equal([("b", 3, 2), ("a", 2, 1), ("g", 1, 1)], fixes.Select(x => (x.Category, x.Documents, x.Tier)));
        Assert.Equal([1, 2, 3], fixes.Select(x => x.Rank));
        Assert.DoesNotContain(fixes, x => x.Category == "f");
    }

    private static StratumAccuracy Stratum(string s, int docs, double lower, string status) =>
        new(s, docs, new Proportion(95, 100, lower + 0.02, lower, 1, 1, docs), Config.Target(s).KeyFieldAccuracyLower, status);

    private static readonly Proportion LowReview = new(1, 10, 0.1, 0.02, 0.4, 1, 10);

    private static TuneBatch Previous(int batch, int tier2) =>
        new(batch, "abc", "stub", DateTimeOffset.UnixEpoch, "corpus", 10, new Dictionary<string, int>(), [], LowReview, 0, 0, null, new GrammarChanges(null, "abc", [], 0), tier2, 0, [], [],
            new TuneVerdict("continue", []), "1.0.0");

    [Fact]
    public void The_verdict_follows_the_stopping_criteria()
    {
        var ok = new[] { Stratum("native-pdf", 20, 0.98, "met"), Stratum("csv", 2, 0.5, "insufficient sample") };
        Assert.Equal("continue", Tune.Verdict(1, ok, LowReview, 0.5m, 5, 0, null, Config).Status); // zero design changes shows only from batch 2
        Assert.Equal("met", Tune.Verdict(2, ok, LowReview, 0.5m, 5, 0, Previous(1, 3), Config).Status);
        Assert.Equal("continue", Tune.Verdict(2, [Stratum("native-pdf", 20, 0.90, "not met")], LowReview, 0.5m, 5, 0, Previous(1, 3), Config).Status);
        Assert.Equal("continue", Tune.Verdict(2, ok, new Proportion(5, 10, 0.5, 0.2, 0.8, 1, 10), 0.5m, 5, 0, Previous(1, 3), Config).Status); // review rate above 30 %
        Assert.Equal("stop: not converging", Tune.Verdict(2, ok, LowReview, 0.5m, 5, 3, Previous(1, 2), Config).Status);
        Assert.Equal("continue", Tune.Verdict(3, [Stratum("native-pdf", 20, 0.90, "not met")], LowReview, 0.5m, 5, 1, Previous(2, 2), Config).Status); // fewer than last time
        Assert.Equal("stop: budget", Tune.Verdict(2, ok, LowReview, 5.01m, 5, 0, Previous(1, 0), Config).Status);
        Assert.Equal("stop: max batches", Tune.Verdict(Config.MaxBatches, [Stratum("native-pdf", 20, 0.90, "not met")], LowReview, 0.5m, 5, 0, Previous(4, 0), Config).Status);
    }

    [Fact]
    public void Batch_folders_number_and_find_their_predecessor_and_blind_paths_are_refused()
    {
        var root = Path.Combine(Path.GetTempPath(), "tune-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "batch-01"));
            File.WriteAllText(Path.Combine(root, "batch-01", "batch.json"),
                System.Text.Json.JsonSerializer.Serialize(Previous(1, 2), LossRun.Contracts.CanonicalJson.Options));
            Directory.CreateDirectory(Path.Combine(root, "batch-03")); // no batch.json: not a predecessor
            Assert.Equal(4, Tune.BatchNumber(Path.Combine(root, "batch-04")));
            Assert.Equal(1, Tune.PreviousBatch(Path.Combine(root, "batch-04"))!.Batch);
            Assert.Null(Tune.PreviousBatch(Path.Combine(root, "batch-01")));
        }
        finally
        {
            Directory.Delete(root, true);
        }

        Assert.True(Tune.IsBlindPath("/work/blind/results"));
        Assert.True(Tune.IsBlindPath(@"C:\work\Blind\results"));
        Assert.False(Tune.IsBlindPath("/work/blind-report"));
    }

    [Fact]
    public void Grammar_paths_include_the_learn_prompt_artefact_list()
    {
        var cwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Root());
            var paths = Tune.GrammarPathspecs(Config);
            Assert.Contains("src/LossRun.Learning/PromptBuilder.cs", paths);
            Assert.Contains("src/LossRun.Learning/SpecLint*.cs", paths);
            Assert.Contains("docs/evaluation/rule-catalog.json", paths);
            Assert.DoesNotContain("src/LossRun.Learning/FormatLearner.cs", paths); // brief 27 Answers 3: counted only in tier-2 commits
            Assert.Equal(["src/LossRun.Learning/FormatLearner.cs"], Config.LabelledGrammarPaths);
            Assert.DoesNotContain(paths, p => p.StartsWith('#') || p.StartsWith('@'));
        }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
        }
    }

    [Fact]
    public void Strata_split_native_pdfs_by_template_and_carry_the_acceptance_targets()
    {
        Assert.Equal("native-pdf/recurring", Tune.StratumOf("native-pdf", "seen"));
        Assert.Equal("native-pdf/recurring", Tune.StratumOf("native-pdf", "unseen-repeat"));
        Assert.Equal("native-pdf/first-seen", Tune.StratumOf("native-pdf", "first-seen"));
        Assert.Equal("xlsx", Tune.StratumOf("xlsx", "seen"));
        Assert.Equal((0.99, 0.05), (Config.Target("native-pdf/recurring").KeyFieldAccuracyLower, Config.Target("native-pdf/recurring").MaxReviewRate!.Value));
        Assert.Equal(("prefill", 0.85), (Config.Target("scan-150dpi-fax").Measure, Config.Target("scan-150dpi-fax").KeyFieldAccuracyLower));
        Assert.Equal(0.2, Config.MaxReviewRate);

        var capped = new StratumAccuracy("native-pdf/recurring", 20, new Proportion(1000, 1000, 1, 0.995, 1, 1, 20), 0.99, "review rate above cap", "delivered",
            new Proportion(2, 20, 0.1, 0.02, 0.3, 1, 20), 0.05);
        var verdict = Tune.Verdict(2, [capped], LowReview, 0.5m, 5, 0, Previous(1, 0), Config);
        Assert.Equal("continue", verdict.Status);
        Assert.Contains(verdict.Reasons, r => r.Contains("review rate 10% > its cap 5%", StringComparison.Ordinal));
    }

    [Fact]
    public void With_the_spec_a_plain_mapping_mistake_is_data_and_a_construct_gap_is_design()
    {
        var spec = LossRun.Formats.FormatJson.Parse(File.ReadAllText(Path.Combine(Root(), "src", "TestShared", "formats", "synthetic-mutual-csv", "1.json")));
        Assert.Equal("a", Tune.SpecCause(spec, "claim.medical.paid", "").Category); // plain column → alias/mapping mistake
        Assert.Equal("b", Tune.SpecCause(spec, "claim.medical.incurred", "").Category); // derived sum → construct
        Assert.Equal("b", Tune.SpecCause(spec, "claim.lossDescription", "").Category); // no column reaches it
        Assert.Equal("a", Tune.SpecCause(spec, "claim.lossDescription", "2 unmapped header(s): Desc").Category); // …while headers were left unmapped

        LossRun.Formats.CarrierFormat? Resolve(string? r) => r == spec.Ref ? spec : null;
        var run = Run("s", formatRef: spec.Ref);
        Assert.Equal("a", Assert.Single(Tune.Classify(Doc("s"), run, [Miss("s", "claim.medical.paid", "missing"), Miss("s", "claim.medical.reserve", "missing")], Resolve)).Category);
        Assert.Equal("b", Assert.Single(Tune.Classify(Doc("s"), run, [Miss("s", "claim.medical.incurred", "wrong")], Resolve)).Category);
        Assert.StartsWith("spec synthetic-mutual-csv@1", Assert.Single(Tune.Classify(Doc("s"), run, [Miss("s", "claim.medical.paid", "wrong")], Resolve)).Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tier_two_fix_gets_a_pull_request_body_that_stops_for_a_human()
    {
        var fix = new TuneFix(1, "b", 2, "fmt-b@1", 3, ["d1", "d2", "d3"], ["R-02", "learn exhausted # iteration(s)"], Tune.Causes["b"].Action);
        var body = Tune.PullRequestBody(Previous(2, 0), fix);
        Assert.Contains("Tier 2 (design change)", body, StringComparison.Ordinal);
        Assert.Contains("ADR", body, StringComparison.Ordinal);
        Assert.Contains("a human reviews and merges", body, StringComparison.Ordinal);
        Assert.Contains("corpus v5", body, StringComparison.Ordinal);
        Assert.Contains("`tier-2`", body, StringComparison.Ordinal);
    }
}
