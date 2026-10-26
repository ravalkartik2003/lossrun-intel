using System.Security.Cryptography;
using System.Text.Json;
using LossRun.Contracts;
using PdfSharp.Fonts;

namespace LossRun.Evaluation.Tests;

public class GeneratorTests
{
    public GeneratorTests() => PdfFonts.Configure();

    [Fact]
    public void Truth_is_deterministic_per_seed()
    {
        string Truths(int seed)
        {
            var factory = new TruthFactory(new Random(seed));
            return string.Join("\n", Carriers.All.Select(c => JsonSerializer.Serialize(factory.Create(c, $"{c.Key}-000", [Strata.Reopened]), CanonicalJson.Options)));
        }

        Assert.Equal(Truths(8), Truths(8));
        Assert.NotEqual(Truths(8), Truths(9));
    }

    [Fact]
    public void Corpus_v2_has_every_stratum_at_least_five_documents_and_little_narrative()
    {
        var (strata, documents, narrative) = CorpusGenerator.PlanStrata();

        Assert.InRange(documents, 300, 500);
        Assert.True(narrative <= 0.04, $"narrative share {narrative:P1}");
        foreach (var stratum in Strata.EdgeCases.Concat([Strata.Password, Strata.Bundle, Strata.NonWcRows]))
        {
            Assert.True(strata.GetValueOrDefault(stratum) >= 5, $"{stratum}: {strata.GetValueOrDefault(stratum)} documents");
        }
    }

    [Fact]
    public void A_summary_only_period_has_no_claim_rows_but_real_subtotals_in_the_grand_total()
    {
        var truth = new TruthFactory(new Random(8)).Create(Carriers.SyntheticMutual, "c1-x", [Strata.SummaryOnlyPeriod]);
        var summary = truth.PolicyPeriods.Single(p => p.DetailLevel == DetailLevel.SummaryOnly);

        Assert.Empty(summary.Claims);
        Assert.True(summary.Subtotals!.ClaimCount >= 3);
        Assert.Equal(truth.PolicyPeriods.Sum(p => p.Subtotals!.Total!.Incurred), truth.ReportTotals!.Total!.Incurred);
    }

    [Fact]
    public void Truth_totals_reconcile_with_the_claims()
    {
        var factory = new TruthFactory(new Random(8));
        foreach (var carrier in Carriers.All)
        {
            var truth = factory.Create(carrier, $"{carrier.Key}-000", []);
            foreach (var period in truth.PolicyPeriods.Where(p => p.Subtotals is not null))
            {
                Assert.Equal(period.Claims.Count, period.Subtotals!.ClaimCount);
                Assert.Equal(period.Claims.Sum(c => c.Total?.Incurred ?? 0), period.Subtotals.Total?.Incurred ?? 0);
            }

            Assert.All(truth.PolicyPeriods.SelectMany(p => p.Claims), c =>
                Assert.Equal((c.Indemnity?.Incurred ?? 0) + (c.Medical?.Incurred ?? 0) + (c.Expense?.Incurred ?? 0), c.Total!.Incurred));
        }
    }

    [Theory]
    [InlineData("c1")]
    [InlineData("c2")]
    [InlineData("c3")]
    [InlineData("c4")]
    [InlineData("c5")]
    public void Renderings_are_byte_identical_for_the_same_truth(string key)
    {
        var carrier = Carriers.All.Single(c => c.Key == key);
        var truth = new TruthFactory(new Random(8)).Create(carrier, $"{key}-000", []);

        var first = Renderers.Render(carrier, truth);
        var second = Renderers.Render(carrier, truth);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(first.Bytes)), Convert.ToHexString(SHA256.HashData(second.Bytes)));
        Assert.True(first.Pages >= 1);
        Assert.NotEmpty(first.Printed.ClaimFields);
    }
}

