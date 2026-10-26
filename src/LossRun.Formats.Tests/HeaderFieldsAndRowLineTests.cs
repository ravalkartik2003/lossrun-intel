using System.Text.Json;
using LossRun.Contracts;
using LossRun.Formats.Replay;
using LossRun.Rules;

namespace LossRun.Formats.Tests;

/// <summary>
/// Brief 23 §1 and §2 on a small hand-built native graph: header-block fields read from the word stream (detector and the
/// <c>headerLabel</c> locator), a single-period run's period from its header, and rows of another line of business kept,
/// not extracted, with the printed policy total tying claims plus kept rows.
/// </summary>
public class HeaderFieldsAndRowLineTests
{
    private static void Line(DocumentGraph graph, double y, params (double X, string Text)[] words)
    {
        foreach (var (x, text) in words)
        {
            var w = 0.08 * text.Length;
            graph.Words.Add(new GraphWord { Page = 1, Text = text, Confidence = 0.99, Polygon = [x, y, x + w, y, x + w, y + 0.15, x, y + 0.15] });
        }
    }

    private static void Words(DocumentGraph graph, double y, double x, string text)
    {
        foreach (var word in text.Split(' '))
        {
            Line(graph, y, (x, word));
            x += 0.08 * word.Length + 0.08;
        }
    }

    /// <summary>A one-period run: header block, then a claim table with a file-prefix column and a policy total row.</summary>
    private static DocumentGraph Graph(string periodLine = "Policy Period: 01/01/2023 - 01/01/2024")
    {
        var graph = new DocumentGraph
        {
            DocumentId = "doc-hand", SourceFile = new SourceFile { FileName = "hand.pdf", Kind = FileKind.Pdf, TextLayer = true }, Pages = [new GraphPage { Number = 1 }],
        };
        Words(graph, 0.5, 0.5, "Insured: Synthetic Widgets (synthetic)");
        Words(graph, 0.5, 5.0, "Valued as of: 06/30/2024");
        Words(graph, 0.8, 0.5, "Policy Number: HAND-23-01");
        Words(graph, 1.1, 0.5, periodLine);
        string[] headers = ["Claim Number", "FP", "Date of Injury", "Status", "Total Incurred"];
        string[][] rows =
        [
            ["HC-0001", "CB", "02/01/2023", "Open", "1,000.00"],
            ["HC-0002", "LR", "03/01/2023", "Closed", "500.00"],
            ["HC-0003", "CM", "04/01/2023", "Closed", "250.00"],
            ["Policy Total", "", "", "", "1,750.00"],
        ];
        graph.Tables.Add(new GraphTable
        {
            Id = "t0", Page = 1, RowCount = rows.Length + 1, ColumnCount = headers.Length, HeaderRows = [0], HeaderTexts = [.. headers],
            HeaderFields = ["claim.claimNumber", "claim.claimType", "claim.dateOfInjury", "claim.status", "claim.total.incurred"], Polygon = [0.5, 1.5, 8, 1.5, 8, 3, 0.5, 3],
        });
        for (var c = 0; c < headers.Length; c++)
        {
            graph.Cells.Add(new GraphCell { TableId = "t0", Page = 1, Row = 0, Column = c, RawValue = headers[c], Kind = "columnHeader", Polygon = [0.5 + c, 1.5, 1.4 + c, 1.5, 1.4 + c, 1.7, 0.5 + c, 1.7] });
        }

        for (var r = 0; r < rows.Length; r++)
        {
            for (var c = 0; c < headers.Length; c++)
            {
                var y = 1.8 + 0.2 * r;
                graph.Cells.Add(new GraphCell { TableId = "t0", Page = 1, Row = r + 1, Column = c, RawValue = rows[r][c], Kind = "content", Polygon = [0.5 + c, y, 1.4 + c, y, 1.4 + c, y + 0.15, 0.5 + c, y + 0.15] });
            }
        }

        return graph;
    }

