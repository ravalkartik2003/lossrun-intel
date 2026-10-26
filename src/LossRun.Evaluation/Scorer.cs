using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LossRun.Contracts;

namespace LossRun.Evaluation;

/// <summary>One scored field value.</summary>
/// <param name="Error">ok | missing (row or value not extracted) | wrong (wrong value on a document that failed a hard rule: caught) |
/// wrong-but-plausible (wrong value on a document that passed every hard rule: silent).</param>
/// <param name="Row">Truth claim number for claim fields (groups the per-claim joint pass).</param>
public sealed record FieldObservation(string Doc, string Carrier, string InputType, string Bucket, string Template, string Scope, string Field, bool Match, string Error,
    string? Expected, string? Actual, string? Row = null);

/// <param name="LearnCalls">Learn / delta / repair model calls (the CU classifier's billed tokens are not a model call here).</param>
/// <param name="Layout">The carrier's layout (narrative statements are excluded from some 08b targets).</param>
public sealed record DocOutcome(string Doc, string Carrier, string InputType, string Bucket, string Template, IReadOnlyList<string> Strata, bool SourceConsistent,
    string FinalTier, bool Extracted, int HardFailures, bool Stp, bool StpStrict, bool QuoteSuccess, bool DqPass, double Seconds, decimal CostUsd, int LlmCalls,
    int TruthClaims, int MatchedClaims, int PredictedClaims, bool AllFieldsCorrect, string? Error, int LearnCalls = 0, string Layout = "", string? RoutingReason = null);

/// <summary><c>lossrun eval score</c> (brief 08 §2).</summary>
public static class Scorer
{
    /// <summary>Key fields of the requirement ("Claim #, DOI, Paid/Reserved splits").</summary>
    public static readonly string[] KeyFields =
        ["claim.claimNumber", "claim.dateOfInjury", "claim.indemnity.paid", "claim.indemnity.reserve", "claim.medical.paid", "claim.medical.reserve"];

    /// <param name="PreFillFields">Printed values of Review documents scored against the reviewer's pre-fill (08 Answers 1).</param>
    /// <param name="TruthVersion">Brief 31 Answers 2: the generator (or import) version of the truth scored against, with its seed; it can differ from the run's corpus (a re-score).</param>
    public sealed record Scored(IReadOnlyList<FieldObservation> Fields, IReadOnlyList<DocOutcome> Docs, ClassificationCounts Classification, RunMetadata? Run,
        IReadOnlyList<FieldObservation>? PreFillFields = null, string? TruthVersion = null);

    public sealed record ClassificationCounts(int TruePositive, int FalsePositive, int FalseNegative, int TrueNegative, int Bundles, int BundleSplitsExact);

    /// <param name="allFields">Brief 31 item 2: also write <c>studies/all-fields.md</c> (every compared field, every wrong delivered value).</param>
    public static int Run(string corpusDir, string resultsDir, string outDir, int bootstrap, bool allFields = false)
    {
        var scored = Score(corpusDir, resultsDir);
        Directory.CreateDirectory(outDir);
        var report = Report.Build(scored, bootstrap);
        File.WriteAllText(Path.Combine(outDir, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = true }));
        var awaiting = scored.Docs.Count(d => d.Error == NoTruth);
        var markdown = Report.Markdown(report);
        var imported = CorpusManifest.Load(corpusDir).GeneratorVersion == Import.Generator;
        if (imported)
        {
            markdown = ForImport(markdown);
        }

        if (awaiting > 0)
        {
            // Field accuracy needs truth: on an imported archive it comes from align + adjudication (docs/operations/blind-run.md).
            var note = $"> **{awaiting} of {scored.Docs.Count} documents have no truth yet** (imported archive). Path, review rate, latency, cost and model calls below are measured; " +
                "accuracy and classification cover only documents with truth. Add truth from `lossrun eval align` + adjudication, then score again.\n\n";
            // After the title, at the first blank line, whatever the platform's newline (on Windows the report has CRLF).
            var blank = System.Text.RegularExpressions.Regex.Match(markdown, @"\r?\n\r?\n");
            markdown = blank.Success ? markdown[..(blank.Index + blank.Length)] + note + markdown[(blank.Index + blank.Length)..] : note + markdown;
            Console.WriteLine($"{awaiting} document(s) scored without truth: operational numbers only");
        }

