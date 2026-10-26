using System.Text.Json.Nodes;
using LossRun.Contracts;

namespace LossRun.Evaluation.Tests;

/// <summary>Brief 31: follow-ups from brief 30's audit (generator code systems, the all-fields study, seal by policy, the batch cap formula).</summary>
public class Brief31Tests
{
    private static readonly Dictionary<string, string> Declared = new(StringComparer.Ordinal)
    {
        ["natureOfInjury"] = "WCIO-Nature", ["causeOfInjury"] = "WCIO-Cause", ["partOfBody"] = "WCIO-PartOfBody",
    };

    [Fact]
    public void Item1_generated_coded_values_name_the_code_sets_table_of_their_field()
    {
        var document = new LossRunDocument { PolicyPeriods = [new PolicyPeriod { Claims = [.. Enumerable.Range(0, 40).Select(_ => new Claim { ClaimType = ClaimType.Indemnity })] }] };
        TruthFactory.EnrichV3(document, new Random(8));

        foreach (var claim in document.PolicyPeriods[0].Claims)
        {
            // Before brief 31: "WCIO", which is no table in code-sets.json (the schema names WCIO-Nature, -Cause, -PartOfBody).
            Assert.Equal(("WCIO-Nature", "WCIO-Cause", "WCIO-PartOfBody"), (claim.NatureOfInjury!.CodeSystem, claim.CauseOfInjury!.CodeSystem, claim.PartOfBody!.CodeSystem));
            Assert.Contains(CanonicalData.CodeSets.Tables["WCIO-Nature"].Codes, c => c.Code == claim.NatureOfInjury.Code);
            Assert.Contains(CanonicalData.CodeSets.Tables["WCIO-Cause"].Codes, c => c.Code == claim.CauseOfInjury.Code);
            Assert.Contains(CanonicalData.CodeSets.Tables["WCIO-PartOfBody"].Codes, c => c.Code == claim.PartOfBody.Code);
        }
    }

    [Fact]
    public void Item1_the_construct_truth_files_name_code_sets_tables_too()
    {
        var dir = Path.Combine(EvalRunner.FindUp("src/TestShared/constructs")!);
        foreach (var file in Directory.GetFiles(dir, "*.truth.json"))
        {
            foreach (var (field, system) in Systems(JsonNode.Parse(File.ReadAllText(file))))
            {
                Assert.True(Declared.TryGetValue(field, out var expected) && expected == system, $"{Path.GetFileName(file)}: {field}.codeSystem = {system}");
            }
        }
    }

    private static IEnumerable<(string Field, string System)> Systems(JsonNode? node)
    {
        if (node is JsonObject o)
        {
            foreach (var (key, value) in o)
            {
                if (value is JsonObject coded && coded["codeSystem"] is JsonValue s)
                {
                    yield return (key, s.GetValue<string>());
                }

                foreach (var inner in Systems(value))
                {
                    yield return inner;
                }
            }
        }
        else if (node is JsonArray a)
        {
            foreach (var inner in a.SelectMany(Systems))
            {
                yield return inner;
            }
        }
    }

    [Theory]
    [InlineData("recoverySubrogation", "747.90", "747.9")]
    [InlineData("netIncurred", "21329.00", "21329")]
    [InlineData("litigationFlag", "true", "True")]
    public void Item2_money_and_flags_outside_the_triplets_compare_by_value(string field, string expected, string actual) =>
        Assert.True(CanonicalComparer.Default.Compare(Scorer.KindOf(field), expected, actual, null).Match); // before: text, so "747.90" != "747.9"

