using System.Text.RegularExpressions;
using LossRun.Contracts;
using LossRun.Ingestion;
using LossRun.Ingestion.Spreadsheets;
using UglyToad.PdfPig;

namespace LossRun.Evaluation;

/// <summary>A policy a file prints: its number and, when printed with it, its period.</summary>
public sealed record PolicyRef(string Number, DateOnly? Start, DateOnly? End);

/// <summary>
/// The policies a file prints, read deterministically before any layout or model call (brief 14 follow-up: import's
/// <c>truth-request.csv</c>, so the data team exports only the claims of the imported pool's policies). Native PDFs are read
/// from their text layer: a "Policy …" token with a digit, and the first two dates on its line as the period. Workbooks and
/// CSVs are read from their tables: the policy column (glossary <c>period.policyNumber</c> or a "Policy (No/Number)"
/// header) and the effective / expiration columns. Scans, images and protected files without a password are not readable
/// here (Readable = false): they need OCR, so the request covers their carrier as a whole.
/// </summary>
public static partial class PolicyReader
{
    public static (IReadOnlyList<PolicyRef> Policies, bool Readable) Read(byte[] bytes, string name, IReadOnlyList<string> passwords)
    {
        try
        {
            var kind = FileSniffer.Sniff(bytes, name);
            return kind switch
            {
                FileKind.Pdf => Pdf(bytes, passwords),
                FileKind.Xlsx or FileKind.Xls or FileKind.Xlsb or FileKind.Csv => (Sheet(bytes, name, kind, passwords), true),
                _ => ([], false),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException or FormatException
                                  or UglyToad.PdfPig.Core.PdfDocumentFormatException or ExcelDataReader.Exceptions.ExcelReaderException)
        {
            return ([], false);
        }
    }

    private static (IReadOnlyList<PolicyRef>, bool) Pdf(byte[] bytes, IReadOnlyList<string> passwords)
    {
        var unlocked = PdfUnlocker.Unlock(bytes, passwords);
        if (!unlocked.Ok || !FirstPageCheck.HasTextLayer(unlocked.Bytes!))
        {
            return ([], false);
        }

        var found = new List<PolicyRef>();
        using var document = PdfDocument.Open(unlocked.Bytes!);
        foreach (var page in document.GetPages())
        {
            // Lines: words grouped by baseline, left to right.
            foreach (var line in page.GetWords().GroupBy(w => Math.Round(w.BoundingBox.Bottom / 2.0)).OrderByDescending(g => g.Key)
                         .Select(g => string.Join(' ', g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text))))
            {
                if (PolicyOnLine().Match(line) is not { Success: true } m)
                {
                    continue;
                }

                var dates = DateOnLine().Matches(line).Select(d => CanonicalComparer.NormalizeDate(d.Value)).OfType<DateOnly>().ToList();
                found.Add(new PolicyRef(m.Groups["number"].Value.TrimEnd('.', ','), dates.ElementAtOrDefault(0) is { } s && dates.Count >= 2 ? s : null,
                    dates.Count >= 2 ? dates[1] : null));
            }
        }

        return (Merge(found), true);
    }

    private static List<PolicyRef> Sheet(byte[] bytes, string name, FileKind kind, IReadOnlyList<string> passwords)
    {
        var graph = SpreadsheetReader.Read(bytes, new SourceFile { FileName = name, Kind = kind }, "import", CanonicalData.Glossary, passwords);
        var found = new List<PolicyRef>();
        foreach (var table in graph.Tables)
        {
            int Column(string field, Regex header) => Enumerable.Range(0, table.HeaderTexts.Count)
                .FirstOrDefault(i => table.HeaderFields.ElementAtOrDefault(i) == field || header.IsMatch(table.HeaderTexts[i].Trim()), -1);
            var policy = Column("period.policyNumber", PolicyHeader());
            if (policy < 0 || table.Range is null || ParseA1(table.Range.Split(':')[0]) is not { } start)
            {
                continue;
            }

            var (effective, expiration) = (Column("period.effectiveDate", EffectiveHeader()), Column("period.expirationDate", ExpirationHeader()));
            var firstData = start.Row + (table.HeaderRows.Count == 0 ? 0 : table.HeaderRows.Max() + 1);
            foreach (var row in graph.Cells.Where(c => c.TableId == table.Id && c.Row >= firstData).GroupBy(c => c.Row))
            {
                string? Text(int index) => index < 0 ? null : row.FirstOrDefault(c => c.Column == start.Column + index)?.RawValue?.Trim();
                if (Text(policy) is { Length: > 2 } number && number.Any(char.IsAsciiDigit))
                {
                    found.Add(new PolicyRef(number, CanonicalComparer.NormalizeDate(Text(effective)), CanonicalComparer.NormalizeDate(Text(expiration))));
                }
            }
        }

        return Merge(found);
    }

    /// <summary>One row per policy number; the period from whichever mention printed one.</summary>
    private static List<PolicyRef> Merge(IEnumerable<PolicyRef> found) =>
        [.. found.GroupBy(p => p.Number, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PolicyRef(g.First().Number, g.Select(p => p.Start).FirstOrDefault(s => s is not null), g.Select(p => p.End).FirstOrDefault(e => e is not null)))];

    private static (int Row, int Column)? ParseA1(string a1)
    {
        var m = A1().Match(a1.Trim());
        return m.Success ? (int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) - 1,
            m.Groups[1].Value.ToUpperInvariant().Aggregate(0, (a, c) => a * 26 + c - 'A' + 1) - 1) : null;
    }

    [GeneratedRegex(@"(?i)\bpolicy(?:\s*(?:no\.?|number|#))?\s*[:#]?\s+(?<number>(?=[A-Z0-9\-/.]*\d)[A-Z0-9][A-Z0-9\-/.]{3,})")]
    private static partial Regex PolicyOnLine();

    [GeneratedRegex(@"\b\d{1,2}/\d{1,2}/\d{2,4}\b|\b\d{4}-\d{2}-\d{2}\b")]
    private static partial Regex DateOnLine();

    [GeneratedRegex(@"(?i)^pol(icy)?\.?\s*(no\.?|number|#|num)?$")]
    private static partial Regex PolicyHeader();

    [GeneratedRegex(@"(?i)^(pol(icy)?\.?\s*)?eff(ective|\.)?(\s*date)?$")]
    private static partial Regex EffectiveHeader();

    [GeneratedRegex(@"(?i)^(pol(icy)?\.?\s*)?exp(iration|iry|\.)?(\s*date)?$")]
    private static partial Regex ExpirationHeader();

    [GeneratedRegex(@"^([A-Za-z]+)(\d+)$")]
    private static partial Regex A1();
}
