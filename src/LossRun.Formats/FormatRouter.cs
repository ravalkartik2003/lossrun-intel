using LossRun.Contracts;
using LossRun.Formats.Replay;
using LossRun.Rules;

namespace LossRun.Formats;

/// <summary>The routing tiers (carrier-format-memory.md KQ4, five-tier policy; Review is the fifth).</summary>
public enum RoutingTier
{
    /// <summary>Fingerprint hit, replay ran, every hard rule passed, nothing unmapped: done with zero model calls.</summary>
    ReplayAccepted,

    /// <summary>Fingerprint hit but unmapped headers or a few soft failures: brief 06 sends only the gap to the small model.</summary>
    ReplayWithDelta,

    /// <summary>Fingerprint miss, drift, low OCR confidence, or handwriting: brief 06 learns a new format version.</summary>
    Learn,

    /// <summary>Anything else (e.g. hard rule failures with everything mapped): human review with cell citations.</summary>
    Review,
}

public sealed record RoutingPolicy
{
    public int MaxSoftFailuresForDelta { get; init; } = 3;

    /// <summary>Mean OCR word confidence below which a scanned document is sent to learn rather than trusted to replay.</summary>
    public double MinOcrConfidence { get; init; } = 0.80;

    /// <summary>
    /// Scans below this resolution are not learned from (08b Answers 5): a 200 dpi rotated scan or a fax as a carrier's
    /// first exemplar goes to Review with pre-fill, and the format is learned from the next native or ≥ 300 dpi document.
    /// 280, not 300, so a 300 dpi scan whose placement rounds a little low still learns.
    /// </summary>
    public int MinScanDpiToLearn { get; init; } = 280;
}

/// <summary>Structured, UI-ready routing outcome ("format memory hit: … · 0 LLM calls").</summary>
/// <param name="FingerprintKey">Brief 26: the document's structural fingerprint (<see cref="GraphFingerprint.ShortKey"/>), the escalation dedupe key when no format matched.</param>
public sealed record RoutingDecision(
    RoutingTier Tier,
    IReadOnlyList<string> Reasons,
    string? FormatRef,
    string? CarrierName,
    string FingerprintTier,
    double FingerprintScore,
    bool Drift,
    int UnmappedHeaders,
    int HardFailures,
    int SoftFailures,
    int Flags,
    double? MeanOcrConfidence,
    int ModelCalls,
    int EmbeddingCalls,
    string Badge,
    string FormatStatus = FormatLifecycle.Active,
    int? ShadowReplay = null,
    bool ConfirmNewFormat = false,
    string? RecoveredFrom = null,
    string? HardFailureKind = null,
    string? FingerprintKey = null,
    int UnnormalizedValues = 0);

/// <summary>Review reasons for printed values replay could not normalize (brief 30 item 4).</summary>
public static class UnnormalizedReasonText
{
    public const string FillDown = "period-fill-down";

    /// <summary>Brief 34 A3: how the fill-down review reason starts; the review screen offers the fill-down toggle when a reason does.</summary>
    public const string FillDownReasonPrefix = "policy cell blank on ";
}

/// <summary>Brief 30 item 4: a printed value replay could not normalize, and where it was printed.</summary>
public sealed record NormalizationIssue(string FieldPath, string Raw, string Normalizer, int? Page, IReadOnlyList<double>? Polygon, string? Sheet, string? Cell)
{
    public string Where => Sheet is not null ? $"sheet {Sheet} cell {Cell}" : Page is { } p ? $"page {p}" : "location unknown";
}

public sealed class FormatRouter(RoutingPolicy? policy = null)
{
    private readonly RoutingPolicy _policy = policy ?? new RoutingPolicy();

