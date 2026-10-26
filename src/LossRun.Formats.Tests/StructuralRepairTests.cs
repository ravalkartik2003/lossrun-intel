using System.Text.Json;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Formats.Replay;
using LossRun.Rules;
using Xunit.Abstractions;

namespace LossRun.Formats.Tests;

/// <summary>Brief 08c: deterministic table repairs in replay, c1 spec v2, learn-from-scan policy.</summary>
public class StructuralRepairTests(ITestOutputHelper output)
{
    private static DocumentGraph RunGraph(string name) =>
        JsonSerializer.Deserialize<DocumentGraph>(File.ReadAllText(Graphs.File("graphs", name)), CanonicalJson.Options)!;

    private static CarrierFormat C2Spec() => FormatJson.Parse(File.ReadAllText(Graphs.File("graphs", "fictional-casualty-group-pdf.v2.1.json")));

    private static CarrierFormat C1Spec(int version) => FormatJson.Parse(File.ReadAllText(Graphs.File("formats", "synthetic-mutual-pdf", $"{version}.json")));

    /// <summary>The recorded run whose CU result merged two headers into one cell, and the spec it replays with (brief 32 item 2 reuses them).</summary>
    internal static DocumentGraph MergedHeaderGraph() => RunGraph("c1-merged-header.graph.json");

    internal static CarrierFormat MergedHeaderSpec() => C1Spec(3);

    private static int Hard(ReplayResult r) => r.RuleOutcomes.Count(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard);

    [Fact]
    public void A_header_cell_cu_returned_blank_is_read_as_the_source_its_sibling_tables_print()
    {
        // c2-011 native (08b corpus v2): CU returned "Med. Pd" blank in the first claim table; the period's subtotal
        // could not tie and the document went to Review.
        var graph = RunGraph("c2-blank-header.graph.json");
        Assert.Equal("", graph.Tables.First(t => t.Id == "t1").HeaderTexts[8]);

        var replay = ReplayEngine.Execute(graph, C2Spec());
        output.WriteLine(string.Join(" | ", replay.StructuralRepairs));

        Assert.Equal(0, Hard(replay));
        Assert.Contains(replay.StructuralRepairs, r => r.Contains("\"Med. Pd\"", StringComparison.Ordinal) && r.Contains("sibling", StringComparison.Ordinal));
        Assert.Equal(5172.75m, replay.Document.PolicyPeriods[0].Claims[0].Medical!.Paid);
        Assert.Equal(40, replay.Stats.ClaimRows);
    }