        File.WriteAllText(Path.Combine(outDir, "report.md"), markdown);
        File.WriteAllText(Path.Combine(outDir, "deck-table.md"), imported ? ForImport(Report.DeckMarkdown(report)) : Report.DeckMarkdown(report));
        Report.WriteCsv(outDir, scored, bootstrap);
        Console.WriteLine($"scored {scored.Docs.Count} documents, {scored.Fields.Count} field values → {outDir}");
        if (allFields)
        {
            var wrong = AllFieldsStudy.Write(scored, outDir);
            Console.WriteLine($"all-fields study: {wrong} wrong delivered value(s) → {Path.Combine(outDir, "studies", AllFieldsStudy.FileName)}");
        }

        return 0;
    }

    public static Scored Score(string corpusDir, string resultsDir)
    {
        var manifest = CorpusManifest.Load(corpusDir);
        var fields = new List<FieldObservation>();
        var preFill = new List<FieldObservation>();
        var docs = new List<DocOutcome>();
        int tp = 0, fp = 0, fn = 0, tn = 0, bundles = 0, splits = 0;
        var runPath = Path.Combine(resultsDir, "run.json");
        var run = File.Exists(runPath) ? JsonSerializer.Deserialize<RunMetadata>(File.ReadAllText(runPath), CanonicalJson.Options) : null;

        foreach (var entry in manifest.Entries)
        {
            var path = Path.Combine(resultsDir, "docs", entry.Id + ".json");
            if (!File.Exists(path))
            {
                continue; // not run (e.g. --only); scored documents are the ones the run produced
            }

            var result = JsonSerializer.Deserialize<RunResult>(File.ReadAllText(path), CanonicalJson.Options)!;
            if (CorpusManifest.TryTruth(corpusDir, entry.Id) is not { } truth)
            {
                // An imported archive before adjudication (brief 14): the path, review rate, latency, cost and model calls are
                // known; field accuracy and classification wait for truth from align + adjudication.
                docs.Add(Operational(entry, result));
                continue;
            }

            var predicted = Predicted(entry, truth, result.Documents);
            // First-seen = no known format matched on arrival (fingerprint miss, nothing recovered), whether or not a learn ran
            // (08b: narrative documents no longer attempt a learn but are still not a recurring template).
            var template = Carriers.Seen.Contains(entry.Carrier) ? "seen" : predicted?.FingerprintTier is null or "miss" ? "first-seen" : "unseen-repeat";
            var bucket = InputTypes.Bucket(entry.InputType);
            // Delivered = what leaves without a human; a Review document delivers nothing (protocol §3). Results from before 08b
            // kept the replay in Document for router-Review documents: it is read as their pre-fill.
            var review = predicted?.FinalTier == "Review";
            var delivered = review ? null : predicted?.Document;
            var hardPassed = delivered is not null && predicted!.HardFailures == 0;

            // ---------------- fields
            var (docFields, matched, predClaimCount, truthClaimCount, predPeriodCount) = ScoreFields(entry, truth, delivered, hardPassed, template);
            fields.AddRange(docFields);
            if (review)
            {
                preFill.AddRange(ScoreFields(entry, truth, predicted!.PreFill ?? predicted.Document, false, template).Fields);
            }

            // ---------------- classification (loss run vs other), bundle split
            var lossRunFound = predicted is not null;
            if (lossRunFound)
            {
                tp++;
            }
            else
            {
                fn++;
            }

            foreach (var other in truth.OtherSegments)
            {
                var claimed = result.Documents.Any(d => d.FirstPage is not null && d.FirstPage <= other.EndPage && d.LastPage >= other.StartPage);
                if (claimed)
                {
                    fp++;
                }
                else
                {
                    tn++;
                }
            }

            if (truth.OtherSegments.Count > 0)
            {
                bundles++;
                if (predicted is { FirstPage: { } f, LastPage: { } l } && f == truth.LossRunSegment.StartPage && l == truth.LossRunSegment.EndPage)
                {
                    splits++;
                }
            }

            // ---------------- document outcome
            var tier = predicted?.FinalTier ?? (result.Error is null ? "NotFound" : "Error");
            var extracted = delivered is not null;
            var stp = extracted && tier is "ReplayAccepted" or "Learned" or "Delta";
            var quote = stp && predicted!.HardFailures == 0 && predPeriodCount > 0;
            docs.Add(new DocOutcome(entry.Id, entry.Carrier, entry.InputType, bucket, template, entry.Strata, truth.SourceConsistent, tier, extracted, predicted?.HardFailures ?? 0,
                stp, stp && !(predicted?.ConfirmNewFormat ?? false), quote, extracted && predicted!.HardFailures == 0, result.LatencyMs / 1000.0, result.CostUsd, result.LlmCalls,
                truthClaimCount, matched, predClaimCount, docFields.Count > 0 && docFields.All(o => o.Match), result.Error,
                result.Documents.Sum(d => d.ModelCalls), entry.Layout, predicted?.Reasons.FirstOrDefault()));
        }

        return new Scored(fields, docs, new ClassificationCounts(tp, fp, fn, tn, bundles, splits), run, preFill,
            manifest.Seed is { } truthSeed ? $"{manifest.GeneratorVersion} seed {truthSeed}" : manifest.GeneratorVersion);
    }

    /// <summary>An imported archive is not the synthetic corpus: the title says so, and the synthetic-corpus caveat goes.</summary>
    private static string ForImport(string markdown) =>
        markdown.Replace("# Evaluation report — synthetic corpus", "# Evaluation report — imported archive", StringComparison.Ordinal)
            .Replace(Report.DeckCaveat, "Imported archive (`lossrun eval import`). Rows with fewer than 5 documents are an insufficient sample, not a result.", StringComparison.Ordinal);

    /// <summary>A document outcome without truth: everything but accuracy and classification (<see cref="DocOutcome.Error"/> notes it).</summary>
    private static DocOutcome Operational(ManifestEntry entry, RunResult result)
    {
        var predicted = entry.InputType == InputTypes.Zip ? Package(result.Documents) : result.Documents.FirstOrDefault();
        var template = Carriers.Seen.Contains(entry.Carrier) ? "seen" : predicted?.FingerprintTier is null or "miss" ? "first-seen" : "unseen-repeat";
        var tier = predicted?.FinalTier ?? (result.Error is null ? "NotFound" : "Error");
        var delivered = tier != "Review" && predicted?.Document is not null;
        var stp = delivered && tier is "ReplayAccepted" or "Learned" or "Delta";
        var periods = predicted?.Document?.PolicyPeriods.Count ?? 0;
        return new DocOutcome(entry.Id, entry.Carrier, entry.InputType, InputTypes.Bucket(entry.InputType), template, entry.Strata, true, tier, delivered,
            predicted?.HardFailures ?? 0, stp, stp && !(predicted?.ConfirmNewFormat ?? false), stp && predicted!.HardFailures == 0 && periods > 0,
            delivered && predicted!.HardFailures == 0, result.LatencyMs / 1000.0, result.CostUsd, result.LlmCalls, 0, 0,
            predicted?.Document?.PolicyPeriods.Sum(p => p.Claims.Count) ?? 0, false, result.Error ?? NoTruth, result.Documents.Sum(d => d.ModelCalls), entry.Layout,
            predicted?.Reasons.FirstOrDefault());
    }

    /// <summary>
    /// A ZIP package's loss runs scored as one document (brief 14): an imported archive's package holds several loss runs,
    /// and its truth is every claim of the package. It goes to Review if any part does; values come from every part.
    /// </summary>
    /// <summary>
    /// The extraction scored against a document's truth: every extracted part overlapping the truth's loss-run pages, merged.
    /// Brief 22 follow-up (corpus v5 c10-005): the classifier split one run into three loss-run segments and only the first
    /// was scored, so a document whose other two segments were in review counted as delivered. One part in review now makes
    /// the document review, as for a ZIP package.
    /// </summary>
    internal static ExtractedDocument? Predicted(ManifestEntry entry, TruthRecord truth, IReadOnlyList<ExtractedDocument> documents) =>
        entry.InputType == InputTypes.Zip ? Package(documents)
            : Package([.. documents.Where(d => d.FirstPage is null || d.FirstPage <= truth.LossRunSegment.EndPage && (d.LastPage ?? int.MaxValue) >= truth.LossRunSegment.StartPage)]);

    internal static ExtractedDocument? Package(IReadOnlyList<ExtractedDocument> parts)
    {
        if (parts.Count <= 1)
        {
            return parts.FirstOrDefault();
        }

        static LossRunDocument? Merge(IEnumerable<LossRunDocument?> documents)
        {
            var list = documents.OfType<LossRunDocument>().ToList();
            return list.Count == 0 ? null : new LossRunDocument
            {
                DocumentId = list[0].DocumentId, CarrierName = list[0].CarrierName,
                ValuationDate = list.Select(d => d.ValuationDate).Distinct().Count() == 1 ? list[0].ValuationDate : null,
                PolicyPeriods = [.. list.SelectMany(d => d.PolicyPeriods)],
            };
        }

        var review = parts.Any(p => p.FinalTier == "Review");
        return new ExtractedDocument
        {
            GraphDocumentId = parts[0].GraphDocumentId, FinalTier = review ? "Review" : parts[0].FinalTier, RoutingTier = parts[0].RoutingTier,
            FingerprintTier = parts[0].FingerprintTier, ConfirmNewFormat = parts.Any(p => p.ConfirmNewFormat), ModelCalls = parts.Sum(p => p.ModelCalls),
            Reasons = [.. parts.SelectMany(p => p.Reasons)], HardFailures = parts.Sum(p => p.HardFailures), SoftFailures = parts.Sum(p => p.SoftFailures),
            Document = review ? null : Merge(parts.Select(p => p.Document)), PreFill = Merge(parts.Select(p => p.Document ?? p.PreFill)),
        };
    }

    /// <summary>Marks a document scored without truth (operational numbers only).</summary>
    public const string NoTruth = "no-truth";

    /// <summary>Scores one extracted document against its truth (also used by the studies).</summary>
    public static (List<FieldObservation> Fields, int Matched, int PredictedClaims, int TruthClaims, int PredictedPeriods) ScoreFields(ManifestEntry entry, TruthRecord truth,
        LossRunDocument? extracted, bool hardPassed, string template)
    {
        var bucket = InputTypes.Bucket(entry.InputType);
        var docFields = new List<FieldObservation>();
        var truthNode = JsonSerializer.SerializeToNode(truth.Document, CanonicalJson.Options)!.AsObject();
        var predNode = extracted is { } pd ? JsonSerializer.SerializeToNode(pd, CanonicalJson.Options)!.AsObject() : null;
        void Observe(string scope, string field, JsonNode? expected, JsonNode? actual, bool rowMissing = false, string? row = null)
        {
            var (e, a) = (Text(expected), Text(actual));
            var cmp = rowMissing ? new ComparisonResult(false, e, null, null, "missing-row") : CanonicalComparer.Default.Compare(KindOf(field), e, a, EnumField(field));
            var error = cmp.Match ? "ok" : a is null || rowMissing ? "missing" : hardPassed ? "wrong-but-plausible" : "wrong";
            docFields.Add(new FieldObservation(entry.Id, entry.Carrier, entry.InputType, bucket, template, scope, $"{scope}.{field}", cmp.Match, error, e, a, row));
        }

        foreach (var field in truth.Printed.DocumentFields)
        {
            Observe("document", field, Get(truthNode, field), predNode is null ? null : Get(predNode, field), predNode is null);
        }

        var truthPeriods = Array(truthNode, "policyPeriods");
        var predPeriods = predNode is null ? [] : Array(predNode, "policyPeriods");
        foreach (var tPeriod in truthPeriods)
        {
            var pPeriod = MatchPeriod(tPeriod, predPeriods);
            foreach (var field in truth.Printed.PeriodFields)
            {
                Observe("period", field, Get(tPeriod, field), pPeriod is null ? null : Get(pPeriod, field), pPeriod is null);
            }

            foreach (var field in truth.Printed.SubtotalFields)
            {
                Observe("subtotal", field, Get(tPeriod, "subtotals." + field), pPeriod is null ? null : Get(pPeriod, "subtotals." + field), pPeriod is null);
            }
        }

        foreach (var field in truth.Printed.GrandTotalFields)
        {
            Observe("grandTotal", field, Get(truthNode, "reportTotals." + field), predNode is null ? null : Get(predNode, "reportTotals." + field), predNode is null);
        }

        // Claims: align on normalized claim number (with the same DOI first: a package can repeat a claim number, brief 14),
        // falling back to DOI + claimant (comparator.md, table/record alignment).
        var truthClaims = truthPeriods.SelectMany(p => Array(p, "claims")).ToList();
        var predClaims = predPeriods.SelectMany(p => Array(p, "claims")).ToList();
        var unused = predClaims.ToList();
        var matched = 0;
        foreach (var tClaim in truthClaims)
        {
            var key = CanonicalComparer.NormalizeClaimNumber(Text(tClaim["claimNumber"]));
            var pClaim = unused.FirstOrDefault(p => key is not null && CanonicalComparer.NormalizeClaimNumber(Text(p["claimNumber"])) == key
                    && Text(p["dateOfInjury"]) == Text(tClaim["dateOfInjury"]))
                ?? unused.FirstOrDefault(p => key is not null && CanonicalComparer.NormalizeClaimNumber(Text(p["claimNumber"])) == key)
                ?? unused.FirstOrDefault(p => Text(p["dateOfInjury"]) == Text(tClaim["dateOfInjury"])
                    && CanonicalComparer.Default.Compare(FieldKind.ShortText, Text(tClaim["claimantName"]), Text(p["claimantName"])).Match);
            if (pClaim is not null)
            {
                unused.Remove(pClaim);
                matched++;
            }

            foreach (var field in truth.Printed.ClaimFields)
            {
                Observe("claim", field, Get(tClaim, field), pClaim is null ? null : Get(pClaim, field), pClaim is null, Text(tClaim["claimNumber"]));
            }
        }

        return (docFields, matched, predClaims.Count, truthClaims.Count, predPeriods.Count);
    }

    /// <summary>
    /// Aligns a truth period to a predicted one: by policy number; for periods without one (summary-only runs report by
    /// fiscal or report year, ADR 0017), by printed label, then by effective date.
    /// </summary>
    internal static JsonObject? MatchPeriod(JsonObject truthPeriod, List<JsonObject> predicted)
    {
        var number = Text(truthPeriod["policyNumber"]);
        if (number is not null)
        {
            return predicted.FirstOrDefault(p => CanonicalComparer.Default.Compare(FieldKind.ShortText, number, Text(p["policyNumber"])).Match);
        }

        var label = Text(truthPeriod["periodLabel"]);
        var effective = Text(truthPeriod["effectiveDate"]);
        return (label is null ? null : predicted.FirstOrDefault(p => CanonicalComparer.Default.Compare(FieldKind.ShortText, label, Text(p["periodLabel"])).Match))
            ?? (effective is null ? null : predicted.FirstOrDefault(p => Text(p["policyNumber"]) is null && Text(p["effectiveDate"]) == effective))
            ?? predicted.FirstOrDefault(p => CanonicalComparer.Default.Compare(FieldKind.ShortText, null, Text(p["policyNumber"])).Match);
    }

    // ------------------------------------------------------------------ value access and kinds

    private static List<JsonObject> Array(JsonObject node, string name) => (node[name] as JsonArray ?? []).OfType<JsonObject>().ToList();

    private static JsonNode? Get(JsonObject root, string path)
    {
        JsonNode? node = root;
        foreach (var part in path.Split('.'))
        {
            node = (node as JsonObject)?[part];
        }

        return node;
    }

    private static string? Text(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => node.ToJsonString(),
    };

    internal static FieldKind KindOf(string field) => field switch
    {
        "claimNumber" => FieldKind.ClaimNumber,
        "status" or "claimType" => FieldKind.Enum,
        "claimCount" or "openCount" or "closedCount" => FieldKind.Integer,
        "noLossesStatement" or "litigationFlag" or "cumulativeTrauma" or "reportOnly" => FieldKind.Boolean,
        // Brief 31 item 2: money outside the paid/reserve/incurred triplets is money too ("747.90" and "747.9" are one amount).
        "recoverySubrogation" or "netIncurred" => FieldKind.Currency,
        _ when field.StartsWith("recoveries.", StringComparison.Ordinal) => FieldKind.Currency,
        _ when field.EndsWith("Date", StringComparison.Ordinal) || field is "dateOfInjury" or "dateReported" or "dateClosed" => FieldKind.Date,
        _ when field.EndsWith(".paid", StringComparison.Ordinal) || field.EndsWith(".reserve", StringComparison.Ordinal) || field.EndsWith(".incurred", StringComparison.Ordinal)
            => FieldKind.Currency,
        _ => FieldKind.ShortText,
    };

    private static string? EnumField(string field) => field is "status" or "claimType" ? $"claim.{field}" : null;
}