    /// <param name="failures">Hard failures split into OCR-level and structural (<see cref="OcrFailures"/>); null = unknown (all structural).</param>
    /// <param name="narrative">A PDF with no table at all (a narrative statement): with no format it goes to Review, not to learn (08 Answers 3).</param>
    /// <param name="scanDpi">Resolution of a scanned PDF (null for born-digital files or when unknown): below
    /// <see cref="RoutingPolicy.MinScanDpiToLearn"/> a Learn decision becomes Review.</param>
    /// <summary>
    /// The review reasons for values that did not normalize. Brief 31 Answers 5: blank policy cells under a format without <c>fillDown</c> get their own
    /// reason in words, because the remedy is the format, not the cell: the confirm-format review is where fill-down is turned on.
    /// </summary>
    public static IReadOnlyList<string> UnnormalizedReasons(IReadOnlyList<NormalizationIssue> unnormalized)
    {
        var reasons = new List<string>();
        var fillDown = unnormalized.Where(u => u.Normalizer == UnnormalizedReasonText.FillDown).ToList();
        if (fillDown.Count > 0)
        {
            reasons.Add($"{UnnormalizedReasonText.FillDownReasonPrefix}{fillDown.Count} row(s), each attached to the period above: a fill-down export, or a dropped cell? "
                + "Confirm the format (the confirm-format review turns fill-down on) → review with pre-fill");
        }

        var other = unnormalized.Where(u => u.Normalizer != UnnormalizedReasonText.FillDown).ToList();
        if (other.Count > 0)
        {
            reasons.Add($"{other.Count} printed value(s) could not be normalized: "
                + string.Join("; ", other.Take(3).Select(u => $"{u.FieldPath} '{u.Raw}' ({u.Normalizer}, {u.Where})"))
                + (other.Count > 3 ? $"; +{other.Count - 3} more" : "") + " → review with pre-fill");
        }

        return reasons;
    }

