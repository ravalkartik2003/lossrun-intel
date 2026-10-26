using System.Globalization;
using System.Text;
using System.Text.Json;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Formats.Replay;
using LossRun.Ingestion;
using LossRun.Ingestion.Metering;
using LossRun.Learning;
using LossRun.Rules;

namespace LossRun.Evaluation;

/// <summary>One study's output: the decision it supports, a Markdown section for the report, the data behind it and what it spent.</summary>
public sealed record StudyResult(string Name, string Decision, string Markdown, object Data, decimal SpendUsd);

/// <summary>Field accuracy of one replay against truth (claim fields and the requirement's key fields).</summary>
public sealed record Accuracy(int Fields, int Correct, int KeyFields, int KeyCorrect, int TruthClaims, int MatchedClaims, int PredictedClaims, int HardFailures)
{
    public double FieldShare => Fields == 0 ? 0 : (double)Correct / Fields;
    public double KeyShare => KeyFields == 0 ? 0 : (double)KeyCorrect / KeyFields;
    public bool Clean => HardFailures == 0 && Fields > 0 && Correct == Fields;

    public static Accuracy Sum(IEnumerable<Accuracy> all)
    {
        var list = all.ToList();
        return new Accuracy(list.Sum(a => a.Fields), list.Sum(a => a.Correct), list.Sum(a => a.KeyFields), list.Sum(a => a.KeyCorrect), list.Sum(a => a.TruthClaims),
            list.Sum(a => a.MatchedClaims), list.Sum(a => a.PredictedClaims), list.Sum(a => a.HardFailures));
    }
}

/// <summary>
/// <c>lossrun eval study &lt;name&gt;</c> (brief 08 §5): the measurements earlier briefs deferred, run over a finished
/// <c>eval run</c> (its graphs, specs and results), so most variants are replays (no model, no layout call). Only the DI
/// second opinion, the page-image and multi-exemplar learns and the embeddings spend money; each study reports its spend.
/// </summary>
public static class Studies
{
    public static readonly string[] Names = ["scan-robustness", "inter-carrier-cosine", "classifier-skip", "multi-exemplar-learn", "cost-by-path", "learn-reliability", "first-seen"];

