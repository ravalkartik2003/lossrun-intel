using System.Globalization;
using System.Text.Json;
using LossRun.Contracts;

namespace LossRun.Ingestion.ContentUnderstanding;

/// <summary>
/// Maps a Content Understanding document result (prebuilt-layout, or a prebuilt-document-based classifier) into the
/// document graph. Polygons keep CU's native unit (inch for PDF, pixel for images) and page numbers refer to the
/// analyzed file, so lineage resolves against the stored original.
/// </summary>
public static partial class CuResultMapper
{
    /// <summary>
    /// A column's header text: its header cells top to bottom (a group header stays: "Claim Cost Breakdown Paid"), except
    /// (brief 17, real samples) a banner cell spanning (nearly) the whole table -- a report title or date range CU marks as a
    /// column header ("Accident Date Range: ... Claim Number") -- and a cell holding a value (an amount, a date, a figure: a
    /// totals row read as a header row, "Paid $160,005.37 Paid"). Both made otherwise known headers unresolvable.
    /// </summary>
    public static List<string> ComposeHeaderTexts(IReadOnlyList<JsonElement> headerCells, int columns) => ComposeHeaderTexts(headerCells, columns, null, null);

    /// <summary>
    /// As <see cref="ComposeHeaderTexts(IReadOnlyList{JsonElement}, int)"/>, plus header repair (brief 18, P4) when a glossary
    /// is given: (1) a form row CU read into the header ("Client Name: | ... | Own Risk # ..." above the real header row) is
    /// dropped when it carries a "Label:" cell and another header row follows; (2) a merged cell spanning k columns is split
    /// into k headers when its text divides into k glossary-resolvable parts (its printed lines, else longest alias first).
    /// A group header ("Claim Cost Breakdown") does not resolve, so it stays a group. Every changed column is logged in
    /// <paramref name="repairs"/> with the text composition would have produced.
    /// </summary>
    public static List<string> ComposeHeaderTexts(IReadOnlyList<JsonElement> headerCells, int columns, Glossary? glossary, List<HeaderRepair>? repairs)
    {
        var cells = headerCells.Select(c => new HeaderCell(c.GetProperty("rowIndex").GetInt32(), c.GetProperty("columnIndex").GetInt32(), Int(c, "columnSpan", 1),
            (Str(c, "content") ?? "").Trim())).ToList();
        var plain = Compose(cells, columns);
        if (glossary is null)
        {
            return plain;
        }

        var rows = cells.Select(c => c.Row).Distinct().Order().ToList();
        var formRows = rows.Where(r => r != rows[^1] && cells.Any(c => c.Row == r && FormLabel().IsMatch(c.Text))).ToHashSet();
        var kept = cells.Where(c => !formRows.Contains(c.Row)).ToList();
        int Resolved(List<string> texts) => texts.Count(t => glossary.ResolveHeader(t) is not null);
        if (formRows.Count > 0 && Resolved(Compose(kept, columns)) <= Resolved(plain))
        {
            // A "Label:" cell in a header row that carries real headers (brief 18 run: a real sample lost five of its
            // column headers): drop the row only when that makes more headers resolve.
            formRows.Clear();
            kept = cells;
        }
        var split = new HashSet<int>();
        var repaired = new List<HeaderCell>();
        foreach (var cell in kept)
        {
            var parts = cell.Span >= 2 && !Banner(cell.Span, columns) ? SplitMerged(cell.Text, cell.Span, glossary) : null;
            if (parts is null)
            {
                repaired.Add(cell);
                continue;
            }

            for (var i = 0; i < parts.Count; i++)
            {
                repaired.Add(new HeaderCell(cell.Row, cell.Column + i, 1, parts[i]));
                split.Add(cell.Column + i);
            }
        }

        var texts = Compose(repaired, columns);
        for (var col = 0; col < columns; col++)
        {
            if (texts[col] != plain[col])
            {
                var dropped = cells.Any(c => formRows.Contains(c.Row) && col >= c.Column && col < c.Column + c.Span);
                var reason = string.Join("+", new[] { dropped ? "form-row-dropped" : null, split.Contains(col) ? "merged-cell-split" : null }.OfType<string>());
                repairs?.Add(new HeaderRepair(col, plain[col], texts[col], reason));
            }
        }

        return texts;
    }

    private sealed record HeaderCell(int Row, int Column, int Span, string Text);

    private static bool Banner(int span, int columns) => columns >= 3 && span >= Math.Max(3, columns - 1);

