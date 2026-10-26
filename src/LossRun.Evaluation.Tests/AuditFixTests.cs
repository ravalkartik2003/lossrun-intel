using System.Security.Cryptography;
using System.Text.Json;
using LossRun.Contracts;

namespace LossRun.Evaluation.Tests;

/// <summary>
/// Brief 30 items 21, 26 (CLI) and 49: the seal's list hash is recomputed on verify, a blind truth cannot silently drop the
/// claims the extractor missed, the tuning loop never aligns blind claims, the archive CSV is read with a real parser, blind
/// results stay under a <c>blind</c> folder, passwords never ride on the command line, and the batch cap is derived.
/// </summary>
public class AuditFixTests
{
    private static string Fixture => EvalRunner.FindUp("src/TestShared/fixtures/import") ?? throw new DirectoryNotFoundException("import fixture");

    private static string Carriers => Path.Combine(Path.GetDirectoryName(Fixture)!, "import-carriers.csv");

    private static string Temp(string what) => Path.Combine(Path.GetTempPath(), $"lossrun-{what}-" + Guid.NewGuid().ToString("n")[..8]);

    private static readonly DateOnly Valued = new(2025, 1, 15);

    private static KeyedClaim Keyed(string carrier, string policy, string number, int month, decimal medical = 10m) =>
        new(carrier, policy, number, new DateOnly(2024, month, 1), Valued, 0m, 0m, medical, 0m, 0m, medical);

    private static void WriteRun(string results, string id, string carrier, string policy, params (string Number, int Month)[] claims)
    {
        Directory.CreateDirectory(Path.Combine(results, "docs"));
        var document = new LossRunDocument
        {
            CarrierName = carrier, ValuationDate = Valued,
            PolicyPeriods =
            [
                new PolicyPeriod
                {
                    PolicyNumber = policy,
                    Claims = [.. claims.Select(c => new Claim
                    {
                        ClaimNumber = c.Number, DateOfInjury = new DateOnly(2024, c.Month, 1), Indemnity = new MoneyTriplet(0m, 0m, 0m), Medical = new MoneyTriplet(10m, 0m, 10m),
                        Expense = new MoneyTriplet(0m, null, null), Total = new MoneyTriplet(null, null, 10m),
                    })],
                },
            ],
        };
        File.WriteAllText(Path.Combine(results, "docs", id + ".json"),
            JsonSerializer.Serialize(new RunResult { Id = id, Documents = [new ExtractedDocument { FinalTier = "ReplayAccepted", Document = document }] }, CanonicalJson.Options));
    }

    // ------------------------------------------------------------------ item 21: seal verification

