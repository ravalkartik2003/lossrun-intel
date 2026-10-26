using System.Security.Cryptography;
using System.Text.Json;
using LossRun.Contracts;
using LossRun.Evaluation;

namespace LossRun.Evaluation.Tests;

/// <summary>
/// Brief 14: <c>lossrun eval import</c> turns a folder of archive files into a corpus that <c>seal</c>, <c>run</c> and
/// <c>score</c> read. The fixture is 10 synthetic files of mixed types in carrier folders, a PNG at the root (carrier
/// from the map) and a .txt that must be skipped.
/// </summary>
public class ImportTests
{
    private static string Fixture => EvalRunner.FindUp("src/TestShared/fixtures/import") ?? throw new DirectoryNotFoundException("import fixture");

    private static string Carriers => Path.Combine(Path.GetDirectoryName(Fixture)!, "import-carriers.csv");

    private static string NewCorpus() => Path.Combine(Path.GetTempPath(), "lossrun-import-" + Guid.NewGuid().ToString("n")[..8]);

    private static Dictionary<string, string> Hashes(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f), f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))));

    [Fact]
    public void Import_sniffs_types_carriers_and_strata_and_leaves_the_source_untouched()
    {
        var before = Hashes(Fixture);
        var corpus = NewCorpus();
        Assert.Equal(0, Import.Run(Fixture, corpus, Carriers, copy: false, passwords: []));

        var manifest = CorpusManifest.Load(corpus);
        Assert.Equal((Import.Generator, (int?)null), (manifest.GeneratorVersion, manifest.Seed));
        Assert.Equal(10, manifest.Entries.Count);
        Assert.Equal(before, Hashes(Fixture)); // never modified
        var skipped = JsonSerializer.Deserialize<List<ImportedFile>>(File.ReadAllText(Path.Combine(corpus, "import-report.json")), CanonicalJson.Options)!
            .Where(f => f.Skipped is not null).ToList();
        Assert.Equal("readme.txt", Assert.Single(skipped).File);

        ManifestEntry Entry(string suffix) => manifest.Entries.Single(e => e.File.EndsWith(suffix, StringComparison.Ordinal));
        Assert.Equal(InputTypes.NativePdf, Entry("2024-renewal-loss-run.pdf").InputType);
        Assert.Equal(InputTypes.Fax150, Entry("faxed-loss-run.pdf").InputType);
        Assert.Equal(InputTypes.Scan200, Entry("scanned-loss-run.pdf").InputType);
        Assert.Equal(InputTypes.Xlsx, Entry("claims-export.xlsx").InputType);
        Assert.Equal(InputTypes.Csv, Entry("claims.csv").InputType);
        Assert.Equal((InputTypes.Zip, Strata.Bundle), (Entry("broker-package.zip").InputType, Entry("broker-package.zip").Strata.Single()));
        Assert.Equal(InputTypes.Fax150, Entry("phone-photo-of-loss-run.png").InputType); // 150 dpi recorded in the PNG
        Assert.Contains(Strata.Password, Entry("protected-loss-run.pdf").Strata);

        // Carrier: the map by file, then by folder (a folder name that cannot end in "."), else the folder name.
        Assert.Equal("Synthetic Mutual Insurance Co.", Entry("phone-photo-of-loss-run.png").Carrier);
        Assert.Equal("Synthetic Mutual Insurance Co.", Entry("faxed-loss-run.pdf").Carrier);
        Assert.Equal("Placeholder Assurance Co.", Entry("claims.csv").Carrier);
        Assert.Equal("Fictional Casualty Group", Entry("Group/loss-run.pdf").Carrier);

        // Files are referenced in place, and the sha in the manifest is the file's.
        Assert.All(manifest.Entries, e =>
            Assert.Equal(e.Sha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(CorpusManifest.DocumentPath(corpus, e.File))))));
        Assert.Equal(manifest.Entries.Count, manifest.Entries.Select(e => e.Id).Distinct().Count());

        // A package stays one entry and records its members (brief 14 follow-up).
        var package = Entry("broker-package.zip");
        Assert.Equal([("claims-2023.xlsx", InputTypes.Xlsx), ("claims-2024.xlsx", InputTypes.Xlsx)], package.Members!.Select(m => (m.File, m.InputType)));

        // truth-request.csv: every policy the readable files print, with its period, and a carrier row for what needs OCR first.
        var request = File.ReadAllLines(Path.Combine(corpus, "truth-request.csv"));
        Assert.Equal("carrier,policy_number,period_start,period_end,documents,note", request[0]);
        Assert.Contains("Fictional Casualty Group,FCG-WC-2101,2021-01-01,2022-01-01,1,", request);
        Assert.Contains("Synthetic Mutual Insurance Co.,SYN-WC-2201,2022-07-01,2023-07-01,2,", request); // the native PDF and the bundle
        Assert.Contains(request, l => l.StartsWith("Notional Workers Comp Exchange,NWX-2024-01,2024-10-01,2025-10-01,", StringComparison.Ordinal)); // read inside the ZIP
        Assert.Contains(request, l => l.StartsWith("Placeholder Assurance Co.,PAC-WC-", StringComparison.Ordinal)); // the CSV
        Assert.Contains(request, l => l.StartsWith("Synthetic Mutual Insurance Co.,,,,3,", StringComparison.Ordinal)); // fax, PNG, protected PDF: not readable before OCR
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_imported_corpus_seals_and_the_seal_verifies(bool copy)
    {
        var corpus = NewCorpus();
        Import.Run(Fixture, corpus, Carriers, copy, []);
        Assert.Equal(copy, Directory.Exists(Path.Combine(corpus, "files")));

        var seal = Sealing.Split(corpus, tuningSize: 3, seed: 14);
        Assert.Equal(10, seal.Tuning.Count + seal.Blind.Count);
        Assert.Null(seal.CorpusSeed);
        Assert.Empty(Sealing.Verify(corpus, seal));
    }

    [Fact]
    public void Align_then_adjudicated_truth_makes_an_imported_corpus_scoreable_for_accuracy()
    {
        var corpus = NewCorpus();
        Import.Run(Fixture, corpus, Carriers, copy: false, passwords: []);
        var entry = CorpusManifest.Load(corpus).Entries.First(e => e.File.EndsWith("2024-renewal-loss-run.pdf", StringComparison.Ordinal));
        var valued = new DateOnly(2025, 1, 15);
        var extracted = new LossRunDocument
        {
            CarrierName = "Synthetic Mutual Insurance Co.", ValuationDate = valued,
            PolicyPeriods =
            [
                new PolicyPeriod
                {
                    PolicyNumber = "WC-100",
                    Claims =
                    [
                        new Claim { ClaimNumber = "WC-24-0001", DateOfInjury = new DateOnly(2024, 2, 1), Indemnity = new MoneyTriplet(1000m, 0m, 1000m), Medical = new MoneyTriplet(500m, 250m, 750m), Expense = new MoneyTriplet(0m, null, null), Total = new MoneyTriplet(null, null, 1750m) },
                        new Claim { ClaimNumber = "WC-24-0002", DateOfInjury = new DateOnly(2024, 3, 1), Indemnity = new MoneyTriplet(0m, 0m, 0m), Medical = new MoneyTriplet(90m, 0m, 90m), Expense = new MoneyTriplet(0m, null, null), Total = new MoneyTriplet(null, null, 90m) },
                    ],
                },
            ],
        };
        var results = Path.Combine(corpus, "results");
        Directory.CreateDirectory(Path.Combine(results, "docs"));
        var run = new RunResult { Id = entry.Id, Documents = [new ExtractedDocument { FinalTier = "ReplayAccepted", Document = extracted }] };
        File.WriteAllText(Path.Combine(results, "docs", entry.Id + ".json"), JsonSerializer.Serialize(run, CanonicalJson.Options));
        var archive = Path.Combine(corpus, "archive.csv");
        File.WriteAllText(archive, Alignment.WriteArchive(
        [
            new KeyedClaim("Synthetic Mutual Insurance Co.", "WC-100", "WC 24 0001", new DateOnly(2024, 2, 1), valued, 1000m, 0m, 500m, 250m, 0m, 1750m),
            new KeyedClaim("Synthetic Mutual Insurance Co.", "WC-100", "WC-24-0002", new DateOnly(2024, 3, 1), valued, 0m, 0m, 90m, 0m, 0m, 90m),
            new KeyedClaim("Synthetic Mutual Insurance Co.", "WC-100", "WC-24-0003", new DateOnly(2024, 4, 1), valued, 0m, 0m, 40m, 0m, 0m, 40m), // printed, not extracted
            new KeyedClaim("Synthetic Mutual Insurance Co.", "WC-999", "WC-19-0009", new DateOnly(2019, 4, 1), valued, 0m, 0m, 10m, 0m, 0m, 10m), // in no submitted document
        ]));

        Assert.Equal(0, Alignment.Run(archive, results, Path.Combine(corpus, "align")));
        // Adjudication: the missed claim is attributed to its document; the claim no document printed stays unattributed.
        var aligned = Path.Combine(corpus, "align", "aligned.csv");
        File.WriteAllLines(aligned, File.ReadAllLines(aligned).Select(l => l.StartsWith("archive-only,", StringComparison.Ordinal) && l.Contains("|2024-04-01", StringComparison.Ordinal)
            ? l.Replace(",,False,", $",{entry.Id},True,", StringComparison.Ordinal) : l));

        Assert.Equal(3, TruthBuilder.Run(archive, aligned, corpus)); // WC-19-0009 is left unattributed: refused without --accept-defaults (brief 30 item 21)
        Assert.Equal(0, TruthBuilder.Run(archive, aligned, corpus, acceptDefaults: true));
        var truth = CorpusManifest.Truth(corpus, entry.Id);
        Assert.Equal(3, truth.Document.PolicyPeriods.Single().Claims.Count);
        Assert.Contains("medical.reserve", truth.Printed.ClaimFields);

        var scored = Scorer.Score(corpus, results);
        var key = scored.Fields.Where(f => f.Doc == entry.Id && Scorer.KeyFields.Contains(f.Field)).ToList();
        Assert.Equal(18, key.Count); // 3 claims × 6 key fields
        Assert.Equal(12, key.Count(f => f.Match)); // the two extracted claims match the archive exactly
        Assert.All(key.Where(f => !f.Match), f => Assert.Equal("missing", f.Error)); // the claim the extractor missed
    }

    [Fact]
    public void The_adjudication_workbook_shows_raw_values_and_its_decisions_build_the_truth()
    {
        var corpus = NewCorpus();
        Import.Run(Fixture, corpus, Carriers, copy: false, passwords: []);
        var entry = CorpusManifest.Load(corpus).Entries.First(e => e.File.EndsWith("2024-renewal-loss-run.pdf", StringComparison.Ordinal));
        var valued = new DateOnly(2025, 1, 15);
        Claim Extracted(string number, int month, decimal medicalPaid) => new()
        {
            ClaimNumber = number, DateOfInjury = new DateOnly(2024, month, 1), Indemnity = new MoneyTriplet(0m, 0m, 0m), Medical = new MoneyTriplet(medicalPaid, 0m, medicalPaid),
            Expense = new MoneyTriplet(0m, null, null), Total = new MoneyTriplet(null, null, medicalPaid),
        };
        var document = new LossRunDocument
        {
            CarrierName = "Synthetic Mutual Insurance Co.", ValuationDate = valued,
            PolicyPeriods = [new PolicyPeriod { PolicyNumber = "WC-100", Claims = [Extracted("WC-24-0001", 2, 500m), Extracted("WC-24-0002", 3, 90m), Extracted("WC-24-0004", 5, 70m)] }],
        };
        var results = Path.Combine(corpus, "results");
        Directory.CreateDirectory(Path.Combine(results, "docs"));
        File.WriteAllText(Path.Combine(results, "docs", entry.Id + ".json"),
            JsonSerializer.Serialize(new RunResult { Id = entry.Id, Documents = [new ExtractedDocument { FinalTier = "ReplayAccepted", Document = document }] }, CanonicalJson.Options));
        KeyedClaim Keyed(string number, int month, decimal medicalPaid, string policy = "WC-100") =>
            new("Synthetic Mutual Insurance Co.", policy, number, new DateOnly(2024, month, 1), valued, 0m, 0m, medicalPaid, 0m, 0m, medicalPaid);
        var archive = Path.Combine(corpus, "truth.csv");
        File.WriteAllText(archive, Alignment.WriteArchive(
        [
            Keyed("WC 24 0001", 2, 500m),          // matched, agrees: needs no decision
            Keyed("WC-24-0002", 3, 95m),           // matched, medical paid differs: the document prints 90
            Keyed("WC-24-0003", 4, 40m),           // printed, not extracted
            Keyed("WC-19-0009", 4, 10m, "WC-999"), // in no document of the set
        ]));                                       // WC-24-0004 was extracted but is not in the archive

        var align = Path.Combine(corpus, "align");
        Assert.Equal(0, Alignment.Run(archive, results, align, corpusDir: corpus));
        var workbook = Path.Combine(align, "adjudication.xlsx");
        using (var wb = new ClosedXML.Excel.XLWorkbook(workbook))
        {
            var ws = wb.Worksheet("Adjudication");
            var rows = ws.RowsUsed().Skip(1).ToList();
            Assert.Equal(4, rows.Count); // the agreeing match is not listed
            Assert.True(ws.Column(25).IsHidden); // the normalized key is never in view
            Assert.DoesNotContain(rows, r => r.Cells(1, 24).Any(c => c.GetString().Contains('|', StringComparison.Ordinal)));
            ClosedXML.Excel.IXLRow Row(string number) => rows.Single(r => r.Cell(6).GetString() == number);
            Assert.Equal(("Matched, amounts differ", 95.0, 90.0), (Row("WC-24-0002").Cell(2).GetString(), Row("WC-24-0002").Cell(14).GetDouble(), Row("WC-24-0002").Cell(15).GetDouble()));
            Row("WC-24-0002").Cell(23).Value = AdjudicationWorkbook.ExtractedCorrect;
            Row("WC-24-0003").Cell(3).Value = entry.File; // printed on this document; decision left blank → archive-correct
            wb.Save();                                     // WC-19-0009 and WC-24-0004 left blank → exclude
        }

        Assert.Equal(0, TruthBuilder.Run(archive, Path.Combine(align, "aligned.csv"), corpus, workbook, acceptDefaults: true));
        var truth = CorpusManifest.Truth(corpus, entry.Id).Document.PolicyPeriods.Single().Claims;
        Assert.Equal(["WC 24 0001", "WC-24-0002", "WC-24-0003"], truth.Select(c => c.ClaimNumber).Order(StringComparer.Ordinal));
        Assert.Equal(90m, truth.Single(c => c.ClaimNumber == "WC-24-0002").Medical!.Paid); // extracted-correct: what the document prints

        var key = Scorer.Score(corpus, results).Fields.Where(f => f.Doc == entry.Id && Scorer.KeyFields.Contains(f.Field)).ToList();
        Assert.Equal(18, key.Count);
        Assert.Equal(12, key.Count(f => f.Match)); // 0001 and 0002 match; 0003 was missed; 0004 is an extra claim, not a truth claim

        // A decision that cannot apply is refused, naming the row.
        using (var wb = new ClosedXML.Excel.XLWorkbook(workbook))
        {
            wb.Worksheet("Adjudication").RowsUsed().Skip(1).Single(r => r.Cell(6).GetString() == "WC-24-0004").Cell(23).Value = AdjudicationWorkbook.ArchiveCorrect;
            wb.Save();
        }

        var error = Assert.Throws<FormatException>(() => TruthBuilder.Run(archive, Path.Combine(align, "aligned.csv"), corpus, workbook, acceptDefaults: true));
        Assert.Contains("the archive has no such claim", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Score_reports_operational_numbers_for_an_imported_corpus_without_truth()
    {
        var corpus = NewCorpus();
        Import.Run(Fixture, corpus, Carriers, copy: false, passwords: []);
        var manifest = CorpusManifest.Load(corpus);
        var results = Path.Combine(corpus, "results");
        Directory.CreateDirectory(Path.Combine(results, "docs"));
        foreach (var e in manifest.Entries.Take(2))
        {
            var run = new RunResult { Id = e.Id, TotalMs = 4000, CostUsd = 0.01m, Documents = [new ExtractedDocument { FinalTier = "Review" }] };
            File.WriteAllText(Path.Combine(results, "docs", e.Id + ".json"), JsonSerializer.Serialize(run, CanonicalJson.Options));
        }

        var scored = Scorer.Score(corpus, results);
        Assert.Equal(2, scored.Docs.Count);
        Assert.All(scored.Docs, d => Assert.Equal((Scorer.NoTruth, "Review"), (d.Error, d.FinalTier)));
        Assert.Empty(scored.Fields);

        var outDir = Path.Combine(corpus, "report");
        Assert.Equal(0, Scorer.Run(corpus, results, outDir, bootstrap: 50)); // no NaN from empty segments, no missing truth file
        var markdown = File.ReadAllText(Path.Combine(outDir, "report.md"));
        Assert.StartsWith("# Evaluation report — imported archive", markdown, StringComparison.Ordinal);
        var lines = markdown.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.StartsWith("> **2 of 2 documents have no truth yet**", lines[2], StringComparison.Ordinal); // right under the title, on every platform
        Assert.DoesNotContain(Report.DeckCaveat, File.ReadAllText(Path.Combine(outDir, "deck-table.md")), StringComparison.Ordinal);
    }
}
