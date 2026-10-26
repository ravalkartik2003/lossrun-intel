using System.Text.Json;
using System.Text.Json.Nodes;
using LossRun.Contracts;
using LossRun.Formats.Replay;
using LossRun.Rules;

namespace LossRun.Formats.Tests;

/// <summary>
/// Brief 19 (ADR 0018): each spec-language construct replayed on a recorded Content Understanding graph of its corpus-gen
/// 0.4.0 rendering (synthetic carriers c6–c8), compared with the generator's truth claim by claim. The graphs, truths and
/// hand specs are in src/TestShared/constructs/ (not formats/, so no registry test sees them).
/// </summary>
public class ConstructTests
{
    private static (DocumentGraph Graph, CarrierFormat Spec, LossRunDocument Truth) Load(string name)
    {
        var graph = JsonSerializer.Deserialize<DocumentGraph>(File.ReadAllText(Graphs.File("constructs", $"{name}.graph.json")), CanonicalJson.Options)!;
        var spec = FormatJson.Parse(File.ReadAllText(Graphs.File("constructs", $"{name}.spec.json")));
        var truth = CanonicalJson.Deserialize(JsonNode.Parse(File.ReadAllText(Graphs.File("constructs", $"{name}.truth.json")))!["document"]!.ToJsonString());
        return (graph, spec, truth);
    }

    private static ReplayResult ReplayAndCompare(string name, Func<Claim, object?>[] fields)
    {
        var (graph, spec, truth) = Load(name);
        var replay = ReplayEngine.Execute(graph, spec);

        var hard = replay.RuleOutcomes.Where(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard).Select(o => $"{o.RuleId} {o.ClaimId} p{o.PeriodIndex} {o.Detail}").ToList();
        Assert.True(hard.Count == 0, string.Join("\n", hard));
        Assert.Empty(replay.UnmappedHeaders);
        Assert.Contains(replay.RuleOutcomes, o => o.RuleId == "R-10" && o.Status == RuleStatus.Pass);
        Assert.Contains(replay.RuleOutcomes, o => o.RuleId == "R-12" && o.Status == RuleStatus.Pass);

        var expected = truth.PolicyPeriods.SelectMany(p => p.Claims).ToList();
        var actual = replay.Document.PolicyPeriods.SelectMany(p => p.Claims).ToList();
        Assert.Equal(expected.Count, actual.Count);
        foreach (var claim in expected)
        {
            var read = actual.SingleOrDefault(c => c.ClaimNumber == claim.ClaimNumber);
            Assert.True(read is not null, $"{name}: claim {claim.ClaimNumber} not read");
            foreach (var field in fields)
            {
                Assert.Equal(field(claim), field(read!));
            }
        }

        return replay;
    }

    private static readonly Func<Claim, object?>[] Money =
    [
        c => c.Indemnity?.Paid, c => c.Medical?.Paid, c => c.Expense?.Paid, c => c.Indemnity?.Reserve, c => c.Medical?.Reserve, c => c.Total?.Incurred,
    ];

    [Fact]
    public void Two_line_records_read_the_amounts_line_under_the_key_line_headers()
    {
        var replay = ReplayAndCompare("c6-two-line-records",
            [.. Money, c => c.Status, c => c.DateOfInjury, c => c.DateReported, c => c.DateClosed, c => c.ClaimantName, c => c.JurisdictionState]);

        // Lineage of a line-1 value points at its own printed cell, not the key line's.
        var lineage = replay.Document.Lineage!;
        Assert.NotEqual(lineage["policyPeriods[0].claims[0].claimNumber"].Polygon![1], lineage["policyPeriods[0].claims[0].indemnity.paid"].Polygon![1]);
    }

    [Fact]
    public void A_claim_per_table_reads_its_loose_lines_and_the_total_lines_by_column_position()
    {
        var replay = ReplayAndCompare("c7-claim-per-table", [.. Money, c => c.Status, c => c.DateOfInjury, c => c.ClaimantName, c => c.ClaimType]);

        Assert.All(replay.Document.PolicyPeriods, p => Assert.NotNull(p.Subtotals?.Total?.Incurred)); // "Policy Total:" lines
        Assert.NotNull(replay.Document.ReportTotals?.Total?.Incurred); // "Total All Years:"
        Assert.Equal("replay", replay.Document.Lineage!["policyPeriods[0].claims[0].claimNumber"].Extractor);
    }

    [Fact]
    public void Claim_numbers_of_three_shapes_printed_beside_a_file_number_are_each_read_from_their_part_of_the_line()
    {
        // Brief 20 §2 (corpus-gen 0.6.0, recorded CU graph): three claim-number shapes in one run; on every other claim the
        // claim number and a file number print apart on one line, inside the table CU drew over the loose lines.
        ReplayAndCompare("c7b-mixed-claim-numbers", [.. Money, c => c.Status, c => c.DateOfInjury, c => c.ClaimantName, c => c.ClaimType]);

        // A pattern written from one example matches only that shape: the records it misses are named with their printed lines.
        var (graph, spec, _) = Load("c7b-mixed-claim-numbers");
        spec.TableLocator.Records!.LineFields.Single(l => l.Target == "claim.claimNumber").Pattern = @"^(?<value>SPC-\d{2}-\d{5})$";
        var narrow = ReplayEngine.Execute(graph, spec);
        Assert.Contains(narrow.RuleOutcomes, o => o.RuleId == "R-19" && o.Status == RuleStatus.Fail);
        Assert.Contains(narrow.UnmatchedClaimNumberRecords, r => r.Contains(" | ", StringComparison.Ordinal));
    }

