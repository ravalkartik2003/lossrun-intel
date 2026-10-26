using LossRun.Contracts;
using LossRun.Ingestion;

namespace LossRun.Ingestion.Tests;

/// <summary>ADR 0020 (brief 20 Answers 1): loss runs of another line of business are kept, not processed, before any learn.</summary>
public class LineOfBusinessTests
{
    private static DocumentGraph Table(string id, string[] headers, string?[] fields, params string[][] rows)
    {
        var graph = new DocumentGraph { DocumentId = id, SourceFile = new SourceFile { FileName = "x.pdf", Kind = FileKind.Pdf }, Pages = [new GraphPage { Number = 1 }] };
        graph.Tables.Add(new GraphTable { Id = "t0", Page = 1, RowCount = rows.Length + 1, ColumnCount = headers.Length, HeaderRows = [0], HeaderTexts = [.. headers], HeaderFields = [.. fields] });
        for (var c = 0; c < headers.Length; c++)
        {
            graph.Cells.Add(new GraphCell { TableId = "t0", Page = 1, Row = 0, Column = c, RawValue = headers[c], Kind = "columnHeader" });
        }

        for (var r = 0; r < rows.Length; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                graph.Cells.Add(new GraphCell { TableId = "t0", Page = 1, Row = r + 1, Column = c, RawValue = rows[r][c], Kind = "content" });
            }
        }

        return graph;
    }

    private static void Words(DocumentGraph graph, string text)
    {
        var offset = 0;
        foreach (var word in text.Split(' '))
        {
            graph.Words.Add(new GraphWord { Page = 1, Text = word, SpanOffset = offset, Confidence = 0.99 });
            offset += word.Length + 1;
        }
    }

    [Fact]
    public void A_policy_type_column_printing_property_is_a_property_run()
    {
        var graph = Table("d", ["Policy Term", "Policy Type", "Total Incurred"], [null, null, "claim.total.incurred"],
            ["2014-2015", "Property", "1,000.00"], ["2015-2016", "Property", "0.00"], ["TOTALS:", "", "1,000.00"]);

        var verdict = LineOfBusinessDetector.Detect(graph, CanonicalData.Glossary);

        Assert.Equal(("property", true, 2), (verdict.Line, verdict.OutOfScope, verdict.OtherEvidence));
    }

    [Fact]
    public void A_claim_type_label_cu_split_from_its_value_is_read_from_the_words()
    {
        var graph = Table("d", ["Date Of Loss", "Total Incurred"], ["claim.dateOfInjury", "claim.total.incurred"], ["01/02/2020", "500.00"]);
        Words(graph, "Claimant : A Person Claim Type : Property Claim CLM12345 1234567");

        Assert.True(LineOfBusinessDetector.Detect(graph, CanonicalData.Glossary).OutOfScope);
    }

    [Fact]
    public void Workers_comp_claim_types_and_a_mostly_workers_comp_line_column_stay_in_scope()
    {
        var wc = Table("d", ["Claim Type", "Total Incurred"], ["claim.claimType", "claim.total.incurred"], ["Medical Only", "10.00"], ["Indemnity", "20.00"]);
        var mixed = Table("m", ["LOB", "Total Incurred"], [null, "claim.total.incurred"], ["WC", "1.00"], ["WC", "1.00"], ["WC", "1.00"], ["WC", "1.00"], ["GL", "1.00"]);
        var none = Table("n", ["Claim #", "Total Incurred"], ["claim.claimNumber", "claim.total.incurred"], ["C-1", "1.00"]);

        Assert.Equal(("workersCompensation", false), (LineOfBusinessDetector.Detect(wc, CanonicalData.Glossary).Line, LineOfBusinessDetector.Detect(wc, CanonicalData.Glossary).OutOfScope));
        Assert.False(LineOfBusinessDetector.Detect(mixed, CanonicalData.Glossary).OutOfScope); // an all-lines run with workers' comp claims
        Assert.Null(LineOfBusinessDetector.Detect(none, CanonicalData.Glossary).Line); // no evidence: processed as before
    }

    [Fact]
    public void An_out_of_scope_document_becomes_a_kept_segment_and_a_bundle_segment_loses_its_child()
    {
        var single = Table("doc-single", ["Policy Type", "Total Incurred"], [null, "claim.total.incurred"], ["Auto", "1.00"], ["Automobile", "2.00"]);
        var parent = new DocumentGraph { DocumentId = "doc-bundle", SourceFile = new SourceFile { FileName = "b.pdf", Kind = FileKind.Pdf } };
        var segment = new GraphSegment { Category = "LossRun", StartPage = 3, EndPage = 4, ChildDocumentId = "doc-bundle-p3-4" };
        parent.Segments = [segment];
        var child = Table("doc-bundle-p3-4", ["Policy Type", "Total Incurred"], [null, "claim.total.incurred"], ["Property", "1.00"], ["Property", "2.00"]);
        child.SourceFile = new SourceFile { FileName = "b.pdf", Kind = FileKind.Pdf, ParentDocumentId = "doc-bundle", FirstPage = 3, LastPage = 4 };
        child.Segments = [segment];
        var result = new IngestionResult();
        result.Documents.AddRange([single, parent, child]);

        IngestionPipeline.ApplyLineOfBusiness(result, CanonicalData.Glossary, "LossRun");

        var kept = Assert.Single(single.Segments);
        Assert.StartsWith("line of business: auto", kept.Reason, StringComparison.Ordinal);
        Assert.Null(kept.ChildDocumentId);
        Assert.DoesNotContain(child, result.Documents);
        Assert.Null(segment.ChildDocumentId);
        Assert.StartsWith("line of business: property", segment.Reason, StringComparison.Ordinal);
    }
}
