using System.Text.Json;
using LossRun.Contracts;

namespace LossRun.Evaluation;

/// <summary>
/// <c>lossrun eval truth --archive &lt;truth.csv&gt; --aligned &lt;aligned.csv&gt; --corpus &lt;corpus&gt; [--adjudication adjudication.xlsx] [--accept-defaults]</c>
/// (brief 14). This is the last step before an imported archive can be scored for accuracy. It writes
/// <c>&lt;corpus&gt;/truth/&lt;id&gt;.json</c> for every document with at least one kept claim.
/// <para>
/// <b>With the workbook</b> (the evaluator's file, brief 14 follow-up): matched claims that agree are kept as aligned. Every
/// row the workbook lists takes its decision:
/// </para>
/// <list type="bullet">
/// <item><c>archive-correct</c>: the archive amounts are truth;</item>
/// <item><c>extracted-correct</c>: what the document printed (the extracted values) is truth;</item>
/// <item><c>not-comparable</c>: the claim is kept and its amounts are not scored;</item>
/// <item><c>exclude</c>: the claim is dropped.</item>
/// </list>
/// <para>
/// <b>Without it</b>, an edited <c>aligned.csv</c> is read as before: <c>document</c> filled on archive-only rows, rows deleted.
/// </para>
/// <para>
/// Truth carries claim number, date of injury and policy number for every kept claim. It carries amounts only where they
/// are comparable (research A1).
/// </para>
/// </summary>
public static class TruthBuilder
{
    /// <summary>A claim kept in the truth of a document; amounts are scored only when <paramref name="Comparable"/>.</summary>
    public sealed record Kept(string Document, KeyedClaim Claim, bool Comparable);

    /// <param name="acceptDefaults">
    /// <c>--accept-defaults</c>: build the truth although archive-only rows were left at their default (dropped). Without it
    /// such rows refuse the run (exit 3), so a blind truth cannot silently become the extractor's view.
    /// </param>
    public static int Run(string archiveCsv, string alignedCsv, string corpusDir, string? workbook = null, bool acceptDefaults = false)
    {
        var archive = Alignment.ReadArchive(File.ReadAllText(archiveCsv));
        var lines = File.ReadAllLines(alignedCsv);
        var rows = workbook is null ? null : AdjudicationWorkbook.Read(workbook);
        if (rows is not null)
        {
            var decided = rows.GroupBy(r => (r.Status, r.Decision, r.Defaulted)).OrderBy(g => g.Key.Status, StringComparer.Ordinal)
                .Select(g => $"{g.Key.Status} → {g.Key.Decision}{(g.Key.Defaulted ? " (blank, default)" : "")}: {g.Count()}");
            Console.WriteLine($"adjudication: {rows.Count} row(s); {string.Join(" · ", decided)}");
        }

        var defaulted = DefaultedArchiveOnly(lines, rows);
        Console.WriteLine($"archive-only claims left at the default (dropped from the truth): {defaulted}");
        if (defaulted > 0 && !acceptDefaults)
        {
            Console.Error.WriteLine($"eval truth: refusing — {defaulted} archive claim(s) were not extracted and have no decision, so the truth would drop them " +
                "and score the extractor against its own view. Decide each row (a Document and archive-correct, or exclude) in " +
                $"{(workbook is null ? "aligned.csv" : "adjudication.xlsx")}, or pass --accept-defaults to accept the drops (blind-run.md, section 8).");
            return 3;
        }

        var kept = rows is null ? FromAlignedCsv(archive, lines, corpusDir) : FromWorkbook(archive, lines, rows, corpusDir);

        var written = Records(kept, corpusDir);
        Directory.CreateDirectory(Path.Combine(corpusDir, "truth"));
        foreach (var truth in written)
        {
            File.WriteAllText(Path.Combine(corpusDir, "truth", truth.Id + ".json"), JsonSerializer.Serialize(truth, CanonicalJson.Options));
        }

        var claims = written.Sum(t => t.Document.PolicyPeriods.Sum(p => p.Claims.Count));
        Console.WriteLine($"truth: {written.Count} document(s), {claims} claim(s); {written.Count(t => t.Printed.ClaimFields.Contains("medical.paid"))} with comparable amounts → {Path.Combine(corpusDir, "truth")}");
        return 0;
    }