    private static List<string> Compose(List<HeaderCell> cells, int columns)
    {
        static bool Value(string text) => text.Any(char.IsDigit) && (text.Contains('$') || ValueCell().IsMatch(text));
        return Enumerable.Range(0, columns)
            .Select(col => string.Join(" ", cells
                .Where(c => col >= c.Column && col < c.Column + c.Span)
                .Where(c => !Banner(c.Span, columns))
                .OrderBy(c => c.Row)
                .Select(c => c.Text)
                .Where(t => t.Length > 0 && !Value(t))))
            .ToList();
    }

    /// <summary>
    /// ADR 0018 (P3): an unresolved header that divides into exactly two known headers ("Claim Number Claim Type", one cell
    /// printing two fields stacked) resolves to both, top first. Its column counts as resolved by its first field.
    /// </summary>
    /// <remarks>
    /// Brief 19 follow-up: one header cell, two claim fields. Both parts resolve to claim.* fields by alias or composite with
    /// confidence ≥ 0.8 ("Policy Term" and "Total Recoveries" are not stacked cells), and the table has one header row (two
    /// header rows composed — "Claimant(s): Total Incurred:" — are a two-line record, not a stacked cell).
    /// </remarks>
    public static Dictionary<int, List<string>> StackedHeaders(IReadOnlyList<string> headerTexts, TableHeaderResolution resolution, Glossary glossary, int headerRows = 1)
    {
        var stacked = new Dictionary<int, List<string>>();
        if (headerRows > 1)
        {
            return stacked;
        }

        for (var i = 0; i < headerTexts.Count; i++)
        {
            if (resolution.Matches[i] is null && !resolution.Ambiguous.Contains(i) && headerTexts[i].Trim().Length > 0
                && SplitMerged(headerTexts[i], 2, glossary) is { } parts
                && parts.Select(glossary.ResolveHeader).All(m => m is { Method: "alias" or "composite", Confidence: >= 0.8 } && m.Field.StartsWith("claim.", StringComparison.Ordinal)))
            {
                stacked[i] = [.. parts.Select(p => glossary.ResolveHeader(p)!.Field)];
            }
        }

        return stacked;
    }

    /// <summary>Splits a merged header into exactly <paramref name="count"/> resolvable parts, or returns null.</summary>
    private static List<string>? SplitMerged(string text, int count, Glossary glossary)
    {
        bool Resolves(string part) => glossary.ResolveHeader(part) is { Method: not "learned" };
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == count && lines.All(Resolves))
        {
            return [.. lines];
        }

        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        List<string>? Split(int start, int remaining)
        {
            if (remaining == 0)
            {
                return start == tokens.Length ? [] : null;
            }

            for (var length = tokens.Length - start - (remaining - 1); length >= 1; length--)
            {
                var part = string.Join(" ", tokens[start..(start + length)]);
                if (Resolves(part) && Split(start + length, remaining - 1) is { } rest)
                {
                    return [part, .. rest];
                }
            }

            return null;
        }

        return Split(0, count);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[\s(]*-?[\d,.]+%?[)\s]*$|\d{1,2}/\d{1,2}/\d{2,4}")]
    private static partial System.Text.RegularExpressions.Regex ValueCell();

    /// <summary>A form label cell: a short label ending with a colon ("Client Name:").</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z][A-Za-z .#/'-]{0,40}:$")]
    private static partial System.Text.RegularExpressions.Regex FormLabel();

