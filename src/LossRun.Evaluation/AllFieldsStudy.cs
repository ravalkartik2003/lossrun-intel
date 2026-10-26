using System.Globalization;
using System.Text;

namespace LossRun.Evaluation;

/// <summary>
/// Brief 31 item 2: every field the scorer compares (every field the layout prints, per the truth's <c>printed</c> list), not only the key
/// fields the deck table's accuracy uses. The comparison that surfaced brief 30's 6,036 generator rows; it must be 0 wrong on a synthetic
/// corpus, and every wrong delivered value is listed with its document, row, expected and actual value.
/// </summary>
public static class AllFieldsStudy
{
    public const string FileName = "all-fields.md";

    /// <summary>Writes <c>studies/all-fields.md</c> and returns the number of delivered values that are wrong (caught or silent).</summary>
    public static int Write(Scorer.Scored scored, string outDir)
    {
        var delivered = scored.Fields;
        var wrong = delivered.Where(f => f.Error is "wrong" or "wrong-but-plausible").ToList();
        var md = new StringBuilder();
        md.AppendLine("# All-fields study");
        md.AppendLine();
        md.AppendLine("Every field the scorer compares on **delivered** documents (every field the layout prints, by the truth's `printed` list), not only the");
        md.AppendLine("key fields of the deck table (`docs/evaluation/scoring-protocol.md` §8). On a synthetic corpus the expected result is **0 wrong**.");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"**Delivered values compared: {delivered.Count:N0} · wrong: {wrong.Count:N0}** " +
            $"(caught by a hard rule {wrong.Count(f => f.Error == "wrong"):N0}, silent {wrong.Count(f => f.Error == "wrong-but-plausible"):N0}) · missing {delivered.Count(f => f.Error == "missing"):N0}");
        md.AppendLine();
        md.AppendLine("| Field | Compared | OK | Missing | Wrong (caught) | Wrong (silent) |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var g in delivered.GroupBy(f => Generic(f.Field)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| `{g.Key}` | {g.Count():N0} | {g.Count(f => f.Match):N0} | {g.Count(f => f.Error == "missing"):N0} | {g.Count(f => f.Error == "wrong"):N0} | {g.Count(f => f.Error == "wrong-but-plausible"):N0} |");
        }

        md.AppendLine();
        md.AppendLine("## Wrong delivered values");
        md.AppendLine();
        if (wrong.Count == 0)
        {
            md.AppendLine("None.");
        }
        else
        {
            md.AppendLine("| Document | Field | Row | Expected | Delivered | Kind |");
            md.AppendLine("|---|---|---|---|---|---|");
            foreach (var f in wrong.OrderBy(f => f.Doc, StringComparer.Ordinal).ThenBy(f => f.Field, StringComparer.Ordinal).Take(500))
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"| {f.Doc} | `{f.Field}` | {f.Row} | {Cell(f.Expected)} | {Cell(f.Actual)} | {f.Error} |");
            }

            if (wrong.Count > 500)
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"| … {wrong.Count - 500:N0} more in `field-errors.csv` | | | | | |");
            }
        }

        var dir = Path.Combine(outDir, "studies");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, FileName), md.ToString());
        return wrong.Count;
    }

    private static string Generic(string field) => System.Text.RegularExpressions.Regex.Replace(field, @"\[\d+\]", "[]");

    private static string Cell(string? value) => value is null ? "—" : value.Replace("|", @"\|", StringComparison.Ordinal);
}
