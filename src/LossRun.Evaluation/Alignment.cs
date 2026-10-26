using System.Globalization;
using System.Text;
using System.Text.Json;
using LossRun.Contracts;

namespace LossRun.Evaluation;

/// <summary>One claim row of the insurer's keyed archive (the "silver" truth of research A1).</summary>
public sealed record KeyedClaim(string Carrier, string PolicyNumber, string ClaimNumber, DateOnly? DateOfInjury, DateOnly? ValuationDate, decimal? IndemnityPaid,
    decimal? IndemnityReserve, decimal? MedicalPaid, decimal? MedicalReserve, decimal? ExpensePaid, decimal? TotalIncurred);

/// <param name="Status">matched | archive-only (keyed claim not extracted) | extracted-only (extracted claim not in the archive).</param>
/// <param name="AmountsComparable">Amounts are compared only when the archive's valuation date equals the loss run's.</param>
/// <param name="Extracted">The extracted claim in the archive's shape (matched and extracted-only rows): the workbook shows it next to the archive.</param>
public sealed record AlignedClaim(string Status, string Key, string? Document, bool AmountsComparable, IReadOnlyList<string> Disagreements, KeyedClaim? Archive,
    KeyedClaim? Extracted = null);

/// <summary>
/// <c>lossrun eval align</c> (brief 08 §4; research A1): aligns extracted claims to a keyed archive on (normalized claim
/// number, DOI, policy number, carrier). Amounts are only usable as truth when the archive row's valuation date equals
/// the document's; everything else is emitted as a disagreement list for adjudication. Ready for the client archive;
/// tested here on synthetic keyed data.
/// </summary>
public static class Alignment
{
    private static readonly string[] Header =
        ["carrier", "policy_number", "claim_number", "date_of_injury", "valuation_date", "indemnity_paid", "indemnity_reserve", "medical_paid", "medical_reserve", "expense_paid",
         "total_incurred"];