    private static string SealCorpus()
    {
        var dir = Temp("seal21");
        Directory.CreateDirectory(Path.Combine(dir, "docs"));
        var manifest = new CorpusManifest { Seed = 8 };
        for (var i = 0; i < 12; i++)
        {
            var id = $"c1-{i:000}-native-pdf";
            var bytes = System.Text.Encoding.UTF8.GetBytes(id);
            File.WriteAllBytes(Path.Combine(dir, "docs", id + ".pdf"), bytes);
            manifest.Entries.Add(new ManifestEntry(id, id + ".pdf", "c1", "layout", InputTypes.NativePdf, id, [], Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, 1));
        }

        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, CanonicalJson.Options));
        return dir;
    }

    [Fact]
    public void Verify_recomputes_the_blind_list_hash_so_a_swapped_file_with_its_new_hash_written_in_fails()
    {
        var corpus = SealCorpus();
        var seal = Sealing.Split(corpus, tuningSize: 4, seed: 8);
        Assert.Empty(Sealing.Verify(corpus, seal, seal.BlindManifestSha256));

        // Swap a blind file and write its new hash into the list: every file matches its entry, the list hash does not.
        var swapped = System.Text.Encoding.UTF8.GetBytes("a different document");
        File.WriteAllBytes(Path.Combine(corpus, "docs", seal.Blind[0].File), swapped);
        var edited = seal with { Blind = [seal.Blind[0] with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(swapped)) }, .. seal.Blind.Skip(1)] };

        var problems = Sealing.Verify(corpus, edited);
        Assert.Contains(problems, p => p.Contains("not the recorded blindManifestSha256", StringComparison.Ordinal));

        // Rewriting the recorded field too is caught only by the published value.
        var rewritten = edited with { BlindManifestSha256 = Sealing.BlindListHash(edited.Blind) };
        Assert.Empty(Sealing.Verify(corpus, rewritten));
        Assert.Contains(Sealing.Verify(corpus, rewritten, seal.BlindManifestSha256), p => p.Contains("not the published", StringComparison.Ordinal));

        var sealPath = Path.Combine(corpus, "seal-manifest.json");
        File.WriteAllText(sealPath, JsonSerializer.Serialize(rewritten, CanonicalJson.Options));
        Assert.Equal(1, Sealing.VerifyCommand(corpus, sealPath, seal.BlindManifestSha256));
        Assert.Equal(0, Sealing.VerifyCommand(corpus, sealPath));
    }

    // ------------------------------------------------------------------ item 21: defaulted archive-only rows

    [Fact]
    public void Truth_refuses_archive_only_rows_left_at_their_default_unless_accept_defaults()
    {
        var corpus = Temp("truth21");
        Import.Run(Fixture, corpus, Carriers, copy: false, passwords: []);
        var entry = CorpusManifest.Load(corpus).Entries.First(e => e.File.EndsWith("2024-renewal-loss-run.pdf", StringComparison.Ordinal));
        const string carrier = "Synthetic Mutual Insurance Co.";
        var results = Path.Combine(corpus, "results");
        WriteRun(results, entry.Id, carrier, "WC-100", ("WC-24-0001", 2));
        var archive = Path.Combine(corpus, "truth.csv");
        File.WriteAllText(archive, Alignment.WriteArchive([Keyed(carrier, "WC-100", "WC-24-0001", 2), Keyed(carrier, "WC-100", "WC-24-0003", 4)])); // 0003 missed
        var align = Path.Combine(corpus, "align");
        Assert.Equal(0, Alignment.Run(archive, results, align, corpusDir: corpus));
        var aligned = Path.Combine(align, "aligned.csv");
        var workbook = Path.Combine(align, "adjudication.xlsx");

        // aligned.csv: the missed claim has no document → dropped by default → refused.
        Assert.Equal(1, TruthBuilder.DefaultedArchiveOnly(File.ReadAllLines(aligned), null));
        Assert.Equal(3, TruthBuilder.Run(archive, aligned, corpus));
        Assert.False(Directory.Exists(Path.Combine(corpus, "truth")));

        // The workbook: blank decision, no document → exclude by default → refused; accepted only on --accept-defaults.
        Assert.Equal(1, TruthBuilder.DefaultedArchiveOnly(File.ReadAllLines(aligned), AdjudicationWorkbook.Read(workbook)));
        Assert.Equal(3, TruthBuilder.Run(archive, aligned, corpus, workbook));
        Assert.Equal(0, TruthBuilder.Run(archive, aligned, corpus, workbook, acceptDefaults: true));
        Assert.Single(CorpusManifest.Truth(corpus, entry.Id).Document.PolicyPeriods.Single().Claims);

        // A decision (here: the Document is chosen) clears the refusal.
        using (var wb = new ClosedXML.Excel.XLWorkbook(workbook))
        {
            wb.Worksheet("Adjudication").RowsUsed().Skip(1).Single(r => r.Cell(6).GetString() == "WC-24-0003").Cell(3).Value = entry.File;
            wb.Save();
        }

        Assert.Equal(0, TruthBuilder.DefaultedArchiveOnly(File.ReadAllLines(aligned), AdjudicationWorkbook.Read(workbook)));
        Assert.Equal(0, TruthBuilder.Run(archive, aligned, corpus, workbook));
        Assert.Equal(2, CorpusManifest.Truth(corpus, entry.Id).Document.PolicyPeriods.Single().Claims.Count);
    }

    // ------------------------------------------------------------------ item 21: tune never aligns blind claims

    [Fact]
    public void Tune_cuts_the_archive_to_the_tuning_documents_policies_so_blind_claims_never_appear_as_archive_only()
    {
        var corpus = Temp("tune21");
        Import.Run(Fixture, corpus, Carriers, copy: false, passwords: []);
        var entries = CorpusManifest.Load(corpus).Entries;
        var csv = entries.Single(e => e.File.EndsWith("claims.csv", StringComparison.Ordinal));          // prints PAC-WC-2022x..2024x in its table
        var fax = entries.Single(e => e.File.EndsWith("faxed-loss-run.pdf", StringComparison.Ordinal)); // no text layer: its policy comes from the run
        var results = Temp("tune21-results");
        WriteRun(results, fax.Id, "Synthetic Mutual Insurance Co.", "SYN-WC-2201", ("SYN-22-0001", 3));

        var policies = Tune.TuningPolicies(corpus, [csv.Id, fax.Id], results);
        Assert.Contains(Tune.PolicyKey("PAC-WC-20231"), policies);
        Assert.Contains(Tune.PolicyKey("SYN-WC-2201"), policies);
        Assert.DoesNotContain(Tune.PolicyKey("FCG-WC-2101"), policies); // printed only by a document outside the tuning list

        KeyedClaim[] archive =
        [
            Keyed("Placeholder Assurance Co.", "PAC-WC-20231", "PAC-23-0001", 5),
            Keyed("Synthetic Mutual Insurance Co.", "SYN-WC-2201", "SYN-22-0001", 3),
            Keyed("Fictional Casualty Group", "FCG-WC-2101", "FCG-21-0001", 6), // a blind claim
        ];
        var kept = Tune.FilterArchive(archive, policies);
        Assert.Equal(["PAC-WC-20231", "SYN-WC-2201"], kept.Select(k => k.PolicyNumber));

        var faxRun = JsonSerializer.Deserialize<RunResult>(File.ReadAllText(Path.Combine(results, "docs", fax.Id + ".json")), CanonicalJson.Options)!;
        var aligned = Alignment.Align(kept, [(fax.Id, faxRun.Documents[0].Document!)]);
        Assert.DoesNotContain(aligned, a => a.Key.Contains("fcg", StringComparison.Ordinal));
        Assert.Single(aligned, a => a.Status == "archive-only"); // the tuning CSV's claim, not run here
    }

    // ------------------------------------------------------------------ item 21: a real CSV parser

    [Fact]
    public void The_archive_csv_reads_quoted_fields_and_refuses_a_row_with_the_wrong_field_count()
    {
        var quoted = Keyed("Acme, Mutual \"East\"", "WC-1,2", "C-1", 2);
        var csv = Alignment.WriteArchive([quoted]);
        Assert.Contains("\"Acme, Mutual \"\"East\"\"\"", csv, StringComparison.Ordinal);
        Assert.Equal(quoted, Assert.Single(Alignment.ReadArchive(csv)));

        // CRLF, a blank line and a quoted line break are fine.
        var header = "carrier,policy_number,claim_number,date_of_injury,valuation_date,indemnity_paid,indemnity_reserve,medical_paid,medical_reserve,expense_paid,total_incurred";
        var multi = Alignment.ReadArchive(header + "\r\n\r\n\"Two\nLines Co\",P-1,C-2,2024-02-01,2025-01-15,0,0,10,0,0,10\r\n");
        Assert.Equal("Two\nLines Co", Assert.Single(multi).Carrier);

        // An unquoted comma shifts every later column: refused with the line number, never read shifted.
        var shifted = header + "\nAcme, Mutual,WC-1,C-1,2024-02-01,2025-01-15,0,0,10,0,0,10\n";
        var error = Assert.Throws<FormatException>(() => Alignment.ReadArchive(shifted));
        Assert.Contains("line 2", error.Message, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => Alignment.ReadArchive(header + "\nA,P,C,2024-02-01\n"));
        Assert.Contains("no 'claim_number' column", Assert.Throws<FormatException>(() => Alignment.ReadArchive("carrier,policy_number\nA,P\n")).Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ item 26 (CLI): blind results under blind/

    [Fact]
    public async Task Eval_run_only_blind_refuses_an_out_without_a_blind_segment()
    {
        var work = Temp("blind26");
        Assert.Equal(2, await EvalCli.RunAsync(["run", "--corpus", Path.Combine(work, "corpus"), "--out", Path.Combine(work, "results", "build"), "--mode", "stub",
            "--only-blind", Path.Combine(work, "seal", "seal-manifest.json")]));
        Assert.False(Directory.Exists(Path.Combine(work, "results")));
        Assert.False(Directory.Exists(Path.Combine(work, "blind-results"))); // "blind-results" is not a blind segment either
        Assert.Equal(2, await EvalCli.RunAsync(["run", "--corpus", Path.Combine(work, "corpus"), "--out", Path.Combine(work, "blind-results"), "--mode", "stub",
            "--only-blind", Path.Combine(work, "seal", "seal-manifest.json")]));
    }

    // ------------------------------------------------------------------ item 49: CLI secrets and the derived cap

    [Fact]
    public async Task Eval_import_refuses_passwords_on_the_command_line()
    {
        var corpus = Temp("pw49");
        Assert.Equal(2, await EvalCli.RunAsync(["import", "--in", Fixture, "--out", corpus, "--carriers", Carriers, "--passwords", "secret"]));
        Assert.False(Directory.Exists(corpus));
    }

    [Fact]
    public void The_batch_budget_is_the_tco_per_document_figure_times_the_batch_size()
    {
        string? root = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && root is null; dir = dir.Parent)
        {
            root = File.Exists(Path.Combine(dir.FullName, "config", "tuning-config.json")) ? dir.FullName : null;
        }

        var path = Path.Combine(root ?? throw new DirectoryNotFoundException("config/tuning-config.json"), "config", "tuning-config.json");
        var config = TuningConfig.Load(path);
        // Brief 31 item 4: replay × documents + learn × ceil(share × documents), from docs/poc-plan/tco.md §3.
        Assert.Equal((0.075m, 0.30m, 0.10m, 100), (config.ReplayUsdPerDocument, config.LearnUsdPerFormat, config.ExpectedNewFormatsShare, config.BatchDocuments));
        Assert.Equal(10, config.ExpectedNewFormats);
        Assert.Equal(10.50m, config.BatchBudgetUsd); // before brief 31: 7.50, no learns budgeted
        Assert.DoesNotContain("\"batchBudgetUsd\"", File.ReadAllText(path), StringComparison.Ordinal); // derived, never typed in
        Assert.Equal(2.50m + 0.30m * 3, (config with { BatchDocuments = 25, ReplayUsdPerDocument = 0.1m }).BatchBudgetUsd);
        Assert.Contains("10.50 USD", config.BudgetFormula, StringComparison.Ordinal);
    }
}
