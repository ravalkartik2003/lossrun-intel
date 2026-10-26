using LossRun.Contracts;

namespace LossRun.Formats.Replay;

/// <summary>One printed value with its lineage unit.</summary>
internal sealed record SourceCell(string Text, int Page, List<double>? Polygon, string? Sheet, string? A1, double Confidence)
{
    /// <summary>Position in a stacked cell (ADR 0018); null for the whole cell.</summary>
    public int? Part { get; init; }
}

/// <summary>A row of a claim table: printed values keyed by the normalized header of their column.</summary>
internal sealed class SourceRow
{
    public Dictionary<string, SourceCell> Cells { get; } = new(StringComparer.Ordinal);

    /// <summary>Non-empty cell texts in column order joined by spaces (row-kind and count regexes run on this).</summary>
    public string Text { get; init; } = "";

    /// <summary>First non-empty cell (lineage for row-level values such as counts).</summary>
    public SourceCell? Label { get; init; }

    public string? Origin { get; init; }

    /// <summary>Non-empty cells by table column index (total-row realignment reads a neighbouring column).</summary>
    public List<(int Index, SourceCell Cell)> Columns { get; } = [];

    /// <summary>The table's column keys (source per column index); null for line-fallback rows.</summary>
    public string?[]? Keys { get; init; }

    /// <summary>Page and top of the row (inches), for ordering rows against loose lines (ADR 0018 records).</summary>
    public int Page { get; init; }
    public double Top { get; init; }

    /// <summary>ADR 0018 lines mode: the loose lines printed after this claim row, in reading order.</summary>
    public List<(string Text, SourceCell Source)> Lines { get; } = [];

    /// <summary>
    /// Brief 20: each loose line's parts (split at gaps wider than two word heights), aligned with <see cref="Lines"/>. A line
    /// field that does not match the whole line is tried on each part: "CLM12345      1234567" is printed as two values on one
    /// line, which CU returns as two paragraphs and the model reads as two lines.
    /// </summary>
    public List<IReadOnlyList<SourceCell>> LineParts { get; } = [];
}

/// <summary>Word geometry helpers over CU polygons (x1,y1,…,x4,y4).</summary>
internal static class Geometry
{
    public static (double MinX, double MinY, double MaxX, double MaxY) Box(IReadOnlyList<double> polygon)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (var i = 0; i + 1 < polygon.Count; i += 2)
        {
            minX = Math.Min(minX, polygon[i]);
            maxX = Math.Max(maxX, polygon[i]);
            minY = Math.Min(minY, polygon[i + 1]);
            maxY = Math.Max(maxY, polygon[i + 1]);
        }

        return (minX, minY, maxX, maxY);
    }

    public static List<double> Rectangle(double minX, double minY, double maxX, double maxY) =>
        [Round(minX), Round(minY), Round(maxX), Round(minY), Round(maxX), Round(maxY), Round(minX), Round(maxY)];

    public static double Round(double value) => Math.Round(value, 4);

    /// <summary>Minimum OCR confidence of the words whose centre lies in the polygon's box (1.0 when no word is found).</summary>
    public static double MinWordConfidence(IReadOnlyList<GraphWord> pageWords, IReadOnlyList<double>? polygon)
    {
        if (polygon is null || polygon.Count < 8)
        {
            return 1.0;
        }

        var (minX, minY, maxX, maxY) = Box(polygon);
        const double pad = 0.02;
        var confidence = 1.0;
        foreach (var word in pageWords)
        {
            if (word.Polygon is not { Count: >= 8 })
            {
                continue;
            }

            var (wx1, wy1, wx2, wy2) = Box(word.Polygon);
            var cx = (wx1 + wx2) / 2;
            var cy = (wy1 + wy2) / 2;
            if (cx >= minX - pad && cx <= maxX + pad && cy >= minY - pad && cy <= maxY + pad)
            {
                confidence = Math.Min(confidence, word.Confidence ?? 1.0); // brief 31: unknown is no OCR evidence, not 0
            }
        }

        return confidence;
    }
}