    [Fact]
    public void Without_a_sibling_the_one_missing_source_fills_a_single_blank_in_spec_order()
    {
        var graph = RunGraph("c2-blank-header.graph.json");
        foreach (var table in graph.Tables.Where(t => t.Id != "t1" && t.HeaderTexts.Count == 13))
        {
            table.HeaderTexts[8] = "Med Pd (other)"; // no sibling agrees any more
        }

        var replay = ReplayEngine.Execute(graph, C2Spec());

        Assert.Contains(replay.StructuralRepairs, r => r.StartsWith("blank header in t1 column 9", StringComparison.Ordinal) && r.Contains("lacks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_blank_header_document_is_now_delivered_with_zero_model_calls()
    {
        var registry = new InMemoryFormatRegistry();
        await registry.PublishAsync(C2Spec());

        var (_, replay, decision) = await new FormatMemory(registry, null).ProcessAsync(RunGraph("c2-blank-header.graph.json"), "run", DateTimeOffset.UtcNow,
            submission: new SubmissionContext("Fictional Casualty Group"));
        output.WriteLine($"{decision.Tier} | {string.Join(" | ", decision.Reasons)}");

        Assert.Equal(RoutingTier.ReplayAccepted, decision.Tier);
        Assert.Equal(0, replay!.ModelCalls);
        Assert.Contains(decision.Reasons, r => r.StartsWith("deterministic table repair", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void A_total_row_whose_money_cells_sit_one_column_off_is_realigned_when_it_reconciles(int shift)
    {
        // The label spans one more (or one fewer) cell, so every money cell of the account total moves one column.
        var graph = RunGraph("c2-blank-header.graph.json");
        var cells = Row(graph, "Account Total").Where(c => c.Column >= 5).ToDictionary(c => c.Column);
        var printed = cells.ToDictionary(c => c.Key, c => c.Value.RawValue);
        foreach (var (column, cell) in cells)
        {
            cell.RawValue = printed.GetValueOrDefault(column - shift, "");
        }

        var replay = ReplayEngine.Execute(graph, C2Spec());
        output.WriteLine(string.Join(" | ", replay.StructuralRepairs));

        Assert.Equal(0, Hard(replay));
        Assert.Contains(replay.StructuralRepairs, r => r.StartsWith("reportTotals: money cells read one column", StringComparison.Ordinal));
        Assert.Equal(473129.63m, replay.Document.ReportTotals!.Indemnity!.Paid);
    }

    [Fact]
    public void A_total_row_that_ties_neither_way_is_left_as_read()
    {
        var graph = RunGraph("c2-blank-header.graph.json");
        var cell = Row(graph, "Account Total").Single(c => c.Column == 6);
        cell.RawValue = "473,229.63"; // a printing error of 100.00: the source, not the layout

        var replay = ReplayEngine.Execute(graph, C2Spec());

        Assert.DoesNotContain(replay.StructuralRepairs, r => r.StartsWith("reportTotals", StringComparison.Ordinal));
        Assert.Equal(473229.63m, replay.Document.ReportTotals!.Indemnity!.Paid);
        Assert.True(Hard(replay) > 0);
    }

    [Fact]
    public void Spec_v2_reads_the_grand_total_counts_when_cu_drops_the_parenthesis()
    {
        // c1-000 native: CU split "GRAND TOTAL (50 claims: 23 open / 27 closed)" into "GRAND TOTAL" | "50 claims: 23 open / 27" | "closed)".
        var graph = RunGraph("c1-grand-total-split.graph.json");

        var v1 = ReplayEngine.Execute(graph, C1Spec(1));
        var v2 = ReplayEngine.Execute(graph, C1Spec(2));

        Assert.Null(v1.Document.ReportTotals!.ClaimCount);
        Assert.Equal(50, v2.Document.ReportTotals!.ClaimCount);
        Assert.Equal(23, v2.Document.ReportTotals.OpenCount);
        Assert.Equal(27, v2.Document.ReportTotals.ClosedCount);
        Assert.Equal(14, v2.Document.PolicyPeriods[0].Subtotals!.ClaimCount); // subtotals still read
        Assert.Equal(0, Hard(v2));
        Assert.Equal("synthetic-mutual-pdf@1", C1Spec(2).Supersedes);
    }

    [Theory]
    [InlineData("GRAND TOTAL (12 claims: 1 open / 11 cosed)", 11)]
    [InlineData("GRAND TOTAL (15 claims: 0 open / 15 c osed)", 15)]
    [InlineData("GRAND TOTAL 28 claims: 9 open / 19 c osed)", 19)]
    public void Spec_v3_reads_the_closed_count_when_cu_splits_or_loses_the_word_closed(string text, int closed)
    {
        var regex = C1Spec(3).TableLocator.RowKinds.Single(k => k.Kind == "grandTotal").CountsRegex!;
        Assert.Equal(closed.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Text.RegularExpressions.Regex.Match(text, regex).Groups["closedCount"].Value);
        Assert.False(System.Text.RegularExpressions.Regex.Match(text, C1Spec(2).TableLocator.RowKinds.Single(k => k.Kind == "grandTotal").CountsRegex!).Success);
        Assert.Equal("synthetic-mutual-pdf@2", C1Spec(3).Supersedes);
    }

    [Theory]
    [InlineData("4 Claims: 1 open / 3 closed", 4)]
    [InlineData("- 4 claims: 1 open / 3 closed", 4)]
    [InlineData("(12 claims: 5 open / 7 closed)", 12)]
    public void Spec_v2_counts_regex_tolerates_a_missing_parenthesis_and_a_dash(string text, int claims)
    {
        var regex = C1Spec(2).TableLocator.RowKinds.Single(k => k.Kind == "grandTotal").CountsRegex!;
        var m = System.Text.RegularExpressions.Regex.Match(text, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.True(m.Success);
        Assert.Equal(claims.ToString(System.Globalization.CultureInfo.InvariantCulture), m.Groups["claimCount"].Value);
    }

    [Theory]
    [InlineData(200, RoutingTier.Review)]
    [InlineData(150, RoutingTier.Review)]
    [InlineData(300, RoutingTier.Learn)]
    [InlineData(null, RoutingTier.Learn)]
    public void A_low_resolution_scan_is_not_learned_from(int? dpi, RoutingTier expected)
    {
        var miss = new FingerprintMatch(null, "miss", 0.4, false, new GraphFingerprint("pdf", "", "", "", ""), 0, new Dictionary<string, double>(), []);

        var decision = new FormatRouter().Decide(miss, null, 0.9, scanDpi: dpi);

        Assert.Equal(expected, decision.Tier);
        if (expected == RoutingTier.Review)
        {
            Assert.Contains(decision.Reasons, r => r.Contains($"{dpi} dpi scan", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_version_records_the_rendering_it_was_learned_from()
    {
        Assert.Equal("native", LearningRecord.Rendering(new SourceFile { Kind = FileKind.Pdf, TextLayer = true }));
        Assert.Equal("scan-300", LearningRecord.Rendering(new SourceFile { Kind = FileKind.Pdf, TextLayer = false, ScanDpi = 299 }));
        Assert.Equal("scan", LearningRecord.Rendering(new SourceFile { Kind = FileKind.Pdf, TextLayer = false }));
        Assert.Equal("spreadsheet", LearningRecord.Rendering(new SourceFile { Kind = FileKind.Xlsx }));
        Assert.Null(LearningRecord.Rendering(new SourceFile { Kind = FileKind.Pdf }));
    }

    [Fact]
    public async Task A_hand_version_that_a_learned_version_already_holds_resolves_to_the_learned_one_everywhere()
    {
        var hand = new InMemoryFormatRegistry();
        await hand.PublishAsync(C1Spec(1));
        await hand.PublishAsync(C1Spec(2));
        var learned = new InMemoryFormatRegistry();
        var learnedV2 = C1Spec(1);
        (learnedV2.Version, learnedV2.CreatedBy, learnedV2.Supersedes) = (2, "learn:test", "synthetic-mutual-pdf@1");
        await learned.PublishAsync(learnedV2);
        var layered = new LayeredFormatRegistry(hand, learned);

        var index = await layered.GetIndexAsync();

        Assert.Equal([1, 2], index.Where(e => e.FormatId == "synthetic-mutual-pdf").Select(e => e.Version).Order());
        Assert.Equal("learn:test", (await layered.GetAsync("synthetic-mutual-pdf", 2))!.CreatedBy);
        Assert.Equal("learn:test", (await layered.GetAsync("synthetic-mutual-pdf"))!.CreatedBy);
    }

    [Fact]
    public async Task A_replay_that_reads_no_total_row_of_a_format_that_prints_them_goes_to_review()
    {
        // c2-002 fax (08c run): OCR misread every "Policy Total" label; the subtotal rows became claim rows and no rule could fail.
        var spec = FormatJson.Parse(File.ReadAllText(Graphs.File("graphs", "fictional-casualty-group-pdf.v2c.1.json")));
        var registry = new InMemoryFormatRegistry();
        await registry.PublishAsync(spec);
        var graph = RunGraph("c2-fax-no-totals.graph.json");

        var (_, replay, decision) = await new FormatMemory(registry, null).ProcessAsync(graph, "run", DateTimeOffset.UtcNow);
        output.WriteLine($"{decision.Tier} | {string.Join(" | ", decision.Reasons)}");

        Assert.Equal(0, Hard(replay!));
        Assert.Equal(0, replay!.Stats.SubtotalRows + replay.Stats.GrandTotalRows);
        Assert.Equal(RoutingTier.Review, decision.Tier);
        Assert.Contains(decision.Reasons, r => r.Contains("prints totals but none were read", StringComparison.Ordinal));
        Assert.Equal(150, graph.SourceFile.ScanDpi);
    }

    [Fact]
    public void Two_headers_cu_merged_into_one_cell_are_split_and_read_with_the_body_cells()
    {
        // c1-014 scan 300 dpi (08c run): "Reported\nStatus" in one header cell, "10/01/2022\nClosed" in every body cell.
        var graph = RunGraph("c1-merged-header.graph.json");
        Assert.Contains("Reported\nStatus", graph.Tables.Single(t => t.Id == "t2").HeaderTexts);

        var replay = ReplayEngine.Execute(graph, C1Spec(3));
        output.WriteLine(string.Join(" | ", replay.StructuralRepairs));

        Assert.Empty(replay.UnmappedHeaders);
        Assert.Equal(0, Hard(replay));
        Assert.True(replay.HeadersRepaired);
        Assert.Contains(replay.StructuralRepairs, r => r.StartsWith("merged header in t2 column 4 split into \"Reported\" | \"Status\"", StringComparison.Ordinal));
        var claim = replay.Document.PolicyPeriods.SelectMany(p => p.Claims).Single(c => c.ClaimNumber == "WC-22-0025");
        Assert.Equal((new DateOnly(2022, 10, 1), ClaimStatus.Closed), (claim.DateReported, claim.Status));
    }

    [Fact]
    public async Task The_merged_header_document_replays_with_zero_model_calls()
    {
        var registry = await Graphs.RegistryAsync(maxVersion: null);

        var (match, replay, decision) = await new FormatMemory(registry, null).ProcessAsync(RunGraph("c1-merged-header.graph.json"), "run", DateTimeOffset.UtcNow,
            submission: new SubmissionContext("Synthetic Mutual Insurance Co."));
        output.WriteLine($"{match.Tier} drift={match.Drift} {decision.Tier} | {string.Join(" | ", decision.Reasons)}");

        Assert.Equal(RoutingTier.ReplayAccepted, decision.Tier);
        Assert.Equal(0, replay!.ModelCalls);
    }

    [Fact]
    public void A_merged_cell_whose_lines_do_not_match_its_headers_is_not_split()
    {
        var graph = RunGraph("c1-merged-header.graph.json");
        var cell = graph.Cells.First(c => c.TableId == "t2" && c.RawValue.StartsWith("10/01/2022", StringComparison.Ordinal));
        cell.RawValue = "10/01/2022"; // the status line is missing

        var replay = ReplayEngine.Execute(graph, C1Spec(3));

        Assert.Contains(replay.Issues, i => i.Contains("has 1 line(s) for 2 merged header(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_formats_known_to_print_totals_require_a_total_row()
    {
        var learnedXlsx = Graphs.Spec("synthetic-mutual-xlsx");
        learnedXlsx.TableLocator.RowKinds = [new RowKindRule { Kind = "subtotal", Regex = "^$" }, new RowKindRule { Kind = "grandTotal", Regex = "^$" }];
        learnedXlsx.Learning = new LearningRecord { Stage = "learn", VerificationGate = "spreadsheet-rules" };
        var learnedPdf = C2Spec();

        Assert.False(FormatRouter.PrintsTotals(learnedXlsx)); // 08d: placeholder total rows on a rules-gated spreadsheet spec
        Assert.True(FormatRouter.PrintsTotals(learnedPdf));   // admitted by pdf-totals
        learnedXlsx.Learning.VerificationGate = "spreadsheet-totals";
        Assert.True(FormatRouter.PrintsTotals(learnedXlsx));  // totals tied at verification (e.g. from a summary-sheet lookup)
        Assert.True(FormatRouter.PrintsTotals(C1Spec(3)));    // hand spec declaring total rows
        Assert.False(FormatRouter.PrintsTotals(Graphs.Spec("synthetic-mutual-csv")));
    }

    private static List<GraphCell> Row(DocumentGraph graph, string label)
    {
        var first = graph.Cells.Single(c => c.RawValue.StartsWith(label, StringComparison.Ordinal));
        return graph.Cells.Where(c => c.TableId == first.TableId && c.Row == first.Row).ToList();
    }
}