    /// <summary>A small corpus: 12 one-file logical documents of one carrier and input type; d01 and d02 print the same policy, so do d05, d06, d07.</summary>
    private static string PolicyCorpus(out string policies)
    {
        var dir = Path.Combine(Path.GetTempPath(), "lossrun-seal-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        var manifest = new CorpusManifest
        {
            Seed = null, DocsRoot = "docs",
            Entries = [.. Enumerable.Range(1, 12).Select(i => new ManifestEntry($"d{i:00}", $"d{i:00}.pdf", "c1", "blocks", "native-pdf", $"d{i:00}", [], $"sha{i:00}", 1, 1))],
        };
        File.WriteAllText(Path.Combine(dir, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(manifest, CanonicalJson.Options));
        policies = Path.Combine(dir, "policies.csv");
        var lines = Enumerable.Range(1, 12).Select(i => $"d{i:00}.pdf,POL-{i:00}").ToList();
        lines[1] = "d02.pdf,POL-01";                    // d02 shares d01's policy
        lines.Add("d06.pdf,POL-05");                     // d06 prints two policies, one of them d05's
        lines.Add("d07.pdf,POL-06");                     // d07 shares d06's second one: d05, d06, d07 are one cluster
        File.WriteAllText(policies, "file,policyNumber" + Environment.NewLine + string.Join(Environment.NewLine, lines));
        return dir;
    }

    [Fact]
    public void Item3_documents_sharing_a_policy_are_never_split_between_tuning_and_blind()
    {
        var corpus = PolicyCorpus(out var policies);
        foreach (var seed in Enumerable.Range(1, 40))
        {
            var seal = Sealing.Split(corpus, tuningSize: 5, seed: seed, policiesCsv: policies);
            var tuning = seal.Tuning.Select(t => t.Id).ToHashSet();
            Assert.Equal(tuning.Contains("d01"), tuning.Contains("d02"));
            Assert.Equal(tuning.Contains("d05"), tuning.Contains("d06"));
            Assert.Equal(tuning.Contains("d06"), tuning.Contains("d07"));
        }

        // Without the map the split is today's (logical documents), so an existing seal reproduces; at some seed d01 and d02 split.
        Assert.Contains(Enumerable.Range(1, 40), seed => Sealing.Split(corpus, 5, seed).Tuning.Any(t => t.Id == "d01") != Sealing.Split(corpus, 5, seed).Tuning.Any(t => t.Id == "d02"));
        Assert.Equal(Sealing.Split(corpus, 5, 8).Tuning.Select(t => t.Id), Sealing.Split(corpus, 5, 8, policiesCsv: null).Tuning.Select(t => t.Id));
    }

    [Fact]
    public void Item7_22_reconcile_model_lines_come_from_the_model_config_and_the_bill_meters_from_the_price_table()
    {
        var prices = LossRun.Ingestion.Metering.PriceTable.Embedded;
        // Today's config/models.json: learn and delta both gpt-5.4-mini (ADR 0004), so one set of model lines and no gpt-5.2 line.
        var models = LossRun.Learning.LearnOptions.FromConfiguration();
        var configured = Reconcile.LineNames([models.LearnDeployment, models.DeltaDeployment], models.EmbeddingModel, prices);
        Assert.Equal(["gpt-5.4-mini input (uncached)", "gpt-5.4-mini cached input", "gpt-5.4-mini output", "text-embedding-3-large"],
            configured.Where(n => !n.StartsWith("CU ", StringComparison.Ordinal) && !n.StartsWith("Document Intelligence", StringComparison.Ordinal)));

        // The learn model moved to gpt-5.2 (quota granted): its lines appear with no code change, each claiming its own bill meter.
        string[] moved = ["gpt-5.2", "gpt-5.4-mini"];
        Assert.Equal("gpt-5.2 cached input", Reconcile.LineBilling("5.2 cd Inp Dz glbl", moved, "text-embedding-3-large", prices));
        Assert.Equal("gpt-5.2 input (uncached)", Reconcile.LineBilling("5.2 Inp Dz glbl", moved, "text-embedding-3-large", prices));
        Assert.Equal("gpt-5.4-mini cached input", Reconcile.LineBilling("5.4 mini cd Inp Dz glbl", moved, "text-embedding-3-large", prices));
        Assert.Equal("gpt-5.4-mini output", Reconcile.LineBilling("5.4 mini Opt Dz glbl", moved, "text-embedding-3-large", prices));
        Assert.Equal("text-embedding-3-large", Reconcile.LineBilling("text embedding 3 large Tokens", moved, "text-embedding-3-large", prices));

        // A deployment named apart from its model is priced and billed as its model; a model without a price is a config error, not a silent gap.
        var named = prices.WithDeployments("learn-prod=gpt-5.2");
        Assert.Contains("gpt-5.2 output", Reconcile.LineNames(["learn-prod"], null, named));
        Assert.Throws<InvalidDataException>(() => Reconcile.LineNames(["gpt-9"], null, prices));
    }

    [Fact]
    public void Answers2_every_table_names_the_truth_it_was_scored_against()
    {
        var run = new RunMetadata("3228641349c549b7acdd50ccb41ee0a7aa7d2132", new DateTimeOffset(2026, 10, 26, 4, 14, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 26, 4, 24, 0, TimeSpan.Zero),
            "local", false, "gpt-5.4-mini", "2026-03-17", "gpt-5.4-mini", "2026-03-17", null, "learn-v11", "learn-gate-0.3.0", "0.1.0", "0.11.0", "corpus-gen-0.7.0", 8, 332, []);
        var report = new EvalReport { Run = run, TruthVersion = "corpus-gen-0.9.0 seed 8" };

        // A re-score: the run's corpus was 0.7.0, the truth 0.9.0; both are on the stamp, at the top of the deck table and the report.
        Assert.EndsWith("corpus `corpus-gen-0.7.0` seed 8 · truth `corpus-gen-0.9.0 seed 8`", Report.Stamp(report), StringComparison.Ordinal);
        Assert.StartsWith("Run 2026-10-26 04:14–04:24 UTC", Report.DeckMarkdown(report), StringComparison.Ordinal); // before: the deck table had no stamp
        Assert.Equal("Scored against truth `corpus-gen-0.9.0 seed 8`", Report.Stamp(new EvalReport { TruthVersion = "corpus-gen-0.9.0 seed 8" }));
    }

    [Fact]
    public void Answers3_a_run_records_the_never_run_artefacts_it_supersedes()
    {
        var root = Directory.CreateTempSubdirectory("artefact-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            Directory.CreateDirectory(Path.Combine(root, "docs", "evaluation"));
            File.WriteAllText(Path.Combine(root, PromptArtefact.PathsFile), "prompt.txt\n");
            File.WriteAllText(Path.Combine(root, "prompt.txt"), "v3\n");
            var current = PromptArtefact.Compute(root)!;
            var artefact = Path.Combine(root, "docs", "evaluation", "learn-prompt-artefact.json");
            File.WriteAllText(artefact, $$"""{ "sha256": "{{current}}", "supersedes": ["{{new string('b', 64)}}", "{{new string('a', 64)}}"] }""");

            Assert.Equal([new string('b', 64), new string('a', 64)], PromptArtefact.Supersedes(root));
            File.WriteAllText(Path.Combine(root, "prompt.txt"), "v4\n"); // stale artefact file: the chain is not this code's
            Assert.Null(PromptArtefact.Supersedes(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Answers8_the_multi_turn_gate_is_the_median_of_three_runs()
    {
        static IReadOnlyList<ChatMultiTurnEval.Scored> Run(int passing, bool leak = false) => [.. Enumerable.Range(0, 40).Select(i =>
            new ChatMultiTurnEval.Scored("c", i % 8 + 1, "q", "a", i < passing ? [] : ["fact"], 0, 0, leak && i == 0, 0.003m, 100, 0))];

        // The 2026-10-26 wake: 34 then 37 on the same code. One run decided the gate before; the median of three does now.
        Assert.False(ChatMultiTurnEval.Judge([Run(34)]).Pass);
        var verdict = ChatMultiTurnEval.Judge([Run(34), Run(37), Run(38)]);
        Assert.Equal((37.0, true), (verdict.Median, verdict.Pass));
        Assert.False(ChatMultiTurnEval.Judge([Run(34), Run(35), Run(39)]).Pass); // median 35 < 36 (90 % of 40)
        Assert.False(ChatMultiTurnEval.Judge([Run(38), Run(38, leak: true), Run(39)]).Pass); // a leak in any run fails the gate
    }
}