public class ScanDeterminismTests
{
    [Theory]
    [InlineData(InputTypes.Scan300)]
    [InlineData(InputTypes.Scan200)]
    [InlineData(InputTypes.Fax150)]
    public void Scan_renderings_are_byte_identical_for_the_same_seed(string inputType)
    {
        PdfFonts.Configure();
        var carrier = Carriers.FictionalCasualty;
        var native = Renderers.Render(carrier, new TruthFactory(new Random(8)).Create(carrier, "c2-000", [], minClaims: 5, maxClaims: 8)).Bytes;

        var a = Renderers.Scan(native, inputType, new Random(42));
        var b = Renderers.Scan(native, inputType, new Random(42));

        Assert.Equal(Convert.ToHexString(SHA256.HashData(a)), Convert.ToHexString(SHA256.HashData(b)));
    }
}

public class StatsTests
{
    [Fact]
    public void Agresti_coull_without_clustering_matches_the_textbook_interval()
    {
        // 90/100 independent observations: Agresti-Coull 95 % = [82.4 %, 94.7 %] (ñ = 103.84, p̃ = 0.885).
        var observations = Enumerable.Range(0, 100).Select(i => ($"d{i}", i < 90)).ToList();

        var p = Stats.Estimate(observations, bootstrap: 0);

        Assert.Equal(0.9, p.Value, 6);
        Assert.Equal(0.8236, p.Lower, 0.0005);
        Assert.Equal(0.9465, p.Upper, 0.0005);
        Assert.Equal(1.0, p.DesignEffect);
    }

    [Fact]
    public void Clustered_errors_widen_the_interval()
    {
        // Same 90 % rate, but every error sits in one of two documents: the design effect must exceed 1.
        var independent = Enumerable.Range(0, 200).Select(i => ($"d{i % 20}", i % 10 != 0)).ToList();
        var clustered = Enumerable.Range(0, 200).Select(i => ($"d{i / 10}", i >= 20)).ToList();

        var a = Stats.Estimate(independent, bootstrap: 1000);
        var b = Stats.Estimate(clustered, bootstrap: 1000);

        Assert.Equal(a.Value, b.Value, 6);
        Assert.True(b.DesignEffect > 1.5, $"deff {b.DesignEffect}");
        Assert.True(b.Upper - b.Lower > a.Upper - a.Lower);
    }

    [Fact]
    public void Percentiles_use_the_nearest_rank()
    {
        Assert.Equal(5, Stats.Percentile([10, 9, 8, 7, 6, 5, 4, 3, 2, 1], 0.5), 6);
        Assert.Equal(10, Stats.Percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 0.95), 6);
        Assert.Equal(10, Stats.Percentile([10], 0.95), 6);
    }
}

public class SealingTests
{
    private static string Corpus()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lossrun-seal-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "docs"));
        var manifest = new CorpusManifest { Seed = 8 };
        foreach (var carrier in new[] { "c1", "c2" })
        {
            for (var logical = 0; logical < 10; logical++)
            {
                foreach (var input in new[] { InputTypes.NativePdf, InputTypes.Scan300 })
                {
                    var id = $"{carrier}-{logical:000}-{input}";
                    var bytes = System.Text.Encoding.UTF8.GetBytes(id);
                    File.WriteAllBytes(Path.Combine(dir, "docs", id + ".pdf"), bytes);
                    manifest.Entries.Add(new ManifestEntry(id, id + ".pdf", carrier, "layout", input, $"{carrier}-{logical:000}", logical % 5 == 1 ? [Strata.Reopened] : [],
                        Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, 1));
                }
            }
        }

        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, CanonicalJson.Options));
        return dir;
    }

    [Fact]
    public void Renderings_of_one_document_land_on_the_same_side_and_the_split_is_reproducible()
    {
        var corpus = Corpus();

        var seal = Sealing.Split(corpus, tuningSize: 10, seed: 8);
        var again = Sealing.Split(corpus, tuningSize: 10, seed: 8);

        var tuningLogical = seal.Tuning.Select(t => t.Id[..6]).ToHashSet();
        Assert.DoesNotContain(seal.Blind, b => tuningLogical.Contains(b.Id[..6]));
        Assert.Equal(40, seal.Tuning.Count + seal.Blind.Count);
        Assert.InRange(seal.Tuning.Count, 10, 14);
        Assert.Contains(seal.Tuning, t => t.Carrier == "c1");
        Assert.Contains(seal.Tuning, t => t.Carrier == "c2");
        Assert.Equal(seal.BlindManifestSha256, again.BlindManifestSha256);
    }

    [Fact]
    public void Verify_names_a_blind_file_changed_after_sealing()
    {
        var corpus = Corpus();
        var seal = Sealing.Split(corpus, tuningSize: 10, seed: 8);
        Assert.Empty(Sealing.Verify(corpus, seal));

        File.WriteAllText(Path.Combine(corpus, "docs", seal.Blind[0].File), "swapped");

        Assert.Equal([seal.Blind[0].File], Sealing.Verify(corpus, seal));
    }
}

