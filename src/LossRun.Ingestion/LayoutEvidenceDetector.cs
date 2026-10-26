using System.Text.RegularExpressions;
using LossRun.Contracts;

namespace LossRun.Ingestion;

/// <summary>
/// Brief 19 Answers 3: what the document graph shows about claim records that the spec language has a construct for
/// (ADR 0018), found deterministically at ingestion. The learn prompt states the construct as an instruction when the
/// evidence is present; replay never proposes one.
/// <list type="bullet">
/// <item><c>records-rows</c>: in a claim table, rows with a claim number are each followed by a row with amounts and no claim
/// number (a real sample, brief 17: a key line, then an amounts line in the same columns).</item>
/// <item><c>records-lines</c>: a small claim table without a claim-number column, followed below it by a printed line that
/// carries a claim-number-like token (two real samples, brief 17: the claim number on a line of its own).</item>
/// <item><c>stacked</c>: a header printing two known fields in one cell (two real samples, brief 17).</item>
/// <item><c>claims-with-period-totals</c> (brief 20 Answers 2): claim rows (a table with a claim-number column) and period
/// totals, either a separate period table (period label, totals and counts, no claim number) or per-period total rows inside
/// the claim table ("Policy Total …"). Claim rows are the records; the period rows are totals to tie against.</item>
/// <item><c>measures-as-rows</c> (brief 22 Answers 1): a table whose rows carry a measure label in a cell ("Inc:", "Pd:", "O/S:")
/// for at least two measures, each on at least two rows: a claim prints one row per measure under component columns
/// (a carrier-published sample, both generations of its standard claim report).</item>
/// </list>
/// </summary>
public static partial class LayoutEvidenceDetector
{
    public static List<LayoutEvidence> Detect(DocumentGraph graph)
    {
        var evidence = new List<LayoutEvidence>();
        foreach (var table in graph.Tables)
        {
            if (TwoLineRows(graph, table) is { } rows)
            {
                evidence.Add(rows);
            }

            if (MeasureRows(graph, table) is { } measures)
            {
                evidence.Add(measures);
            }

            if (ClaimNumberOnItsOwnLine(graph, table) is { } lines)
            {
                evidence.Add(lines);
            }

            if (table.StackedHeaders is { Count: > 0 } stacked)
            {
                evidence.Add(new LayoutEvidence("stacked", table.Id, table.Page,
                    $"header(s) {string.Join(", ", stacked.Keys.Select(k => $"\"{table.HeaderTexts[k].ReplaceLineEndings(" ")}\""))} print two fields in one cell"));
            }
        }

        if (ClaimsWithPeriodTotals(graph) is { } totals)
        {
            evidence.Add(totals);
        }