    [Fact]
    public void Stacked_cells_give_two_values_each_with_the_cell_recorded_once_and_its_part()
    {
        var replay = ReplayAndCompare("c8-stacked-columns",
            [.. Money, c => c.ClaimType, c => c.ClaimantName, c => c.Status, c => c.DateOfInjury, c => c.DateReported]);

        var lineage = replay.Document.Lineage!;
        var (top, bottom) = (lineage["policyPeriods[0].claims[0].claimNumber"], lineage["policyPeriods[0].claims[0].claimType"]);
        Assert.Equal((0, 1), (top.Part, bottom.Part));
        Assert.Equal(top.Polygon, bottom.Polygon); // one cell
    }

    [Fact]
    public void An_anchor_that_also_matches_the_continued_heading_keeps_the_period_whole()
    {
        // The learned c6 spec's anchor accepted "(continued)": every page break opened a second period with the same policy.
        var (graph, spec, truth) = Load("c6-two-line-records");
        spec.TableLocator.BlockAnchor = spec.TableLocator.BlockAnchor![..^1] + @"(?:\s+\(continued\))?$";

        var replay = ReplayEngine.Execute(graph, spec);

        Assert.Equal(truth.PolicyPeriods.Count, replay.Document.PolicyPeriods.Count);
        Assert.DoesNotContain(replay.RuleOutcomes, o => o.RuleId == "R-10" && o.Status == RuleStatus.Fail);
    }

    [Theory]
    [InlineData("c6-two-line-records", "records-rows")]
    [InlineData("c7-claim-per-table", "records-lines")]
    [InlineData("c8-stacked-columns", "stacked")]
    public void Ingestion_finds_the_evidence_for_each_construct(string name, string construct)
    {
        // Brief 19 Answers 3: the learn prompt states the construct as an instruction from this evidence.
        var evidence = LossRun.Ingestion.LayoutEvidenceDetector.Detect(Load(name).Graph);

        Assert.Contains(evidence, e => e.Construct == construct);
        Assert.All(evidence.Where(e => e.Construct != "claims-with-period-totals"), e => Assert.Equal(construct, e.Construct)); // brief 20 Answers 2: a layout fact
    }

    [Fact]
    public void An_ordinary_one_line_layout_shows_no_construct_evidence()
    {
        // No record or stacked construct; its per-period total rows are the brief 20 Answers 2 evidence, a layout fact.
        Assert.All(LossRun.Ingestion.LayoutEvidenceDetector.Detect(Graphs.Native()), e => Assert.Equal("claims-with-period-totals", e.Construct));
    }

    [Fact]
    public void Claim_rows_beside_a_period_table_or_per_period_total_rows_are_claims_with_period_totals()
    {
        // Brief 20 Answers 2: a period table (term, counts, totals; no claim number) next to a claim table.
        var graph = new DocumentGraph
        {
            Tables =
            [
                new GraphTable { Id = "t1", Page = 1, RowCount = 3, HeaderRows = [0], HeaderTexts = ["Policy Term", "# of Claims", "Losses Paid", "Total Incurred"],
                    HeaderFields = [null, null, "claim.indemnity.paid", "claim.total.incurred"] },
                new GraphTable { Id = "t4", Page = 3, RowCount = 3, HeaderRows = [0], HeaderTexts = ["Claim #", "Loss Date", "Total Incurred"],
                    HeaderFields = ["claim.claimNumber", "claim.dateOfInjury", "claim.total.incurred"] },
            ],
        };

        var periodTable = Assert.Single(LossRun.Ingestion.LayoutEvidenceDetector.Detect(graph), e => e.Construct == "claims-with-period-totals");
        Assert.Equal("t1", periodTable.TableId);

        // Without the period table, a "Policy Total" row inside the claim table is the same fact.
        graph.Tables.RemoveAt(0);
        graph.Cells.Add(new GraphCell { TableId = "t4", Page = 3, Row = 1, Column = 0, RawValue = "C-1" });
        graph.Cells.Add(new GraphCell { TableId = "t4", Page = 3, Row = 2, Column = 0, RawValue = "Policy Total" });
        Assert.Contains(LossRun.Ingestion.LayoutEvidenceDetector.Detect(graph), e => e.Construct == "claims-with-period-totals" && e.TableId == "t4");
    }