    public RoutingDecision Decide(FingerprintMatch match, ReplayResult? replay, double? meanOcrConfidence, bool handwriting = false,
        OcrFailures.Classification? failures = null, bool narrative = false, int? scanDpi = null)
    {
        var reasons = new List<string>();
        var hard = replay?.RuleOutcomes.Count(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard) ?? 0;
        // Brief 29 §3: context rules (R-15 stale valuation, R-25 missing year) judge the submission; no repair fixes them.
        var soft = replay?.RuleOutcomes.Count(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Soft && !RuleCatalog.Embedded.ContextRules.Contains(o.RuleId)) ?? 0;
        var flags = replay?.RuleOutcomes.Count(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Flag) ?? 0;
        var unmapped = replay?.UnmappedHeaders.Count ?? 0;
        var formatLabel = match.Format is { } f ? $"{f.CarrierName} {f.FormatId} v{f.Version}" : null;

        // Drift that OCR made (the header tuple differs only by misreads the replay resolved): every header is mapped
        // and rows were read, so it is a hit, not a new layout (08b corpus v2: a c2 fax went to learn on such "drift").
        // So is drift the replay repaired deterministically (a merged or blank header cell, brief 08d).
        var driftFromOcr = match.Drift && replay is { Stats.ClaimRows: > 0, UnmappedHeaders.Count: 0 } && (replay.OcrHeaderMatches.Count > 0 || replay.HeadersRepaired);
        if (driftFromOcr && replay!.OcrHeaderMatches.Count > 0)
        {
            reasons.Add($"header tuple differs only by OCR misreads ({string.Join(", ", replay.OcrHeaderMatches.Select(m => $"\"{m.Key}\" → \"{m.Value}\""))}): treated as a hit");
        }
        else if (driftFromOcr)
        {
            reasons.Add("header tuple differs only by header cells the replay repaired: treated as a hit");
        }

        RoutingTier tier;
        if (match.CarrierMismatch)
        {
            tier = RoutingTier.Review;
            reasons.Add($"carrier-mismatch: the submission names a different carrier than {match.Format?.CarrierName} ({match.Format?.Ref})");
        }
        else if (CarrierOnPageDiffers(match.Format, replay) is { } printed)
        {
            // Brief 35 §3: the same signal without a hint. A spec that reads another party's name as the carrier (brief 34 wake, c8: the
            // insured) would deliver it on every replay; the format knows its own carrier.
            tier = RoutingTier.Review;
            reasons.Add($"{CarrierOnPageReasonPrefix}\"{printed}\" differs from the format's \"{match.Format!.CarrierName}\" ({match.Format.Ref})");
        }
        else if (match.Format is null && match.Ambiguous is { Count: > 1 } ambiguous)
        {
            // Brief 30 item 11: two formats print exactly these keys and nothing (no carrier hint, no embedding) separates them.
            tier = RoutingTier.Review;
            reasons.Add($"ambiguous format: {string.Join(", ", ambiguous)} share this layout's keys and the submission names no carrier → review; not learned");
        }
        else if (match.Format is null && narrative)
        {
            tier = RoutingTier.Review;
            reasons.Add("narrative statement (no table) and no known format → review with the paragraphs as pre-fill; not learned");
        }
        else if (match.Format is null)
        {
            tier = RoutingTier.Learn;
            reasons.Add($"fingerprint miss (best score {match.Score:0.000}) and no candidate format verified");
        }
        else if (match.Drift && !driftFromOcr && replay is not null && unmapped > 0 && soft <= _policy.MaxSoftFailuresForDelta)
        {
            // Header-level drift (renamed/added columns): the prior version still replays, so repair only the gap
            // (carrier-format-memory.md KQ7: "re-learn scope should match the drift").
            tier = RoutingTier.ReplayWithDelta;
            reasons.Add($"drift: identity matches {match.Format.Ref} but the header tuple differs; header-level change → delta");
            reasons.Add($"{unmapped} unmapped header(s): {string.Join(", ", replay.UnmappedHeaders)}");
        }
        else if (match.Drift && !driftFromOcr)
        {
            tier = RoutingTier.Learn;
            reasons.Add($"drift: identity matches {match.Format.Ref} but the header tuple differs");
        }
        else if (handwriting)
        {
            tier = RoutingTier.Learn;
            reasons.Add("handwriting detected");
        }
        else if (replay is null)
        {
            tier = RoutingTier.Review;
            reasons.Add("replay did not run");
        }
        else if (replay.Stats.ClaimRows == 0 && replay.Document.NoLossesStatement != true && replay.Document.PolicyPeriods.All(p => p.DetailLevel != DetailLevel.SummaryOnly || p.Subtotals is null))
        {
            // An empty extraction passes every claim-level rule vacuously; without a printed "no losses" statement it is
            // a replay that found nothing, not a clean loss run (brief 08 eval run: 38 CSVs accepted with 0 claims).
            tier = RoutingTier.Review;
            reasons.Add($"replay read no claim rows ({replay.Stats.TablesUsed} table(s) used) and the document states no \"no losses\"");
        }
        else if (hard > 0 && failures is { AllOcr: true })
        {
            // A repair cannot fix what OCR misread; the replay is the reviewer's pre-fill (08 Answers 2).
            tier = RoutingTier.Review;
            reasons.Add($"{hard} OCR-level hard failure(s) ({string.Join("; ", failures.Examples)}) → review with pre-fill; no repair attempted");
        }
        else if (hard == 0 && unmapped == 0 && soft == 0)
        {
            tier = RoutingTier.ReplayAccepted;
            reasons.Add($"{match.Tier} fingerprint hit; all hard and soft rules pass");
        }
        else if (hard == 0 && unmapped == 0 && soft <= _policy.MaxSoftFailuresForDelta)
        {
            tier = RoutingTier.ReplayAccepted;
            reasons.Add($"{match.Tier} fingerprint hit; all hard rules pass; {soft} soft warning(s) for the reviewer");
        }
        else if (unmapped > 0 && soft <= _policy.MaxSoftFailuresForDelta)
        {
            tier = RoutingTier.ReplayWithDelta;
            reasons.Add($"{unmapped} unmapped header(s): {string.Join(", ", replay.UnmappedHeaders)}");
            if (hard > 0)
            {
                reasons.Add($"{hard} hard rule failure(s) may come from the unmapped columns");
            }
        }
        else
        {
            tier = RoutingTier.Review;
            reasons.Add(hard > 0 ? $"{hard} hard rule failure(s) with every header mapped" : $"{soft} soft rule failure(s) exceed {_policy.MaxSoftFailuresForDelta}");
        }

        // A format that prints total rows, replayed with none found (08c corpus v2: OCR misread "Policy Total" on a fax, the
        // subtotal rows were read as claims, and R-12 had nothing to reconcile, so wrong values passed every hard rule).
        // The test is "no totals of any kind were read" (no subtotals from rows or a summary lookup, no report totals), and only
        // for a format known to have them: a hand spec declaring total rows, or a learned one admitted by a totals gate (08d
        // full run: c4 workbooks carry their totals on a summary sheet; a learned spec's spurious total-row regexes never
        // matched, and a row-count test sent 37 clean workbooks to Review).
        if (tier == RoutingTier.ReplayAccepted && replay is { Stats.ClaimRows: > 0 } && replay.Document.ReportTotals is null
            && replay.Document.PolicyPeriods.All(p => p.Subtotals is null) && PrintsTotals(match.Format!))
        {
            tier = RoutingTier.Review;
            reasons.Add($"{match.Format!.Ref} prints totals but none were read: the claims cannot be reconciled → review with pre-fill");
        }

        // Brief 17 Answers 2: "Loss Paid" read as indemnity without a separate medical column is accepted only when a printed
        // total reconciles to it (the tie-breaker); otherwise the reviewer decides the reading.
        if (tier == RoutingTier.ReplayAccepted && replay is { AmbiguousComposites.Count: > 0 }
            && !(replay.RuleOutcomes.Any(o => o.RuleId is "R-10" or "R-12" && o.Status == RuleStatus.Pass)
                 && !replay.RuleOutcomes.Any(o => o.RuleId is "R-10" or "R-12" && o.Status == RuleStatus.Fail)))
        {
            tier = RoutingTier.Review;
            reasons.Add($"{string.Join("; ", replay.AmbiguousComposites)}; no printed total confirms the reading → review");
        }

        // Brief 30 item 4: a printed value that did not normalize was dropped, and with R-02/R-20 not applicable on null and sums
        // skipping nulls every rule can still pass. Accepting would deliver a claim with a gap; the reviewer gets the cell instead.
        // (Kept rows of another line of business never report their claim type or line: ReplayEngine leaves those out.)
        var unnormalized = replay?.Unnormalized ?? [];
        if (tier is RoutingTier.ReplayAccepted or RoutingTier.Review && unnormalized.Count > 0)
        {
            tier = RoutingTier.Review;
            reasons.AddRange(UnnormalizedReasons(unnormalized));
        }

        if (tier == RoutingTier.Learn && scanDpi is { } dpi && dpi < _policy.MinScanDpiToLearn)
        {
            tier = RoutingTier.Review;
            reasons.Add($"{dpi} dpi scan is below {_policy.MinScanDpiToLearn} dpi: not learned from → review with pre-fill; the format is learned from the next native or ≥ 300 dpi document");
        }

        if (replay is { StructuralRepairs.Count: > 0 })
        {
            reasons.Add($"deterministic table repair: {string.Join("; ", replay.StructuralRepairs)}");
        }

        if (flags > 0)
        {
            reasons.Add($"{flags} routing flag(s) raised");
        }

        if (match.RecoveredFrom is { } from)
        {
            reasons.Insert(0, $"format-recovered: fingerprint missed; {match.Format?.Ref} replayed as a candidate ({from})");
        }

        if (meanOcrConfidence is { } ocr && ocr < _policy.MinOcrConfidence)
        {
            reasons.Add($"low OCR confidence {ocr:0.000} < {_policy.MinOcrConfidence:0.00}: values checked by the rules, not re-learned");
        }

        var badge = tier switch
        {
            RoutingTier.ReplayAccepted when match.RecoveredFrom is not null => $"format memory hit · recovered from noisy scan: {formatLabel} · 0 LLM calls",
            RoutingTier.ReplayAccepted => $"format memory hit: {formatLabel} · 0 LLM calls",
            RoutingTier.ReplayWithDelta => match.Drift ? $"format changed: {formatLabel} · delta for {unmapped} column(s)" : $"format memory hit: {formatLabel} · delta needed for {unmapped} column(s)",
            RoutingTier.Learn => match.Drift ? $"format changed: {formatLabel} → learn new version" : "new format → learn path",
            _ => $"needs review{(formatLabel is null ? "" : $": {formatLabel}")}",
        };

        return new RoutingDecision(tier, reasons, match.Format?.Ref, match.Format?.CarrierName, match.Tier, match.Score, match.Drift, unmapped, hard, soft, flags,
            meanOcrConfidence, replay?.ModelCalls ?? 0, match.EmbeddingCalls, badge, RecoveredFrom: match.RecoveredFrom,
            HardFailureKind: hard == 0 ? null : failures is { AllOcr: true } ? "ocr" : "structural", UnnormalizedValues: unnormalized.Count);
    }

    /// <summary>
    /// The format is known to print totals: a hand spec declaring total rows or a subtotal lookup, or a learned spec admitted by
    /// a totals gate (its exemplar's totals tied).
    /// </summary>
    public static bool PrintsTotals(CarrierFormat spec) => spec.Learning is null
        ? spec.TableLocator.RowKinds.Any(k => k.Kind is "subtotal" or "grandTotal") || spec.Periods.Lookup is { Subtotals.Count: > 0 }
        : spec.Learning.VerificationGate is "pdf-totals" or "spreadsheet-totals";

    /// <summary>A scanned PDF below <see cref="RoutingPolicy.MinScanDpiToLearn"/>: never learned from, by routing or by delta escalation.</summary>
    public static bool IsLowResolutionScan(DocumentGraph graph, RoutingPolicy? policy = null) =>
        OcrHeaders.IsScanned(graph) && graph.SourceFile.ScanDpi is { } dpi && dpi < (policy ?? new RoutingPolicy()).MinScanDpiToLearn;

    /// <summary>Mean OCR word confidence of a graph (null for spreadsheets, which have no OCR).</summary>
    /// <summary>Brief 35 §3: how the review reason starts when the carrier name on the page is not the format's carrier.</summary>
    public const string CarrierOnPageReasonPrefix = "carrier name on the page differs from the format's: ";

    /// <summary>
    /// Brief 35 §3: the carrier name the replay read, when it is not the format's own carrier (<see cref="FormatFingerprinter.SameCarrier"/>:
    /// normalized text, punctuation and spacing ignored). Null when either is absent: a hand spec without a carrier name is exempt.
    /// </summary>
    public static string? CarrierOnPageDiffers(CarrierFormat? spec, ReplayResult? replay) =>
        spec is not null && spec.KnownCarrierNames.Any() && replay?.Document.CarrierName is { Length: > 0 } printed && !FormatFingerprinter.KnowsCarrier(spec, printed)
            ? printed : null;

    public static double? MeanOcrConfidence(DocumentGraph graph) =>
        WordConfidence.Mean(graph.Words) is { } mean ? Math.Round(mean, 4) : null; // brief 31: words without a confidence are unknown, not 0
}

