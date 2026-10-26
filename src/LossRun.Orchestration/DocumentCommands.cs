using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;

namespace LossRun.Orchestration;

/// <summary>
/// Document actions outside the upload orchestration (brief 12b): reopen a processed document for review (11 Answers 2),
/// decide a reopened review, and an underwriter's direct override. The orchestration that waited for review decisions
/// has finished for these documents, so the decision is applied here through the same command path
/// (<see cref="DocumentEdits"/> → <see cref="IRecordStore.TryApplyDecisionAsync"/>: overrides, lineage, re-validation,
/// audit, feature-snapshot invalidation) and announced as <c>review.decided</c>.
/// </summary>
public sealed class DocumentCommands(LossRunRuntime runtime)
{
    /// <summary>Retries when another decision took the document's next revision first (concurrent overrides).</summary>
    public const int MaxAttempts = 5;

    private IRecordStore Records => runtime.RecordStore;

    /// <summary><c>POST /reviews/{documentId}/reopen</c>.</summary>
    public async Task<ApiResult> ReopenAsync(Caller caller, string documentId, ReopenRequest? body, CancellationToken cancellationToken = default)
    {
        if (!caller.IsReviewer)
        {
            return ApiResult.Error(403, "forbidden", "Underwriter, UWManager and Admin roles reopen reviews.");
        }

        if (string.IsNullOrWhiteSpace(body?.Reason))
        {
            return ApiResult.Error(400, "no-reason", "Send { reason } — the reopen is audited.");
        }

        if (ErrorResponses.TooLong(("reason", body.Reason)) is { } tooLong)
        {
            return tooLong; // brief 30 item 28
        }

        var result = await Records.GetResultAsync(documentId, cancellationToken).ConfigureAwait(false);
        var uploadId = await Records.UploadOfDocumentAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (result is null || uploadId is null)
        {
            return ApiResult.Error(404, "not-found", $"No processed document '{documentId}'.");
        }

        var existing = await Records.GetReviewAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (existing?.State == "pending")
        {
            return ApiResult.Error(409, "already-in-review", $"Document {documentId} already has an open review.");
        }

        var now = runtime.Clock.GetUtcNow();
        var reason = body.Reason.Trim();
        var item = new ReviewItem
        {
            DocumentId = documentId, UploadId = uploadId, Kind = ReviewKinds.DocumentReview, State = "pending", Reopened = true, OpenedAt = now,
            Reasons = [$"reopened by {caller.UserId} ({caller.Role}): {reason}"], Badge = existing?.Badge, FormatRef = result.FormatRef,
        };
        result.ReviewState = ReviewStates.Reopened;
        await Records.ReopenReviewAsync(item, result, new Actor(caller.UserId, caller.Role, "api"), reason, cancellationToken).ConfigureAwait(false);
        await Records.UpdateManifestAsync(uploadId, m =>
        {
            var manifest = m ?? throw new InvalidOperationException($"Manifest of {uploadId} is missing.");
            if (manifest.Items.FirstOrDefault(i => i.DocumentId == documentId) is { } i)
            {
                (i.Status, i.ReviewKind, i.Reason) = (ItemStatuses.AwaitingReview, ReviewKinds.DocumentReview, item.Reasons[0]);
            }

            manifest.Status = UploadStatuses.AwaitingReview;
            return manifest;
        }, Actor.System, cancellationToken).ConfigureAwait(false);

        // Brief 30 item 16 (ADR 0024): the reopened document leaves the verified view, so the quote moves to a new revision (which also keys
        // every cached feature snapshot) and the quote system is sent quote.revised and the payload at it.
        if ((await Records.DocumentHeadAsync(documentId, cancellationToken).ConfigureAwait(false))?.QuoteId is { } quoteId)
        {
            var readiness = await QuotePushes.ReadinessAsync(Records, quoteId, null, cancellationToken).ConfigureAwait(false);
            var changed = new QuotePushes.ChangedDocument(documentId, result.Revision, result.ReviewState, 0);
            var actor = new Actor(caller.UserId, caller.Role, "api");
            await Records.AuditWithOutboxAsync(new AuditEvent(now, caller.UserId, caller.Role, "", uploadId, documentId), actor,
            [
                QuotePushes.Revised(quoteId, QuoteRevisions.Reopened, documentId, caller.UserId, now),
                QuotePushes.Payload(quoteId, QuoteRevisions.Reopened, readiness, [changed], new System.Text.Json.Nodes.JsonObject
                {
                    ["uploadId"] = uploadId, ["documentId"] = documentId, ["action"] = "reopen", ["reopenedBy"] = caller.UserId, ["reopenedAt"] = now.ToString("O"),
                }, caller.UserId, now, $"documents/{documentId}"),
            ], cancellationToken, new QuoteRevisionBump(quoteId, QuoteRevisions.Reopened, documentId, result.Revision)).ConfigureAwait(false);
        }

        if (runtime.ChatIndex is { } chatIndex)
        {
            await chatIndex.EnqueueAsync(uploadId, cancellationToken).ConfigureAwait(false); // brief 32 item 7: its chunks become superseded
        }

        return new ApiResult(201, item);
    }