    /// <param name="documentFilter">Optional regex on result ids (e.g. one rendering per logical document).</param>
    /// <param name="corpusDir">The corpus: the adjudication workbook names documents by their file (else by id).</param>
    public static int Run(string archiveCsv, string resultsDir, string outDir, string? documentFilter = null, string? corpusDir = null)
    {
        var archive = ReadArchive(File.ReadAllText(archiveCsv));
        var filter = documentFilter is null ? null : new System.Text.RegularExpressions.Regex(documentFilter);
        var extracted = Directory.GetFiles(Path.Combine(resultsDir, "docs"), "*.json").Where(f => filter is null || filter.IsMatch(Path.GetFileNameWithoutExtension(f)))
            .Select(f => JsonSerializer.Deserialize<RunResult>(File.ReadAllText(f), CanonicalJson.Options)!)
            .SelectMany(r => r.Documents.Where(d => d.Document is not null).Select(d => (r.Id, d.Document!)))
            .ToList();
        var aligned = Align(archive, extracted);
        Directory.CreateDirectory(outDir);
        var csv = new StringBuilder("status,key,document,amounts_comparable,disagreements\n");
        foreach (var a in aligned)
        {
            csv.AppendLine(CultureInfo.InvariantCulture, $"{a.Status},{Csv.Cell(a.Key)},{Csv.Cell(a.Document)},{a.AmountsComparable},{Csv.Cell(string.Join("; ", a.Disagreements), always: true)}");
        }

        File.WriteAllText(Path.Combine(outDir, "aligned.csv"), csv.ToString());

        // The evaluator's file (brief 14 follow-up): raw values side by side, a decision per row, no normalized keys in view.
        var ids = Directory.GetFiles(Path.Combine(resultsDir, "docs"), "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList();
        var files = corpusDir is null ? ids.ToDictionary(i => i, i => i, StringComparer.Ordinal)
            : CorpusManifest.Load(corpusDir).Entries.Where(e => ids.Contains(e.Id)).ToDictionary(e => e.Id, e => e.File, StringComparer.Ordinal);
        AdjudicationWorkbook.Write(Path.Combine(outDir, "adjudication.xlsx"), aligned, files);
        Console.WriteLine($"adjudication.xlsx: {aligned.Count(AdjudicationWorkbook.NeedsDecision)} row(s) for the evaluator");
        Console.WriteLine($"aligned {aligned.Count(a => a.Status == "matched")} · archive-only {aligned.Count(a => a.Status == "archive-only")} · " +
            $"extracted-only {aligned.Count(a => a.Status == "extracted-only")} · disagreements {aligned.Count(a => a.Disagreements.Count > 0)}");
        return 0;
    }

    /// <summary>
    /// Reads the keyed archive (RFC 4180: a quoted field may hold commas, quotes and line breaks). A row whose field count
    /// differs from the header's is refused with its line number, never read shifted; a missing column is refused by name.
    /// </summary>
    public static List<KeyedClaim> ReadArchive(string csv)
    {
        var rows = Csv.Parse(csv);
        if (rows.Count == 0)
        {
            throw new FormatException("the archive CSV is empty (expected the header " + string.Join(",", Header) + ")");
        }

        var columns = rows[0].Fields.Select(c => c.Trim()).ToArray();
        int Col(string name) => System.Array.IndexOf(columns, name) is var i and >= 0 ? i
            : throw new FormatException($"the archive CSV has no '{name}' column (header: {string.Join(",", columns)})");
        var index = Header.ToDictionary(h => h, Col, StringComparer.Ordinal);
        var claims = new List<KeyedClaim>();
        foreach (var (line, fields) in rows.Skip(1))
        {
            if (fields.Length != columns.Length)
            {
                throw new FormatException($"archive CSV line {line}: {fields.Length} field(s) where the header has {columns.Length} (an unquoted comma in a value? quote the value)");
            }

            string C(string name) => fields[index[name]].Trim();
            claims.Add(new KeyedClaim(C("carrier"), C("policy_number"), C("claim_number"), CanonicalComparer.NormalizeDate(C("date_of_injury")),
                CanonicalComparer.NormalizeDate(C("valuation_date")), CanonicalComparer.NormalizeCurrency(C("indemnity_paid")),
                CanonicalComparer.NormalizeCurrency(C("indemnity_reserve")), CanonicalComparer.NormalizeCurrency(C("medical_paid")),
                CanonicalComparer.NormalizeCurrency(C("medical_reserve")), CanonicalComparer.NormalizeCurrency(C("expense_paid")),
                CanonicalComparer.NormalizeCurrency(C("total_incurred"))));
        }

        return claims;
    }

    public static string WriteArchive(IEnumerable<KeyedClaim> claims)
    {
        static string M(decimal? m) => m?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
        static string D(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        var sb = new StringBuilder(string.Join(",", Header) + "\n");
        foreach (var c in claims)
        {
            sb.AppendLine(string.Join(",", Csv.Cell(c.Carrier), Csv.Cell(c.PolicyNumber), Csv.Cell(c.ClaimNumber), D(c.DateOfInjury), D(c.ValuationDate), M(c.IndemnityPaid), M(c.IndemnityReserve),
                M(c.MedicalPaid), M(c.MedicalReserve), M(c.ExpensePaid), M(c.TotalIncurred)));
        }

        return sb.ToString();
    }

    /// <summary>An extracted claim in the archive's shape (what the document printed, as read).</summary>
    public static KeyedClaim AsKeyed(LossRunDocument doc, PolicyPeriod period, Claim claim) =>
        new(doc.CarrierName ?? "", period.PolicyNumber ?? "", claim.ClaimNumber ?? "", claim.DateOfInjury, doc.ValuationDate, claim.Indemnity?.Paid, claim.Indemnity?.Reserve,
            claim.Medical?.Paid, claim.Medical?.Reserve, claim.Expense?.Paid, claim.Total?.Incurred);

    /// <summary>The alignment key of an archive row (the <c>key</c> column of aligned.csv).</summary>
    public static string KeyOf(KeyedClaim k) => Key(k.Carrier, k.PolicyNumber, k.ClaimNumber, k.DateOfInjury);

    private static string Key(string? carrier, string? policy, string? claim, DateOnly? doi) =>
        // Brief 35 Answers 1: the carrier part compares like the replay carrier check (corporate words dropped), so an archive keyed
        // "X Insurance Company" aligns with documents printing "X Indemnity Co"; policy, claim number and DOI keep two carriers' claims apart.
        $"{CarrierNames.Normalize(carrier)}|{CanonicalComparer.NormalizeText(policy)}|{CanonicalComparer.NormalizeClaimNumber(claim)}|{doi:yyyy-MM-dd}";

    public static List<AlignedClaim> Align(IReadOnlyList<KeyedClaim> archive, IReadOnlyList<(string Document, LossRunDocument Extracted)> documents)
    {
        // The same claim can arrive in several documents (renderings, a later loss run): the first one is aligned.
        var extracted = documents.SelectMany(d => d.Extracted.PolicyPeriods.SelectMany(p => p.Claims.Select(c => (d.Document, Doc: d.Extracted, Period: p, Claim: c))))
            .GroupBy(x => Key(x.Doc.CarrierName, x.Period.PolicyNumber, x.Claim.ClaimNumber, x.Claim.DateOfInjury)).ToDictionary(g => g.Key, g => g.First());
        var result = new List<AlignedClaim>();
        var seen = new HashSet<string>();
        foreach (var keyed in archive)
        {
            var key = Key(keyed.Carrier, keyed.PolicyNumber, keyed.ClaimNumber, keyed.DateOfInjury);
            if (!extracted.TryGetValue(key, out var x))
            {
                result.Add(new AlignedClaim("archive-only", key, null, false, [], keyed));
                continue;
            }

            seen.Add(key);
            var comparable = keyed.ValuationDate is not null && keyed.ValuationDate == x.Doc.ValuationDate;
            var disagreements = new List<string>();
            if (comparable)
            {
                void Compare(string field, decimal? archived, decimal? read)
                {
                    if (!CanonicalComparer.Default.Compare(FieldKind.Currency, archived?.ToString(CultureInfo.InvariantCulture), read?.ToString(CultureInfo.InvariantCulture)).Match)
                    {
                        disagreements.Add($"{field}: archive {archived} vs extracted {read}");
                    }
                }

                Compare("indemnity.paid", keyed.IndemnityPaid, x.Claim.Indemnity?.Paid);
                Compare("indemnity.reserve", keyed.IndemnityReserve, x.Claim.Indemnity?.Reserve);
                Compare("medical.paid", keyed.MedicalPaid, x.Claim.Medical?.Paid);
                Compare("medical.reserve", keyed.MedicalReserve, x.Claim.Medical?.Reserve);
                Compare("expense.paid", keyed.ExpensePaid, x.Claim.Expense?.Paid);
                Compare("total.incurred", keyed.TotalIncurred, x.Claim.Total?.Incurred);
            }

            result.Add(new AlignedClaim("matched", key, x.Document, comparable, disagreements, keyed, AsKeyed(x.Doc, x.Period, x.Claim)));
        }

        result.AddRange(extracted.Where(e => !seen.Contains(e.Key))
            .Select(e => new AlignedClaim("extracted-only", e.Key, e.Value.Document, false, [], null, AsKeyed(e.Value.Doc, e.Value.Period, e.Value.Claim))));
        return result;
    }
}

/// <summary>A small RFC 4180 reader and cell writer for the archive CSV and aligned.csv (brief 30 item 21).</summary>
public static class Csv
{
    /// <summary>The records of a CSV text, each with the 1-based line it starts on. Blank lines are skipped; a quoted field may span lines.</summary>
    public static List<(int Line, string[] Fields)> Parse(string text)
    {
        var records = new List<(int, string[])>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var start = 1;
        var quoted = false;
        var content = false; // the record has a separator, a quote or a non-blank character (a blank line is not a record)
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch != '"')
                {
                    line += ch == '\n' ? 1 : 0;
                    field.Append(ch);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }

                continue;
            }

            switch (ch)
            {
                case '"' when field.ToString().Trim().Length == 0:
                    field.Clear();
                    quoted = true;
                    content = true;
                    break;
                case '"':
                    throw new FormatException($"CSV line {line}: a quote inside an unquoted field (quote the whole field and double the quote)");
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    content = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    if (content)
                    {
                        fields.Add(field.ToString());
                        records.Add((start, [.. fields]));
                    }

                    fields.Clear();
                    field.Clear();
                    content = false;
                    start = ++line;
                    break;
                default:
                    content |= !char.IsWhiteSpace(ch);
                    field.Append(ch);
                    break;
            }
        }

        if (quoted)
        {
            throw new FormatException($"CSV line {start}: a quoted field is not closed");
        }

        if (content)
        {
            fields.Add(field.ToString());
            records.Add((start, [.. fields]));
        }

        return records;
    }

    /// <summary>A cell, quoted when it holds a comma, a quote or a line break (or always).</summary>
    public static string Cell(string? value, bool always = false) =>
        value is null ? always ? "\"\"" : ""
        : always || value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
}