    public static async Task<int> RunAsync(string name, string corpus, string results, string outDir, Func<string, string?> option)
    {
        var context = new StudyContext(corpus, results, option);
        var names = name == "all" ? Names : [name];
        if (names.Any(n => !Names.Contains(n)))
        {
            Console.Error.WriteLine($"usage: lossrun eval study {string.Join("|", Names)}|all --corpus <dir> --results <run dir> --out <dir>");
            return 2;
        }

        Directory.CreateDirectory(outDir);
        foreach (var n in names)
        {
            var result = n switch
            {
                "scan-robustness" => await ScanRobustnessAsync(context).ConfigureAwait(false),
                "inter-carrier-cosine" => await InterCarrierCosineAsync(context).ConfigureAwait(false),
                "classifier-skip" => ClassifierSkip(context),
                "multi-exemplar-learn" => await MultiExemplarAsync(context).ConfigureAwait(false),
                "learn-reliability" => await LearnReliabilityAsync(context).ConfigureAwait(false),
                "first-seen" => await FirstSeenAsync(context).ConfigureAwait(false),
                _ => CostByPath(context),
            };
            File.WriteAllText(Path.Combine(outDir, $"{n}.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = true }));
            File.WriteAllText(Path.Combine(outDir, $"{n}.md"), result.Markdown);
            Console.WriteLine($"{n}: {result.Decision} (spend ${result.SpendUsd:0.0000})");
        }

        return 0;
    }

    // ------------------------------------------------------------------ scan-robustness (06 answers 6–7)

    public sealed record ScanRow(string Doc, string Carrier, string InputType, string Spec, double MeanOcrConfidence, int Pages, IReadOnlyDictionary<string, Accuracy> Variants,
        IReadOnlyList<string> Signals, decimal DiCostUsd, string? Error);

    public sealed record PageImageRow(string Doc, string InputType, bool ImageOn, bool Published, int ModelCalls, decimal CostUsd, Accuracy? Accuracy, string? Error);

    private static async Task<StudyResult> ScanRobustnessAsync(StudyContext ctx)
    {
        var perCarrier = ctx.Int("--per-carrier", 12);
        var withDi = ctx.Option("--no-di") is null;
        var pageImageDocs = ctx.Int("--page-image", 6);
        var registry = await ctx.RegistryAsync().ConfigureAwait(false);
        var pdfCarriers = Carriers.All.Where(c => !c.Layout.StartsWith("xlsx", StringComparison.Ordinal) && !c.Layout.StartsWith("csv", StringComparison.Ordinal)).Select(c => c.Key).ToList();
        var secondOpinion = ctx.SecondOpinion();
        var rows = new List<ScanRow>();
        decimal spend = 0;

        foreach (var carrier in pdfCarriers)
        {
            var spec = await ctx.CarrierSpecAsync(registry, carrier).ConfigureAwait(false);
            var candidates = ctx.Manifest.Entries.Where(e => e.Carrier == carrier && InputTypes.IsScan(e.InputType) && ctx.SingleGraph(e) is not null)
                .GroupBy(e => e.InputType).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.ToList()).ToList();
            var picked = RoundRobin(candidates, perCarrier);
            foreach (var entry in picked)
            {
                var truth = CorpusManifest.Truth(ctx.Corpus, entry.Id);
                var graph = ctx.SingleGraph(entry)!;
                if (spec is null)
                {
                    rows.Add(new ScanRow(entry.Id, carrier, entry.InputType, "none", 0, graph.Pages.Count, new Dictionary<string, Accuracy>(), [], 0, "no published spec for the carrier"));
                    continue;
                }

                var submission = new SubmissionContext(EvalRunner.CarrierName(carrier));
                var asRun = ReplayEngine.Execute(graph, spec, submission: submission);
                var variants = new Dictionary<string, Accuracy>
                {
                    ["cu+fallback"] = Measure(entry, truth, asRun),
                    ["cu-tables-only"] = Measure(entry, truth, ReplayEngine.Execute(graph, WithFallback(spec, false), submission: submission)),
                    ["line-fallback-only"] = Measure(entry, truth, ReplayEngine.Execute(WithoutTables(graph), WithFallback(spec, true), submission: submission)),
                };

                decimal diCost = 0;
                string? error = null;
                if (withDi)
                {
                    // DI tables instead of CU tables on every page (the upper bound of "second opinion on"): strip CU tables, merge DI's.
                    try
                    {
                        var di = WithoutTables(graph);
                        var pdf = await File.ReadAllBytesAsync(CorpusManifest.DocumentPath(ctx.Corpus, entry.File)).ConfigureAwait(false);
                        var usage = new List<UsageEvent>();
                        foreach (var page in di.Pages.Select(p => p.Number))
                        {
                            var merged = await secondOpinion.MergePageAsync(di, pdf, page, $"study-{entry.Id}", CanonicalData.Glossary).ConfigureAwait(false);
                            usage.AddRange(merged.Usage);
                        }

                        diCost = Cost(entry.Id, usage);
                        variants["di-second-opinion"] = Measure(entry, truth, ReplayEngine.Execute(di, spec, submission: submission));
                    }
                    catch (Exception e) when (e is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException)
                    {
                        error = $"DI: {e.GetType().Name}: {e.Message}";
                    }
                }

                spend += diCost;
                var confidence = WordConfidence.Mean(graph.Words) ?? 1.0; // brief 31: unknown confidences are left out
                var signals = new List<string>();
                if (asRun.Stats.ClaimRows == 0)
                {
                    signals.Add("zero-rows");
                }

                if (asRun.RuleOutcomes.Any(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard))
                {
                    signals.Add("hard-failure");
                }

                if (asRun.Stats.LineFallbackRows > 0)
                {
                    signals.Add("fallback-rows");
                }

                if (confidence < 0.9)
                {
                    signals.Add("ocr-confidence<0.9");
                }

                rows.Add(new ScanRow(entry.Id, carrier, entry.InputType, $"{spec.FormatId}@{spec.Version}", Math.Round(confidence, 3), graph.Pages.Count, variants, signals, diCost, error));
                Console.WriteLine($"  scan {entry.Id}: {string.Join(" · ", variants.Select(v => $"{v.Key} {v.Value.FieldShare:P1}"))}{(signals.Count > 0 ? " · " + string.Join(",", signals) : "")}");
            }
        }

        // Trigger policies: DI replaces CU on a document when the signal fires (cost = that document's DI pages).
        var scored = rows.Where(r => r.Variants.ContainsKey("di-second-opinion")).ToList();
        var triggers = new[] { "never", "always", "zero-rows", "hard-failure", "fallback-rows", "ocr-confidence<0.9" };
        var policies = triggers.Select(t =>
        {
            bool Fires(ScanRow r) => t == "always" || t != "never" && r.Signals.Contains(t);
            var chosen = scored.Select(r => Fires(r) ? r.Variants["di-second-opinion"] : r.Variants["cu+fallback"]).ToList();
            var sum = Accuracy.Sum(chosen);
            return new { Trigger = t, Fired = scored.Count(Fires), Accuracy = sum, CleanDocs = chosen.Count(a => a.Clean), DiCostUsd = scored.Where(Fires).Sum(r => r.DiCostUsd) };
        }).ToList();
        var baseline = policies[0];
        var best = policies.Where(p => p.Trigger != "never").OrderByDescending(p => p.CleanDocs).ThenByDescending(p => p.Accuracy.FieldShare).ThenBy(p => p.DiCostUsd).FirstOrDefault();
        var triggerDecision = best is null || scored.Count == 0
            ? "second opinion stays off: DI was not measured"
            : best.CleanDocs > baseline.CleanDocs
                ? $"trigger the DI second opinion on \"{best.Trigger}\": {best.CleanDocs}/{scored.Count} documents fully correct vs {baseline.CleanDocs} without, for ${best.DiCostUsd:0.00} of DI over {best.Fired} documents"
                : $"second opinion stays off: no trigger raised fully-correct documents above {baseline.CleanDocs}/{scored.Count}";

        // Page image on/off: learn the same scans twice on an empty registry.
        var imageRows = new List<PageImageRow>();
        var imageDocs = rows.Where(r => r.Error is null && !Carriers.Seen.Contains(r.Carrier)).GroupBy(r => r.InputType)
            .SelectMany(g => g.Take(Math.Max(1, pageImageDocs / 3))).Take(pageImageDocs).ToList();
        foreach (var row in imageDocs)
        {
            var entry = ctx.Manifest.Entries.First(e => e.Id == row.Doc);
            var truth = CorpusManifest.Truth(ctx.Corpus, entry.Id);
            var pdf = await File.ReadAllBytesAsync(CorpusManifest.DocumentPath(ctx.Corpus, entry.File)).ConfigureAwait(false);
            foreach (var on in new[] { false, true })
            {
                var (outcome, cost, error) = await ctx.LearnAsync(ctx.SingleGraph(entry)!, entry, on, pdf, null).ConfigureAwait(false);
                spend += cost;
                var spec = outcome?.Spec ?? outcome?.Attempts.LastOrDefault(a => a.Spec is not null)?.Spec;
                var accuracy = spec is null ? null : Measure(entry, truth, ReplayEngine.Execute(ctx.SingleGraph(entry)!, spec, submission: new SubmissionContext(EvalRunner.CarrierName(entry.Carrier))));
                imageRows.Add(new PageImageRow(entry.Id, entry.InputType, on, outcome?.Published == true, outcome?.ModelCalls ?? 0, cost, accuracy, error));
                Console.WriteLine($"  page image {(on ? "on " : "off")} {entry.Id}: published {outcome?.Published} · calls {outcome?.ModelCalls} · ${cost:0.0000}");
            }
        }

        var off = imageRows.Where(r => !r.ImageOn).ToList();
        var onRows = imageRows.Where(r => r.ImageOn).ToList();
        var imageDecision = imageRows.Count == 0
            ? "page image: not measured"
            : onRows.Count(r => r.Published) > off.Count(r => r.Published) || Accuracy.Sum(onRows.Select(r => r.Accuracy).OfType<Accuracy>()).FieldShare > Accuracy.Sum(off.Select(r => r.Accuracy).OfType<Accuracy>()).FieldShare + 0.01
                ? $"keep the page-image flag and turn it on for scans: published {onRows.Count(r => r.Published)}/{onRows.Count} with vs {off.Count(r => r.Published)}/{off.Count} without"
                : $"drop the page image: published {onRows.Count(r => r.Published)}/{onRows.Count} with vs {off.Count(r => r.Published)}/{off.Count} without, at {(off.Sum(r => r.CostUsd) == 0 ? 0 : onRows.Sum(r => r.CostUsd) / off.Sum(r => r.CostUsd)):0.0}× the learn cost";

        var md = new StringBuilder();
        md.AppendLine("### Scan robustness (06 answers 6–7)").AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"{rows.Count} scans ({string.Join(", ", rows.GroupBy(r => r.Carrier).Select(g => $"{g.Key} {g.Count()}"))}) across {string.Join(", ", rows.Select(r => r.InputType).Distinct().Order())}; each replayed with the carrier's spec four ways. Claim-field accuracy (all printed claim, period and total fields) and key-field accuracy (claim #, DOI, indemnity/medical paid and reserve):").AppendLine();
        md.AppendLine("| Rendering | Variant | Docs | Field accuracy | Key-field accuracy | Fully correct docs | Claims read / truth |");
        md.AppendLine("|---|---|---:|---:|---:|---:|---:|");
        foreach (var group in rows.Where(r => r.Variants.Count > 0).GroupBy(r => r.InputType).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            foreach (var variant in group.SelectMany(r => r.Variants.Keys).Distinct())
            {
                var list = group.Where(r => r.Variants.ContainsKey(variant)).Select(r => r.Variants[variant]).ToList();
                var sum = Accuracy.Sum(list);
                md.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {variant} | {list.Count} | {sum.FieldShare:P1} | {sum.KeyShare:P1} | {list.Count(a => a.Clean)} | {sum.PredictedClaims}/{sum.TruthClaims} |");
            }
        }

        md.AppendLine().AppendLine("Second-opinion trigger policies (DI replaces CU tables on the documents where the signal fires):").AppendLine();
        md.AppendLine("| Trigger | Docs fired | Field accuracy | Fully correct docs | DI cost |");
        md.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var p in policies)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {p.Trigger} | {p.Fired} | {p.Accuracy.FieldShare:P1} | {p.CleanDocs}/{scored.Count} | ${p.DiCostUsd:0.00} |");
        }

        if (imageRows.Count > 0)
        {
            md.AppendLine().AppendLine("Page image on vs off (fresh registry, same scan learned twice):").AppendLine();
            md.AppendLine("| Doc | Image | Published | Calls | Cost | Field accuracy of the spec |");
            md.AppendLine("|---|---|---|---:|---:|---:|");
            foreach (var r in imageRows)
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"| {r.Doc} | {(r.ImageOn ? "on" : "off")} | {(r.Published ? "yes" : "no")} | {r.ModelCalls} | ${r.CostUsd:0.0000} | {(r.Accuracy is null ? "—" : r.Accuracy.FieldShare.ToString("P1", CultureInfo.InvariantCulture))} |");
            }
        }

        md.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"**Decision (06 answer 6):** {triggerDecision}.").AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"**Decision (06 answer 7):** {imageDecision}.");
        return new StudyResult("scan-robustness", $"{triggerDecision}; {imageDecision}", md.ToString(), new { rows, policies, imageRows }, spend);
    }

    private static List<ManifestEntry> RoundRobin(List<List<ManifestEntry>> groups, int take)
    {
        var picked = new List<ManifestEntry>();
        for (var i = 0; picked.Count < take && groups.Any(g => g.Count > i); i++)
        {
            picked.AddRange(groups.Where(g => g.Count > i).Select(g => g[i]).Take(take - picked.Count));
        }

        return picked;
    }

    private static CarrierFormat WithFallback(CarrierFormat spec, bool enabled)
    {
        var clone = FormatJson.Parse(FormatJson.Write(spec));
        clone.TableLocator.LineFallback = clone.TableLocator.LineFallback ?? new LineFallback();
        clone.TableLocator.LineFallback.Enabled = enabled;
        return clone;
    }

    private static DocumentGraph WithoutTables(DocumentGraph graph)
    {
        var clone = JsonSerializer.Deserialize<DocumentGraph>(JsonSerializer.Serialize(graph, CanonicalJson.Options), CanonicalJson.Options)!;
        clone.Tables.Clear();
        clone.Cells.Clear();
        return clone;
    }

    // ------------------------------------------------------------------ inter-carrier-cosine (05 answer 2)

    private static async Task<StudyResult> InterCarrierCosineAsync(StudyContext ctx)
    {
        var perCarrier = ctx.Int("--per-carrier", 6);
        var registry = await ctx.RegistryAsync().ConfigureAwait(false);
        var meter = new InMemoryCostMeter();
        var embeddings = ctx.Embeddings(meter);
        var docs = new List<(string Carrier, ManifestEntry Entry, DocumentGraph Graph, float[] Vector)>();
        foreach (var carrier in Carriers.All.Select(c => c.Key))
        {
            var groups = ctx.Manifest.Entries.Where(e => e.Carrier == carrier && ctx.SingleGraph(e) is not null).GroupBy(e => e.InputType)
                .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.ToList()).ToList();
            foreach (var entry in RoundRobin(groups, perCarrier))
            {
                var graph = ctx.SingleGraph(entry)!;
                var embedded = await embeddings.EmbedAsync(FingerprintText.ForEmbedding(graph), entry.Id, "study-cosine", CancellationToken.None).ConfigureAwait(false);
                docs.Add((carrier, entry, graph, embedded.Vector));
            }
        }

        var carriers = docs.Select(d => d.Carrier).Distinct().Order(StringComparer.Ordinal).ToList();
        var centroids = carriers.ToDictionary(c => c, c => Vectors.Centroid([.. docs.Where(d => d.Carrier == c).Select(d => (IReadOnlyList<float>)d.Vector)]));
        var identity = new Dictionary<string, string>();
        foreach (var c in carriers)
        {
            var spec = await ctx.CarrierSpecAsync(registry, c).ConfigureAwait(false);
            identity[c] = spec is { Fingerprint.IdentityText.Length: > 0 } ? spec.Fingerprint.IdentityText : FingerprintText.Compute(docs.First(d => d.Carrier == c).Graph).IdentityText;
        }

        // Doc → other-carrier centroid: a cosine-only false hit clears the 0.95 embedding threshold; the identity guard also needs ≥ 0.8 containment.
        var pairs = docs.SelectMany(d => carriers.Where(c => c != d.Carrier).Select(c => new
        {
            Doc = d.Entry.Id, From = d.Carrier, To = c, Cosine = Math.Round(Vectors.Cosine(d.Vector, centroids[c]), 4),
            Identity = Math.Round(FingerprintText.Containment(identity[c], FingerprintText.PageTokens(d.Graph)), 3),
        })).ToList();
        var own = docs.Select(d => new { Doc = d.Entry.Id, d.Carrier, d.Entry.InputType, Cosine = Math.Round(Vectors.Cosine(d.Vector, centroids[d.Carrier]), 4),
            Identity = Math.Round(FingerprintText.Containment(identity[d.Carrier], FingerprintText.PageTokens(d.Graph)), 3) }).ToList();
        var cosineHits = pairs.Where(p => p.Cosine >= 0.95).ToList();
        var guardedHits = cosineHits.Where(p => p.Identity >= FormatFingerprinter.IdentityGuardThreshold).ToList();
        var maxInter = pairs.MaxBy(p => p.Cosine)!;
        var minOwn = own.MinBy(o => o.Cosine)!;
        var spend = Cost("study-cosine", meter.Events);

        var md = new StringBuilder("### Inter-carrier cosine (05 answer 2)\n\n");
        md.AppendLine(CultureInfo.InvariantCulture, $"{docs.Count} documents ({perCarrier} per carrier across renderings), `{FingerprintText.Normalization}` text of the first page, embedded with the run's embedding model; carrier centroids are the mean of each carrier's documents. Mean cosine, document vs carrier centroid:").AppendLine();
        md.AppendLine("| from \\ to | " + string.Join(" | ", carriers) + " |");
        md.AppendLine("|---|" + string.Concat(carriers.Select(_ => "---:|")));
        foreach (var from in carriers)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {from} | {string.Join(" | ", carriers.Select(to => docs.Where(d => d.Carrier == from).Average(d => Vectors.Cosine(d.Vector, centroids[to])).ToString("0.000", CultureInfo.InvariantCulture)))} |");
        }

        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"- Highest inter-carrier cosine: **{maxInter.Cosine:0.000}** ({maxInter.Doc} → {maxInter.To}), i.e. a minimum inter-carrier distance of {1 - maxInter.Cosine:0.000}.");
        md.AppendLine(CultureInfo.InvariantCulture, $"- Lowest own-carrier cosine: **{minOwn.Cosine:0.000}** ({minOwn.Doc}, {minOwn.InputType}); own-carrier identity containment min {own.Min(o => o.Identity):0.00}.");
        md.AppendLine(CultureInfo.InvariantCulture, $"- Cosine-only false hits (≥ 0.95 to another carrier): **{cosineHits.Count}**; after the identity guard (containment ≥ {FormatFingerprinter.IdentityGuardThreshold}): **{guardedHits.Count}**.");
        var decision = guardedHits.Count == 0
            ? $"keep the 0.95 embedding threshold with the identity guard: 0 false hits after the guard ({cosineHits.Count} before); minimum inter-carrier distance {1 - maxInter.Cosine:0.000}"
            : $"tighten matching: {guardedHits.Count} false hit(s) survive the identity guard";
        md.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"**Decision:** {decision}. Spreadsheets are matched on header/identity keys, not embeddings; they are in the matrix for completeness.");
        return new StudyResult("inter-carrier-cosine", decision, md.ToString(), new { own, pairs, identity }, spend);
    }

    // ------------------------------------------------------------------ classifier-skip (04 answer 3)

    private static StudyResult ClassifierSkip(StudyContext ctx)
    {
        var rows = new List<(ManifestEntry Entry, bool Bundle, bool Skipped, double HeaderResolution, bool Correct, decimal ClassifierCost)>();
        foreach (var entry in ctx.Manifest.Entries)
        {
            if (ctx.Run(entry.Id) is not { Error: null } run || run.ClassifierDecisions.Count == 0)
            {
                continue;
            }

            var truth = CorpusManifest.Truth(ctx.Corpus, entry.Id);
            var bundle = truth.OtherSegments.Count > 0;
            var check = run.ClassifierDecisions[0];
            var predicted = ctx.Predicted(entry, truth);
            var correct = check.Skipped
                ? !bundle && predicted is not null
                : bundle ? predicted is { FirstPage: { } f, LastPage: { } l } && f == truth.LossRunSegment.StartPage && l == truth.LossRunSegment.EndPage : predicted is not null;
            var classifierCost = Cost(entry.Id, run.Usage.Where(u => u.Stage.Contains("classif", StringComparison.OrdinalIgnoreCase)));
            rows.Add((entry, bundle, check.Skipped, check.HeaderResolution, correct, classifierCost));
        }

        var md = new StringBuilder("### Classifier skip (04 answer 3)\n\n");
        md.AppendLine("The first-page check skips the CU classifier when a single loss-run table header resolves on page 1. Correct = a skipped document was a single loss run that was extracted; a classified bundle split exactly at the loss-run segment; a classified single document was found.").AppendLine();
        md.AppendLine("| Population | Docs | Skipped | Skip rate | Correct when skipped | Correct when classified | Mean classifier cost when run |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        void Row(string label, IReadOnlyCollection<(ManifestEntry Entry, bool Bundle, bool Skipped, double HeaderResolution, bool Correct, decimal ClassifierCost)> set)
        {
            var skipped = set.Where(r => r.Skipped).ToList();
            var classified = set.Where(r => !r.Skipped).ToList();
            md.AppendLine(CultureInfo.InvariantCulture, $"| {label} | {set.Count} | {skipped.Count} | {(set.Count == 0 ? 0 : (double)skipped.Count / set.Count):P0} | {skipped.Count(r => r.Correct)}/{skipped.Count} | {classified.Count(r => r.Correct)}/{classified.Count} | ${(classified.Count == 0 ? 0 : classified.Average(r => r.ClassifierCost)):0.0000} |");
        }

        Row("single documents", [.. rows.Where(r => !r.Bundle)]);
        Row("bundles", [.. rows.Where(r => r.Bundle)]);
        foreach (var bucket in rows.GroupBy(r => InputTypes.Bucket(r.Entry.InputType)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Row($"— {bucket.Key}", [.. bucket]);
        }

        var falseSkips = rows.Count(r => r.Skipped && r.Bundle);
        var skipRate = rows.Count == 0 ? 0 : (double)rows.Count(r => r.Skipped) / rows.Count;
        var decision = falseSkips == 0
            ? $"keep the first-page skip: {skipRate:P0} of documents skip the classifier and no bundle was skipped"
            : $"tighten the first-page skip: {falseSkips} bundle(s) skipped the classifier";
        md.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"**Decision:** {decision}.");
        return new StudyResult("classifier-skip", decision, md.ToString(),
            rows.Select(r => new { r.Entry.Id, r.Entry.InputType, r.Bundle, r.Skipped, r.HeaderResolution, r.Correct, r.ClassifierCost }).ToList(), 0);
    }

    // ------------------------------------------------------------------ multi-exemplar-learn (06 answer 5)

    public sealed record ExemplarRow(string Carrier, string Seed, IReadOnlyList<string> Exemplars, int HeldOut, int PassWithout, int PassWith, bool PublishedWith, int CallsWith,
        decimal CostWith, string? Note);

    private static async Task<StudyResult> MultiExemplarAsync(StudyContext ctx)
    {
        var exemplarCount = ctx.Int("--exemplars", 2);
        var registry = await ctx.RegistryAsync().ConfigureAwait(false);
        var rows = new List<ExemplarRow>();
        decimal spend = 0;
        foreach (var carrier in Carriers.All.Select(c => c.Key).Where(c => !Carriers.Seen.Contains(c)))
        {
            var entries = ctx.Manifest.Entries.Where(e => e.Carrier == carrier && ctx.SingleGraph(e) is not null).ToList();
            // Seed: the document the run learned the carrier from (else its first learn attempt), so both arms start where the run did.
            var seed = entries.FirstOrDefault(e => ctx.Predicted(e, CorpusManifest.Truth(ctx.Corpus, e.Id)) is { LearnPublished: true })
                ?? entries.FirstOrDefault(e => ctx.Predicted(e, CorpusManifest.Truth(ctx.Corpus, e.Id)) is { Attempts.Count: > 0 });
            if (seed is null)
            {
                rows.Add(new ExemplarRow(carrier, "-", [], 0, 0, 0, false, 0, 0, "no learn in the run"));
                continue;
            }

            // Exemplars: other renderings first (a native learn should also hold on scans), then other documents of the same rendering.
            var others = entries.Where(e => e.Id != seed.Id).OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
            var exemplars = others.Where(e => e.InputType != seed.InputType).DistinctBy(e => e.InputType)
                .Concat(others.Where(e => e.InputType == seed.InputType && e.LogicalId != seed.LogicalId)).Take(exemplarCount).ToList();
            var heldOut = others.Where(e => !exemplars.Contains(e)).ToList();
            var pdf = seed.File.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? await File.ReadAllBytesAsync(CorpusManifest.DocumentPath(ctx.Corpus, seed.File)).ConfigureAwait(false) : null;

            // Both arms are fresh learns from the same seed; only the exemplar check differs.
            var (plain, plainCost, plainError) = await ctx.LearnAsync(ctx.SingleGraph(seed)!, seed, false, pdf, null).ConfigureAwait(false);
            var (checkedOutcome, checkedCost, checkedError) = await ctx.LearnAsync(ctx.SingleGraph(seed)!, seed, false, pdf, [.. exemplars.Select(e => ctx.SingleGraph(e)!)])
                .ConfigureAwait(false);
            spend += plainCost + checkedCost;

            bool Passes(CarrierFormat spec, ManifestEntry e) =>
                SpecVerifier.Verify(ctx.SingleGraph(e)!, spec, new SubmissionContext(EvalRunner.CarrierName(e.Carrier))).Gate() is not null;
            int PassCount(LearnOutcome? o) => o is { Published: true, Spec: { } spec } ? heldOut.Count(e => Passes(spec, e)) : 0;
            var passWithout = PassCount(plain);
            var passWith = PassCount(checkedOutcome);
            rows.Add(new ExemplarRow(carrier, seed.Id, [.. exemplars.Select(e => e.Id)], heldOut.Count, passWithout, passWith, checkedOutcome?.Published == true,
                checkedOutcome?.ModelCalls ?? 0, checkedCost,
                $"without: {(plain?.Published == true ? "published" : "not published")} in {plain?.ModelCalls ?? 0} call(s), ${plainCost:0.0000}{(plainError is null ? "" : $"; {plainError}")}{(checkedError is null ? "" : $"; with: {checkedError}")}"));
            Console.WriteLine($"  multi-exemplar {carrier}: without {passWithout}/{heldOut.Count} (published {plain?.Published}) · with {passWith}/{heldOut.Count} (published {checkedOutcome?.Published}) · ${plainCost + checkedCost:0.0000}");
        }

        var md = new StringBuilder("### Multi-exemplar learn (06 answer 5)\n\n");
        md.AppendLine(CultureInfo.InvariantCulture, $"Each unseen carrier, two fresh learns from the same seed (the document the run learned the carrier from): **without** exemplars, and **with** `LearnRequest.Exemplars` = {exemplarCount} other documents (other renderings first), publishing only a spec that verifies on all of them. Each published spec is then verified (replay only, learn gate) on every remaining document of the carrier; an unpublished learn scores 0 (its documents would go to Review).").AppendLine();
        md.AppendLine("| Carrier | Seed | Exemplars | Held-out docs | Gate passes without | Gate passes with | Published with | Calls with | Cost with | Without |");
        md.AppendLine("|---|---|---|---:|---:|---:|---|---:|---:|---|");
        foreach (var r in rows)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {r.Carrier} | {r.Seed} | {string.Join(", ", r.Exemplars)} | {r.HeldOut} | {r.PassWithout} | {r.PassWith} | {(r.PublishedWith ? "yes" : "no")} | {r.CallsWith} | ${r.CostWith:0.0000} | {r.Note} |");
        }

        var (sumWithout, sumWith, total) = (rows.Sum(r => r.PassWithout), rows.Sum(r => r.PassWith), rows.Sum(r => r.HeldOut));
        var decision = sumWith > sumWithout
            ? $"turn the multi-exemplar check on when other documents of the format are at hand: held-out gate passes {sumWith}/{total} with vs {sumWithout}/{total} without"
            : $"keep the multi-exemplar check optional (off by default): held-out gate passes {sumWith}/{total} with vs {sumWithout}/{total} without";
        md.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"**Decision:** {decision}.");
        return new StudyResult("multi-exemplar-learn", decision, md.ToString(), rows, spend);
    }

    // ------------------------------------------------------------------ learn-reliability (08 Answers 5)

    public sealed record LearnTrial(string Carrier, string Doc, string FileKind, bool Published, int Iterations, decimal CostUsd, IReadOnlyList<string> AttemptSummaries,
        IReadOnlyList<string> LintFindings, string? Error);

    private static async Task<StudyResult> LearnReliabilityAsync(StudyContext ctx)
    {
        var trials = ctx.Int("--trials", 10);
        var maxIterations = ctx.Int("--max-iterations", 5);
        var carriers = (ctx.Option("--carriers") ?? "c1,c2,c4,c5").Split(',');
        var work = new List<(string Carrier, ManifestEntry Entry)>();
        foreach (var carrier in carriers)
        {
            // Native rendering: the native PDF (single documents, not bundle segments) or the spreadsheet; one trial per document.
            work.AddRange(ctx.Manifest.Entries.Where(e => e.Carrier == carrier && e.InputType is InputTypes.NativePdf or InputTypes.Xlsx or InputTypes.Csv
                    && !e.Strata.Contains(Strata.Password) && ctx.SingleGraph(e) is not null)
                .OrderBy(e => e.Id, StringComparer.Ordinal).Take(trials).Select(e => (carrier, e)));
        }

        var results = new System.Collections.Concurrent.ConcurrentBag<LearnTrial>();
        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = ctx.Int("--concurrency", 4) }, async (item, _) =>
        {
            var graph = ctx.SingleGraph(item.Entry)!;
            var (outcome, cost, error) = await ctx.LearnAsync(graph, item.Entry, false, null, null, maxIterations).ConfigureAwait(false);
            var lint = outcome?.Attempts.Where(a => a.Verification is not null).SelectMany(a => a.Verification!.Issues.Where(i => i.StartsWith("lint:", StringComparison.Ordinal)))
                .ToList() ?? [];
            var trial = new LearnTrial(item.Carrier, item.Entry.Id, graph.SourceFile.Kind is FileKind.Pdf ? "pdf" : "spreadsheet", outcome?.Published == true,
                outcome?.ModelCalls ?? 0, cost, [.. outcome?.Attempts.Select(a => a.Verification?.Summary ?? a.Error ?? "?") ?? []], lint, error);
            results.Add(trial);
            if ((outcome?.Spec ?? outcome?.Attempts.LastOrDefault(a => a.Spec is not null)?.Spec) is { } spec && ctx.Option("--save-specs") is { } dir)
            {
                Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(Path.Combine(dir, $"{item.Entry.Id}.{(trial.Published ? "published" : "last")}.json"), FormatJson.Write(spec)).ConfigureAwait(false);
            }

            Console.WriteLine($"  learn {item.Entry.Id}: {(trial.Published ? $"published at iteration {trial.Iterations}" : $"not published in {trial.Iterations}")} · ${cost:0.0000}");
        }).ConfigureAwait(false);

        var rows = results.OrderBy(r => r.Carrier, StringComparer.Ordinal).ThenBy(r => r.Doc, StringComparer.Ordinal).ToList();
        var md = new StringBuilder("### Learn reliability (`docs/evaluation/scoring-protocol.md`)\n\n");
        md.AppendLine(CultureInfo.InvariantCulture, $"{rows.Count} fresh learns (empty registry, `{ctx.Options.LearnDeployment}`, prompt `{PromptBuilder.Version}`), one per native document, up to {maxIterations} iterations each. \"Published at k\" = the spec verified (learn gate) after k model calls.").AppendLine();
        md.AppendLine("| Carrier | File kind | Trials | Published | at 1 | at 2 | at 3 | at 4 | at 5 | Mean cost |");
        md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var g in rows.GroupBy(r => (r.Carrier, r.FileKind)))
        {
            int At(int k) => g.Count(r => r.Published && r.Iterations == k);
            md.AppendLine(CultureInfo.InvariantCulture, $"| {g.Key.Carrier} | {g.Key.FileKind} | {g.Count()} | {g.Count(r => r.Published)} | {At(1)} | {At(2)} | {At(3)} | {At(4)} | {At(5)} | ${g.Average(r => r.CostUsd):0.0000} |");
        }

        md.AppendLine().AppendLine("Cumulative success by iteration budget (what `MaxIterations` = k would have published):").AppendLine();
        md.AppendLine("| File kind | k = 1 | k = 2 | k = 3 | k = 4 | k = 5 |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|");
        var decisions = new List<string>();
        foreach (var g in rows.GroupBy(r => r.FileKind).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var n = g.Count();
            var cumulative = Enumerable.Range(1, 5).Select(k => g.Count(r => r.Published && r.Iterations <= k)).ToList();
            md.AppendLine(CultureInfo.InvariantCulture, $"| {g.Key} | {string.Join(" | ", cumulative.Select(c => $"{c}/{n}"))} |");
            // Smallest budget that reaches the plateau (no gain from more iterations).
            var best = cumulative.Max();
            var k = cumulative.IndexOf(best) + 1;
            decisions.Add($"{g.Key}: MaxIterations {Math.Max(3, k)} ({best}/{n} published at k ≤ {k})");
        }

        var failed = rows.Where(r => !r.Published).ToList();
        var topLint = failed.SelectMany(r => r.LintFindings.Select(Normalize).Distinct()).GroupBy(l => l).OrderByDescending(g => g.Count()).Take(3).ToList();
        md.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Failed trials: {failed.Count}. Top lint findings on failed trials (share of failed trials where the finding appears in any attempt):").AppendLine();
        foreach (var l in topLint)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"- {l.Count()}/{failed.Count}: `{l.Key}`");
        }

        var lintShare = failed.Count == 0 ? 0 : (double)failed.Count(r => r.LintFindings.Count > 0 && r.AttemptSummaries.LastOrDefault()?.Contains("0 hard", StringComparison.Ordinal) == true) / failed.Count;
        md.AppendLine().AppendLine("Last attempt of each failed trial:").AppendLine();
        foreach (var r in failed)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"- {r.Doc}: {r.AttemptSummaries.LastOrDefault()}");
        }

        var decision = string.Join("; ", decisions);
        md.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"**Decision:** {decision}.");
        return new StudyResult("learn-reliability", decision, md.ToString(), rows, rows.Sum(r => r.CostUsd));

        // Lint text without the document-specific parts (header names, values) so findings group.
        static string Normalize(string lint) => System.Text.RegularExpressions.Regex.Replace(lint, "\"[^\"]*\"", "\"…\"").Split(';')[0];
    }

    // ------------------------------------------------------------------ first-seen (brief 13 §3)

    /// <param name="Why">For a reviewed trial, what blocked its best attempt (hard rule ids, missing fields, the first issues).</param>
    public sealed record FirstSeenTrial(string Carrier, string Doc, bool Published, bool Delivered, int ModelCalls, decimal CostUsd, int KeyFields, int KeyCorrect, string? Error,
        string? Why = null);

    /// <summary>
    /// The deck's "native PDF, first-seen template" row has one document per run (the first of each unseen carrier). Here
    /// each of <c>--trials</c> native PDFs per unseen carrier is the first document of its format: a fresh learn (empty
    /// registry, production iteration budget), delivered when the spec publishes and the replay has no hard failure, else
    /// Review. Delivered key fields are scored against truth exactly as the scorer does. With <c>--doc &lt;id&gt;</c> (brief 20
    /// Answers 4) the same document is learned <c>--trials</c> times, each from an empty registry: learn variance on one
    /// document, reported as a deck-table row.
    /// </summary>
    private static async Task<StudyResult> FirstSeenAsync(StudyContext ctx)
    {
        var trials = ctx.Int("--trials", 5);
        var carriers = ctx.Option("--carriers")?.Split(',') ?? [.. Carriers.All.Select(c => c.Key).Where(c => !Carriers.Seen.Contains(c))];
        var single = ctx.Option("--doc") is { } docId
            ? ctx.Manifest.Entries.SingleOrDefault(e => e.Id == docId) ?? throw new ArgumentException($"--doc {docId}: not in the corpus manifest")
            : null;
        if (single is not null)
        {
            carriers = [single.Carrier];
        }

        var work = single is not null
            ? Enumerable.Range(0, trials).Select(_ => (Carrier: single.Carrier, Entry: single)).ToList()
            : carriers.SelectMany(carrier => ctx.Manifest.Entries.Where(e => e.Carrier == carrier && e.InputType == InputTypes.NativePdf
                    && !e.Strata.Contains(Strata.Password) && ctx.SingleGraph(e) is not null)
                .OrderBy(e => e.Id, StringComparer.Ordinal).Take(trials).Select(e => (Carrier: carrier, Entry: e))).ToList();
        var results = new System.Collections.Concurrent.ConcurrentBag<(FirstSeenTrial Trial, List<(string, bool)> Key)>();
        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = ctx.Int("--concurrency", 4) }, async (item, _) =>
        {
            var graph = ctx.SingleGraph(item.Entry)!;
            var (outcome, cost, error) = await ctx.LearnAsync(graph, item.Entry, false, null, null).ConfigureAwait(false);
            var key = new List<(string, bool)>();
            var delivered = false;
            if (outcome is { Published: true, Spec: { } spec })
            {
                var verification = SpecVerifier.Verify(graph, spec, new SubmissionContext(EvalRunner.CarrierName(item.Entry.Carrier)));
                delivered = verification.HardFailures.Count == 0;
                if (delivered)
                {
                    var truth = CorpusManifest.Truth(ctx.Corpus, item.Entry.Id);
                    var (fields, _, _, _, _) = Scorer.ScoreFields(item.Entry, truth, verification.Replay.Document, true, "study");
                    key.AddRange(fields.Where(f => Scorer.KeyFields.Contains(f.Field)).Select(f => (item.Entry.Id, f.Match)));
                }
            }

            string? why = null;
            if (!delivered && outcome?.Attempts.Where(a => a.Verification is not null).MinBy(a => a.Verification!.Penalty)?.Verification is { } best)
            {
                // Brief 20 Answers 4: a reviewed trial says why, so learn variance points at a failure mode, not at luck.
                why = string.Join("; ", new[]
                    {
                        best.HardFailures.Count > 0 ? "hard " + string.Join(",", best.HardFailures.Select(h => h.RuleId).Distinct()) : null,
                        best.MissingRequired.Count > 0 ? "missing " + string.Join(",", best.MissingRequired.Take(3)) : null,
                        best.Issues.Count > 0 ? $"{best.Issues.Count} issue(s): " + System.Text.RegularExpressions.Regex.Replace(best.Issues[0], "'[^']*'", "'…'")[..Math.Min(110, best.Issues[0].Length)] : null,
                        best.TotalsTie == false ? "totals do not tie" : null,
                    }.OfType<string>());
            }

            results.Add((new FirstSeenTrial(item.Carrier, item.Entry.Id, outcome?.Published == true, delivered, outcome?.ModelCalls ?? 0, cost, key.Count,
                key.Count(k => k.Item2), error, why), key));
            Console.WriteLine($"  first-seen {item.Entry.Id}: {(delivered ? "delivered" : "review")} · {outcome?.ModelCalls ?? 0} call(s) · ${cost:0.0000}");
        }).ConfigureAwait(false);

        // A trial that failed for an infrastructure reason (e.g. a 429 throttle) measured nothing about the learn: listed, not counted.
        var errored = results.Where(r => r.Trial.Error is not null).Select(r => r.Trial).ToList();
        var rows = results.Where(r => r.Trial.Error is null).OrderBy(r => r.Trial.Doc, StringComparer.Ordinal).ToList();
        var accuracy = Stats.Estimate([.. rows.SelectMany(r => r.Key)], 1000);
        var reviewed = rows.Count(r => !r.Trial.Delivered);
        var md = new StringBuilder(single is null ? "### First-seen native PDF templates (`docs/poc-plan/closeout/README.md`)\n\n" : $"### First-seen learn variance: {single.Id}, {trials} trials\n\n");
        var intro = single is null
            ? $"{rows.Count} native PDFs of the unseen carriers ({string.Join(", ", carriers)}), each learned from an empty registry as if it were the first document of its format (`{ctx.Options.LearnDeployment}`, prompt `{PromptBuilder.Version}`). Delivered = the spec published and the replay has no hard rule failure; otherwise Review."
            : $"{single.Id} learned {rows.Count} times, each from an empty registry as the first document of its format (`{ctx.Options.LearnDeployment}`, prompt `{PromptBuilder.Version}`). Delivered = the spec published and the replay has no hard rule failure; otherwise Review.";
        md.AppendLine(intro).AppendLine();
        md.AppendLine("| Document | Carrier | Path | Model calls | Cost | Key fields correct | Why reviewed (best attempt) |");
        md.AppendLine("|---|---|---|---:|---:|---:|---|");
        foreach (var (t, _) in rows)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {t.Doc} | {t.Carrier} | {(t.Delivered ? "delivered" : "review")} | {t.ModelCalls} | ${t.CostUsd:0.0000} | {(t.Delivered ? $"{t.KeyCorrect}/{t.KeyFields}" : "—")} | {(t.Delivered ? "" : t.Why?.Replace("|", "/") ?? "")} |");
        }

        var decision = $"first-seen native PDF: delivered key-field accuracy {accuracy.Format()}; review rate {reviewed}/{rows.Count}; mean {rows.Average(r => r.Trial.ModelCalls):0.0} model calls, ${rows.Average(r => r.Trial.CostUsd):0.0000} per learn";
        md.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"**Result:** {decision}.");
        foreach (var e in errored)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"- Not counted: a {e.Doc} trial failed before learning ({e.Error!.Split(':')[0]}).");
        }

        if (single is not null)
        {
            // The deck-table row (brief 20 Answers 4): variance on one document is a finding, reported like a stratum.
            var reviewRate = Stats.Estimate([.. rows.Select(r => (r.Trial.Doc, !r.Trial.Delivered))], 1000);
            md.AppendLine().AppendLine("Deck-table row:").AppendLine();
            md.AppendLine("| Input type | Docs | Delivered key-field accuracy | Review rate | Pre-fill key-field accuracy (reviewed docs) | Learn calls / doc | Commitment |");
            md.AppendLine("|---|---:|---|---|---|---:|---|");
            md.AppendLine(CultureInfo.InvariantCulture, $"| first-seen learn, {single.Id} ({rows.Count} trials) | {rows.Count} | {(accuracy.N == 0 ? "— (none delivered)" : accuracy.Format())} | {reviewRate.Format()} | — (study: no pre-fill scored) | {rows.Average(r => r.Trial.ModelCalls):0.000} | measured, not committed (learn variance on one document) |");
        }

        return new StudyResult("first-seen", decision, md.ToString(), rows.Select(r => r.Trial).ToList(), rows.Sum(r => r.Trial.CostUsd));
    }

    // ------------------------------------------------------------------ cost-by-path

    private static StudyResult CostByPath(StudyContext ctx)
    {
        var docs = new List<(ManifestEntry Entry, string Path, decimal Cost, decimal Cu, decimal Llm, decimal Other, int LlmCalls, double Seconds)>();
        foreach (var entry in ctx.Manifest.Entries)
        {
            if (ctx.Run(entry.Id) is not { Error: null } run)
            {
                continue;
            }

            var predicted = ctx.Predicted(entry, CorpusManifest.Truth(ctx.Corpus, entry.Id));
            var path = predicted is null ? "not-found" : predicted.FinalTier;
            var cu = Cost(entry.Id, run.Usage.Where(u => u.Service == "ContentUnderstanding"));
            var llm = Cost(entry.Id, run.Usage.Where(u => u.Service == "AzureOpenAI" && u.Stage != CostCalculator.EmbeddingStage));
            docs.Add((entry, path, run.CostUsd, cu, llm, run.CostUsd - cu - llm, run.LlmCalls, run.LatencyMs / 1000.0));
        }

        var md = new StringBuilder("### Cost by path\n\n");
        md.AppendLine("Per corpus file (bundles and archives count once), by the path the loss-run document took. CU = layout + classifier; LLM = learn/delta/repair calls; other = embeddings and DI.").AppendLine();
        md.AppendLine("| Path | Docs | Mean cost | Mean CU | Mean LLM | Mean other | LLM calls / doc | p50 s | p95 s |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var g in docs.GroupBy(d => d.Path).OrderByDescending(g => g.Count()))
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {g.Key} | {g.Count()} | ${g.Average(d => d.Cost):0.0000} | ${g.Average(d => d.Cu):0.0000} | ${g.Average(d => d.Llm):0.0000} | ${g.Average(d => d.Other):0.0000} | {g.Average(d => d.LlmCalls):0.00} | {Stats.Percentile([.. g.Select(d => d.Seconds)], 0.5):0.0} | {Stats.Percentile([.. g.Select(d => d.Seconds)], 0.95):0.0} |");
        }

        // Break-even: a model-per-document pipeline pays at least one learn-sized call on every document (a lower bound:
        // extraction also emits every row); format memory pays the carrier's learn/repair spend once.
        md.AppendLine().AppendLine("Learn-vs-replay break-even per carrier (layout cost is the same on both paths and left out):").AppendLine();
        md.AppendLine("| Carrier | Docs | LLM spend (all docs) | Docs with 0 LLM calls | Per-call cost | Model-per-doc lower bound | Break-even at doc # |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        var breakEven = new List<object>();
        foreach (var g in docs.GroupBy(d => d.Entry.Carrier).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var llm = g.Sum(d => d.Llm);
            var calls = g.Sum(d => d.LlmCalls);
            var perCall = calls == 0 ? 0 : llm / calls;
            var modelPerDoc = perCall * g.Count();
            var n = perCall == 0 ? (int?)null : (int)Math.Ceiling(llm / perCall);
            md.AppendLine(CultureInfo.InvariantCulture, $"| {g.Key} | {g.Count()} | ${llm:0.0000} | {g.Count(d => d.LlmCalls == 0)} | ${perCall:0.0000} | ${modelPerDoc:0.0000} | {(n is null ? "— (no model call)" : n.Value.ToString(CultureInfo.InvariantCulture))} |");
            breakEven.Add(new { Carrier = g.Key, Docs = g.Count(), LlmUsd = llm, ZeroCallDocs = g.Count(d => d.LlmCalls == 0), PerCallUsd = perCall, ModelPerDocUsd = modelPerDoc, BreakEvenDoc = n });
        }

        var total = docs.Sum(d => d.Cost);
        var replayShare = docs.Count == 0 ? 0 : (double)docs.Count(d => d.LlmCalls == 0) / docs.Count;
        var decision = $"{replayShare:P0} of documents ran with 0 LLM calls; corpus total ${total:0.00} (CU ${docs.Sum(d => d.Cu):0.00}, LLM ${docs.Sum(d => d.Llm):0.00})";
        md.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"**Result:** {decision}.");
        return new StudyResult("cost-by-path", decision, md.ToString(),
            new { paths = docs.Select(d => new { d.Entry.Id, d.Path, d.Cost, d.Cu, d.Llm, d.Other, d.LlmCalls, d.Seconds }).ToList(), breakEven }, 0);
    }

    // ------------------------------------------------------------------ shared

    public static Accuracy Measure(ManifestEntry entry, TruthRecord truth, ReplayResult replay)
    {
        var hard = replay.RuleOutcomes.Count(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard);
        var (fields, matched, predicted, truthClaims, _) = Scorer.ScoreFields(entry, truth, replay.Document, hard == 0, "study");
        var key = fields.Where(f => Scorer.KeyFields.Contains(f.Field)).ToList();
        return new Accuracy(fields.Count, fields.Count(f => f.Match), key.Count, key.Count(f => f.Match), truthClaims, matched, predicted, hard);
    }

    internal static decimal Cost(string id, IEnumerable<UsageEvent> usage)
    {
        var events = usage.Select(e => e with { DocumentId = id }).ToList();
        return events.Count == 0 ? 0 : CostCalculator.Compute(id, "study", "study", events, PriceTable.Embedded).CostUsd;
    }
}