    public static DocumentGraph Map(JsonElement operation, string documentId, SourceFile source, string engine, Glossary glossary,
        int? firstPage = null, int? lastPage = null)
    {
        var content = Content(operation);
        bool InRange(int page) => (firstPage is null || page >= firstPage) && (lastPage is null || page <= lastPage);

        var markdown = content.TryGetProperty("markdown", out var md) ? md.GetString() ?? "" : "";
        var graph = new DocumentGraph
        {
            DocumentId = documentId,
            SourceFile = source,
            Engine = engine,
            Unit = content.TryGetProperty("unit", out var unit) ? unit.GetString() : null,
        };

        // The inline (sync, 2026-06-01-preview) analyze omits every "span" (brief 07). Offsets index the markdown, so they
        // are rebuilt by locating each element there in reading order; page markdown comes from the PageBreak markers.
        var fallback = new SpanFallback(markdown);
        var pageBreaks = markdown.Split("<!-- PageBreak -->");
        var pageIndex = 0;
        foreach (var page in Array(content, "pages"))
        {
            var number = page.GetProperty("pageNumber").GetInt32();
            if (!InRange(number))
            {
                continue;
            }

            var words = Array(page, "words").ToList();
            graph.Pages.Add(new GraphPage
            {
                Number = number,
                Width = Number(page, "width"),
                Height = Number(page, "height"),
                Angle = Number(page, "angle"),
                Markdown = page.TryGetProperty("spans", out _) ? Slice(markdown, Array(page, "spans")) : pageBreaks.ElementAtOrDefault(pageIndex)?.Trim(),
                WordCount = words.Count,
            });
            pageIndex++;
            graph.Words.AddRange(words.Select(w =>
            {
                var (_, polygon) = ParseSource(Str(w, "source"));
                return new GraphWord
                {
                    Page = number,
                    Text = Str(w, "content") ?? "",
                    Confidence = Number(w, "confidence"), // brief 31: absent is unknown, not 0
                    Polygon = polygon,
                    SpanOffset = Span(w).Offset,
                };
            }));
        }

        var paragraphIndex = 0;
        foreach (var paragraph in Array(content, "paragraphs"))
        {
            var (page, polygon) = ParseSource(Str(paragraph, "source"));
            var id = $"p{paragraphIndex++}";
            if (page is null || !InRange(page.Value))
            {
                continue;
            }

            var (offset, length) = Span(paragraph);
            if (offset is null)
            {
                (offset, length) = fallback.Paragraph(Str(paragraph, "content") ?? "");
            }

            graph.Paragraphs.Add(new GraphParagraph
            {
                Id = id,
                Page = page.Value,
                Role = Str(paragraph, "role"),
                Text = Str(paragraph, "content") ?? "",
                Polygon = polygon,
                SpanOffset = offset,
                SpanLength = length,
            });
        }

        var tableIndex = 0;
        var firstTableOffset = int.MaxValue;
        foreach (var table in Array(content, "tables"))
        {
            var (page, polygon) = ParseSource(Str(table, "source"));
            var id = $"t{tableIndex++}";
            if (page is null || !InRange(page.Value))
            {
                continue;
            }

            if (graph.Tables.Count == 0)
            {
                firstTableOffset = Span(table).Offset ?? fallback.FirstTable() ?? int.MaxValue;
            }

            var cells = Array(table, "cells").ToList();
            var columns = table.GetProperty("columnCount").GetInt32();
            var headerCells = cells.Where(c => Str(c, "kind") == "columnHeader").ToList();
            var headerRows = headerCells.Select(c => c.GetProperty("rowIndex").GetInt32()).Distinct().Order().ToList();
            var repairs = new List<HeaderRepair>();
            var headerTexts = ComposeHeaderTexts(headerCells, columns, glossary, repairs);
            var resolution = glossary.ResolveTableHeaders(headerTexts);
            var stacked = StackedHeaders(headerTexts, resolution, glossary, headerRows.Count);

            graph.Tables.Add(new GraphTable
            {
                Id = id,
                Page = page.Value,
                RowCount = table.GetProperty("rowCount").GetInt32(),
                ColumnCount = columns,
                HeaderRows = headerRows,
                HeaderTexts = headerTexts,
                HeaderFields = [.. resolution.Matches.Select((m, i) => m?.Field ?? (stacked.TryGetValue(i, out var fields) ? fields[0] : null))],
                StackedHeaders = stacked.Count == 0 ? null : stacked,
                HeaderRepairs = repairs.Count == 0 ? null : repairs,
                AmbiguousHeaders = resolution.Ambiguous.Count == 0 ? null : [.. resolution.Ambiguous],
                Polygon = polygon,
            });

            foreach (var cell in cells)
            {
                var (cellPage, cellPolygon) = ParseSource(Str(cell, "source"));
                var raw = Str(cell, "content") ?? "";
                var (typed, type) = Type(raw);
                graph.Cells.Add(new GraphCell
                {
                    TableId = id,
                    Page = cellPage ?? page.Value,
                    Row = cell.GetProperty("rowIndex").GetInt32(),
                    Column = cell.GetProperty("columnIndex").GetInt32(),
                    RowSpan = Int(cell, "rowSpan", 1),
                    ColumnSpan = Int(cell, "columnSpan", 1),
                    Kind = Str(cell, "kind"),
                    RawValue = raw,
                    TypedValue = typed,
                    ValueType = type,
                    Polygon = cellPolygon,
                    Confidence = Number(cell, "confidence"),
                });
            }
        }

        if (operation.TryGetProperty("result", out var result) && result.TryGetProperty("warnings", out var warnings))
        {
            graph.Warnings.AddRange(warnings.EnumerateArray().Select(w => w.ToString()));
        }

        graph.Segments.AddRange(MapSegments(operation).Where(s => InRange(s.StartPage)));
        graph.FingerprintInputs = Fingerprint(graph, firstTableOffset);
        var evidence = LayoutEvidenceDetector.Detect(graph);
        graph.LayoutEvidence = evidence.Count == 0 ? null : evidence;
        var header = LossRun.Contracts.HeaderFields.Detect(graph, glossary);
        graph.HeaderFields = header.Count == 0 ? null : header;
        return graph;
    }

