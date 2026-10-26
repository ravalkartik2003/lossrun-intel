using LossRun.Contracts;
using LossRun.Ingestion;

namespace LossRun.Ingestion.Tests;

/// <summary>
/// Brief 18 (P6): when CU calls the only document "Other", only pages with a qualifying loss table go on, and only when
/// such pages are a real share of the document; everything else is kept-not-processed with "no loss table found".
/// Graphs are built by hand in the shapes the real-sample run showed (an RFP, a carrier manual), with generic headers.
/// </summary>
public class ClassifierFallbackTests
{
    private static readonly string[] ClaimHeaders = ["Claim Number", "Date of Injury", "Claimant", "Status", "Total Paid", "Total Incurred"];

    private static DocumentGraph Graph(int pages, params (int Page, string[] Headers)[] tables)
    {
        var graph = new DocumentGraph { DocumentId = "doc-test" };
        for (var p = 1; p <= pages; p++)
        {
            graph.Pages.Add(new GraphPage { Number = p, WordCount = 400 });
            graph.Words.AddRange(Enumerable.Range(0, 400).Select(i => new GraphWord { Page = p, Text = "word" }));
        }

        foreach (var (page, headers) in tables)
        {
            graph.Tables.Add(new GraphTable
            {
                Id = $"t{graph.Tables.Count}", Page = page, ColumnCount = headers.Length, HeaderTexts = [.. headers],
                HeaderFields = [.. headers.Select(h => CanonicalData.Glossary.ResolveHeader(h)?.Field)],
            });
        }

        return graph;
    }

    private static List<GraphSegment> Fallback(DocumentGraph graph) =>
        IngestionPipeline.FallbackSegments(graph, new GraphSegment { Category = "Other", StartPage = 1, EndPage = graph.Pages.Count }, false, new IngestionOptions(),
            CanonicalData.Glossary);

    [Fact]
    public void A_two_header_table_in_an_rfp_is_not_a_loss_table()
    {
        // laccd: a 64-page RFP whose one resolving table was "Claim Type | Total Claims".
        var segments = Fallback(Graph(64, (31, ["Claim Type", "Total Claims"])));

        var kept = Assert.Single(segments);
        Assert.Null(kept.DerivedDecision);
        Assert.Equal("no loss table found", kept.Reason);
    }

    [Fact]
    public void Sample_loss_tables_on_a_few_pages_of_a_manual_are_kept_not_learned()
    {
        // travelers-loss-reports-manual: loss-report examples on 3 of 25 pages.
        var segments = Fallback(Graph(25, (20, ClaimHeaders), (21, ClaimHeaders), (24, ClaimHeaders)));

        var kept = Assert.Single(segments);
        Assert.Null(kept.DerivedDecision);
        Assert.StartsWith("no loss table found: loss-run-like tables on 3 of 25 pages", kept.Reason);
    }

    [Fact]
    public void Only_the_pages_carrying_the_loss_table_go_on_and_the_rest_is_kept()
    {
        // A 4-page fax: a cover page, then the loss table on pages 2-4 (page 4 a continuation under unreadable headers).
        var segments = Fallback(Graph(4, (2, ClaimHeaders), (3, ClaimHeaders), (4, ["Clm Nmbr", "Dt of Inj", "Clmnt", "Sts", "Ttl Pd", "Ttl Inc"])));

        Assert.Equal([(1, 1, (string?)null), (2, 4, "review")], segments.Select(s => (s.StartPage, s.EndPage, s.DerivedDecision)));
        Assert.Equal("no loss table found", segments[0].Reason);
        Assert.Contains("fallback: CU category Other", segments[1].DerivedConfidenceMethod);
    }

    [Fact]
    public void A_period_summary_table_marks_the_segment_summary_only()
    {
        // ADR 0017: year rows with counts and totals, no claim identity column.
        var segment = new GraphSegment { Category = "LossRun", StartPage = 1, EndPage = 1 };
        var graph = Graph(1, (1, ["Fiscal Year", "Open Claims", "Closed Claims", "Total Paid", "Total Incurred"]));

        IngestionPipeline.MarkSummaryOnly(segment, graph.Tables);

        Assert.True(segment.SummaryOnly);
        Assert.Null(Mark(ClaimHeaders).SummaryOnly);
    }

    private static GraphSegment Mark(string[] headers)
    {
        var segment = new GraphSegment { Category = "LossRun", StartPage = 1, EndPage = 1 };
        IngestionPipeline.MarkSummaryOnly(segment, Graph(1, (1, headers)).Tables);
        return segment;
    }
}