    private static CarrierFormat Spec() => new()
    {
        FormatId = "hand-pdf", Version = 1, CarrierName = "Synthetic Mutual Insurance Co.", FileKind = "pdf", CreatedBy = "hand",
        TableLocator = new TableLocator { HeaderMatchMin = 0.8, RowKinds = [new RowKindRule { Kind = "grandTotal", Regex = "^Policy Total" }] },
        ColumnMap =
        [
            new ColumnMapping { Source = "Claim Number", Target = "claim.claimNumber" },
            new ColumnMapping { Source = "FP", Target = "claim.claimType", Normalizer = new NormalizerSpec { Type = "enum", GlossaryField = "claim.claimType" } },
            new ColumnMapping { Source = "Date of Injury", Target = "claim.dateOfInjury", Normalizer = new NormalizerSpec { Type = "date" } },
            new ColumnMapping { Source = "Status", Target = "claim.status", Normalizer = new NormalizerSpec { Type = "enum", GlossaryField = "claim.status" } },
            new ColumnMapping { Source = "Total Incurred", Target = "claim.total.incurred", Normalizer = new NormalizerSpec { Type = "currency" } },
        ],
        DocumentFields =
        [
            new DocumentFieldSpec { Target = "document.valuationDate", Locator = new FieldLocator { Type = "headerLabel", Label = "Valued as of" }, Normalizer = new NormalizerSpec { Type = "date" } },
            new DocumentFieldSpec { Target = "period.policyNumber", Locator = new FieldLocator { Type = "headerLabel", Label = "Policy Number" } },
            new DocumentFieldSpec { Target = "period.effectiveDate", Locator = new FieldLocator { Type = "headerLabel", Label = "Policy Period" }, Normalizer = new NormalizerSpec { Type = "date" } },
            new DocumentFieldSpec { Target = "period.expirationDate", Locator = new FieldLocator { Type = "headerLabel", Label = "Policy Period" }, Normalizer = new NormalizerSpec { Type = "date" } },
        ],
    };

    [Fact]
    public void The_header_block_is_read_from_the_word_stream_with_labels_and_positions()
    {
        var found = HeaderFields.Detect(Graph(), CanonicalData.Glossary).ToDictionary(f => f.Field);

        Assert.Equal("06/30/2024", found["document.valuationDate"].Value);
        Assert.Equal("Synthetic Widgets (synthetic)", found["document.insuredName"].Value); // stops at the next label on the line
        Assert.Equal("HAND-23-01", found["period.policyNumber"].Value);
        Assert.Equal(("01/01/2023", "01/01/2024"), (found["period.effectiveDate"].Value, found["period.expirationDate"].Value));
        Assert.NotNull(found["document.valuationDate"].Polygon);
    }

    [Fact]
    public void A_losses_window_is_recorded_as_such_and_never_as_the_policy_period()
    {
        var found = HeaderFields.Detect(Graph("Accident Date Range: 01/01/2023 to 12/31/2023"), CanonicalData.Glossary);

        Assert.Contains(found, f => f.Field == HeaderFields.LossWindow && f.Label == "Accident Date Range");
        Assert.DoesNotContain(found, f => f.Field == "period.effectiveDate");
    }

    [Fact]
    public void Header_labels_locate_document_and_single_period_fields_with_lineage()
    {
        var replay = ReplayEngine.Execute(Graph(), Spec());
        var period = Assert.Single(replay.Document.PolicyPeriods);

        Assert.Equal(new DateOnly(2024, 6, 30), replay.Document.ValuationDate);
        Assert.Equal(("HAND-23-01", new DateOnly(2023, 1, 1), new DateOnly(2024, 1, 1)), (period.PolicyNumber, period.EffectiveDate, period.ExpirationDate));
        Assert.Equal(1, replay.Document.Lineage!["policyPeriods[0].effectiveDate"].Page);
        Assert.DoesNotContain(replay.Issues, i => i.Contains("header", StringComparison.Ordinal));
    }

    [Fact]
    public void A_row_of_another_line_is_kept_and_the_policy_total_ties_claims_plus_kept_rows()
    {
        var replay = ReplayEngine.Execute(Graph(), Spec());
        var period = replay.Document.PolicyPeriods[0];

        Assert.Equal(["HC-0001", "HC-0003"], period.Claims.Select(c => c.ClaimNumber));
        var kept = Assert.Single(period.KeptRows!);
        Assert.Equal(("HC-0002", "line of business: general liability (claim-type code \"LR\")"), (kept.ClaimNumber, kept.KeptReason));
        Assert.Equal(1, replay.Stats.KeptRows);
        Assert.DoesNotContain(replay.Issues, i => i.Contains("claimType", StringComparison.Ordinal)); // the code is the reason, not an error
        Assert.Contains(replay.RuleOutcomes, o => o.RuleId == "R-12" && o.Status == RuleStatus.Pass);
    }