/// <summary>Fingerprint → replay → route for one loss-run graph; updates per-field success counters on accepted replays.</summary>
public sealed class FormatMemory(IFormatRegistry registry, IEmbeddingService? embeddings, FormatRouter? router = null, AliasPolicy? aliasPolicy = null)
{
    /// <summary>Learned-alias changes of the last <see cref="ProcessAsync"/> (ADR 0019: evidence and promotions from an accepted replay).</summary>
    public IReadOnlyList<AliasChange> AliasChanges { get; private set; } = [];

    private readonly FormatFingerprinter _fingerprinter = new(registry, embeddings);
    private readonly FormatRouter _router = router ?? new FormatRouter();

    public async Task<(FingerprintMatch Match, ReplayResult? Replay, RoutingDecision Decision)> ProcessAsync(DocumentGraph graph, string runId,
        DateTimeOffset processedAt, RuleContext? context = null, SubmissionContext? submission = null, CancellationToken cancellationToken = default)
    {
        AliasChanges = [];
        var match = await _fingerprinter.MatchAsync(graph, runId, submission, cancellationToken).ConfigureAwait(false);
        var scanned = OcrHeaders.IsScanned(graph);
        var glossary = await registry.GlossaryAsync(cancellationToken: cancellationToken).ConfigureAwait(false); // ADR 0019: with the active learned aliases

        // On drift the prior version is replayed too: its unmapped headers decide delta (header-level) vs learn.
        var replay = match.Format is not null && !match.CarrierMismatch ? ReplayEngine.Execute(graph, match.Format, context, glossary, submission: submission) : null;
        if (match.Format is null && !match.CarrierMismatch && match.Ambiguous is not { Count: > 1 } && await RecoverAsync(graph, match, context, submission, scanned, glossary, cancellationToken).ConfigureAwait(false) is { } recovered)
        {
            (match, replay) = (match with { Format = recovered.Spec, Tier = "recovered", RecoveredFrom = recovered.From }, recovered.Replay);
        }

        var failures = replay is null ? null : OcrFailures.Classify(replay, scanned);
        var narrative = graph.SourceFile.Kind == FileKind.Pdf && graph.Tables.Count == 0;
        var decision = _router.Decide(match, replay, FormatRouter.MeanOcrConfidence(graph), failures: failures, narrative: narrative,
            scanDpi: scanned ? graph.SourceFile.ScanDpi : null);

        if (decision.Tier == RoutingTier.ReplayAccepted && replay is not null)
        {
            var fields = replay.Document.Lineage!.Where(l => l.Value.Extractor == "replay").Select(l => FieldKey(l.Key)).Distinct(StringComparer.Ordinal).ToList();
            var spec = match.Format!;
            var (status, shadowReplay) = (FormatLifecycle.Active, (int?)null);
            var cosine = match.Similarities.TryGetValue(spec.FormatId, out var c) ? c : (double?)null;
            await registry.UpdateStatsAsync(spec.FormatId, stats =>
            {
                if (cosine is { } value)
                {
                    (stats.Embedding ??= new EmbeddingHits()).Add(value);
                }

                foreach (var path in fields)
                {
                    var stat = stats.Fields.TryGetValue(path, out var s) ? s : stats.Fields[path] = new FieldStat();
                    stat.SuccessCount++;
                    stat.LastSeen = processedAt;
                    stat.LastFormatVersion = spec.Ref;
                }

                // Shadow formats: count clean replays; the third flips the version to active (06 Answers 4).
                (status, shadowReplay) = (FormatLifecycle.Status(spec, stats), null);
                if (status == FormatLifecycle.Shadow)
                {
                    var live = stats.Lifecycle.TryGetValue(FormatLifecycle.Key(spec), out var l) ? l : stats.Lifecycle[FormatLifecycle.Key(spec)] = new VersionLifecycle { Status = FormatLifecycle.Shadow };
                    shadowReplay = ++live.CleanReplays;
                    if (live.CleanReplays >= FormatLifecycle.ShadowReplays)
                    {
                        (live.Status, live.ActivatedBy, live.ActivatedAt) = (FormatLifecycle.Active, "clean-replays", processedAt);
                    }
                }
            }, cancellationToken).ConfigureAwait(false);

            // ADR 0019: the accepted replay verifies the learned aliases its spec maps (evidence; 3 documents across 2 formats activate).
            var tie = replay.RuleOutcomes.Where(o => o.RuleId is "R-10" or "R-12").ToList() is { Count: > 0 } totals ? totals.All(o => o.Status != RuleStatus.Fail) : (bool?)null;
            var policy = await (aliasPolicy ?? AliasPolicy.Default).WithRegistryAsync(registry, cancellationToken).ConfigureAwait(false); // brief 29 Answers 4
            AliasChanges = await registry.UpdateLearnedGlossaryAsync(g => AliasLifecycle.RecordVerified(g, spec, graph.DocumentId, "replay", tie, processedAt,
                policy), cancellationToken).ConfigureAwait(false);

            if (shadowReplay is { } n)
            {
                decision = decision with
                {
                    FormatStatus = FormatLifecycle.Shadow,
                    ShadowReplay = n,
                    ConfirmNewFormat = true,
                    Badge = $"format learned · shadow {n}/{FormatLifecycle.ShadowReplays}: {spec.CarrierName} {spec.FormatId} v{spec.Version} · 0 LLM calls",
                };
            }
        }
        else if (match.Format is not null)
        {
            if (decision.Tier == RoutingTier.Review || decision.HardFailures > 0)
            {
                await FormatLifecycle.ResetShadowCountAsync(registry, match.Format, cancellationToken).ConfigureAwait(false);
            }

            decision = decision with { FormatStatus = await FormatLifecycle.StatusAsync(registry, match.Format, cancellationToken).ConfigureAwait(false) };
        }

        return (match, replay, decision);
    }

