using LossRun.Contracts;

namespace LossRun.Ingestion.Spreadsheets;

/// <summary>A typed cell of a sheet grid.</summary>
public sealed record GridCell(string Raw, string? Typed, CellValueType Type);

/// <summary>A merged range (0-based, inclusive).</summary>
public sealed record MergedRange(int FromRow, int FromColumn, int ToRow, int ToColumn);

/// <param name="HeaderRow">0-based sheet row of the (lowest) header row.</param>
/// <param name="GroupRow">0-based row of the group header above it, for stacked headers.</param>
/// <param name="FirstColumn">First column of the table.</param>
/// <param name="LastColumn">Last column of the table.</param>
/// <param name="LastDataRow">Last data row (0-based, inclusive).</param>
/// <param name="HeaderTexts">Composed header text per column FirstColumn..LastColumn.</param>
/// <param name="Matches">Glossary match per column.</param>
/// <param name="Score">Mean glossary match confidence over non-empty header cells (0 when none resolve).</param>
public sealed record HeaderDetection(
    int HeaderRow,
    int? GroupRow,
    int FirstColumn,
    int LastColumn,
    int LastDataRow,
    IReadOnlyList<string> HeaderTexts,
    IReadOnlyList<HeaderMatch?> Matches,
    double Score);

/// <summary>
/// Finds the header row of a loss-run sheet without a model call (docs/architecture/carrier-format-memory.md, KQ6):
/// a row whose non-empty cells are mostly strings, followed by a row with at least one number or date, scored by the
/// fraction of cells the glossary resolves. A group row above it (merged cells such as "Indemnity" over
/// "Paid | Reserve | Incurred") is composed into the headers when that resolves more columns.
/// </summary>
public static class HeaderDetector
{
    private const double MinStringShare = 0.6;

    public static HeaderDetection? Detect(IReadOnlyList<GridCell?[]> grid, IReadOnlyList<MergedRange> merges, Glossary glossary)
    {
        HeaderDetection? best = null;
        for (var r = 0; r < grid.Count; r++)
        {
            var cells = NonEmpty(grid[r]).ToList();
            if (cells.Count < 2 || cells.Count(c => c.Cell.Type == CellValueType.Text) < MinStringShare * cells.Count)
            {
                continue;
            }

            var next = NextNonEmptyRow(grid, r + 1);
            if (next is null || !NonEmpty(grid[next.Value]).Any(c => c.Cell.Type is CellValueType.Number or CellValueType.Date))
            {
                continue;
            }

            var first = cells.Min(c => c.Column);
            var last = cells.Max(c => c.Column);
            var plain = Score(Enumerable.Range(first, last - first + 1).Select(c => Text(grid[r], c)).ToList(), glossary);
            var candidate = Build(grid, r, null, first, last, plain);

            if (r > 0 && Stacked(grid, merges, r, first, last) is { } composed)
            {
                var stacked = Score(composed, glossary);
                // Also when composing removes repeated headers ("Paid | Outstanding | Total" under each group): a repeated
                // header cannot be mapped to one column, whatever the glossary scores.
                if (stacked.Score > plain.Score || Repeats(composed) < Repeats(plain.Texts))
                {
                    candidate = Build(grid, r, r - 1, first, last, stacked);
                }
            }

            if (candidate.Score > 0 && (best is null || candidate.Score > best.Score))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static HeaderDetection Build(IReadOnlyList<GridCell?[]> grid, int headerRow, int? groupRow, int first, int last,
        (IReadOnlyList<string> Texts, IReadOnlyList<HeaderMatch?> Matches, double Score) scored)
    {
        var lastData = headerRow;
        for (var r = headerRow + 1; r < grid.Count && NonEmpty(grid[r]).Any(); r++)
        {
            lastData = r;
        }

        return new HeaderDetection(headerRow, groupRow, first, last, lastData, scored.Texts, scored.Matches, scored.Score);
    }

    /// <summary>Composes "Group Measure" texts when the row above holds group labels (merged across columns or directly above).</summary>
    private static List<string>? Stacked(IReadOnlyList<GridCell?[]> grid, IReadOnlyList<MergedRange> merges, int r, int first, int last)
    {
        var above = grid[r - 1];
        var groupCells = NonEmpty(above).Where(c => c.Column >= first && c.Column <= last).ToList();
        if (groupCells.Count == 0 || groupCells.Any(c => c.Cell.Type != CellValueType.Text))
        {
            return null;
        }

        var texts = new List<string>();
        for (var c = first; c <= last; c++)
        {
            var own = Text(grid[r], c);
            var group = Text(above, c);
            if (group.Length == 0)
            {
                var merge = merges.FirstOrDefault(m => m.FromRow <= r - 1 && m.ToRow >= r - 1 && m.FromColumn <= c && m.ToColumn >= c);
                group = merge is null ? "" : Text(grid[merge.FromRow], merge.FromColumn);
            }

            texts.Add(group.Length > 0 && own.Length > 0 && group != own ? $"{group} {own}" : own.Length > 0 ? own : group);
        }

        return texts;
    }

    private static (IReadOnlyList<string> Texts, IReadOnlyList<HeaderMatch?> Matches, double Score) Score(List<string> texts, Glossary glossary)
    {
        // Confidence-weighted, so a bare "Paid" (measure-only → claim.total.paid, discounted) scores below
        // "Indemnity Paid" composed from a stacked header.
        var matches = texts.Select(t => t.Length == 0 ? null : glossary.ResolveHeader(t)).ToList();
        var nonEmpty = texts.Count(t => t.Length > 0);
        return (texts, matches, nonEmpty == 0 ? 0 : matches.Sum(m => m?.Confidence ?? 0) / nonEmpty);
    }

    private static int Repeats(IReadOnlyList<string> texts) =>
        texts.Where(t => t.Length > 0).GroupBy(t => t.Trim(), StringComparer.OrdinalIgnoreCase).Sum(g => g.Count() - 1);

    private static int? NextNonEmptyRow(IReadOnlyList<GridCell?[]> grid, int from)
    {
        for (var r = from; r < grid.Count; r++)
        {
            if (NonEmpty(grid[r]).Any())
            {
                return r;
            }
        }

        return null;
    }

    private static IEnumerable<(int Column, GridCell Cell)> NonEmpty(GridCell?[] row) =>
        row.Select((cell, column) => (column, cell)).Where(x => x.cell is not null && x.cell.Type != CellValueType.Empty).Select(x => (x.column, x.cell!));

    private static string Text(GridCell?[] row, int column) => column < row.Length ? row[column]?.Raw.Trim() ?? "" : "";
}