    /// <summary>The adjudicated <c>aligned.csv</c> (edited by hand or by a script).</summary>
    public static List<Kept> FromAlignedCsv(IReadOnlyList<KeyedClaim> archive, IReadOnlyList<string> alignedLines, string corpusDir)
    {
        var byKey = ByKey(archive);
        var documents = Documents(corpusDir);
        var kept = new List<Kept>();
        foreach (var c in Rows(alignedLines))
        {
            if (c.Status is not ("matched" or "archive-only") || c.Document.Length == 0)
            {
                continue; // extracted-only rows and archive-only rows the adjudicator did not attribute to a document
            }

            kept.Add(new Kept(Known(documents, c.Document, $"aligned row {c.Key}"), Archived(byKey, c.Key), c.Comparable));
        }

        return kept;
    }

    /// <summary>The workbook's decisions over the alignment: matched rows not in the workbook are kept as aligned.</summary>
    public static List<Kept> FromWorkbook(IReadOnlyList<KeyedClaim> archive, IReadOnlyList<string> alignedLines, IReadOnlyList<AdjudicatedRow> rows, string corpusDir)
    {
        var byKey = ByKey(archive);
        var documents = Documents(corpusDir);
        var decided = rows.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var kept = Rows(alignedLines).Where(c => c.Status == "matched" && !decided.Contains(c.Key))
            .Select(c => new Kept(Known(documents, c.Document, $"aligned row {c.Key}"), Archived(byKey, c.Key), c.Comparable)).ToList();
        var errors = new List<string>();
        foreach (var r in rows.Where(r => r.Decision != AdjudicationWorkbook.Exclude))
        {
            if (r.DocumentId is not { } document)
            {
                errors.Add($"row {r.Row}: {r.Decision} needs the Document that prints the claim");
                continue;
            }

            if (!documents.Contains(document))
            {
                errors.Add($"row {r.Row}: document '{document}' is not in the corpus manifest");
                continue;
            }

            switch (r.Status, r.Decision)
            {
                case ("archive-only", AdjudicationWorkbook.ExtractedCorrect):
                    errors.Add($"row {r.Row}: nothing was extracted, so '{AdjudicationWorkbook.ExtractedCorrect}' does not apply ({AdjudicationWorkbook.ArchiveCorrect}, {AdjudicationWorkbook.NotComparable} or {AdjudicationWorkbook.Exclude})");
                    break;
                case ("extracted-only", AdjudicationWorkbook.ArchiveCorrect):
                    errors.Add($"row {r.Row}: the archive has no such claim, so '{AdjudicationWorkbook.ArchiveCorrect}' does not apply ({AdjudicationWorkbook.ExtractedCorrect}, {AdjudicationWorkbook.NotComparable} or {AdjudicationWorkbook.Exclude})");
                    break;
                case (_, AdjudicationWorkbook.ArchiveCorrect):
                    kept.Add(new Kept(document, Archived(byKey, r.Key), true));
                    break;
                case (_, AdjudicationWorkbook.ExtractedCorrect):
                    kept.Add(new Kept(document, r.Extracted!, true));
                    break;
                default: // not-comparable: the claim, without amounts
                    kept.Add(new Kept(document, r.Status == "extracted-only" ? r.Extracted! : Archived(byKey, r.Key), false));
                    break;
            }
        }

        return errors.Count == 0 ? kept : throw new FormatException($"adjudication.xlsx has {errors.Count} problem(s); fix them and run again:\n  {string.Join("\n  ", errors)}");
    }