public class AlignmentTests
{
    private static LossRunDocument Extracted(DateOnly valuation, decimal medicalPaid) => new()
    {
        CarrierName = "Synthetic Mutual Insurance Co.",
        ValuationDate = valuation,
        PolicyPeriods =
        [
            new PolicyPeriod
            {
                PolicyNumber = "WC-100",
                Claims =
                [
                    new Claim
                    {
                        ClaimNumber = "WC-23-0001", DateOfInjury = new DateOnly(2023, 2, 1),
                        Indemnity = new MoneyTriplet(1000m, 0m, 1000m), Medical = new MoneyTriplet(medicalPaid, 0m, medicalPaid), Total = new MoneyTriplet(1000m + medicalPaid, 0m, 1000m + medicalPaid),
                    },
                    new Claim { ClaimNumber = "WC-23-0002", DateOfInjury = new DateOnly(2023, 3, 1) },
                ],
            },
        ],
    };

    private static readonly KeyedClaim Keyed = new("Synthetic Mutual Insurance Co.", "WC-100", "WC 23 0001", new DateOnly(2023, 2, 1), new DateOnly(2025, 1, 1),
        1000m, 0m, 500m, 0m, null, 1500m);

    [Fact]
    public void Claims_align_on_normalized_keys_and_amounts_compare_only_at_the_same_valuation()
    {
        var same = Alignment.Align([Keyed], [("doc", Extracted(new DateOnly(2025, 1, 1), 600m))]);
        var later = Alignment.Align([Keyed], [("doc", Extracted(new DateOnly(2025, 6, 30), 600m))]);

        var matched = Assert.Single(same, a => a.Status == "matched");
        Assert.True(matched.AmountsComparable);
        Assert.Contains(matched.Disagreements, d => d.StartsWith("medical.paid", StringComparison.Ordinal));
        Assert.Contains(same, a => a.Status == "extracted-only");
        var unvalued = Assert.Single(later, a => a.Status == "matched");
        Assert.False(unvalued.AmountsComparable);
        Assert.Empty(unvalued.Disagreements);
    }

    [Fact]
    public void A_keyed_claim_that_was_not_extracted_is_archive_only()
    {
        var aligned = Alignment.Align([Keyed with { ClaimNumber = "WC-23-0099" }], [("doc", Extracted(new DateOnly(2025, 1, 1), 500m))]);

        Assert.Contains(aligned, a => a.Status == "archive-only");
    }

    [Fact]
    public void The_archive_csv_round_trips()
    {
        var csv = Alignment.WriteArchive([Keyed]);

        Assert.Equal(Keyed, Assert.Single(Alignment.ReadArchive(csv)));
    }
}

public class ScorerTests
{
    private static (ManifestEntry Entry, TruthRecord Truth) Fixture()
    {
        var carrier = Carriers.FictionalCasualty;
        var truth = new TruthFactory(new Random(8)).Create(carrier, "c2-000", []);
        var printed = new PrintedFields(["claimNumber", "dateOfInjury", "indemnity.paid", "medical.paid"], ["carrierName", "valuationDate"], ["policyNumber"], [], []);
        var record = new TruthRecord { Id = "c2-000-native-pdf", Carrier = "c2", InputType = InputTypes.NativePdf, Printed = printed, Document = truth };
        return (new ManifestEntry(record.Id, "x.pdf", "c2", carrier.Layout, InputTypes.NativePdf, "c2-000", [], "", 0, 1), record);
    }

