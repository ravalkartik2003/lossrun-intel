using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Formats.Replay;
using LossRun.Rules;

namespace LossRun.Learning;

/// <param name="Match">Fingerprint result of the first routing.</param>
/// <param name="Decision">Routing decision of the first pass (what the badge shows before learning).</param>
/// <param name="Replay">Final replay: of the matched spec, or of the newly published spec on the same document.</param>
/// <param name="Learn">Learn/delta outcome when the model was used; null on a replay hit.</param>
/// <param name="FinalTier">ReplayAccepted, Learned, Delta, or Review.</param>
public sealed record LearningResult(FingerprintMatch Match, RoutingDecision Decision, ReplayResult? Replay, LearnOutcome? Learn, string FinalTier,
    IReadOnlyList<UsageEvent> SecondOpinionUsage)
{
    public CarrierFormat? Format => Learn?.Published == true ? Learn.Spec : Match.Format;

    /// <summary>
    /// Queue a "confirm new format" review item: the document that taught a new (shadow) format, and the shadow
    /// replays after it (06 Answers 4). Processing is not held; the item asks an underwriter to confirm the format.
    /// </summary>
    public bool ConfirmNewFormat => Decision.ConfirmNewFormat || (Learn?.Published == true && Learn.Spec!.Status == FormatLifecycle.Shadow);
}

/// <summary>
/// Brief 34 A2: a format's last accepted documents, which a drift repair is also verified on (brief 30 item 19). Production keeps them in Blob
/// (<c>formats/{id}/exemplars.json</c>, <c>UploadActivities</c>); the evaluation runner in files under its run folder, so a tuning run exercises
/// the same repair gate as the live system.
/// </summary>
public interface IExemplarStore
{
    /// <summary>The format's accepted documents, newest first, at most <see cref="LearnGate.Exemplars"/>.</summary>
    Task<IReadOnlyList<DocumentGraph>> GetAsync(string formatId, CancellationToken cancellationToken = default);

    /// <summary>A document of the format was accepted (replayed, learned or repaired without review).</summary>
    Task RecordAsync(string formatId, DocumentGraph graph, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fingerprint → route → (replay | delta | learn) for one loss-run graph. The model runs only on Learn and
/// ReplayWithDelta; a published spec makes the next document of the format a zero-model-call replay.
/// </summary>
/// <param name="exemplars">Brief 34 A2: the format's accepted documents for the repair gate; null skips that cross-check (and records nothing).</param>
public sealed class LearningPipeline(IFormatRegistry registry, IEmbeddingService? embeddings, FormatLearner learner, FormatRepairer repairer,
    SecondOpinionOcr? secondOpinion = null, FormatRouter? router = null, AliasPolicy? aliasPolicy = null, IExemplarStore? exemplars = null)
{
    public async Task<LearningResult> ProcessAsync(DocumentGraph graph, string runId, DateTimeOffset processedAt, SubmissionContext? submission = null,
        RuleContext? context = null, byte[]? sourcePdf = null, CancellationToken cancellationToken = default)
    {
        var memory = new FormatMemory(registry, embeddings, router, aliasPolicy);
        var (match, replay, decision) = await memory.ProcessAsync(graph, runId, processedAt, context, submission, cancellationToken).ConfigureAwait(false);

        var ocrUsage = new List<UsageEvent>();
        if (secondOpinion is { Enabled: true } && sourcePdf is not null && decision.Tier is RoutingTier.Learn or RoutingTier.Review
            && SecondOpinionOcr.PagesNeedingSecondOpinion(graph, replay) is { Count: > 0 } pages)
        {
            foreach (var page in pages)
            {
                var (_, _, usage) = await secondOpinion.MergePageAsync(graph, sourcePdf, page, runId, CanonicalData.Glossary, cancellationToken).ConfigureAwait(false);
                ocrUsage.AddRange(usage);
            }

            (match, replay, decision) = await memory.ProcessAsync(graph, runId, processedAt, context, submission, cancellationToken).ConfigureAwait(false);
        }

        LearningResult result;
        switch (decision.Tier)
        {
            case RoutingTier.ReplayWithDelta when match.Format is not null && replay is not null:
            {
                // Brief 34 A2: as in production (UploadActivities.DeltaAsync), the repair must keep the format's accepted documents verifying.
                var accepted = exemplars is null ? null : await exemplars.GetAsync(match.Format.FormatId, cancellationToken).ConfigureAwait(false);
                var outcome = await repairer.RepairAsync(graph, match.Format, replay.UnmappedHeaders, runId, submission, sourcePdf, cancellationToken, accepted)
                    .ConfigureAwait(false);
                result = new LearningResult(match, decision, outcome.Published ? outcome.Verification!.Replay : replay, outcome,
                    outcome.Published ? (outcome.Spec!.Learning?.Stage == "delta" ? "Delta" : "Learned") : "Review", ocrUsage);
                break;
            }

            case RoutingTier.Learn:
            {
                var reason = string.Join("; ", decision.Reasons);
                var outcome = await learner.LearnAsync(new LearnRequest(graph, runId, submission, match.Format, reason, sourcePdf), cancellationToken).ConfigureAwait(false);
                result = new LearningResult(match, decision, outcome.Verification?.Replay, outcome, outcome.Published ? "Learned" : "Review", ocrUsage);
                break;
            }

            default:
                result = new LearningResult(match, decision, replay, null, decision.Tier.ToString(), ocrUsage);
                break;
        }

        // Brief 34 A2: an accepted document becomes an exemplar of its format (production: ValidateAndPersist → RecordExemplarAsync).
        if (exemplars is not null && result.FinalTier is "ReplayAccepted" or "Learned" or "Delta" && (result.Learn?.Spec?.FormatId ?? match.Format?.FormatId) is { } formatId)
        {
            await exemplars.RecordAsync(formatId, graph, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}