/// <summary>A finished run's results, graphs and specs, plus the Azure clients a study needs (keyless).</summary>
internal sealed class StudyContext(string corpus, string results, Func<string, string?> option)
{
    private readonly Dictionary<string, RunResult?> _runs = [];
    private readonly Dictionary<string, DocumentGraph?> _graphs = [];
    private readonly Azure.Core.TokenCredential _credential = AzureCredentials.Default();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly Uri _ai = Settings.RequiredUri("LOSSRUN_AOAI_ENDPOINT", "the Azure OpenAI endpoint: terraform output ai_endpoint");
    private InMemoryFormatRegistry? _registry;

    public string Corpus { get; } = corpus;
    public CorpusManifest Manifest { get; } = CorpusManifest.Load(corpus);
    public LearnOptions Options { get; } = LearnOptions.FromConfiguration();

    public string? Option(string name) => option(name);

    public int Int(string name, int fallback) => option(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

    public RunResult? Run(string id)
    {
        if (!_runs.TryGetValue(id, out var run))
        {
            var path = Path.Combine(results, "docs", id + ".json");
            run = _runs[id] = File.Exists(path) ? JsonSerializer.Deserialize<RunResult>(File.ReadAllText(path), CanonicalJson.Options) : null;
        }

        return run;
    }

    /// <summary>The loss-run document the scorer would pick for this entry (truth segment overlap).</summary>
    public ExtractedDocument? Predicted(ManifestEntry entry, TruthRecord truth) =>
        Run(entry.Id)?.Documents.FirstOrDefault(d => d.FirstPage is null || d.FirstPage <= truth.LossRunSegment.EndPage && (d.LastPage ?? int.MaxValue) >= truth.LossRunSegment.StartPage);

    /// <summary>The graph of a single-document entry (not a bundle segment), as the run saved it.</summary>
    public DocumentGraph? SingleGraph(ManifestEntry entry)
    {
        if (Run(entry.Id) is not { Documents: [{ FirstPage: null } only] })
        {
            return null;
        }

        if (!_graphs.TryGetValue(only.GraphDocumentId, out var graph))
        {
            var path = Path.Combine(results, "graphs", only.GraphDocumentId + ".json");
            graph = _graphs[only.GraphDocumentId] = File.Exists(path) ? JsonSerializer.Deserialize<DocumentGraph>(File.ReadAllText(path), CanonicalJson.Options) : null;
        }

        return graph;
    }

    /// <summary>Hand specs plus every spec the run published.</summary>
    public async Task<InMemoryFormatRegistry> RegistryAsync()
    {
        if (_registry is null)
        {
            _registry = new InMemoryFormatRegistry();
            await FormatDirectory.LoadIntoAsync(EvalRunner.FindUp("src/TestShared/formats")!, _registry).ConfigureAwait(false);
            var specs = Path.Combine(results, "specs");
            foreach (var file in (Directory.Exists(specs) ? Directory.GetFiles(specs, "*.published.json") : []).Order(StringComparer.Ordinal))
            {
                var spec = FormatJson.Parse(await File.ReadAllTextAsync(file).ConfigureAwait(false));
                if (await _registry.GetAsync(spec.FormatId, spec.Version).ConfigureAwait(false) is null)
                {
                    await _registry.PublishAsync(spec).ConfigureAwait(false);
                }
            }
        }

        return _registry;
    }

    /// <summary>The spec most of the carrier's documents replayed with in the run.</summary>
    public async Task<CarrierFormat?> CarrierSpecAsync(InMemoryFormatRegistry registry, string carrier)
    {
        var used = Manifest.Entries.Where(e => e.Carrier == carrier).Select(e => Run(e.Id)?.Documents.FirstOrDefault()?.FormatRef).OfType<string>()
            .GroupBy(r => r).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        if (used is null || used.Split('@') is not [var id, var v])
        {
            return null;
        }

        return await registry.GetAsync(id, int.Parse(v, CultureInfo.InvariantCulture)).ConfigureAwait(false);
    }

    public CarrierFormat? BestAttemptSpec(ManifestEntry entry)
    {
        var id = Run(entry.Id)?.Documents.FirstOrDefault()?.GraphDocumentId;
        var path = id is null ? null : Path.Combine(results, "specs", $"{id}.best-attempt.json");
        return path is not null && File.Exists(path) ? FormatJson.Parse(File.ReadAllText(path)) : null;
    }

    public SecondOpinionOcr SecondOpinion() => new(_ai, _credential, new InMemoryCostMeter(), Options.Region, enabled: true, _http);

    public AzureOpenAiEmbeddingService Embeddings(ICostMeter meter) =>
        new(_ai, _credential, Options.EmbeddingModel!, meter, Options.Region, Options.DeploymentType, _http);

    /// <summary>One learn on an empty registry (nothing the run learned is visible), returning the outcome and its model cost.</summary>
    public async Task<(LearnOutcome? Outcome, decimal Cost, string? Error)> LearnAsync(DocumentGraph graph, ManifestEntry entry, bool pageImage, byte[]? pdf,
        IReadOnlyList<DocumentGraph>? exemplars, int? maxIterations = null)
    {
        var meter = new InMemoryCostMeter();
        var options = Options with
        {
            IncludePageImage = pageImage, MaxIterations = maxIterations ?? Options.MaxIterations,
            MaxIterationsSpreadsheet = maxIterations ?? Options.MaxIterationsSpreadsheet,
        };
        var learner = new FormatLearner(new AzureOpenAiLearnModel(_ai, _credential, meter, options.DeploymentType, options.Region, _http), new InMemoryFormatRegistry(), options);
        try
        {
            var outcome = await learner.LearnAsync(new LearnRequest(graph, $"study-{entry.Id}", new SubmissionContext(EvalRunner.CarrierName(entry.Carrier)), SourcePdf: pdf,
                Exemplars: exemplars)).ConfigureAwait(false);
            return (outcome, Studies.Cost(entry.Id, meter.Events), null);
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return (null, Studies.Cost(entry.Id, meter.Events), $"{e.GetType().Name}: {e.Message}");
        }
    }
}