    private static LossRunDocument Copy(LossRunDocument d) =>
        JsonSerializer.Deserialize<LossRunDocument>(JsonSerializer.Serialize(d, CanonicalJson.Options), CanonicalJson.Options)!;

    [Fact]
    public void A_perfect_extraction_matches_every_printed_field()
    {
        var (entry, truth) = Fixture();

        var (fields, matched, predicted, truthClaims, _) = Scorer.ScoreFields(entry, truth, Copy(truth.Document), hardPassed: true, "seen");

        Assert.All(fields, f => Assert.True(f.Match, $"{f.Field}: {f.Expected} vs {f.Actual}"));
        Assert.Equal(truthClaims, matched);
        Assert.Equal(truthClaims, predicted);
    }

    [Fact]
    public void Errors_are_typed_missing_wrong_and_wrong_but_plausible()
    {
        var (entry, truth) = Fixture();
        var extracted = Copy(truth.Document);
        var claims = extracted.PolicyPeriods.SelectMany(p => p.Claims).ToList();
        claims[0].Indemnity!.Paid += 250m;                                     // wrong value
        extracted.PolicyPeriods.First(p => p.Claims.Contains(claims[^1])).Claims.Remove(claims[^1]); // dropped row

        var silent = Scorer.ScoreFields(entry, truth, extracted, hardPassed: true, "seen").Fields;
        var caught = Scorer.ScoreFields(entry, truth, extracted, hardPassed: false, "seen").Fields;
        var none = Scorer.ScoreFields(entry, truth, null, hardPassed: false, "seen").Fields;

        Assert.Equal("wrong-but-plausible", silent.Single(f => f.Field == "claim.indemnity.paid" && f.Error != "missing" && !f.Match).Error);
        Assert.Equal("wrong", caught.Single(f => f.Field == "claim.indemnity.paid" && f.Error != "missing" && !f.Match).Error);
        Assert.Equal(4, silent.Count(f => f.Error == "missing"));                  // the dropped claim's four printed fields
        Assert.All(none, f => Assert.Equal("missing", f.Error));               // no document: everything missing, nothing excluded
    }
}

public class CuCacheTests
{
    private sealed class CountingCu : LossRun.Ingestion.ContentUnderstanding.IContentUnderstandingService
    {
        public int Calls { get; private set; }

        public Task EnsureAnalyzerAsync(string analyzerId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<LossRun.Ingestion.ContentUnderstanding.CuCallResult> AnalyzeAsync(string analyzerId, BinaryData content, string contentType, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new LossRun.Ingestion.ContentUnderstanding.CuCallResult(analyzerId, "op-1", "{\"id\":\"op-1\",\n\"result\":{}}", TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public async Task A_cached_call_is_served_from_disk_with_the_same_body_and_the_inner_service_is_not_called_again()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cu-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var inner = new CountingCu();
            var cache = new CachingContentUnderstanding(inner, dir);
            var first = await cache.AnalyzeAsync("prebuilt-layout", BinaryData.FromString("%PDF a"), "application/pdf", CancellationToken.None);
            var second = await cache.AnalyzeAsync("prebuilt-layout", BinaryData.FromString("%PDF a"), "application/pdf", CancellationToken.None);
            var other = await cache.AnalyzeAsync("prebuilt-layout", BinaryData.FromString("%PDF b"), "application/pdf", CancellationToken.None);

            Assert.Equal(2, inner.Calls); // the repeat came from disk; different bytes are a different key
            Assert.Equal(first.RawJson, second.RawJson); // a multi-line body survives
            Assert.Equal(("op-1", "async+cache", TimeSpan.Zero), (second.OperationId, second.Mode, second.Latency));
            Assert.Equal((1, 2), (cache.Hits, cache.Misses));
            Assert.Equal("{\"id\":\"op-1\",\n\"result\":{}}", other.RawJson);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
