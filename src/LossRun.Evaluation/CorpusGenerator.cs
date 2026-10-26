using System.Security.Cryptography;
using System.Text.Json;
using LossRun.Contracts;
using PdfSharp.Fonts;

namespace LossRun.Evaluation;

/// <summary>
/// Generates the stratified synthetic corpus (brief 08 §1): five fictitious carriers, three policy years each, 5–60
/// claims per document, PDF layouts rendered native and as three scan qualities, edge cases on about a quarter of the
/// documents, password-protected and bundled variants. Deterministic from the seed (PDF metadata timestamps aside).
/// </summary>
public static class CorpusGenerator
{
    // 0.2.0 (corpus v2, 08 Answers 6): every edge stratum ≥ 5 documents, narrative ≤ 4 %, summary-only periods with real subtotals.
    // 0.1.1: byte-identical output (Deterministic); 0.1.0 differed only in PDF/XLSX metadata.
    // 0.3.0 (corpus v3, brief 13): v2 plus WCIO codes, cumulative trauma, litigation, recoveries, last day worked (printed by the
    // XLSX and CSV layouts) and payroll/FTE submission context; the extra fields draw from a second random stream, so v2's
    // claims, amounts and PDF bytes are unchanged.
    // 0.4.0 (brief 19, ADR 0018): a separate constructs plan (--constructs) — two-line records, one table per claim with loose
    // lines, stacked two-field cells, component-sum headers — so the spec-language constructs stay covered; the v3 plan and its
    // output are unchanged.
    // 0.5.0 (brief 19 Answers 5): corpus v4 (--v4) = the v3 plan, then the constructs plan, in one corpus and one deck table;
    // the v3 plan runs first on the same random stream, so v3's documents are unchanged by seed.
    // 0.6.0 (brief 20 §2): c7 prints claim numbers of three shapes (as after a carrier's system migration) and, on every
    // other claim, a file number on the claim-number line; only the construct carriers change, so v3 is unchanged by seed.
    // 0.7.0 (brief 22 Answers 1): corpus v5 (--v5) = corpus v4, then c10 (measures as rows: one row per measure under component
    // columns); c10 runs last on the same random stream, so v4's documents are unchanged by seed. --constructs also emits c10.
    // 0.8.0 (brief 23 §2): the truth keeps rows of another line of business (c5 non-wc-rows) on the period's keptRows, as
    // replay now does; the rows are moved after rendering, so every file is unchanged by seed.
    // 0.9.0 (brief 31 item 1): coded values name their code-sets.json table (WCIO-Nature, WCIO-Cause, WCIO-PartOfBody), as the
    // schema says, not "WCIO"; truth only, on the same random stream, so every rendered file is unchanged by seed.
    public const string Version = "corpus-gen-0.9.0";

    /// <summary>Test password for the protected variants (synthetic; supplied to the pipeline as configuration).</summary>
    public const string Password = "synthetic-pass-08";

    private sealed record Plan(CarrierProfile Carrier, int Truths, string[] Renderings, string[] Edges, int PasswordVariants, int Bundles,
        Func<LossRunDocument, LossRunDocument>? Transform = null, string? Construct = null);

    private static readonly string[] PdfRenderings = [InputTypes.NativePdf, InputTypes.Scan300, InputTypes.Scan200, InputTypes.Fax150];

    /// <summary>Every <see cref="EdgeEvery"/>-th logical document (offset 1) carries the next edge case of its carrier's list.</summary>
    private const int EdgeEvery = 3;

    // Edge lists may repeat a stratum so that every stratum reaches ≥ 5 documents across carriers (a PDF truth yields 4
    // documents, a spreadsheet truth 1); CSV cannot print a summary-only period, XLSX prints no line of business.
    private static readonly Plan[] Plans =
    [
        new(Carriers.SyntheticMutual, 24, PdfRenderings,
            [Strata.MissingYear, Strata.PartialRun, Strata.ClosedOnly, Strata.Reopened, Strata.CatOutlier, Strata.MultiPolicy, Strata.SummaryOnlyPeriod, Strata.SourceInconsistent], 6, 8),
        new(Carriers.FictionalCasualty, 24, PdfRenderings,
            [Strata.MissingYear, Strata.PartialRun, Strata.ClosedOnly, Strata.Reopened, Strata.CatOutlier, Strata.MultiPolicy, Strata.SourceInconsistent, Strata.SummaryOnlyPeriod], 4, 0),
        new(Carriers.ImaginaryIndemnity, 3, PdfRenderings, [Strata.Reopened], 0, 0),
        new(Carriers.NotionalExchange, 40, [InputTypes.Xlsx],
            [Strata.MissingYear, Strata.PartialRun, Strata.ClosedOnly, Strata.Reopened, Strata.CatOutlier, Strata.MultiPolicy, Strata.NonCa, Strata.SourceInconsistent,
             Strata.SummaryOnlyPeriod, Strata.NonCa, Strata.NonCa, Strata.Reopened, Strata.MissingYear], 0, 0),
        new(Carriers.PlaceholderAssurance, 40, [InputTypes.Csv],
            [Strata.NonWcRows, Strata.NonCa, Strata.MissingYear, Strata.NonWcRows, Strata.PartialRun, Strata.NonCa, Strata.ClosedOnly, Strata.NonWcRows, Strata.Reopened,
             Strata.CatOutlier, Strata.NonWcRows, Strata.MultiPolicy, Strata.NonWcRows], 0, 0),
    ];