    private static readonly Func<Claim, object?>[] AllMeasures =
    [
        c => c.Total?.Paid, c => c.Total?.Reserve, c => c.Total?.Incurred, c => c.Indemnity?.Paid, c => c.Indemnity?.Reserve, c => c.Indemnity?.Incurred,
        c => c.Medical?.Paid, c => c.Medical?.Reserve, c => c.Medical?.Incurred, c => c.Expense?.Paid, c => c.Expense?.Reserve, c => c.Expense?.Incurred,
    ];

    [Fact]
    public void Measures_as_rows_read_each_component_on_the_row_its_measure_label_names()
    {
        // Brief 22 Answers 1 (corpus-gen 0.7.0 c10, recorded CU graph, the learned spec after RepairMeasureColumns): each claim
        // prints Inc / Pd / O/S rows under Total | Claim | Medical | Expense; "Claim" is the indemnity component.
        var replay = ReplayAndCompare("c10-measures-rows", [.. AllMeasures, c => c.Status, c => c.DateOfInjury, c => c.ClaimantName]);

        Assert.All(replay.Document.PolicyPeriods, p => Assert.NotNull(p.Subtotals?.Total?.Paid)); // the policy totals print their measure rows too
        Assert.NotNull(replay.Document.ReportTotals?.Medical?.Reserve);
        var lineage = replay.Document.Lineage!;
        // A paid value's lineage is its own printed row, not the key row's.
        Assert.NotEqual(lineage["policyPeriods[0].claims[0].total.incurred"].Polygon![1], lineage["policyPeriods[0].claims[0].total.paid"].Polygon![1]);
    }

    [Fact]
    public void Skip_row_kinds_on_the_measure_labels_do_not_drop_the_measure_rows()
    {
        // Brief 23 (corpus v5 c10-000 learn): the model also declared "^Inc:", "^Pd:", "^O/S:" as skip rows.
        var (graph, spec, truth) = Load("c10-measures-rows");
        spec.TableLocator.RowKinds.AddRange(new[] { "^Inc:", "^Pd:", "^O/S:" }.Select(r => new RowKindRule { Kind = "skip", Regex = r }));

        var claims = ReplayEngine.Execute(graph, spec).Document.PolicyPeriods.SelectMany(p => p.Claims).ToList();

        Assert.Equal(truth.PolicyPeriods.SelectMany(p => p.Claims).Select(c => c.Indemnity?.Paid), claims.Select(c => c.Indemnity?.Paid));
    }

    [Fact]
    public void Measures_as_rows_evidence_is_found_at_ingestion()
    {
        var evidence = LossRun.Ingestion.LayoutEvidenceDetector.Detect(Load("c10-measures-rows").Graph);

        Assert.Contains(evidence, e => e.Construct == "measures-as-rows");
        Assert.DoesNotContain(LossRun.Ingestion.LayoutEvidenceDetector.Detect(Load("c6-two-line-records").Graph), e => e.Construct == "measures-as-rows");
    }

    [Fact]
    public void Without_measures_mode_the_measure_rows_are_not_read()
    {
        var (graph, spec, _) = Load("c10-measures-rows");
        spec.TableLocator.Records = null;

        var replay = ReplayEngine.Execute(graph, spec);

        // The measure-less component targets read nothing outside measures mode, so no amount is delivered.
        Assert.All(replay.Document.PolicyPeriods.SelectMany(p => p.Claims), c => Assert.Null(c.Indemnity?.Paid));
    }

    [Fact]
    public void A_status_value_that_states_report_only_sets_the_flag_with_the_status_lineage()
    {
        // Brief 22 Answers 4: status "X" (census only) is open with rawStatus kept, and reportOnly: true.
        var (graph, spec, _) = Load("c10-measures-rows");
        var replay0 = ReplayEngine.Execute(graph, spec);
        var statusCell = replay0.Document.Lineage!["policyPeriods[0].claims[0].status"];
        var cell = graph.Cells.First(c => c.Page == statusCell.Page && c.Polygon is { } p && p.SequenceEqual(statusCell.Polygon!));
        cell.RawValue = "X";

        var claim = ReplayEngine.Execute(graph, spec).Document.PolicyPeriods[0].Claims[0];

        Assert.Equal(ClaimStatus.Open, claim.Status);
        Assert.Equal("X", claim.RawStatus);
        Assert.True(claim.ReportOnly);
        Assert.All(replay0.Document.PolicyPeriods.SelectMany(p => p.Claims), c => Assert.Null(c.ReportOnly));
    }

    [Fact]
    public void Without_records_the_two_line_layout_does_not_verify()
    {
        // The construct is what makes the layout readable: the same spec without records reads the amounts lines as
        // claims with no claim number.
        var (graph, spec, _) = Load("c6-two-line-records");
        spec.TableLocator.Records = null;

        var replay = ReplayEngine.Execute(graph, spec);

        // Amounts lines become claims without a number and key lines claims without amounts: printed totals no longer tie.
        Assert.DoesNotContain(replay.RuleOutcomes, o => o.RuleId == "R-12" && o.Status == RuleStatus.Pass);
        Assert.NotEqual(Load("c6-two-line-records").Truth.PolicyPeriods.Sum(p => p.Claims.Count), replay.Stats.ClaimRows);
    }
}
