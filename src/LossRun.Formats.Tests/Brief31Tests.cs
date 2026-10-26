using LossRun.Contracts;

namespace LossRun.Formats.Tests;

/// <summary>Brief 31 item 2: the all-fields study on corpus v5 found the only delivered errors left after the generator fix, in header names on scans.</summary>
public class Brief31Tests
{
    private static DocumentGraph Header(params (double Y, double X, string Text)[] lines)
    {
        var graph = new DocumentGraph { DocumentId = "doc-h", SourceFile = new SourceFile { FileName = "h.pdf", Kind = FileKind.Pdf }, Pages = [new GraphPage { Number = 1 }] };
        foreach (var (y, x0, text) in lines)
        {
            var x = x0;
            foreach (var word in text.Split(' '))
            {
                var w = 0.08 * word.Length;
                graph.Words.Add(new GraphWord { Page = 1, Text = word, Confidence = 0.98, Polygon = [x, y, x + w, y, x + w, y + 0.15, x, y + 0.15] });
                x += w + 0.08;
            }
        }

        return graph;
    }

    private static string? Insured(DocumentGraph graph) => HeaderFields.Read(graph, "Named Insured", "document.insuredName")?.Value;

    [Fact]
    public void A_header_name_stops_at_a_colon_from_the_next_line_of_a_skewed_scan()
    {
        // A rotated scan puts the next line's ": 03/04/2024" on the insured's printed line (corpus v5 c2-014, 200 dpi rotated).
        var graph = Header((0.50, 0.5, "Named Insured: Eta Printing ( synthetic )"), (0.52, 4.6, ": 03/04/2024"));
        Assert.Equal("Eta Printing ( synthetic )", Insured(graph)); // before brief 31: "Eta Printing ( synthetic ) : 03/04/2024"
    }

    [Theory]
    [InlineData("lota Staffing (synthetic)", "Iota Staffing (synthetic)")]   // corpus v5 c1-005 / c2-003 scans: OCR "l" for "I"
    [InlineData("lota Staffing ( synthetic )", "Iota Staffing ( synthetic )")]
    [InlineData("Iota Staffing (synthetic)", "Iota Staffing (synthetic)")]
    [InlineData("Acme lndustrial Supply", "Acme lndustrial Supply")]        // a word inside the name: no evidence ("lite" is a word), left as printed
    [InlineData("lowe", "lowe")]                                            // one word: no Title-Case evidence
    [InlineData("lowe and Sons", "lowe and Sons")]                          // another word is lower case: the name is not Title Case
    public void A_scanned_title_case_name_reads_a_first_word_lowercase_l_as_capital_i(string printed, string expected) =>
        Assert.Equal(expected, HeaderFields.TitleCaseNameRepair(printed));

    [Fact]
    public void The_name_repair_applies_to_scans_only()
    {
        var scan = Header((0.50, 0.5, "Named Insured: lota Staffing (synthetic)"));
        Assert.Equal("Iota Staffing (synthetic)", Insured(scan)); // before brief 31: "lota Staffing (synthetic)"
        var native = Header((0.50, 0.5, "Named Insured: lota Staffing (synthetic)"));
        native.SourceFile.TextLayer = true;
        Assert.Equal("lota Staffing (synthetic)", Insured(native)); // a text layer prints what the carrier printed
    }

    [Fact]
    public void A_scanned_name_read_by_any_locator_gets_the_repair_and_keeps_the_printed_text_in_lineage()
    {
        var graph = Graphs.Scanned();
        var paragraph = graph.Paragraphs.First(p => p.Text.Contains("Insured:", StringComparison.Ordinal));
        var printed = paragraph.Text[(paragraph.Text.IndexOf("Insured:", StringComparison.Ordinal) + 8)..].Trim();
        paragraph.Text = paragraph.Text.Replace(printed, "l" + printed[1..], StringComparison.Ordinal); // the first capital OCR'd as l
        var replay = LossRun.Formats.Replay.ReplayEngine.Execute(graph, Graphs.Spec("synthetic-mutual-pdf"));

        Assert.Equal("I" + printed[1..], replay.Document.InsuredName); // regex locator; before brief 31: "l…"
        Assert.StartsWith("l", replay.Document.Lineage!["insuredName"].SourceText, StringComparison.Ordinal);
        Assert.Equal("ocrRepair: l→I", replay.Document.Lineage["insuredName"].Repair); // brief 31 Answers 1: the repair is visible
    }

    [Fact]
    public void A_header_label_read_keeps_the_printed_name_in_lineage_and_notes_the_repair()
    {
        var spec = Graphs.Spec("synthetic-mutual-pdf");
        spec.DocumentFields.RemoveAll(f => f.Target == "document.insuredName");
        spec.DocumentFields.Add(new DocumentFieldSpec { Target = "document.insuredName", Locator = new FieldLocator { Type = "headerLabel", Label = "Named Insured" } });
        var replay = LossRun.Formats.Replay.ReplayEngine.Execute(Header((0.50, 0.5, "Named Insured: lota Staffing (synthetic)")), spec);

        Assert.Equal("Iota Staffing (synthetic)", replay.Document.InsuredName);
        var lineage = replay.Document.Lineage!["insuredName"];
        Assert.Equal("lota Staffing (synthetic)", lineage.SourceText); // before: the repaired value, the printed text was lost
        Assert.Equal("ocrRepair: l→I", lineage.Repair);

        var unrepaired = LossRun.Formats.Replay.ReplayEngine.Execute(Header((0.50, 0.5, "Named Insured: Iota Staffing (synthetic)")), spec);
        Assert.Null(unrepaired.Document.Lineage!["insuredName"].Repair);
    }

    [Fact]
    public void Item7_a_word_without_a_confidence_is_unknown_not_zero()
    {
        // Before brief 31 the mapper wrote 0 for a missing confidence: such a graph read as a low-confidence scan.
        var graph = new DocumentGraph { SourceFile = new SourceFile { FileName = "x.pdf", Kind = FileKind.Pdf } };
        graph.Words.Add(new GraphWord { Page = 1, Text = "Claim", Confidence = null, Polygon = [0, 0, 1, 0, 1, 1, 0, 1] });
        graph.Words.Add(new GraphWord { Page = 1, Text = "WC-1", Confidence = null, Polygon = [2, 0, 3, 0, 3, 1, 2, 1] });

        Assert.Null(FormatRouter.MeanOcrConfidence(graph));
        Assert.False(LossRun.Formats.Replay.OcrHeaders.IsScanned(graph)); // no text-layer flag and no known confidence: no evidence of a scan
        Assert.Equal(1.0, WordConfidence.Min(graph.Words));

        graph.Words.Add(new GraphWord { Page = 1, Text = "1,200", Confidence = 0.62 });
        Assert.Equal(0.62, FormatRouter.MeanOcrConfidence(graph)); // the known value alone, not (0 + 0 + 0.62) / 3
        Assert.True(LossRun.Formats.Replay.OcrHeaders.IsScanned(graph));
    }
}