    /// <summary>ADR 0018 constructs (brief 19): one fictitious carrier per construct, native renderings only, six documents each.</summary>
    private static readonly Plan[] ConstructPlans =
    [
        new(Carriers.HypotheticalMutual, 6, [InputTypes.NativePdf], [], 0, 0, Construct: Strata.TwoLineRecords),
        new(Carriers.SpecimenIndemnity, 6, [InputTypes.NativePdf], [], 0, 0, Construct: Strata.ClaimPerTable),
        new(Carriers.IllustrativeComp, 6, [InputTypes.NativePdf], [], 0, 0, Construct: Strata.StackedColumns),
        new(Carriers.SampleComposite, 6, [InputTypes.Csv], [], 0, 0, Renderers.IndemnityMedicalTotals, Strata.ComponentSum),
    ];

    /// <summary>Corpus v5 additions (brief 22 Answers 1): the measures-as-rows construct, after the v4 plans.</summary>
    private static readonly Plan[] V5Plans =
    [
        new(Carriers.ExemplarCasualty, 6, [InputTypes.NativePdf], [], 0, 0, Construct: Strata.MeasuresAsRows),
    ];

    /// <summary>Documents per stratum and the narrative share the plans produce (no rendering; checked by tests).</summary>
    public static (IReadOnlyDictionary<string, int> Strata, int Documents, double NarrativeShare) PlanStrata()
    {
        var strata = new Dictionary<string, int>(StringComparer.Ordinal);
        var documents = 0;
        var narrative = 0;
        foreach (var plan in Plans)
        {
            var planDocs = plan.Truths * plan.Renderings.Length + plan.PasswordVariants + plan.Bundles;
            documents += planDocs;
            narrative += plan.Carrier.Layout == "narrative" ? planDocs : 0;
            for (var i = 0; i < plan.Truths; i++)
            {
                if (EdgeFor(plan, i) is { } edge)
                {
                    strata[edge] = strata.GetValueOrDefault(edge) + plan.Renderings.Length;
                }
            }

            strata[Strata.Password] = strata.GetValueOrDefault(Strata.Password) + plan.PasswordVariants;
            strata[Strata.Bundle] = strata.GetValueOrDefault(Strata.Bundle) + plan.Bundles;
        }

        return (strata, documents, (double)narrative / documents);
    }

    private static string? EdgeFor(Plan plan, int truth) => truth % EdgeEvery == 1 && plan.Edges.Length > 0 ? plan.Edges[truth / EdgeEvery % plan.Edges.Length] : null;