    /// <summary>Segments of a classifier result (enableSegment). CU GA returns no per-segment confidence; it stays null.</summary>
    public static IReadOnlyList<GraphSegment> MapSegments(JsonElement operation) =>
        Array(Content(operation), "segments")
            .Select(s => new GraphSegment
            {
                Category = Str(s, "category") ?? "",
                StartPage = s.GetProperty("startPageNumber").GetInt32(),
                EndPage = s.GetProperty("endPageNumber").GetInt32(),
                Confidence = Number(s, "confidence"),
            })
            .ToList();

    /// <summary>Parses CU grounding <c>D(page,x1,y1,…,x4,y4)</c>; for a multi-region source the first region wins.</summary>
    public static (int? Page, List<double>? Polygon) ParseSource(string? source)
    {
        if (string.IsNullOrEmpty(source) || !source.StartsWith("D(", StringComparison.Ordinal))
        {
            return (null, null);
        }

        var end = source.IndexOf(')');
        var parts = source[2..(end < 0 ? source.Length : end)].Split(',');
        var page = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var polygon = parts.Skip(1).Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToList();
        return (page, polygon.Count > 0 ? polygon : null);
    }

    private static JsonElement Content(JsonElement operation)
    {
        var result = operation.TryGetProperty("result", out var r) ? r : operation;
        return result.TryGetProperty("contents", out var contents) && contents.GetArrayLength() > 0
            ? contents[0]
            : throw new InvalidDataException("Content Understanding result has no contents.");
    }

    private static (string? Typed, CellValueType Type) Type(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0)
        {
            return (null, CellValueType.Empty);
        }

        if (text.IndexOfAny(['/', '-']) > 0 && text.Any(char.IsDigit) && CanonicalComparer.NormalizeDate(text) is { } date)
        {
            return (date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), CellValueType.Date);
        }

        return text.Any(char.IsDigit) && CanonicalComparer.NormalizeCurrency(text) is { } number
            ? (number.ToString(CultureInfo.InvariantCulture), CellValueType.Number)
            : (text, CellValueType.Text);
    }

    /// <param name="tableStart">Markdown offset of the first table: header paragraphs are those printed before it.</param>
    private static FingerprintInputs Fingerprint(DocumentGraph graph, int tableStart)
    {
        var firstPage = graph.Pages.FirstOrDefault()?.Number;
        var firstTable = graph.Tables.FirstOrDefault();
        var header = graph.Paragraphs
            .Where(p => p.Page == firstPage && p.Role is not ("pageFooter" or "pageNumber") && (p.SpanOffset ?? 0) < tableStart)
            .Select(p => p.Text)
            .Take(10)
            .ToList();

        return new FingerprintInputs
        {
            TitleText = graph.Paragraphs.FirstOrDefault(p => p.Role == "title")?.Text ?? header.FirstOrDefault(),
            HeaderParagraphs = header,
            FirstTableHeaderCells = firstTable?.HeaderTexts ?? [],
            FirstPageMarkdown = graph.Pages.FirstOrDefault()?.Markdown is { } m ? m[..Math.Min(m.Length, 4000)] : null,
        };
    }

    private static string? Slice(string markdown, IEnumerable<JsonElement> spans)
    {
        var parts = spans.Select(s => (Offset: s.GetProperty("offset").GetInt32(), Length: s.GetProperty("length").GetInt32()))
            .Where(s => s.Offset >= 0 && s.Offset + s.Length <= markdown.Length)
            .Select(s => markdown.Substring(s.Offset, s.Length))
            .ToList();
        return parts.Count == 0 ? null : string.Concat(parts);
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static int Int(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : fallback;

    private static (int? Offset, int? Length) Span(JsonElement element) =>
        element.TryGetProperty("span", out var span)
            ? (span.GetProperty("offset").GetInt32(), span.GetProperty("length").GetInt32())
            : (null, null);
}

/// <summary>Reading-order offsets into the markdown for results without spans (inline analyze).</summary>
internal sealed class SpanFallback(string markdown)
{
    private int _cursor;

    public (int? Offset, int? Length) Paragraph(string content)
    {
        if (content.Length == 0)
        {
            return (_cursor, 0);
        }

        var index = markdown.IndexOf(content, _cursor, StringComparison.Ordinal);
        if (index < 0)
        {
            return (_cursor, content.Length); // keep reading order even when the markdown escapes the text
        }

        _cursor = index + content.Length;
        return (index, content.Length);
    }

    public int? FirstTable() => markdown.IndexOf("<table", StringComparison.Ordinal) is var i and >= 0 ? i : null;
}