    /// <summary>Identity-token containment at or above which a format is a recovery candidate (08 Answers 2).</summary>
    public const double RecoveryIdentityContainment = 0.5;

    private sealed record Recovered(CarrierFormat Spec, ReplayResult Replay, string From);

    /// <summary>
    /// Format recovery on a fingerprint miss (08 Answers 2): the formats of the same file kind whose carrier matches the
    /// submission's hint, or whose identity tokens are ≥ 50 % on the first page, are replayed (0 model calls, ms each).
    /// A candidate is usable when it reads claim rows and either verifies (no hard failure, every header mapped), needs
    /// only a delta for unmapped headers, or fails only on OCR-level values (→ Review with pre-fill). Structural failures
    /// or no rows reject it. Learning happens only when no candidate is usable.
    /// </summary>
    /// <param name="glossary">Brief 30 item 18: the registry's glossary (seed + active learned aliases), the one a fingerprint hit replays with.</param>
    private async Task<Recovered?> RecoverAsync(DocumentGraph graph, FingerprintMatch match, RuleContext? context, SubmissionContext? submission, bool scanned,
        Glossary glossary, CancellationToken cancellationToken)
    {
        var hint = submission?.CarrierHint;
        var tokens = FingerprintText.PageTokens(graph);
        var index = (await registry.GetIndexAsync(cancellationToken).ConfigureAwait(false)).Where(e => e.FileKind == match.Fingerprint.FileKind).ToList();
        ((int Rank, int Rows, int Soft, int Issues, double Identity) Score, Recovered Candidate)? best = null;
        foreach (var formatId in index.Select(e => e.FormatId).Distinct(StringComparer.Ordinal))
        {
            var spec = await registry.GetAsync(formatId, null, cancellationToken).ConfigureAwait(false);
            if (spec is null || await FormatLifecycle.StatusAsync(registry, spec, cancellationToken).ConfigureAwait(false) == FormatLifecycle.Retired)
            {
                continue;
            }

            var carrierHit = hint is not null && FormatFingerprinter.SameCarrier(hint, spec.CarrierName);
            // A spec learned from a page without a title line has no identity text; its carrier name is the identity then.
            var identityText = spec.Fingerprint.IdentityText.Length > 0 ? spec.Fingerprint.IdentityText : CanonicalComparer.NormalizeText(spec.CarrierName);
            var identity = identityText.Length == 0 ? 0 : FingerprintText.Containment(identityText, tokens);
            if (!carrierHit && identity < RecoveryIdentityContainment)
            {
                continue;
            }

            ReplayResult replay;
            try
            {
                replay = ReplayEngine.Execute(graph, spec, context, glossary, submission: submission);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                continue; // brief 30 item 38: a candidate that cannot replay is unusable; it never fails the document's routing
            }

            var hard = replay.RuleOutcomes.Count(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard);
            var unmapped = replay.UnmappedHeaders.Count;
            // On a scan, a candidate that reads rows with every header mapped is the reviewer's pre-fill even when its failures
            // look structural: OCR also breaks period headers (claims land in the wrong period), and a learn from OCR text
            // almost never verifies (08 run: 2 of ~40 scan learns published, 0 on fax).
            var rank = replay.Stats.ClaimRows == 0 ? 0
                : hard == 0 && unmapped == 0 ? 3
                : hard == 0 ? 2
                : OcrFailures.Classify(replay, scanned).AllOcr || scanned && unmapped == 0 ? 1
                : 0;
            if (rank == 0)
            {
                continue;
            }

            var score = (rank, replay.Stats.ClaimRows, -replay.RuleOutcomes.Count(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Soft), -replay.Issues.Count, identity);
            if (best is null || score.CompareTo(best.Value.Score) > 0)
            {
                best = (score, new Recovered(spec, replay, carrierHit ? $"carrier hint \"{hint}\", identity tokens {identity:0.00}" : $"identity tokens {identity:0.00}"));
            }
        }

        return best?.Candidate;
    }

    /// <summary>"policyPeriods[1].claims[0].indemnity.paid" → "claim.indemnity.paid" (counter key per canonical field).</summary>
    internal static string FieldKey(string lineagePath)
    {
        var path = System.Text.RegularExpressions.Regex.Replace(lineagePath, @"\[\d+\]", "");
        return path.StartsWith("policyPeriods.claims.", StringComparison.Ordinal) ? "claim." + path["policyPeriods.claims.".Length..]
            : path.StartsWith("policyPeriods.", StringComparison.Ordinal) ? "period." + path["policyPeriods.".Length..]
            : "document." + path;
    }
}