    /// <param name="constructs">Generate the ADR 0018 constructs plan instead of the corpus v3 plan.</param>
    /// <param name="v4">Generate corpus v4: the v3 plan followed by the constructs plan.</param>
    /// <param name="v5">Generate corpus v5: corpus v4 followed by the measures-as-rows construct (c10).</param>
    public static CorpusManifest Generate(string outDir, int seed, Action<string>? log = null, bool constructs = false, bool v4 = false, bool v5 = false)
    {
        PdfFonts.Configure();
        Directory.CreateDirectory(Path.Combine(outDir, "docs"));
        Directory.CreateDirectory(Path.Combine(outDir, "truth"));
        var random = new Random(seed);
        var truths = new TruthFactory(random);
        var manifest = new CorpusManifest { Seed = seed, GeneratedAt = DateTimeOffset.UtcNow };
        var v3 = new Random(seed * 7919 + 13); // corpus v3 fields: a stream of their own (see Version)
        var contexts = new Dictionary<LossRunDocument, SubmissionTruth>(ReferenceEqualityComparer.Instance);

        foreach (var plan in constructs ? [.. ConstructPlans, .. V5Plans] : v5 ? [.. Plans, .. ConstructPlans, .. V5Plans] : v4 ? [.. Plans, .. ConstructPlans] : Plans)
        {
            var logical = 0;
            void Emit(LossRunDocument truth, List<string> strata, string inputType, Rendered rendered, byte[] bytes, int pages, List<TruthSegment>? others = null,
                string? password = null, int lossRunPages = 0)
            {
                var id = $"{plan.Carrier.Key}-{logical:000}-{inputType}{(password is null ? "" : "-locked")}{(others is null ? "" : "-bundle")}";
                var file = $"{id}.{rendered.Extension}";
                File.WriteAllBytes(Path.Combine(outDir, "docs", file), bytes);
                var record = new TruthRecord
                {
                    Id = id, File = file, Carrier = plan.Carrier.Key, Layout = plan.Carrier.Layout, InputType = inputType, LogicalId = $"{plan.Carrier.Key}-{logical:000}",
                    Strata = strata, SourceConsistent = !strata.Contains(Strata.SourceInconsistent), Password = password, Pages = pages,
                    LossRunSegment = new TruthSegment("LossRun", 1, lossRunPages > 0 ? lossRunPages : pages), OtherSegments = others ?? [], Printed = rendered.Printed,
                    Document = truth, Context = contexts.GetValueOrDefault(truth),
                };
                File.WriteAllText(Path.Combine(outDir, "truth", id + ".json"), JsonSerializer.Serialize(record, CanonicalJson.Options));
                manifest.Entries.Add(new ManifestEntry(id, file, plan.Carrier.Key, plan.Carrier.Layout, inputType, record.LogicalId, strata,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength, pages));
            }

            LossRunDocument NewTruth(List<string> strata)
            {
                var truth = truths.Create(plan.Carrier, $"{plan.Carrier.Key}-{logical:000}", strata);
                if (strata.Contains(Strata.SourceInconsistent))
                {
                    // A printing error in the source: the first period's medical paid subtotal is 100.00 too high.
                    var s = truth.PolicyPeriods[0].Subtotals!;
                    s.Medical = new MoneyTriplet(s.Medical!.Paid + 100m, s.Medical.Reserve, s.Medical.Incurred + 100m);
                    s.Total = new MoneyTriplet(s.Total!.Paid + 100m, s.Total.Reserve, s.Total.Incurred + 100m);
                }

                TruthFactory.EnrichV3(truth, v3);
                truth = plan.Transform?.Invoke(truth) ?? truth;
                contexts[truth] = TruthFactory.Context(truth, v3);
                return truth;
            }

            for (var i = 0; i < plan.Truths; i++, logical++)
            {
                var strata = EdgeFor(plan, i) is { } edge ? [edge] : new List<string>();
                if (plan.Construct is { } construct)
                {
                    strata.Add(construct);
                }

                var truth = NewTruth(strata);
                var native = Renderers.Render(plan.Carrier, truth);
                TruthFactory.KeepOtherLines(truth);
                foreach (var inputType in plan.Renderings)
                {
                    var bytes = inputType is InputTypes.NativePdf or InputTypes.Xlsx or InputTypes.Csv ? native.Bytes : Renderers.Scan(native.Bytes, inputType, random);
                    Emit(truth, strata, inputType, native, bytes, native.Pages);
                }

                log?.Invoke($"{plan.Carrier.Key} #{logical}: {truth.PolicyPeriods.Sum(p => p.Claims.Count)} claims, {native.Pages} page(s), strata [{string.Join(",", strata)}]");
            }

            for (var i = 0; i < plan.PasswordVariants; i++, logical++)
            {
                List<string> strata = [Strata.Password];
                var truth = NewTruth(strata);
                var native = Renderers.Render(plan.Carrier, truth);
                Emit(truth, strata, InputTypes.NativePdf, native, Renderers.Lock(native.Bytes, Password), native.Pages, password: Password);
            }

            for (var i = 0; i < plan.Bundles; i++, logical++)
            {
                List<string> strata = [Strata.Bundle];
                var truth = NewTruth(strata);
                var native = Renderers.Render(plan.Carrier, truth);
                var (bytes, others, pages) = Renderers.Bundle(native.Bytes, truth);
                Emit(truth, strata, InputTypes.NativePdf, native, bytes, pages, others, lossRunPages: native.Pages);
            }
        }

        File.WriteAllText(Path.Combine(outDir, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = true }));
        return manifest;
    }
}
