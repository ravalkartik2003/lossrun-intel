namespace LossRun.Contracts;

// Normalized document graph produced by preprocessing (brief 04) and consumed by the format registry (05) and
// learn path (06). One graph per logical document (a bundle's loss-run segment becomes its own graph).
// Lineage units: PDF/image content carries page + polygon (CU-native units); spreadsheet content carries sheet + A1.

public sealed class DocumentGraph
{
    public const string CurrentVersion = "graph-0.2.0";

    public string GraphVersion { get; set; } = CurrentVersion;
    public string DocumentId { get; set; } = "";
    public SourceFile SourceFile { get; set; } = new();

    /// <summary>How the content was obtained: <c>spreadsheet-native</c> (no Azure call) or the CU analyzer id (e.g. <c>prebuilt-layout</c>).</summary>
    public string Engine { get; set; } = "";

    /// <summary>Unit of polygons: <c>inch</c> (PDF) or <c>pixel</c> (images) as returned by CU; null for spreadsheets.</summary>
    public string? Unit { get; set; }

    public List<GraphPage> Pages { get; set; } = [];
    public List<GraphParagraph> Paragraphs { get; set; } = [];
    public List<GraphTable> Tables { get; set; } = [];
    public List<GraphCell> Cells { get; set; } = [];
    public List<GraphWord> Words { get; set; } = [];
    public List<GraphSegment> Segments { get; set; } = [];

    /// <summary>Brief 19 Answers 3: constructs the layout shows (ADR 0018), found at ingestion; null when none.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<LayoutEvidence>? LayoutEvidence { get; set; }

    /// <summary>Brief 23 §1: document-level fields printed as "Label: value" in the header block, found at ingestion; null when none.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<HeaderField>? HeaderFields { get; set; }

    /// <summary>ADR 0020: the line of business the graph prints (workersCompensation, property, …); null without evidence.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? LineOfBusiness { get; set; }

    /// <summary>ADR 0021: <c>loss-analysis</c> or <c>changes-only</c> when the graph is that kind of report (kept, not processed); null for a loss run.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ReportKind { get; set; }
    public FingerprintInputs FingerprintInputs { get; set; } = new();
    public List<string> Warnings { get; set; } = [];
}

public sealed class SourceFile
{
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public FileKind Kind { get; set; }
    public bool WasEncrypted { get; set; }

    /// <summary>PDFs: page 1 has a text layer (born-digital); false = scanned image (OCR text). Null when unknown (older graphs).</summary>
    public bool? TextLayer { get; set; }

    /// <summary>Scanned PDFs: resolution of the page-1 image (pixels per inch as placed). Null for born-digital files or when unknown.</summary>
    public int? ScanDpi { get; set; }

    /// <summary>For a document split out of a bundle or ZIP: the parent document id.</summary>
    public string? ParentDocumentId { get; set; }

    /// <summary>For a segment of a bundle: 1-based inclusive page range in the parent file.</summary>
    public int? FirstPage { get; set; }
    public int? LastPage { get; set; }

    /// <summary>Path inside a ZIP archive, when the file came from one.</summary>
    public string? ArchivePath { get; set; }

    /// <summary>Blob path of the raw analyzer result (e.g. <c>extractions/{documentId}/layout.json</c>), when one exists.</summary>
    public string? RawResultBlobPath { get; set; }
}

public enum FileKind
{
    Unknown,
    Pdf,
    Xlsx,
    Xls,
    Xlsb,
    Csv,
    Zip,
    Png,
    Jpeg,
    Tiff,
    Bmp,
    Heif,
    Docx,
}

/// <summary>A PDF/image page, or one spreadsheet sheet (one logical page per sheet).</summary>
public sealed class GraphPage
{
    public int Number { get; set; }
    public string? SheetName { get; set; }
    public bool Hidden { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }

    /// <summary>Rotation CU detected (degrees); CU reads skewed pages without deskewing the coordinates.</summary>
    public double? Angle { get; set; }

