using LossRun.Contracts;
using LossRun.Ingestion;

namespace LossRun.Ingestion.Tests;

/// <summary>ADR 0021 (brief 22 Answers 3 and 5): loss-analysis pages and changes-only runs are kept, not processed, before any learn.</summary>
public class ReportKindTests
{
    private static DocumentGraph Table(string id, string? heading, string[] headers, string?[] fields, params string[][] rows)
    {
        var graph = new DocumentGraph { DocumentId = id, SourceFile = new SourceFile { FileName = "x.pdf", Kind = FileKind.Pdf }, Pages = [new GraphPage { Number = 1 }] };
        if (heading is not null)
        {
            graph.Paragraphs.Add(new GraphParagraph { Id = "p0", Page = 1, Role = "title", Text = heading });
        }

        graph.Tables.Add(new GraphTable { Id = "t0", Page = 1, RowCount = rows.Length + 1, ColumnCount = headers.Length, HeaderRows = [0], HeaderTexts = [.. headers], HeaderFields = [.. fields] });
        for (var r = 0; r < rows.Length; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                graph.Cells.Add(new GraphCell { TableId = "t0", Page = 1, Row = r + 1, Column = c, RawValue = rows[r][c], Kind = "content" });
            }
        }

        return graph;
    }

    private static readonly string?[] ClaimFields = ["claim.claimNumber", "claim.dateOfInjury", "claim.total.incurred"];

    [Fact]
    public void A_lag_analysis_without_a_claim_table_is_a_loss_analysis()
    {
        var lag = Table("d", "Claim Lag Analysis Report", ["Lag Period (Days)", "Number of Claims", "Incurred"], [null, "document.reportTotals.claimCount", "claim.total.incurred"],
            ["0-7", "4", "1,000.00"], ["8-30", "2", "500.00"]);
        var byColumn = Table("e", null, ["Region", "Number of Claims", "Percent To Total Claims"], [null, "document.reportTotals.claimCount", null], ["West", "3", "50.0"]);

        var verdict = ReportKindDetector.Detect(lag, "LossRun");
        Assert.Equal(("loss-analysis", "LossAnalysis"), (verdict.Kind, verdict.Category));
        Assert.Equal("loss-analysis", ReportKindDetector.Detect(byColumn, "LossRun").Kind);
    }

    [Fact]
    public void A_claim_table_or_a_plain_summary_stays_a_loss_run()
    {
        var claims = Table("d", "Frequency Analysis", ["Claim Number", "Date of Loss", "Total Incurred"], ClaimFields, ["WC-1", "01/02/2022", "10.00"]);
        var summary = Table("s", "Financial Summary Report", ["Line of Insurance", "Incurred", "Number of Claims", "Percent Paid/Incurred"],
            [null, "claim.total.incurred", "document.reportTotals.claimCount", null], ["WC", "1,000.00", "3", "40.0"]);

        Assert.Null(ReportKindDetector.Detect(claims, "LossRun").Kind); // claims are printed: a loss run whatever its title says
        Assert.Null(ReportKindDetector.Detect(summary, "LossRun").Kind); // a summary-only loss run (ADR 0017), not an analysis
    }

    [Fact]
    public void A_changes_only_run_is_kept_and_a_bundle_segment_loses_its_child()
    {
        var single = Table("doc-single", "Detail Changes Only", ["Claim Number", "Date of Loss", "Total Incurred"], ClaimFields, ["WC-1", "01/02/2022", "10.00"]);
        var parent = new DocumentGraph { DocumentId = "doc-bundle", SourceFile = new SourceFile { FileName = "b.pdf", Kind = FileKind.Pdf } };
        var segment = new GraphSegment { Category = "LossRun", StartPage = 2, EndPage = 2, ChildDocumentId = "doc-bundle-p2-2" };
        parent.Segments = [segment];
        var child = Table("doc-bundle-p2-2", "Claim Lag Analysis", ["Lag Period (Days)", "Number of Claims"], [null, "document.reportTotals.claimCount"], ["0-7", "4"]);
        child.SourceFile = new SourceFile { FileName = "b.pdf", Kind = FileKind.Pdf, ParentDocumentId = "doc-bundle", FirstPage = 2, LastPage = 2 };
        child.Segments = [segment];
        var plain = Table("doc-plain", "Loss Run", ["Claim Number", "Date of Loss", "Total Incurred"], ClaimFields, ["WC-2", "01/03/2022", "20.00"]);
        var result = new IngestionResult();
        result.Documents.AddRange([single, parent, child, plain]);

        IngestionPipeline.ApplyReportKind(result, "LossRun");

        var kept = Assert.Single(single.Segments);
        Assert.Equal("LossRun", kept.Category);
        Assert.StartsWith(ReportKindDetector.ChangesOnlyReason, kept.Reason, StringComparison.Ordinal);
        Assert.Equal("changes-only", single.ReportKind);
        Assert.DoesNotContain(child, result.Documents);
        Assert.Null(segment.ChildDocumentId);
        Assert.Equal("LossAnalysis", segment.Category);
        Assert.StartsWith(ReportKindDetector.LossAnalysisReason, segment.Reason, StringComparison.Ordinal);
        Assert.Empty(plain.Segments);
        Assert.Null(plain.ReportKind);
    }
}
