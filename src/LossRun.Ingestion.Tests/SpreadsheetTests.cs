using LossRun.Contracts;
using LossRun.Ingestion.Spreadsheets;

namespace LossRun.Ingestion.Tests;

public class SpreadsheetTests
{
    private static DocumentGraph Read(string name, FileKind kind) =>
        SpreadsheetReader.Read(Fixture.Bytes(name), new SourceFile { FileName = name, Kind = kind }, "doc-test", CanonicalData.Glossary, []);

    [Fact]
    public void Every_sheet_is_a_page_and_hidden_sheets_are_flagged()
    {
        var graph = Read("lossrun-stacked.xlsx", FileKind.Xlsx);

        Assert.Equal(["Loss Run", "Summary", "Notes"], graph.Pages.Select(p => p.SheetName));
        Assert.Equal([false, false, true], graph.Pages.Select(p => p.Hidden));
        Assert.Contains(graph.Warnings, w => w.Contains("'Notes' is hidden"));
        Assert.Equal("spreadsheet-native", graph.Engine);
        Assert.Null(graph.Unit);
        Assert.Empty(graph.Words);
    }

    [Fact]
    public void Stacked_headers_are_composed_and_resolved_through_the_glossary()
    {
        var table = Read("lossrun-stacked.xlsx", FileKind.Xlsx).Tables[0];

        Assert.Equal("A5:Q9", table.Range);
        Assert.Equal([0, 1], table.HeaderRows);
        Assert.Equal("Indemnity Paid", table.HeaderTexts[7]);
        Assert.Equal("claim.indemnity.paid", table.HeaderFields[7]);
        Assert.Equal("claim.medical.reserve", table.HeaderFields[11]);
        Assert.Equal("claim.expense.incurred", table.HeaderFields[15]);
        Assert.Equal("claim.claimNumber", table.HeaderFields[1]);
        Assert.Equal("claim.total.incurred", table.HeaderFields[16]);
        Assert.All(table.HeaderFields, Assert.NotNull);
    }

    [Fact]
    public void Cells_carry_sheet_and_a1_lineage_with_typed_values()
    {
        var cells = Read("lossrun-stacked.xlsx", FileKind.Xlsx).Cells.Where(c => c.Sheet == "Loss Run").ToDictionary(c => c.A1!);

        Assert.Equal(("WC-22-0001", CellValueType.Text, "t0", "content"), (cells["B7"].RawValue, cells["B7"].ValueType, cells["B7"].TableId, cells["B7"].Kind));
        Assert.Equal(("2022-09-15", CellValueType.Date), (cells["D7"].TypedValue, cells["D7"].ValueType));
        Assert.Equal(("12000", CellValueType.Number), (cells["H7"].TypedValue, cells["H7"].ValueType));
        Assert.Equal(("2024-09-30", CellValueType.Date), (cells["B2"].TypedValue, cells["B2"].ValueType)); // valuation date above the table
        Assert.Null(cells["B2"].TableId);
        Assert.Equal("columnHeader", cells["H5"].Kind); // merged group header "Indemnity"
        Assert.Equal((6, 1), (cells["B7"].Row, cells["B7"].Column));
    }

    [Fact]
    public void Csv_values_are_typed_by_inference()
    {
        var graph = Read("lossrun-export.csv", FileKind.Csv);
        var table = Assert.Single(graph.Tables);
        var cells = graph.Cells.ToDictionary(c => c.A1!);

        Assert.Equal("A1:M4", table.Range);
        Assert.All(table.HeaderFields, Assert.NotNull);
        Assert.Equal(("2022-09-15", CellValueType.Date), (cells["D2"].TypedValue, cells["D2"].ValueType));
        Assert.Equal(("12000.00", CellValueType.Number), (cells["H2"].TypedValue, cells["H2"].ValueType));
        Assert.Equal(("SYN-WC-1001", CellValueType.Text), (cells["A2"].TypedValue, cells["A2"].ValueType));
        Assert.Equal("lossrun-export", graph.Pages[0].SheetName);
    }

    [Fact]
    public void Fingerprint_inputs_come_from_above_the_header()
    {
        var inputs = Read("lossrun-stacked.xlsx", FileKind.Xlsx).FingerprintInputs;

        Assert.Equal("Synthetic Mutual Insurance Co.", inputs.TitleText);
        Assert.Contains("Valued as of", inputs.HeaderParagraphs);
        Assert.Equal(17, inputs.FirstTableHeaderCells.Count);
        Assert.Equal(["Loss Run", "Summary", "Notes"], inputs.SheetNames);
        Assert.StartsWith("| Synthetic Mutual Insurance Co.", inputs.FirstPageMarkdown);
    }

