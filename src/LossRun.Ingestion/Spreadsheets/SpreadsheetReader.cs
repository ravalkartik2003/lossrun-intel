using System.Globalization;
using System.Text;
using ExcelDataReader;
using ExcelDataReader.Exceptions;
using LossRun.Contracts;

namespace LossRun.Ingestion.Spreadsheets;

/// <summary>
/// Reads XLS/XLSX/XLSB/CSV natively (no Azure call): one logical page per sheet (hidden sheets included and flagged),
/// every non-empty cell with sheet + A1 lineage and a typed value, and one table per sheet where a header row is found.
/// </summary>
public static class SpreadsheetReader
{
    private const int MarkdownRowLimit = 60;

    static SpreadsheetReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // legacy .xls code pages
    }

    public static DocumentGraph Read(byte[] bytes, SourceFile source, string documentId, Glossary glossary, IReadOnlyList<string> passwords)
    {
        var sheets = ReadSheets(bytes, source, passwords);
        var graph = new DocumentGraph { DocumentId = documentId, SourceFile = source, Engine = "spreadsheet-native" };

        for (var s = 0; s < sheets.Count; s++)
        {
            var sheet = sheets[s];
            var pageNumber = s + 1;
            graph.Pages.Add(new GraphPage
            {
                Number = pageNumber,
                SheetName = sheet.Name,
                Hidden = sheet.Hidden,
                Markdown = Markdown(sheet.Grid),
                WordCount = sheet.Grid.Sum(row => row.Count(c => c is { Type: not CellValueType.Empty })),
            });

            var detection = HeaderDetector.Detect(sheet.Grid, sheet.Merges, glossary);
            string? tableId = null;
            if (detection is not null)
            {
                tableId = $"t{graph.Tables.Count}";
                var top = detection.GroupRow ?? detection.HeaderRow;
                graph.Tables.Add(new GraphTable
                {
                    Id = tableId,
                    Page = pageNumber,
                    SheetName = sheet.Name,
                    RowCount = detection.LastDataRow - top + 1,
                    ColumnCount = detection.LastColumn - detection.FirstColumn + 1,
                    HeaderRows = detection.GroupRow is { } g ? [g - top, detection.HeaderRow - top] : [detection.HeaderRow - top],
                    HeaderTexts = [.. detection.HeaderTexts],
                    HeaderFields = [.. detection.Matches.Select(m => m?.Field)],
                    Range = $"{A1(top, detection.FirstColumn)}:{A1(detection.LastDataRow, detection.LastColumn)}",
                });
            }

            for (var r = 0; r < sheet.Grid.Count; r++)
            {
                var row = sheet.Grid[r];
                for (var c = 0; c < row.Length; c++)
                {
                    if (row[c] is not { Type: not CellValueType.Empty } cell)
                    {
                        continue;
                    }

                    var inTable = detection is not null && r >= (detection.GroupRow ?? detection.HeaderRow) && r <= detection.LastDataRow
                        && c >= detection.FirstColumn && c <= detection.LastColumn;
                    graph.Cells.Add(new GraphCell
                    {
                        TableId = inTable ? tableId : null,
                        Page = pageNumber,
                        Sheet = sheet.Name,
                        A1 = A1(r, c),
                        Row = r,
                        Column = c,
                        Kind = inTable ? (r <= detection!.HeaderRow ? "columnHeader" : "content") : null,
                        RawValue = cell.Raw,
                        TypedValue = cell.Typed,
                        ValueType = cell.Type,
                    });
                }
            }

            if (sheet.Hidden)
            {
                graph.Warnings.Add($"Sheet '{sheet.Name}' is hidden; included and flagged.");
            }
        }

        graph.FingerprintInputs = Fingerprint(graph, sheets);
        return graph;
    }

    /// <summary>0-based row/column to an A1 address (column letters A..XFD).</summary>
    public static string A1(int row, int column)
    {
        var letters = new StringBuilder();
        for (var c = column + 1; c > 0; c = (c - 1) / 26)
        {
            letters.Insert(0, (char)('A' + (c - 1) % 26));
        }

        return letters.Append(row + 1).ToString();
    }

    internal sealed record Sheet(string Name, bool Hidden, List<GridCell?[]> Grid, List<MergedRange> Merges);

    internal static List<Sheet> ReadSheets(byte[] bytes, SourceFile source, IReadOnlyList<string> passwords)
    {
        foreach (var password in new string?[] { null }.Concat(passwords))
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                var config = new ExcelReaderConfiguration { Password = password, FallbackEncoding = Encoding.UTF8 };
                using var reader = source.Kind == FileKind.Csv
                    ? ExcelReaderFactory.CreateCsvReader(stream, config)
                    : ExcelReaderFactory.CreateReader(stream, config);
                if (password is not null)
                {
                    source.WasEncrypted = true;
                }

                return ReadAll(reader, source);
            }
            catch (InvalidPasswordException)
            {
                source.WasEncrypted = true;
            }
        }

        throw new InvalidPasswordException(passwords.Count == 0
            ? "Workbook is password-protected and no passwords are configured."
            : $"Workbook is password-protected and none of the {passwords.Count} configured passwords opened it.");
    }

    private static List<Sheet> ReadAll(IExcelDataReader reader, SourceFile source)
    {
        var sheets = new List<Sheet>();
        var csv = source.Kind == FileKind.Csv;
        do
        {
            var grid = new List<GridCell?[]>();
            while (reader.Read())
            {
                var row = new GridCell?[reader.FieldCount];
                for (var c = 0; c < reader.FieldCount; c++)
                {
                    row[c] = Cell(reader.GetValue(c), csv);
                }

                grid.Add(row);
            }

            var name = csv || string.IsNullOrEmpty(reader.Name) ? Path.GetFileNameWithoutExtension(source.FileName) : reader.Name;
            var hidden = !csv && !string.Equals(reader.VisibleState, "visible", StringComparison.OrdinalIgnoreCase);
            var merges = (reader.MergeCells ?? []).Select(m => new MergedRange(m.FromRow, m.FromColumn, m.ToRow, m.ToColumn)).ToList();
            sheets.Add(new Sheet(name, hidden, grid, merges));
        }
        while (reader.NextResult());

        return sheets;
    }

    /// <summary>Types an Excel value (or infers one from CSV text): numbers as invariant decimals, dates as yyyy-MM-dd.</summary>
    internal static GridCell? Cell(object? value, bool inferFromText)
    {
        switch (value)
        {
            case null:
                return null;
            case double d:
                return new GridCell(d.ToString("R", CultureInfo.InvariantCulture), ((decimal)d).ToString(CultureInfo.InvariantCulture), CellValueType.Number);
            case int or long or decimal or float:
                var n = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return new GridCell(n.ToString(CultureInfo.InvariantCulture), n.ToString(CultureInfo.InvariantCulture), CellValueType.Number);
            case DateTime dt:
                var iso = dt.ToString(dt.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                return new GridCell(iso, iso, CellValueType.Date);
            case bool b:
                return new GridCell(b ? "TRUE" : "FALSE", b ? "true" : "false", CellValueType.Boolean);
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (inferFromText)
        {
            // Dates need a separator so that class codes / years are not read as Excel serials.
            if (text.IndexOfAny(['/', '-']) > 0 && CanonicalComparer.NormalizeDate(text) is { } date && text.Any(char.IsDigit))
            {
                return new GridCell(text, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), CellValueType.Date);
            }

            if (CanonicalComparer.NormalizeCurrency(text) is { } number && text.Any(char.IsDigit))
            {
                return new GridCell(text, number.ToString(CultureInfo.InvariantCulture), CellValueType.Number);
            }
        }

        return new GridCell(text, text.Trim(), CellValueType.Text);
    }

    private static string Markdown(List<GridCell?[]> grid)
    {
        var rows = grid.Where(r => r.Any(c => c is { Type: not CellValueType.Empty })).Take(MarkdownRowLimit).ToList();
        if (rows.Count == 0)
        {
            return "";
        }

        var width = rows.Max(r => r.Length);
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.Append('|');
            for (var c = 0; c < width; c++)
            {
                sb.Append(' ').Append(c < row.Length ? row[c]?.Raw.Replace("|", "\\|") : "").Append(" |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static FingerprintInputs Fingerprint(DocumentGraph graph, List<Sheet> sheets)
    {
        var table = graph.Tables.FirstOrDefault();
        var page = table is null ? graph.Pages.FirstOrDefault(p => !p.Hidden) : graph.Pages[table.Page - 1];
        var sheet = page is null ? null : sheets[page.Number - 1];
        var headerTop = table is null ? int.MaxValue : graph.Cells.Where(c => c.TableId == table.Id).Min(c => c.Row);
        var above = sheet is null
            ? []
            : sheet.Grid.Take(Math.Min(headerTop, sheet.Grid.Count)).SelectMany(r => r).Where(c => c is { Type: CellValueType.Text }).Select(c => c!.Raw.Trim()).ToList();

        return new FingerprintInputs
        {
            TitleText = above.FirstOrDefault(),
            HeaderParagraphs = [.. above.Take(10)],
            FirstTableHeaderCells = table?.HeaderTexts ?? [],
            FirstPageMarkdown = page?.Markdown is { } md ? md[..Math.Min(md.Length, 4000)] : null,
            SheetNames = [.. graph.Pages.Select(p => p.SheetName ?? "")],
        };
    }
}
