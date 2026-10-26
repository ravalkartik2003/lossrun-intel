using System.Diagnostics;
using System.Text.Json;
using LossRun.Contracts;
using LossRun.Formats;

namespace LossRun.Learning;

/// <summary>
/// Delta repair (brief 06 §3; carrier-format-memory.md KQ7 "re-learn scope should match the drift"): the delta model sees
/// only the unmapped headers, three sample values each, the glossary (cached prefix) and the existing column map, and
/// returns column-map additions only. The repaired spec is re-verified and published as v(n+1) with <c>supersedes</c>;
/// if verification fails, the full learn loop runs once with the old spec as its prior.
/// </summary>
public sealed class FormatRepairer(ILearnModel model, FormatLearner learner, LearnOptions options)
{
    /// <param name="exemplars">Brief 30 item 19: the format's last accepted documents (at most <see cref="LearnGate.Exemplars"/> are used). The repair must
    /// verify on each of them too; one that it breaks rejects the delta, and the escalated learn verifies on them as well.</param>
    public async Task<LearnOutcome> RepairAsync(DocumentGraph graph, CarrierFormat spec, IReadOnlyList<string> unmapped, string runId, SubmissionContext? submission = null,
        byte[]? sourcePdf = null, CancellationToken cancellationToken = default, IReadOnlyList<DocumentGraph>? exemplars = null)
    {
        exemplars = [.. (exemplars ?? []).Where(e => e.DocumentId != graph.DocumentId).Take(LearnGate.Default.Exemplars)];
        var stopwatch = Stopwatch.StartNew();
        var glossary = await learner.LearnedGlossaryAsync(cancellationToken).ConfigureAwait(false);
        var response = await model.CompleteAsync(new ModelRequest("delta", options.DeltaDeployment, options.DeltaModelVersion, PromptBuilder.StaticPrefix,
            [new ModelTurn("user", PromptBuilder.DeltaDocument(graph, spec, unmapped))], "column_map_delta", ModelSchema.Delta, null, FormatLearner.CacheKey,
            graph.DocumentId, runId), cancellationToken).ConfigureAwait(false);

        var attempts = new List<LearnAttempt>();
        string? error = null;
        CarrierFormat? candidate = null;
        Verification? verification = null;
        try
        {
            var (additions, ignored) = ModelSchema.ToDelta(response.Json);
            candidate = Apply(spec, graph, unmapped, additions, ignored);
            verification = SpecVerifier.Verify(graph, candidate, submission, glossary);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            error = e.Message;
        }

        attempts.Add(new LearnAttempt("delta", 1, candidate, verification, error, response));
        string? broken = null;
        if (candidate is not null && verification!.Clean(options.SpreadsheetMinRequiredCoverage))
        {
            foreach (var exemplar in exemplars)
            {
                // The exemplar's values must still read right: no hard failure, no issue, nothing required lost, printed totals not contradicted.
                // A header it prints that the new version no longer maps is expected (a renamed column: the new version reads the new name).
                var check = SpecVerifier.Verify(exemplar, candidate, null, glossary);
                if (check.HardFailures.Count > 0 || check.Issues.Count > 0 || check.MissingRequired.Count > 0 || check.TotalsTie == false || check.Replay.Stats.ClaimRows == 0)
                {
                    broken = $"breaks exemplar {exemplar.DocumentId} (an accepted document of {spec.FormatId}): {check.Summary}";
                    break;
                }
            }
        }

        if (candidate is not null && broken is null && verification!.Clean(options.SpreadsheetMinRequiredCoverage))
        {
            var published = await learner.PublishAsync(candidate, graph, "delta", spec, attempts, false, glossary, cancellationToken).ConfigureAwait(false);
            verification = SpecVerifier.Verify(graph, published, submission, glossary);
            return FormatLearner.Outcome(true, published, verification, attempts, [$"delta repaired {spec.Ref} → {published.Ref}: {verification.Summary}"], stopwatch.Elapsed);
        }

        // Escalate once to the full learn loop with the old spec as prior — never from a low-resolution scan (08b Answers 5:
        // the 08c run escalated a 150 dpi fax's failed delta to a 5-call learn).
        var reason = broken is not null
            ? $"delta repair for {string.Join(", ", unmapped)} verified on {graph.DocumentId} but {broken}"
            : $"delta repair for {string.Join(", ", unmapped)} did not verify ({verification?.Summary ?? error})";
        if (FormatRouter.IsLowResolutionScan(graph))
        {
            return FormatLearner.Outcome(false, null, verification, attempts,
                [$"delta failed: {reason}; {graph.SourceFile.ScanDpi} dpi scan is not learned from → review with pre-fill"], stopwatch.Elapsed);
        }

        var learned = await learner.LearnAsync(new LearnRequest(graph, runId, submission, spec, reason, sourcePdf, exemplars.Count > 0 ? exemplars : null), cancellationToken)
            .ConfigureAwait(false);
        attempts.AddRange(learned.Attempts);
        return FormatLearner.Outcome(learned.Published, learned.Spec, learned.Verification, attempts, [$"delta failed → escalated to learn: {reason}", .. learned.Reasons],
            stopwatch.Elapsed);
    }

    /// <summary>
    /// Applies a delta deterministically: only additions whose source is one of the unmapped headers are kept (anything
    /// else the model returns is dropped); an addition whose target was mapped from a header no longer printed replaces
    /// that entry (a renamed column), so the stale source does not linger.
    /// </summary>
    internal static CarrierFormat Apply(CarrierFormat spec, DocumentGraph graph, IReadOnlyList<string> unmapped, IReadOnlyList<ColumnMapping> additions, IReadOnlyList<string> ignored)
    {
        static string Key(string s) => CanonicalComparer.NormalizeText(s);
        var allowed = unmapped.Select(Key).ToHashSet(StringComparer.Ordinal);
        var printed = graph.Tables.SelectMany(t => t.HeaderTexts).Select(Key).ToHashSet(StringComparer.Ordinal);
        var candidate = FormatJson.Parse(FormatJson.Write(spec));
        var kept = additions.Where(a => allowed.Contains(Key(a.Source))).ToList();
        candidate.ColumnMap.RemoveAll(c => !printed.Contains(Key(c.Source)) && kept.Any(a => a.Target == c.Target));
        candidate.ColumnMap.AddRange(kept);
        candidate.IgnoredHeaders.AddRange(ignored.Where(h => allowed.Contains(Key(h)) && !kept.Any(a => Key(a.Source) == Key(h))));
        SpecLint.RetargetCodedColumns(candidate); // the delta model maps WCIO code columns like the learn model does (brief 13, live)
        candidate.Version = spec.Version + 1;
        return candidate;
    }
}
