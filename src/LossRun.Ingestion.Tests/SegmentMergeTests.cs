using LossRun.Contracts;
using LossRun.Ingestion;

namespace LossRun.Ingestion.Tests;

/// <summary>Brief 23 §3: continuation segments the classifier split off a run are merged before routing.</summary>
public class SegmentMergeTests
{
    private static readonly string[] Header = ["Claim Number", "Claimant", "Date of Injury", "Total"];

    private static DocumentGraph Parent(int pages, params (int From, int To)[] segments)
    {
        var graph = new DocumentGraph { DocumentId = "doc-split", SourceFile = new SourceFile { FileName = "s.pdf", Kind = FileKind.Pdf } };
        for (var p = 1; p <= pages; p++)
        {
            graph.Pages.Add(new GraphPage { Number = p });
            graph.Tables.Add(new GraphTable { Id = $"t{p}", Page = p, RowCount = 5, ColumnCount = Header.Length, HeaderRows = [0], HeaderTexts = [.. Header] });
        }

        graph.Segments = [.. segments.Select(s => new GraphSegment { Category = "LossRun", StartPage = s.From, EndPage = s.To })];
        return graph;
    }

    private static void HeaderBlock(DocumentGraph graph, int page)
    {
        var x = 0.5;
        foreach (var word in "Valued as of: 06/30/2024".Split(' '))
        {
            graph.Words.Add(new GraphWord { Page = page, Text = word, Confidence = 0.99, Polygon = [x, 0.5, x + 0.5, 0.5, x + 0.5, 0.65, x, 0.65] });
            x += 0.6;
        }
    }

    [Fact]
    public void Segments_with_the_same_table_header_and_no_header_block_are_one_run()
    {
        var parent = Parent(6, (1, 2), (3, 3), (4, 6));
        HeaderBlock(parent, 1);

        IngestionPipeline.MergeContinuations(parent, "LossRun", CanonicalData.Glossary);

        var merged = Assert.Single(parent.Segments);
        Assert.Equal((1, 6), (merged.StartPage, merged.EndPage));
        Assert.Contains(parent.Warnings, w => w.StartsWith("segments merged: p3-3 continues p1-2", StringComparison.Ordinal));
    }

    [Fact]
    public void Segments_printing_different_carriers_never_merge_and_one_without_a_carrier_does()
    {
        // Brief 23 Answers 3: compare the printed carriers when both segments print one; otherwise the table signature decides.
        void Title(DocumentGraph g, int page, string text) => g.Paragraphs.Add(new GraphParagraph { Id = $"p{page}", Page = page, Role = "title", Text = text });

        var different = Parent(4, (1, 2), (3, 4));
        Title(different, 1, "Synthetic Mutual Insurance Co.");
        Title(different, 3, "Fictional Casualty Group");
        IngestionPipeline.MergeContinuations(different, "LossRun", CanonicalData.Glossary);
        Assert.Equal(2, different.Segments.Count);

        var same = Parent(4, (1, 2), (3, 4));
        Title(same, 1, "Synthetic Mutual Insurance Co.");
        Title(same, 3, "Synthetic Mutual Insurance Co.");
        IngestionPipeline.MergeContinuations(same, "LossRun", CanonicalData.Glossary);
        Assert.Single(same.Segments);

        var one = Parent(4, (1, 2), (3, 4));
        Title(one, 1, "Synthetic Mutual Insurance Co.");
        IngestionPipeline.MergeContinuations(one, "LossRun", CanonicalData.Glossary);
        Assert.Single(one.Segments);
    }

    [Fact]
    public void A_second_run_with_its_own_header_block_or_another_table_stays_apart()
    {
        var own = Parent(4, (1, 2), (3, 4));
        HeaderBlock(own, 1);
        HeaderBlock(own, 3);
        IngestionPipeline.MergeContinuations(own, "LossRun", CanonicalData.Glossary);
        Assert.Equal(2, own.Segments.Count);

        var other = Parent(4, (1, 2), (3, 4));
        other.Tables.Single(t => t.Page == 3).HeaderTexts = ["Policy Year", "Claims", "Incurred"];
        IngestionPipeline.MergeContinuations(other, "LossRun", CanonicalData.Glossary);
        Assert.Equal(2, other.Segments.Count);

        var gap = Parent(5, (1, 2), (4, 5));
        IngestionPipeline.MergeContinuations(gap, "LossRun", CanonicalData.Glossary);
        Assert.Equal(2, gap.Segments.Count); // not adjacent
    }
}