    /// <summary>Markdown for this page (CU) or a pipe-table rendering of the sheet's used range (spreadsheets).</summary>
    public string? Markdown { get; set; }
    public int WordCount { get; set; }
}

public sealed class GraphParagraph
{
    public string Id { get; set; } = "";
    public int Page { get; set; }

    /// <summary>CU role: title, sectionHeading, pageHeader, pageFooter, pageNumber, footnote; null for body text.</summary>
    public string? Role { get; set; }
    public string Text { get; set; } = "";
    public List<double>? Polygon { get; set; }
    public int? SpanOffset { get; set; }
    public int? SpanLength { get; set; }
}

public sealed class GraphTable
{
    public string Id { get; set; } = "";
    public int Page { get; set; }
    public string? SheetName { get; set; }
    public int RowCount { get; set; }
    public int ColumnCount { get; set; }

    /// <summary>0-based row indices (within the table) that form the header; 2 rows for stacked headers.</summary>
    public List<int> HeaderRows { get; set; } = [];

    /// <summary>Composed header text per column (stacked groups carried across merged cells), e.g. "Indemnity Paid".</summary>
    public List<string> HeaderTexts { get; set; } = [];

    /// <summary>Canonical field each header resolved to via the glossary (null when unresolved), same order as HeaderTexts.</summary>
    public List<string?> HeaderFields { get; set; } = [];

    /// <summary>Header repairs applied while composing HeaderTexts (brief 18, P4): the original text is kept for lineage.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<HeaderRepair>? HeaderRepairs { get; set; }

    /// <summary>Columns whose header prints two fields in one cell ("Claim Number" over "Claim Type"; ADR 0018): column → fields, top first.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<int, List<string>>? StackedHeaders { get; set; }

    /// <summary>Columns whose header uses a conditional component word with no reading in this table (brief 17 Answers 2).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? AmbiguousHeaders { get; set; }

    /// <summary>A1 range of the table on the sheet (spreadsheets).</summary>
    public string? Range { get; set; }
    public List<double>? Polygon { get; set; }
}

/// <summary>
/// Brief 19 Answers 3: ingestion's evidence that a claim table uses a spec-language construct (ADR 0018):
/// <c>records-rows</c>, <c>records-lines</c> or <c>stacked</c>. The learn prompt states the construct as an instruction.
/// </summary>
public sealed record LayoutEvidence(string Construct, string? TableId, int Page, string Evidence);

/// <summary>One header repair (brief 18, P4).</summary>
/// <param name="Column">0-based column the repaired text belongs to.</param>
/// <param name="Original">The header text composition would have produced without the repair.</param>
/// <param name="Repaired">The header text after the repair.</param>
/// <param name="Reason">merged-cell-split | form-row-dropped.</param>
public sealed record HeaderRepair(int Column, string Original, string Repaired, string Reason);

/// <summary>A table cell (PDF/image tables from CU, or every non-empty spreadsheet cell).</summary>
public sealed class GraphCell
{
    public string? TableId { get; set; }
    public int Page { get; set; }
    public string? Sheet { get; set; }

    /// <summary>A1 address on the sheet (spreadsheets) — the lineage unit for spreadsheet values.</summary>
    public string? A1 { get; set; }

    /// <summary>0-based row/column: within the table for CU cells, within the sheet for spreadsheet cells.</summary>
    public int Row { get; set; }
    public int Column { get; set; }
    public int RowSpan { get; set; } = 1;
    public int ColumnSpan { get; set; } = 1;

    /// <summary>CU cell kind (columnHeader, rowHeader, content, stubHead, description) or header/content for spreadsheets.</summary>
    public string? Kind { get; set; }

    public string RawValue { get; set; } = "";

    /// <summary>Invariant typed value: numbers as decimals, dates as yyyy-MM-dd, booleans as true/false.</summary>
    public string? TypedValue { get; set; }
    public CellValueType ValueType { get; set; }
    public List<double>? Polygon { get; set; }
    public double? Confidence { get; set; }
}