    [Fact]
    public void A_printed_total_that_ties_neither_way_fails()
    {
        var graph = Graph();
        graph.Cells.Single(c => c.Row == 4 && c.Column == 4).RawValue = "1,900.00";

        var replay = ReplayEngine.Execute(graph, Spec());

        Assert.Contains(replay.RuleOutcomes, o => o.RuleId == "R-12" && o.Status == RuleStatus.Fail);
    }

    [Fact]
    public void A_header_period_field_on_a_multi_period_run_is_an_issue_and_not_applied()
    {
        var spec = Spec();
        spec.Periods = new PeriodSpec { Source = "column", Column = "Status" }; // two "periods" (Open / Closed): a stand-in for a multi-period run

        var replay = ReplayEngine.Execute(Graph(), spec);

        Assert.Contains(replay.Issues, i => i.StartsWith("document field period.effectiveDate: a header period field applies to a single-period run", StringComparison.Ordinal));
        Assert.All(replay.Document.PolicyPeriods, p => Assert.Null(p.EffectiveDate));
    }

    [Fact]
    public void The_kept_rows_serialize_on_the_period_and_round_trip()
    {
        var document = ReplayEngine.Execute(Graph(), Spec()).Document;
        var json = JsonSerializer.Serialize(document, CanonicalJson.Options);

        Assert.Single(CanonicalJson.Deserialize(json).PolicyPeriods[0].KeptRows!);
    }
    [Fact]
    public void The_seen_csv_format_keeps_its_general_liability_row_and_flags_the_census_only_claim()
    {
        // src/TestShared/fixtures/lossrun-mixed-lines.csv, the mock-capture fixture of brief 23: replay only, no model call.
        var replay = ReplayEngine.Execute(Graphs.MixedLinesCsv(), Graphs.Spec("synthetic-mutual-csv"));
        var period = Assert.Single(replay.Document.PolicyPeriods);

        Assert.Equal(4, period.Claims.Count);
        var kept = Assert.Single(period.KeptRows!);
        Assert.Equal(("GL-23-0104", "line of business: general liability (claim type \"GL\")"), (kept.ClaimNumber, kept.KeptReason));
        var census = period.Claims.Single(c => c.ClaimNumber == "WC-23-0105");
        Assert.Equal((ClaimStatus.Open, "X", true), (census.Status!.Value, census.RawStatus, census.ReportOnly));
        Assert.Empty(replay.Issues);
    }
    [Fact]
    public void A_valuation_date_from_the_submission_is_marked_and_flagged_never_silent()
    {
        // Brief 23 Answers 2: the run prints no valuation date; the submitter states it (lineage context), R-32 flags it.
        var stated = ReplayEngine.Execute(Graphs.MixedLinesCsv(), Graphs.Spec("synthetic-mutual-csv"), submission: new SubmissionContext(ValuationDate: new DateOnly(2024, 6, 30)));

        Assert.Equal((new DateOnly(2024, 6, 30), "submission"), (stated.Document.ValuationDate, stated.Document.ValuationDateSource));
        Assert.Equal("context", stated.Document.Lineage!["valuationDateSource"].Extractor);
        Assert.Contains(stated.RuleOutcomes, o => o.RuleId == "R-32" && o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Flag);

        var printed = ReplayEngine.Execute(Graph(), Spec());
        Assert.Equal("printed", printed.Document.ValuationDateSource);
        Assert.Contains(printed.RuleOutcomes, o => o.RuleId == "R-32" && o.Status == RuleStatus.Pass);
    }

    [Fact]
    public void Kept_rows_without_a_printed_total_raise_the_R31_flag_not_a_failure()
    {
        // Brief 23 Answers 1: the mixed-lines CSV prints no totals; its GL row is kept, so R-31 flags it for the reviewer.
        var replay = ReplayEngine.Execute(Graphs.MixedLinesCsv(), Graphs.Spec("synthetic-mutual-csv"));

        var r31 = Assert.Single(replay.RuleOutcomes, o => o.RuleId == "R-31");
        Assert.Equal((RuleStatus.Fail, RuleSeverity.Flag), (r31.Status, r31.Severity));
        Assert.Contains(ReplayEngine.Execute(Graph(), Spec()).RuleOutcomes, o => o.RuleId == "R-31" && o.Status == RuleStatus.Pass); // a printed total
    }
}