    [Fact]
    public void Header_detection_skips_title_rows_and_prefers_glossary_matches()
    {
        GridCell T(string s) => new(s, s, CellValueType.Text);
        GridCell N(string s) => new(s, s, CellValueType.Number);
        List<GridCell?[]> grid =
        [
            [T("Loss Run Report"), null, null],
            [T("Prepared by"), T("Someone"), null],
            [T("Claim #"), T("DOI"), T("Total Incurred")],
            [T("C-1"), new GridCell("2024-01-01", "2024-01-01", CellValueType.Date), N("100")],
            [T("C-2"), new GridCell("2024-02-01", "2024-02-01", CellValueType.Date), N("200")],
        ];

        var detection = HeaderDetector.Detect(grid, [], CanonicalData.Glossary)!;

        Assert.Equal((2, (int?)null, 4), (detection.HeaderRow, detection.GroupRow, detection.LastDataRow));
        Assert.Equal(["claim.claimNumber", "claim.dateOfInjury", "claim.total.incurred"], detection.Matches.Select(m => m!.Field));
    }

    [Fact]
    public void Stacked_headers_are_composed_when_the_plain_row_repeats_headers()
    {
        // Eval pilot (c4): "Paid | Outstanding | Total" under merged Indemnity / Medical / Expense groups; "Outstanding" and
        // "<group> Total" score no better than the plain row, but plain "Paid" ×3 cannot be mapped to one column.
        GridCell T(string s) => new(s, s, CellValueType.Text);
        GridCell N(string s) => new(s, s, CellValueType.Number);
        List<GridCell?[]> grid =
        [
            [null, T("Indemnity"), null, null, T("Medical"), null, null],
            [T("Claim Ref"), T("Paid"), T("Outstanding"), T("Total"), T("Paid"), T("Outstanding"), T("Total")],
            [T("C-1"), N("1"), N("2"), N("3"), N("4"), N("5"), N("9")],
        ];

        var detection = HeaderDetector.Detect(grid, [new MergedRange(0, 1, 0, 3), new MergedRange(0, 4, 0, 6)], CanonicalData.Glossary)!;

        Assert.Equal(0, detection.GroupRow);
        Assert.Equal(["Claim Ref", "Indemnity Paid", "Indemnity Outstanding", "Indemnity Total", "Medical Paid", "Medical Outstanding", "Medical Total"], detection.HeaderTexts);
    }

    [Fact]
    public void A_text_layer_tells_born_digital_from_scanned()
    {
        // 08b: CU OCRs born-digital pages too, so word confidence cannot decide OCR-aware routing; the text layer can.
        Assert.True(FirstPageCheck.HasTextLayer(Fixture.Bytes("lossrun-native.pdf")));
        Assert.False(FirstPageCheck.HasTextLayer(Fixture.Bytes("lossrun-scanned.pdf")));
    }

    [Fact]
    public void A_scan_reports_its_resolution_and_a_born_digital_pdf_none()
    {
        // 08c: the learn-from-scan policy (08b Answers 5) needs the scan's resolution; the page image carries it.
        var dpi = FirstPageCheck.ScanDpi(Fixture.Bytes("lossrun-scanned.pdf"));
        Assert.NotNull(dpi);
        Assert.InRange(dpi!.Value, 100, 600);
        Assert.Null(FirstPageCheck.ScanDpi(Fixture.Bytes("lossrun-native.pdf")));
    }

    [Theory]
    [InlineData(0, 0, "A1")]
    [InlineData(0, 25, "Z1")]
    [InlineData(0, 26, "AA1")]
    [InlineData(9, 701, "ZZ10")]
    [InlineData(99, 702, "AAA100")]
    public void A1_addresses(int row, int column, string expected)
    {
        Assert.Equal(expected, SpreadsheetReader.A1(row, column));
    }

    [Theory]
    [InlineData("Ind Paid", "claim.indemnity.paid", "composite")]
    [InlineData("Med Reserve", "claim.medical.reserve", "composite")]
    [InlineData("Indemnity Paid", "claim.indemnity.paid", "alias")]
    [InlineData("Paid", "claim.total.paid", "alias")]
    [InlineData("Outstanding", "claim.total.reserve", "alias")]
    [InlineData("Legal Paid", "claim.expense.paid", "alias")]
    [InlineData("Reported", "claim.dateReported", "alias")] // glossary 0.1.2
    [InlineData("Exp Paid", "claim.expense.paid", "composite")] // glossary 0.1.2
    [InlineData("Xyz Paid", null, null)] // unknown component: never guessed as the total
    [InlineData("Total Paid", "claim.total.paid", "alias")]
    [InlineData("Paid to Date", "claim.total.paid", "alias")]
    [InlineData("Total Reserve", "claim.total.reserve", "measure-only")]
    public void Glossary_resolves_headers_without_a_model(string header, string? field, string? method)
    {
        var match = CanonicalData.Glossary.ResolveHeader(header);

        Assert.Equal(field, match?.Field);
        Assert.Equal(method, match?.Method);
    }
}