    /// <summary>A decision on a reopened review: applied now (the upload orchestration is no longer waiting).</summary>
    public async Task<ApiResult> DecideReopenedAsync(ReviewDecision decision, CancellationToken cancellationToken = default)
    {
        var outcome = await new UploadActivities(runtime).ApplyDecisionAsync(decision, cancellationToken).ConfigureAwait(false);
        if (outcome.FinalTier == DecisionOutcomes.NotPending)
        {
            return ApiResult.Error(409, "not-pending", $"Review {decision.DocumentId} was decided by a concurrent decision; this one changed nothing.");
        }

        return outcome.ReviewKind is not null // brief 30 items 6–7: not applied; the review is still open
            ? ApiResult.Error(409, "decision-not-applied", $"The decision on {decision.DocumentId} could not be applied (the document changed or the store refused); it is still in review.")
            : ApiResult.Ok(new { decision, outcome });
    }

    /// <summary>Brief 30 item 3: every override's newValue is typed JSON of its field's canonical type; the first that is not is a 400 naming the path.</summary>
    public static ApiResult? OverrideValueError(IReadOnlyList<FieldOverride>? overrides) =>
        overrides?.Select(o => OverrideTypes.Validate(o.FieldPath, o.NewValue)).FirstOrDefault(e => e is not null) is { } error
            ? ApiResult.Error(400, "bad-override-value", error)
            : null;

    /// <summary>Brief 34 A3: the format changes a decision may carry. One key for now: the reviewer confirms the carrier's export fills the policy down.</summary>
    public const string FillDownChange = "periods.fillDown";

    /// <summary>Brief 35 Answers 2: the reviewer confirms the printed carrier name is another legal name of the format's carrier.</summary>
    public const string CarrierNamesAddChange = "carrierNames.add";

    /// <summary>Longest carrier name a review may add (a legal name, not a paragraph).</summary>
    public const int MaxCarrierNameLength = 200;

    /// <summary>
    /// Brief 34 A3, brief 35 Answers 2: <c>formatChanges</c> answers a question the review asked about the document's format, on a decision that
    /// keeps the document, for a document with a format. <c>{ "periods.fillDown": true }</c> answers the fill-down marker;
    /// <c>{ "carrierNames.add": "&lt;printed name&gt;" }</c> answers "carrier name on the page differs from the format's". A 400 names what is wrong.
    /// </summary>
    public static ApiResult? FormatChangesError(IReadOnlyDictionary<string, System.Text.Json.JsonElement>? changes, string action, ReviewItem item, string? formatRef)
    {
        if (changes is not { Count: > 0 })
        {
            return null;
        }

        if (changes.Keys.FirstOrDefault(k => k is not (FillDownChange or CarrierNamesAddChange)) is { } unknown)
        {
            return ApiResult.Error(400, "bad-format-change", $"formatChanges: \"{unknown}\" is not a change a review can make (allowed: {FillDownChange}, {CarrierNamesAddChange}).");
        }

        if (changes.TryGetValue(FillDownChange, out var fillDown) && fillDown.ValueKind != System.Text.Json.JsonValueKind.True)
        {
            return ApiResult.Error(400, "bad-format-change", $"formatChanges: {FillDownChange} takes true (a format that fills down is confirmed; nothing else is changed here).");
        }

        if (changes.TryGetValue(CarrierNamesAddChange, out var name)
            && (name.ValueKind != System.Text.Json.JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Length > MaxCarrierNameLength))
        {
            return ApiResult.Error(400, "bad-format-change", $"formatChanges: {CarrierNamesAddChange} takes the carrier name as printed (a non-empty string of at most {MaxCarrierNameLength} characters).");
        }

        if (action == ReviewActions.Reject || formatRef is null)
        {
            return ApiResult.Error(400, "bad-format-change", "formatChanges needs a decision that keeps the document, on a document with a format.");
        }

        if (changes.ContainsKey(FillDownChange) && !item.Reasons.Any(r => r.StartsWith(LossRun.Formats.UnnormalizedReasonText.FillDownReasonPrefix, StringComparison.Ordinal)))
        {
            return ApiResult.Error(400, "bad-format-change", $"formatChanges: {FillDownChange} answers a review that found blank policy cells; this review did not.");
        }

        return changes.ContainsKey(CarrierNamesAddChange) && !item.Reasons.Any(r => r.StartsWith(LossRun.Formats.FormatRouter.CarrierOnPageReasonPrefix, StringComparison.Ordinal))
            ? ApiResult.Error(400, "bad-format-change", $"formatChanges: {CarrierNamesAddChange} answers a review that found a carrier name the format does not know; this review did not.")
            : null;
    }

    /// <summary>Brief 32 item 5: an override whose path names an index the document does not have is a 400 naming it.</summary>
    public static ApiResult? OverridePathError(IReadOnlyList<FieldOverride>? overrides, LossRunDocument document) =>
        overrides?.Select(o => OverrideTypes.IndexError(document, o.FieldPath)).FirstOrDefault(e => e is not null) is { } error
            ? ApiResult.Error(400, "bad-override-path", error)
            : null;