public enum CellValueType
{
    Empty,
    Text,
    Number,
    Date,
    Boolean,
}

public sealed class GraphWord
{
    public int Page { get; set; }
    public string Text { get; set; } = "";

    /// <summary>OCR confidence; null when the service gave none (brief 31: unknown, never 0 — aggregates use the known values only).</summary>
    public double? Confidence { get; set; }
    public List<double>? Polygon { get; set; }
    public int? SpanOffset { get; set; }
}

/// <summary>A classified page range of a bundle.</summary>
public sealed class GraphSegment
{
    public string Category { get; set; } = "";
    public int StartPage { get; set; }
    public int EndPage { get; set; }

    /// <summary>Service-reported confidence. Content Understanding GA returns none, so this is null today.</summary>
    public double? Confidence { get; set; }

    /// <summary>Our own confidence for the category (brief 04 Answers 1): glossary header-resolution ratio of the segment's best table.</summary>
    public double? DerivedConfidence { get; set; }

    /// <summary>How DerivedConfidence was computed, shown honestly in the UI (e.g. <c>header-resolution</c>).</summary>
    public string? DerivedConfidenceMethod { get; set; }

    /// <summary>For LossRun segments: <c>accept</c> (≥ 0.8), <c>review</c> (0.5–0.8) or <c>notLossRun</c> (&lt; 0.5).</summary>
    public string? DerivedDecision { get; set; }

    /// <summary>Document id of the graph built for this segment (loss-run segments only).</summary>
    public string? ChildDocumentId { get; set; }

    /// <summary>Why a segment is kept and not processed, for the upload manifest (brief 18, P6: "no loss table found").</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    /// <summary>
    /// The segment's loss tables are period summaries (year rows, counts and totals) with no claim identity column
    /// (ADR 0017): a summary-only loss run. Null when not determined.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? SummaryOnly { get; set; }
}

/// <summary>What the format fingerprint (brief 05) needs, extracted once here.</summary>
public sealed class FingerprintInputs
{
    public string? TitleText { get; set; }
    public List<string> HeaderParagraphs { get; set; } = [];
    public List<string> FirstTableHeaderCells { get; set; } = [];
    public string? FirstPageMarkdown { get; set; }
    public List<string> SheetNames { get; set; } = [];
}

/// <summary>Cost of processing one document (docs/architecture/cost-tracking.md §3).</summary>
public sealed record DocumentCost(
    string DocumentId,
    string RunId,
    string Path,
    int LlmCalls,
    int Pages,
    long TokensIn,
    long TokensOut,
    decimal CostUsd,
    IReadOnlyList<CostLine> Breakdown,
    int EmbeddingCalls = 0)
{
    public const string MissingPrice = "missing price";

    /// <summary>Brief 30 item 22: lines with no price in the table: their cost is unknown, so <see cref="CostUsd"/> covers the priced lines only.</summary>
    public int UnpricedLines => Breakdown.Count(l => l.PriceSource == MissingPrice);

    /// <summary>"service meter (model)" of each unpriced line.</summary>
    public IReadOnlyList<string> Unpriced => [.. Breakdown.Where(l => l.PriceSource == MissingPrice).Select(l => $"{l.Service} {l.Meter}")];
}

public sealed record CostLine(string Service, string Meter, decimal Quantity, string Unit, decimal UnitPrice, decimal CostUsd, string? PriceSource);

/// <summary>
/// Brief 31 (brief 30 item 40): word confidence is null when the OCR service gives none. Aggregates read the known values only; with none known
/// there is no OCR evidence, so a minimum reads as certain (1.0) and a mean is unknown (null).
/// </summary>
public static class WordConfidence
{
    public static double Min(IEnumerable<GraphWord> words) => words.Select(w => w.Confidence).OfType<double>().DefaultIfEmpty(1.0).Min();

    public static double? Mean(IEnumerable<GraphWord> words) =>
        words.Select(w => w.Confidence).OfType<double>().ToList() is { Count: > 0 } known ? known.Average() : null;
}
