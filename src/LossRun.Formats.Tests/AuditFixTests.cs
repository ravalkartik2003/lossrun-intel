using System.Text.RegularExpressions;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Formats.Replay;

namespace LossRun.Formats.Tests;

/// <summary>Brief 30: the audit's fixes in format memory, each with the test that failed before it.</summary>
public class AuditFixTests
{
    [Fact]
    public async Task Item4_a_value_that_does_not_normalize_on_replay_routes_to_review_with_its_page()
    {
        var clean = await new FormatMemory(await Graphs.RegistryAsync(), null).ProcessAsync(Graphs.Native(), "run", DateTimeOffset.UnixEpoch);
        Assert.Equal(RoutingTier.ReplayAccepted, clean.Decision.Tier);
        Assert.Equal(0, clean.Decision.UnnormalizedValues);

        // One money cell OCR'd with a letter O: the replay drops it, every rule still passes, and before brief 30 the
        // document was ReplayAccepted with the value (and the incurred derived from it) missing.
        var graph = Graphs.Native();
        var cell = graph.Cells.First(c => c.Row > 0 && Regex.IsMatch(c.RawValue.Trim(), @"^\$?[1-9][\d,]*\.\d\d$"));
        var original = cell.RawValue;
        cell.RawValue = "$" + Regex.Replace(original.TrimStart('$'), "0", "O", RegexOptions.None, TimeSpan.FromSeconds(1));
        var (_, replay, decision) = await new FormatMemory(await Graphs.RegistryAsync(), null).ProcessAsync(graph, "run", DateTimeOffset.UnixEpoch);

        var issue = Assert.Single(replay!.Unnormalized);
        Assert.Equal(cell.Page, issue.Page);
        Assert.NotNull(issue.Polygon);
        Assert.Equal(RoutingTier.Review, decision.Tier);
        Assert.Equal(1, decision.UnnormalizedValues);
        Assert.Contains(decision.Reasons, r => r.Contains(issue.FieldPath, StringComparison.Ordinal) && r.Contains($"page {cell.Page}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Item4_a_dropped_value_that_every_rule_tolerates_is_no_longer_accepted()
    {
        // A date OCR'd with a letter: before brief 30 the claim lost its report date and the run was ReplayAccepted, 0 LLM calls.
        var graph = Graphs.Csv();
        var cell = graph.Cells.First(c => c.RawValue.Trim() == "09/16/2022");
        cell.RawValue = "09/1G/2022";
        var (_, replay, decision) = await new FormatMemory(await Graphs.RegistryAsync(), null).ProcessAsync(graph, "run", DateTimeOffset.UnixEpoch);

        Assert.DoesNotContain(replay!.RuleOutcomes, o => o.Status == LossRun.Rules.RuleStatus.Fail && o.Severity == LossRun.Rules.RuleSeverity.Hard);
        var issue = Assert.Single(replay.Unnormalized);
        Assert.Equal(("policyPeriods[0].claims[0].dateReported", cell.A1), (issue.FieldPath, issue.Cell));
        Assert.Equal(RoutingTier.Review, decision.Tier);
        Assert.Contains(decision.Reasons, r => r.Contains("dateReported '09/1G/2022'", StringComparison.Ordinal) && r.Contains($"cell {cell.A1}", StringComparison.Ordinal));
    }

    /// <summary>A second carrier whose CSV export prints the same columns (identity is empty for CSV, so the exact keys collide).</summary>
    private static async Task<InMemoryFormatRegistry> TwinRegistryAsync()
    {
        var registry = await Graphs.RegistryAsync();
        var text = System.IO.File.ReadAllText(Graphs.File("formats", "synthetic-mutual-csv", "1.json"))
            .Replace("\"synthetic-mutual-csv\"", "\"other-casualty-csv\"", StringComparison.Ordinal)
            .Replace("Synthetic Mutual Insurance Co.", "Other Casualty Co.", StringComparison.Ordinal)
            .Replace("\"version\": 1", "\"version\": 3", StringComparison.Ordinal);
        await registry.PublishAsync(FormatJson.Parse(text));
        return registry;
    }

    [Fact]
    public async Task Item11_two_formats_sharing_the_exact_keys_without_a_hint_are_ambiguous_not_the_higher_version()
    {
        var registry = await TwinRegistryAsync();
        var match = await new FormatFingerprinter(registry, null).MatchAsync(Graphs.Csv(), "run");
        Assert.Null(match.Format); // before brief 30: other-casualty-csv v3, by version across formats
        Assert.Equal(["other-casualty-csv", "synthetic-mutual-csv"], match.Ambiguous);

        var (_, replay, decision) = await new FormatMemory(registry, null).ProcessAsync(Graphs.Csv(), "run", DateTimeOffset.UnixEpoch);
        Assert.Null(replay);
        Assert.Equal(RoutingTier.Review, decision.Tier);
        Assert.Contains(decision.Reasons, r => r.StartsWith("ambiguous format: other-casualty-csv, synthetic-mutual-csv", StringComparison.Ordinal));

        // The submission's carrier separates them.
        var hinted = await new FormatFingerprinter(registry, null).MatchAsync(Graphs.Csv(), "run", new SubmissionContext("Synthetic Mutual Insurance Co."));
        Assert.Equal("synthetic-mutual-csv", hinted.Format!.FormatId);
    }

    [Fact]
    public async Task Item17_approving_a_retired_format_is_refused_and_unretire_is_the_explicit_way_back()
    {
        var registry = await Graphs.RegistryAsync();
        await FormatLifecycle.RetireAsync(registry, "synthetic-mutual-csv", 1, "boss", "carrier changed its export", DateTimeOffset.UnixEpoch);

        // Before brief 30 this flipped the version back to active ("only checks != Active").
        await Assert.ThrowsAsync<InvalidOperationException>(() => FormatLifecycle.ApproveAsync(registry, "synthetic-mutual-csv", 1, "uw-1", DateTimeOffset.UnixEpoch));
        Assert.Equal(FormatLifecycle.Retired, (await registry.GetStatsAsync("synthetic-mutual-csv")).Lifecycle["1"].Status);

        await FormatLifecycle.UnretireAsync(registry, "synthetic-mutual-csv", 1, "eng-1", "retired by mistake", DateTimeOffset.UnixEpoch);
        var live = (await registry.GetStatsAsync("synthetic-mutual-csv")).Lifecycle["1"];
        Assert.Equal((FormatLifecycle.Active, "unretire:eng-1"), (live.Status, live.ActivatedBy));
    }

    [Theory]
    [InlineData(AliasDecision.Accept)]
    [InlineData(AliasDecision.Edit)]
    public void Item17_a_review_cannot_reactivate_a_retired_alias(string action)
    {
        var glossary = new LearnedGlossary();
        glossary.Mappings.Add(new LearnedMapping { Id = "x", Header = "Inj Typ", CanonicalField = "claim.claimType", Status = AliasStatus.Retired, RetiredBy = "uw-2" });

        Assert.Throws<InvalidOperationException>(() =>
            AliasLifecycle.Review(glossary, [new AliasDecision("Inj Typ", "claim.claimType", action, action == AliasDecision.Edit ? "claim.injuryTypeCode" : null)], "uw-1", DateTimeOffset.UnixEpoch));
        Assert.Equal(AliasStatus.Retired, glossary.Mappings[0].Status);
    }

    [Fact]
    public void Item38_a_counts_pattern_that_captures_text_is_an_issue_not_a_crash()
    {
        // A learned counts pattern that captures any token, on a run whose subtotal reads "(3 claims ...)": "claims" is captured as the count.
        var spec = Graphs.Spec("synthetic-mutual-pdf");
        foreach (var kind in spec.TableLocator.RowKinds)
        {
            kind.CountsRegex = @"\(\s*\d+\s+(?<claimCount>\S+?)\s*:";
        }

        var replay = ReplayEngine.Execute(Graphs.Native(), spec); // before brief 30: FormatException from decimal.Parse

        Assert.Contains(replay.Unnormalized, u => u.FieldPath.EndsWith(".claimCount", StringComparison.Ordinal) && u.Normalizer == "integer");
    }

    [Fact]
    public void Item37_a_catastrophic_learned_pattern_times_out_instead_of_hanging_the_replay()
    {
        var spec = Graphs.Spec("synthetic-mutual-pdf");
        spec.TableLocator.RowKinds[0].Regex = "^(a+)+$"; // exponential backtracking on a run of a's that does not end the line
        var graph = Graphs.Native();
        graph.Cells.First(c => c.Row > 0).RawValue = new string('a', 40) + "!";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<System.Text.RegularExpressions.RegexMatchTimeoutException>(() => ReplayEngine.Execute(graph, spec));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
    }

    /// <summary>The seen CSV export with one more column, mapped to the WCIO nature code by a code normalizer that names no system.</summary>
    private static (DocumentGraph Graph, CarrierFormat Spec) WithNatureColumn(string[] values)
    {
        var lines = System.IO.File.ReadAllLines(Graphs.File("fixtures", "lossrun-export.csv"));
        var csv = string.Join("\n", lines.Select((l, i) => i == 0 ? l + ",Nature Code" : i <= values.Length ? $"{l},\"{values[i - 1]}\"" : l + ","));
        var graph = LossRun.Ingestion.Spreadsheets.SpreadsheetReader.Read(System.Text.Encoding.UTF8.GetBytes(csv),
            new SourceFile { FileName = "nature.csv", Kind = FileKind.Csv }, "doc-nature", CanonicalData.Glossary, []);
        var spec = Graphs.Spec("synthetic-mutual-csv");
        spec.ColumnMap.Add(new ColumnMapping { Source = "Nature Code", Target = "claim.natureOfInjury.code", Normalizer = new NormalizerSpec { Type = "code" } });
        return (graph, spec);
    }

    [Fact]
    public void Item14_a_wcio_code_is_written_with_its_code_system()
    {
        var (graph, spec) = WithNatureColumn(["52", "4", "10"]);
        var replay = ReplayEngine.Execute(graph, spec);

        var nature = replay.Document.PolicyPeriods.SelectMany(p => p.Claims).Select(c => c.NatureOfInjury).ToList();
        Assert.All(nature, n => Assert.Equal("WCIO-Nature", n!.CodeSystem)); // before brief 30: codeSystem null
        Assert.Equal(["52", "04", "10"], nature.Select(n => n!.Code));
        Assert.Empty(replay.Unnormalized);
    }

    [Fact]
    public void Item14_a_descriptive_or_unknown_value_in_a_code_column_is_an_issue_not_a_code()
    {
        var (graph, spec) = WithNatureColumn(["STRAIN OR TEAR", "99", "52"]);
        var replay = ReplayEngine.Execute(graph, spec);

        Assert.Equal(["STRAIN OR TEAR", "99"], replay.Unnormalized.Select(u => u.Raw)); // before brief 30: both read as codes
        Assert.Null(replay.Document.PolicyPeriods.SelectMany(p => p.Claims).First().NatureOfInjury);
    }

    [Theory]
    [InlineData("52", "WCIO-Nature", "52")]
    [InlineData("4", "WCIO-Cause", "04")]
    [InlineData("STRAIN", "WCIO-Nature", null)]
    [InlineData("8810", "WCIRB-ClassCode", "8810")]
    [InlineData("88A", "WCIRB-ClassCode", null)]
    [InlineData("A12", null, "A12")]
    [InlineData("STRAIN OR TEAR", null, null)]
    public void Item14_the_code_normalizer_checks_the_declared_code_set(string raw, string? system, string? expected) =>
        Assert.Equal(expected, ValueNormalizer.Normalize(new NormalizerSpec { Type = "code", CodeSystem = system }, raw, CanonicalData.Glossary)?.GetValue<string>());

    /// <summary>The seen CSV export with the second row's policy cell left blank.</summary>
    private static DocumentGraph BlankPolicyCsv()
    {
        var lines = System.IO.File.ReadAllLines(Graphs.File("fixtures", "lossrun-export.csv"));
        lines[2] = "," + lines[2][(lines[2].IndexOf(',') + 1)..];
        return LossRun.Ingestion.Spreadsheets.SpreadsheetReader.Read(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines)),
            new SourceFile { FileName = "blank-policy.csv", Kind = FileKind.Csv }, "doc-blank", CanonicalData.Glossary, []);
    }

    [Fact]
    public void Item20_a_row_with_a_blank_policy_cell_is_attached_above_as_an_issue_with_a_lineage_note()
    {
        var replay = ReplayEngine.Execute(BlankPolicyCsv(), Graphs.Spec("synthetic-mutual-csv"));

        var issue = Assert.Single(replay.Unnormalized); // before brief 30: silently attached, no issue, no note
        Assert.Equal("policyPeriods[0].claims[1].periodAssignment", issue.FieldPath);
        Assert.Equal("fill-down", replay.Document.Lineage!["policyPeriods[0].claims[1].periodAssignment"].Extractor);
        Assert.Equal(2, replay.Document.PolicyPeriods[0].Claims.Count);

        // Brief 31 Answers 5: the review reason says what to decide, in words (before: "1 printed value(s) could not be normalized: …periodAssignment ''").
        var reason = Assert.Single(FormatRouter.UnnormalizedReasons(replay.Unnormalized));
        Assert.StartsWith("policy cell blank on 1 row(s)", reason, StringComparison.Ordinal);
        Assert.Contains("fill-down export, or a dropped cell? Confirm the format", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Item20_a_spec_that_declares_fill_down_keeps_the_note_without_the_issue()
    {
        var spec = Graphs.Spec("synthetic-mutual-csv");
        spec.Periods.FillDown = true;
        var replay = ReplayEngine.Execute(BlankPolicyCsv(), spec);

        Assert.Empty(replay.Unnormalized);
        Assert.Contains("filled-down", replay.Document.Lineage!["policyPeriods[0].claims[1].periodAssignment"].SourceText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Item36_a_publish_whose_index_write_failed_is_resumed_not_refused_forever()
    {
        var blobs = new FakeBlobContainer { FailIndexWrites = 6 };
        var registry = new BlobFormatRegistry(blobs);
        var spec = Graphs.Spec("synthetic-mutual-csv");

        await Assert.ThrowsAsync<Azure.RequestFailedException>(() => registry.PublishAsync(spec)); // index busy: the version blob is written, the index is not
        Assert.Empty(await registry.GetIndexAsync());

        await registry.PublishAsync(spec); // before brief 30: FormatAlreadyPublishedException on every retry, the version orphaned
        Assert.Single(await registry.GetIndexAsync(), e => e.FormatId == "synthetic-mutual-csv" && e.Version == 1);

        // A different spec under the same version is still refused.
        var other = Graphs.Spec("synthetic-mutual-csv");
        other.Description = "a different spec, same version";
        await Assert.ThrowsAsync<FormatAlreadyPublishedException>(() => registry.PublishAsync(other));
    }

    [Fact]
    public void Item41_the_total_row_tolerance_is_the_catalogs()
    {
        Assert.Equal(1m, LossRun.Rules.RuleCatalog.Embedded.ToleranceOf("R-10")); // replay used its own 0.01 before brief 30
        Assert.Equal(LossRun.Rules.RuleCatalog.Embedded.ToleranceOf("R-10"), LossRun.Rules.RuleCatalog.Embedded.ToleranceOf("R-12"));
    }

    [Fact]
    public async Task Item44_a_centroid_built_under_another_text_normalization_is_not_compared()
    {
        var registry = await Graphs.RegistryAsync();
        await FakeEmbeddingService.SeedCentroidAsync(registry, "synthetic-mutual-pdf", Graphs.Native());
        var graph = Graphs.Scanned();
        graph.FingerprintInputs.FirstTableHeaderCells.Add("forces an exact-tier miss");

        var current = await new FormatFingerprinter(registry, new FakeEmbeddingService()).MatchAsync(graph, "run");
        Assert.True(current.Similarities.ContainsKey("synthetic-mutual-pdf"));

        var stale = (await registry.GetCentroidAsync("synthetic-mutual-pdf"))!;
        stale.TextNormalization = "normalization-0";
        await registry.SaveCentroidAsync(stale);
        var after = await new FormatFingerprinter(registry, new FakeEmbeddingService()).MatchAsync(graph, "run");
        Assert.False(after.Similarities.ContainsKey("synthetic-mutual-pdf")); // before brief 30: compared, whatever text it was built from
    }
}