    /// <summary>One truth record per document with kept claims.</summary>
    public static List<TruthRecord> Records(IReadOnlyList<Kept> kept, string corpusDir)
    {
        var manifest = CorpusManifest.Load(corpusDir).Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var truths = new List<TruthRecord>();
        foreach (var doc in kept.GroupBy(k => k.Document).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var entry = manifest[doc.Key];
            var rows = doc.ToList();
            var allComparable = rows.All(r => r.Comparable);
            var claimFields = new List<string> { "claimNumber", "dateOfInjury" };
            if (allComparable)
            {
                foreach (var (field, value) in new (string, Func<KeyedClaim, decimal?>)[]
                         {
                             ("indemnity.paid", k => k.IndemnityPaid), ("indemnity.reserve", k => k.IndemnityReserve), ("medical.paid", k => k.MedicalPaid),
                             ("medical.reserve", k => k.MedicalReserve), ("expense.paid", k => k.ExpensePaid), ("total.incurred", k => k.TotalIncurred),
                         })
                {
                    if (rows.All(r => value(r.Claim) is not null))
                    {
                        claimFields.Add(field);
                    }
                }
            }

            // A package (ZIP) can hold loss runs valued on different dates: the valuation date is then not a document-level truth.
            var dates = rows.Select(r => r.Claim.ValuationDate).Distinct().ToList();
            var valuation = allComparable && dates.Count == 1 ? dates[0] : null;
            var document = new LossRunDocument
            {
                DocumentId = doc.Key,
                CarrierName = rows[0].Claim.Carrier,
                ValuationDate = valuation,
                PolicyPeriods =
                [
                    .. rows.GroupBy(r => r.Claim.PolicyNumber).Select(p => new PolicyPeriod
                    {
                        PolicyNumber = p.Key,
                        Claims =
                        [
                            .. p.Select(r => new Claim
                            {
                                ClaimNumber = r.Claim.ClaimNumber, DateOfInjury = r.Claim.DateOfInjury,
                                Indemnity = r.Comparable ? new MoneyTriplet(r.Claim.IndemnityPaid, r.Claim.IndemnityReserve, null) : null,
                                Medical = r.Comparable ? new MoneyTriplet(r.Claim.MedicalPaid, r.Claim.MedicalReserve, null) : null,
                                Expense = r.Comparable ? new MoneyTriplet(r.Claim.ExpensePaid, null, null) : null,
                                Total = r.Comparable ? new MoneyTriplet(null, null, r.Claim.TotalIncurred) : null,
                            }),
                        ],
                    }),
                ],
            };
            truths.Add(new TruthRecord
            {
                Id = entry.Id, File = entry.File, Carrier = entry.Carrier, Layout = entry.Layout, InputType = entry.InputType, LogicalId = entry.LogicalId,
                Strata = [.. entry.Strata], SourceConsistent = true, Pages = entry.Pages, LossRunSegment = new TruthSegment("LossRun", 1, Math.Max(1, entry.Pages)),
                Printed = new PrintedFields(claimFields, valuation is null ? [] : ["valuationDate"], ["policyNumber"], [], []),
                Document = document,
            });
        }

        return truths;
    }

    private sealed record AlignedRow(string Status, string Key, string Document, bool Comparable);

    private static IEnumerable<AlignedRow> Rows(IReadOnlyList<string> lines) =>
        Csv.Parse(string.Join("\n", lines)).Skip(1).Select(r => r.Fields).Where(c => c.Length >= 4)
            .Select(c => new AlignedRow(c[0].Trim(), c[1], c[2].Trim(), bool.TryParse(c[3].Trim(), out var comparable) && comparable));

    /// <summary>
    /// Archive claims that a blank decision drops from the truth (brief 30 item 21): an archive-only row left at its default
    /// (workbook: no decision and no document, so <c>exclude</c>; aligned.csv: no document). Accepting them makes the truth
    /// the extractor's view of the set, so <c>eval truth</c> refuses them unless <c>--accept-defaults</c> is passed.
    /// </summary>
    public static int DefaultedArchiveOnly(IReadOnlyList<string> alignedLines, IReadOnlyList<AdjudicatedRow>? rows) =>
        rows is null ? Rows(alignedLines).Count(c => c.Status == "archive-only" && c.Document.Length == 0)
            : rows.Count(r => r.Status == "archive-only" && r.Defaulted && r.Decision == AdjudicationWorkbook.Exclude);

    private static Dictionary<string, KeyedClaim> ByKey(IReadOnlyList<KeyedClaim> archive) =>
        archive.GroupBy(Alignment.KeyOf).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private static KeyedClaim Archived(Dictionary<string, KeyedClaim> byKey, string key) =>
        byKey.TryGetValue(key, out var keyed) ? keyed : throw new FormatException($"aligned row key '{key}' is not in the archive (was the archive re-exported after align?)");

    private static HashSet<string> Documents(string corpusDir) => CorpusManifest.Load(corpusDir).Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

    private static string Known(HashSet<string> documents, string id, string where) =>
        documents.Contains(id) ? id : throw new FormatException($"{where} names document '{id}', which is not in the corpus manifest");
}