        return evidence;
    }

    private static LayoutEvidence? ClaimsWithPeriodTotals(DocumentGraph graph)
    {
        var claimTables = graph.Tables.Where(t => t.HeaderFields.Contains("claim.claimNumber") && t.RowCount > t.HeaderRows.Count).ToList();
        if (claimTables.Count == 0)
        {
            return null;
        }

        static bool Total(string? field) => field is not null && (TotalField().IsMatch(field) || field.EndsWith("Count", StringComparison.Ordinal));
        var periodTable = graph.Tables.FirstOrDefault(t => !t.HeaderFields.Contains("claim.claimNumber") && t.HeaderTexts.Any(h => PeriodHeader().IsMatch(h ?? ""))
            && t.HeaderFields.Count(Total) + t.HeaderTexts.Count(h => CountHeader().IsMatch(h ?? "")) >= 2 && t.RowCount > t.HeaderRows.Count);
        if (periodTable is not null)
        {
            return new LayoutEvidence("claims-with-period-totals", periodTable.Id, periodTable.Page,
                $"claim rows in {claimTables.Count} table(s) with a claim-number column, and a period table (\"{string.Join("\", \"", periodTable.HeaderTexts.Where(h => h is { Length: > 0 }).Take(4).Select(h => h.ReplaceLineEndings(" ")))}\") printing totals per period");
        }

        foreach (var table in claimTables)
        {
            var labels = graph.Cells.Where(c => c.TableId == table.Id && !table.HeaderRows.Contains(c.Row)).GroupBy(c => c.Row)
                .Select(g => g.OrderBy(c => c.Column).Select(c => c.RawValue.Trim()).FirstOrDefault(v => v.Length > 0) ?? "").Count(v => PeriodTotalRow().IsMatch(v));
            if (labels > 0)
            {
                return new LayoutEvidence("claims-with-period-totals", table.Id, table.Page, $"claim rows with a claim-number column and {labels} per-period total row(s) in the same table");
            }
        }

        return null;
    }

    [GeneratedRegex(@"(?i)\b(?:policy (?:term|year|period|eff\w*)|term|year|period|effective)\b")]
    private static partial Regex PeriodHeader();

    [GeneratedRegex(@"(?i)(?:#|no\.?|number) of claims|claim count|\bclaims\b|open claims|closed claims")]
    private static partial Regex CountHeader();

    [GeneratedRegex(@"\.(?:paid|reserve|incurred|recovered)$|recovery")]
    private static partial Regex TotalField();

    [GeneratedRegex(@"(?i)^(?:(?:policy|period|year|term|sub)\s*total\b|total\s+(?:for\s+)?(?:policy|period|year|term)\b)")]
    private static partial Regex PeriodTotalRow();

    private static LayoutEvidence? TwoLineRows(DocumentGraph graph, GraphTable table)
    {
        var claimColumn = table.HeaderFields.IndexOf("claim.claimNumber");
        if (claimColumn < 0)
        {
            return null;
        }

        var rows = graph.Cells.Where(c => c.TableId == table.Id && !table.HeaderRows.Contains(c.Row)).GroupBy(c => c.Row).OrderBy(g => g.Key)
            .Select(g => (Key: g.FirstOrDefault(c => c.Column == claimColumn)?.RawValue.Trim() ?? "", Amounts: g.Count(c => Amount().IsMatch(c.RawValue.Trim()))))
            .Where(r => r.Key.Length > 0 || r.Amounts > 0)
            .ToList();
        int keys = 0, followed = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Key.Length == 0 || TotalLabel().IsMatch(rows[i].Key))
            {
                continue;
            }

            keys++;
            if (i + 1 < rows.Count && rows[i + 1].Key.Length == 0 && rows[i + 1].Amounts >= 2)
            {
                followed++;
            }
        }

        return keys >= 2 && followed >= 0.8 * keys
            ? new LayoutEvidence("records-rows", table.Id, table.Page, $"{followed} of {keys} claim-number rows are each followed by an amounts row without a claim number")
            : null;
    }

    private static LayoutEvidence? MeasureRows(DocumentGraph graph, GraphTable table)
    {
        var labels = new MeasureLabels().Patterns().ToList();
        var counts = graph.Cells.Where(c => c.TableId == table.Id && !table.HeaderRows.Contains(c.Row)).GroupBy(c => c.Row)
            .Select(g => g.Select(c => c.RawValue.Trim()).Select(t => labels.FirstOrDefault(l => Regex.IsMatch(t, l.Pattern)).Measure).FirstOrDefault(m => m is not null))
            .OfType<string>().GroupBy(m => m, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return counts.Count(c => c.Value >= 2) >= 2
            ? new LayoutEvidence("measures-as-rows", table.Id, table.Page,
                $"rows labelled with a measure ({string.Join(", ", counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key} x{c.Value}"))}): each claim prints one row per measure under the component columns")
            : null;
    }

    private static LayoutEvidence? ClaimNumberOnItsOwnLine(DocumentGraph graph, GraphTable table)
    {
        var resolved = table.HeaderFields.OfType<string>().ToList();
        if (resolved.Contains("claim.claimNumber") || resolved.Count < 3 || !resolved.Any(f => f.EndsWith(".paid", StringComparison.Ordinal) || f.EndsWith(".incurred", StringComparison.Ordinal))
            || table.RowCount - table.HeaderRows.Count is < 1 or > 3 || table.Polygon is not { Count: >= 8 })
        {
            return null;
        }

        var bottom = table.Polygon.Where((_, i) => i % 2 == 1).Max();
        // Up to the next claim table (a key/value table of the same claim may sit between, as on a real sample).
        var next = graph.Tables.Where(t => t.Page == table.Page && t.Polygon is { Count: >= 8 } && t.Polygon[1] > bottom && t.HeaderFields.OfType<string>().Count() >= 3)
            .Select(t => t.Polygon![1]).DefaultIfEmpty(bottom + 1.5).Min();
        var line = graph.Paragraphs.FirstOrDefault(p => p.Page == table.Page && p.Polygon is { Count: >= 8 } && p.Polygon[1] >= bottom && p.Polygon[1] < Math.Min(next, bottom + 1.5)
            && ClaimNumberLike().IsMatch(p.Text));
        return line is null
            ? null
            : new LayoutEvidence("records-lines", table.Id, table.Page, "a claim table without a claim-number column is followed by a printed line carrying a claim number");
    }

    [GeneratedRegex(@"^\(?-?\$?\s*\d[\d,]*\.\d{2}\)?$")]
    private static partial Regex Amount();

    [GeneratedRegex(@"(?i)\btotal\b")]
    private static partial Regex TotalLabel();

    /// <summary>A claim-number-like token: letters and digits ("SPC-23-00001", "WCA12345"), not a date or an amount.</summary>
    [GeneratedRegex(@"\b(?:[A-Z]{2,4}-?\d{2}-?\d{4,6}|[A-Za-z]{2,4}\d{5,})\b")]
    private static partial Regex ClaimNumberLike();
}