    /// <summary>The overrides with a coded value sent on its <c>.code</c> path moved to the coded field (<see cref="OverrideTypes.Normalize"/>).</summary>
    public static IReadOnlyList<FieldOverride> Normalized(IReadOnlyList<FieldOverride> overrides) =>
        [.. overrides.Select(o => OverrideTypes.Normalize(o.FieldPath, o.NewValue) is var (path, value) && path != o.FieldPath ? o with { FieldPath = path, NewValue = value } : o)];

    /// <summary><c>POST /documents/{documentId}/overrides</c>: an underwriter corrects values on a processed document.</summary>
    public async Task<ApiResult> OverrideAsync(Caller caller, string documentId, OverrideRequest? body, CancellationToken cancellationToken = default)
    {
        if (!caller.IsReviewer)
        {
            return ApiResult.Error(403, "forbidden", "Underwriter, UWManager and Admin roles override values.");
        }

        if (body is not { Overrides.Count: > 0 } || string.IsNullOrWhiteSpace(body.Rationale) || body.Overrides.Any(o => string.IsNullOrWhiteSpace(o.Rationale) || string.IsNullOrWhiteSpace(o.FieldPath)))
        {
            return ApiResult.Error(400, "bad-overrides", "Send { overrides: [{ fieldPath, newValue, rationale }], rationale }.");
        }

        if (ErrorResponses.TooLong(("rationale", body.Rationale), ("an override's rationale", body.Overrides.Select(o => o.Rationale).MaxBy(r => r?.Length ?? 0))) is { } tooLong)
        {
            return tooLong; // brief 30 item 28
        }

        body = body with { Overrides = Normalized(body.Overrides) };
        if (OverrideValueError(body.Overrides) is { } typeError)
        {
            return typeError;
        }

        var head = await Records.DocumentHeadAsync(documentId, cancellationToken).ConfigureAwait(false);
        var current = head is null ? null : await Records.GetResultAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (head is null || current is null)
        {
            return ApiResult.Error(404, "not-found", $"No processed document '{documentId}'.");
        }

        if (OverridePathError(body.Overrides, current.Document) is { } pathError)
        {
            return pathError;
        }

        // A pending document review blocks overrides (decide it instead); a pending format confirmation does not: the values are
        // processed and the reviewer confirms the format, not the numbers.
        if (head is { ReviewState: "pending", ReviewKind: ReviewKinds.DocumentReview })
        {
            return ApiResult.Error(409, "in-review", $"Document {documentId} has an open review; decide it instead.");
        }

        var uploadId = head.UploadId;
        var ruleContext = await RuleContexts.ForUploadAsync(Records, uploadId, cancellationToken).ConfigureAwait(false); // brief 29: R-15, R-25

        var decisionId = ReviewDecision.NewId();
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var before = attempt == 1 ? current : (await Records.GetResultAsync(documentId, cancellationToken).ConfigureAwait(false))!;
            var decision = new ReviewDecision(documentId, ReviewActions.Override, body.Overrides, false, body.Rationale, caller.UserId, caller.Role, runtime.Clock.GetUtcNow(),
                DecisionId: $"{decisionId}-{attempt}");
            DocumentResult after;
            IReadOnlyList<FieldChange> changes;
            try
            {
                (after, changes) = DocumentEdits.Apply(before, decision, ruleContext);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException or FormatException)
            {
                return ApiResult.Error(400, "bad-field-path", e.Message);
            }

            // One round trip (dbo.ApplyDecision): the decision, its review.decided and quote.revised events, the quote revision bump
            // and the quote.payload command commit together (brief 24 step 3; the document is processed, so its upload's push went out).
            var decided = await runtime.DecisionOutboxAsync(head.QuoteId, uploadId, before, after, decision, QuoteRevisions.Override, push: true, cancellationToken)
                .ConfigureAwait(false);
            if (await Records.TryApplyDecisionAsync(new DecisionRecord(uploadId, before, after, decision, changes, false,
                head.QuoteId is null ? null : new QuoteRevisionBump(head.QuoteId, QuoteRevisions.Override),
                KeepReviewOpen: head is { ReviewState: "pending", ReviewKind: ReviewKinds.ConfirmFormat }), cancellationToken, decided).ConfigureAwait(false))
            {
                runtime.PublishSoon(decided);
                await LearnedAliasReview.RetireOnCorrectionsAsync(runtime, uploadId, after, changes, caller.UserId, caller.Role, decision.At, cancellationToken).ConfigureAwait(false);
                var hard = after.RuleOutcomes.Count(o => o.Status == LossRun.Rules.RuleStatus.Fail && o.Severity == LossRun.Rules.RuleSeverity.Hard);
                return ApiResult.Ok(new OverrideResult(documentId, after.Revision, changes.Count(c => c.Kind == "override"), hard, attempt, decision.At));
            }

            await Task.Delay(Random.Shared.Next(10, 40) * attempt, cancellationToken).ConfigureAwait(false);
        }

        return ApiResult.Error(409, "conflict", $"Document {documentId} kept changing; {MaxAttempts} attempts. Retry.");
    }
}
