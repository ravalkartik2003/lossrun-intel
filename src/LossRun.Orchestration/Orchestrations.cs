using LossRun.Api.Contracts;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace LossRun.Orchestration;

// Orchestrator logic (brief 07), hosted by thin [OrchestrationTrigger] functions in LossRun.Functions and tested with a
// fake TaskOrchestrationContext. Deterministic: no I/O, clock only via context.CurrentUtcDateTime; every payload is small
// (ids and blob paths), because Durable Task Scheduler messages are capped at 1 MB.

/// <summary>
/// Brief 33 (ADR 0028): the activities that are safe to run again (every one idempotent: keyed writes, put-if-absent, the decision and completion
/// keys, ParseFile's reuse of stored CU results) retry a failure only when config/retry-policy.json classifies it transient. The layers below
/// already retried what they can (Blob read-modify-write, the Azure SDK pipelines, EF on SQL), so a permanent or unknown failure fails at once,
/// with its cause. Learn and Delta are never retried here: paid model calls with their own cap and 429 loop.
/// </summary>
public static class ActivityRetry
{
    /// <param name="retrying">Called before each retry with "retry 2/3: HttpRequestException", for the progress line.</param>
    public static TaskOptions Storage(RetryConfig config, Action<string>? retrying = null) => TaskOptions.FromRetryHandler(async ctx =>
    {
        var policy = config.Storage;
        var failureClass = FailureClassifier.Classify(ctx.LastFailure, config);
        if (failureClass == FailureClass.Unknown)
        {
            // Not retried: the type is not in the policy. The failure reaches FailUpload (or the decision loop), which escalates it by its type.
            ctx.OrchestrationContext.CreateReplaySafeLogger("LossRun.Retry").LogWarning("Activity failure of unknown type {ErrorType} is not retried (config/retry-policy.json)",
                ctx.LastFailure.ErrorType);
        }

        if (failureClass != FailureClass.Transient || ctx.LastAttemptNumber >= policy.MaxAttempts || ctx.TotalRetryTime >= policy.MaxTotal)
        {
            return false;
        }

        retrying?.Invoke($"retry {ctx.LastAttemptNumber + 1}/{policy.MaxAttempts}: {FailureClassifier.Short(ctx.LastFailure.ErrorType)}");
        await ctx.OrchestrationContext.CreateTimer(policy.Delay(ctx.LastAttemptNumber), CancellationToken.None).ConfigureAwait(true);
        return true;
    });
}

public static class ActivityNames
{
    public const string ExpandUpload = nameof(ExpandUpload);
    public const string ParseFile = nameof(ParseFile);
    public const string RecordItems = nameof(RecordItems);
    public const string FailUpload = nameof(FailUpload);
    public const string Route = nameof(Route);
    public const string Delta = nameof(Delta);
    public const string Learn = nameof(Learn);
    public const string ValidateAndPersist = nameof(ValidateAndPersist);
    public const string CompleteUpload = nameof(CompleteUpload);
    public const string ApplyDecision = nameof(ApplyDecision);
    public const string ExpireReviews = nameof(ExpireReviews);
}

public static class OrchestratorNames
{
    public const string IngestUpload = "IngestUploadOrchestrator";
    public const string ProcessLossRun = "ProcessLossRun";

    public const string ReviewDecisionEvent = "ReviewDecision";

    /// <summary>Instance id of a document's sub-orchestration (the SSE endpoint reads its custom status).</summary>
    public static string DocumentInstance(string uploadId, string documentId) => $"{uploadId}~{documentId}";
}

/// <param name="ReviewTimeout">Durable timer raced with the review decision (default 7 days, from configuration).</param>
/// <param name="Retry">Brief 33: config/retry-policy.json, carried in the input because an orchestrator never reads a file; null uses the embedded copy.</param>
public sealed record UploadInput(string UploadId, TimeSpan ReviewTimeout, RetryConfig? Retry = null);

public sealed record FileWork(string UploadId, string FileName);

/// <summary>One document graph the parse produced that goes through format memory (a loss run or a loss-run segment).</summary>
/// <param name="Retry">Brief 33: the upload's retry policy, handed to the document's sub-orchestration.</param>
public sealed record DocumentWork(string UploadId, string DocumentId, string FileName, bool IsPdf, RetryConfig? Retry = null);

/// <param name="CacheMisses">Brief 34 Answers 1: stored CU results not reused because a hash moved (the Functions trigger logs them).</param>
public sealed record ParseResult(IReadOnlyList<ManifestItem> Items, IReadOnlyList<DocumentWork> LossRuns, IReadOnlyList<string>? CacheMisses = null);

public sealed record ItemsInput(string UploadId, IReadOnlyList<ManifestItem> Items);

/// <summary>Brief 23 follow-up: an upload whose orchestration failed (the error is in the orchestration status and telemetry).</summary>
/// <param name="Step">Brief 26: the activity or sub-orchestration that failed; <paramref name="ErrorType"/> its exception type (the escalation key).</param>
/// <param name="Classification">Brief 33: "permanent: InvalidOperationException" — whether anyone should expect it to heal.</param>
public sealed record FailInput(string UploadId, string Error, string? Step = null, string? ErrorType = null, string? Classification = null);

/// <param name="Tier">Routing tier name (RoutingTier).</param>
/// <param name="FormatRef">Matched format (null on a miss).</param>
/// <param name="Unmapped">Unmapped headers (delta input).</param>
public sealed record RouteResult(string DocumentId, string Tier, string? FormatRef, bool Drift, IReadOnlyList<string> Unmapped, IReadOnlyList<string> Reasons, string Badge,
    bool ReplaySaved, decimal CostSoFar);

public sealed record LearnInput(DocumentWork Work, RouteResult Route);

/// <param name="Published">A verified spec was published (else Review with the best attempt).</param>
public sealed record LearnResult(string DocumentId, bool Published, string? FormatRef, string? FormatStatus, IReadOnlyList<string> Reasons, int ModelCalls,
    decimal CostSoFar);

public sealed record PersistInput(DocumentWork Work, RouteResult Route, LearnResult? Learn);

/// <param name="ReviewKind">null, document-review (waits for a decision) or confirm-format (processed; waits for confirmation).</param>
public sealed record DocumentOutcome(string DocumentId, string FinalTier, string? ReviewKind, int ClaimCount, decimal CostUsd);

/// <summary>Final tiers of a decision that did not apply for a reason other than a failure.</summary>
public static class DecisionOutcomes
{
    /// <summary>Brief 32 item 1: a concurrent decision on the same review committed first; this one changed nothing and the review is not open.</summary>
    public const string NotPending = "not-pending";
}

public sealed record CompleteInput(string UploadId, IReadOnlyList<DocumentOutcome> Outcomes);

public sealed record UploadCompletion(string UploadId, string Status, decimal CostUsd, int AwaitingReview);

public sealed record ExpireInput(string UploadId, IReadOnlyList<string> DocumentIds);

public sealed record UploadSummary(string UploadId, string Status, int Documents, int ReviewsDecided, int ReviewsExpired);

public static class UploadOrchestration
{
    /// <summary>
    /// Upload → parse files (fan-out) → manifest → one ProcessLossRun sub-orchestration per loss run (fan-out) →
    /// complete (manifest, cost, webhook) → wait for review decisions raced with a durable timer.
    /// The review wait lives here, after completion, so an upload is "completed" (or "awaiting-review") as soon as every
    /// document is processed; the decisions then arrive as external events addressed to the upload instance.
    /// </summary>
    public static async Task<UploadSummary> RunAsync(TaskOrchestrationContext context, UploadInput input)
    {
        var started = context.CurrentUtcDateTime;
        void Status(string step, string? detail = null, decimal cost = 0) =>
            context.SetCustomStatus(new UploadProgress(step, null, (long)(context.CurrentUtcDateTime - started).TotalMilliseconds, cost, detail));
        var retry = input.Retry ?? RetryConfig.Embedded;
        TaskOptions Storage(string step) => ActivityRetry.Storage(retry, detail => Status(step, detail));

        Status("expand");
        var files = await context.CallActivityAsync<IReadOnlyList<FileWork>>(ActivityNames.ExpandUpload, input.UploadId, Storage("expand"));

        Status("parse", $"{files.Count} file(s)");
        DocumentOutcome[] outcomes;
        UploadCompletion completion;
        try
        {
            // Brief 33: ParseFile is retried like the storage activities now that a retry reuses the stored CU results instead of billing again.
            var parsed = await Task.WhenAll(files.Select(f => context.CallActivityAsync<ParseResult>(ActivityNames.ParseFile, f, Storage("parse"))));
            var items = parsed.SelectMany(p => p.Items).ToList();
            var lossRuns = parsed.SelectMany(p => p.LossRuns).Select(w => w with { Retry = retry }).ToList();
            await context.CallActivityAsync(ActivityNames.RecordItems, new ItemsInput(input.UploadId, items), Storage("record"));

            Status("process", $"{lossRuns.Count} loss run(s), {items.Count - lossRuns.Count} other item(s)");
            outcomes = await Task.WhenAll(lossRuns.Select(work => context.CallSubOrchestratorAsync<DocumentOutcome>(OrchestratorNames.ProcessLossRun, work,
                new SubOrchestrationOptions { InstanceId = OrchestratorNames.DocumentInstance(input.UploadId, work.DocumentId) })));

            completion = await context.CallActivityAsync<UploadCompletion>(ActivityNames.CompleteUpload, new CompleteInput(input.UploadId, outcomes), Storage("complete"));
        }
        catch (TaskFailedException e)
        {
            // Brief 23 follow-up: a failed parse left the manifest "processing" for good (the live outage of a missing
            // changeset). The manifest says failed; the orchestration still fails, with the error in its status.
            // Brief 33: the status and the escalation say whether the cause is expected to heal.
            var classification = e.FailureDetails is { } details
                ? FailureClassifier.Describe(FailureClassifier.Classify(details, retry), details.ErrorType, details.ErrorMessage) : null;
            await context.CallActivityAsync(ActivityNames.FailUpload, new FailInput(input.UploadId, e.Message, e.TaskName, e.FailureDetails?.ErrorType,
                e.FailureDetails is { } failure ? FailureClassifier.Reason(failure, retry) : null), Storage("fail"));
            Status(UploadStatuses.Failed, classification ?? e.Message);
            throw;
        }
        var pending = outcomes.Where(o => o.ReviewKind is not null).Select(o => o.DocumentId).ToHashSet(StringComparer.Ordinal);
        Status(completion.Status, pending.Count == 0 ? null : $"{pending.Count} review item(s) open", completion.CostUsd);

        var (decided, expired) = (0, 0);
        var deadline = started + input.ReviewTimeout;
        while (pending.Count > 0)
        {
            using var cancel = new CancellationTokenSource();
            var decision = context.WaitForExternalEvent<ReviewDecision>(OrchestratorNames.ReviewDecisionEvent, cancel.Token);
            var timer = context.CreateTimer(deadline, cancel.Token);
            var winner = await Task.WhenAny(decision, timer);
            cancel.Cancel();
            if (winner == timer)
            {
                await context.CallActivityAsync(ActivityNames.ExpireReviews, new ExpireInput(input.UploadId, [.. pending.Order(StringComparer.Ordinal)]), Storage("expire"));
                expired = pending.Count;
                break;
            }

            var d = await decision;
            if (!pending.Remove(d.DocumentId))
            {
                continue; // a decision for a document that is not (or no longer) open
            }

            DocumentOutcome applied;
            try
            {
                applied = await context.CallActivityAsync<DocumentOutcome>(ActivityNames.ApplyDecision, d, Storage("decision"));
            }
            catch (TaskFailedException e)
            {
                // Brief 32 item 3: the activity failed after its retries (its own failure path failed too). The review stays open, and the loop
                // keeps waiting, so the upload's other decisions are still applied.
                pending.Add(d.DocumentId);
                var why = e.FailureDetails is { } details
                    ? FailureClassifier.Describe(FailureClassifier.Classify(details, retry), details.ErrorType, details.ErrorMessage) : "error";
                Status(UploadStatuses.AwaitingReview, $"{d.DocumentId}: decision activity failed ({why}); {pending.Count} open", completion.CostUsd);
                continue;
            }

            if (applied.ReviewKind is not null)
            {
                pending.Add(d.DocumentId); // brief 30 item 6: not applied (escalated); the review stays open for the next decision
                Status(UploadStatuses.AwaitingReview, $"{d.DocumentId}: decision not applied; {pending.Count} open", completion.CostUsd);
                continue;
            }

            decided++;
            Status(pending.Count == 0 ? UploadStatuses.Completed : UploadStatuses.AwaitingReview, $"{d.Action} {d.DocumentId}; {pending.Count} open", completion.CostUsd);
        }

        var final = expired > 0 ? "reviews-expired" : UploadStatuses.Completed;
        Status(final, null, completion.CostUsd);
        return new UploadSummary(input.UploadId, final, outcomes.Length, decided, expired);
    }
}

public static class LossRunOrchestration
{
    /// <summary>Fingerprint → route → replay | delta | learn → validate → persist (custom status at every step).</summary>
    public static async Task<DocumentOutcome> RunAsync(TaskOrchestrationContext context, DocumentWork work)
    {
        var started = context.CurrentUtcDateTime;
        void Status(string step, decimal cost, string? detail = null) =>
            context.SetCustomStatus(new UploadProgress(step, work.DocumentId, (long)(context.CurrentUtcDateTime - started).TotalMilliseconds, cost, detail));

        var retry = work.Retry ?? RetryConfig.Embedded;
        TaskOptions Storage(string step) => ActivityRetry.Storage(retry, detail => Status(step, 0, detail));

        Status("fingerprint", 0);
        var route = await context.CallActivityAsync<RouteResult>(ActivityNames.Route, work, Storage("fingerprint"));
        Status("route", route.CostSoFar, $"{route.Tier}: {route.Badge}");

        LearnResult? learned = null;
        if (route.Tier == "ReplayWithDelta")
        {
            await AcquireLearnSlotAsync(context, () => Status("delta-queued", route.CostSoFar, "waiting for a model slot"));
            Status("delta", route.CostSoFar, string.Join(", ", route.Unmapped));
            learned = await GatedAsync(context, ActivityNames.Delta, new LearnInput(work, route));
        }
        else if (route.Tier == "Learn")
        {
            if (await AcquireLearnSlotAsync(context, () => Status("learn-queued", route.CostSoFar, "waiting for a model slot")))
            {
                // 12b: a document that queued behind the learn of its own new format (a burst of one carrier's files in one
                // upload) routes again once it has the slot, and replays the format learned meanwhile instead of learning it again.
                var again = await context.CallActivityAsync<RouteResult>(ActivityNames.Route, work, Storage("route"));
                route = again.Tier == "Learn" ? route : again;
            }

            if (route.Tier == "Learn")
            {
                Status("learn", route.CostSoFar, route.Reasons.FirstOrDefault());
                learned = await GatedAsync(context, ActivityNames.Learn, new LearnInput(work, route));
            }
            else if (route.Tier == "ReplayWithDelta")
            {
                Status("delta", route.CostSoFar, string.Join(", ", route.Unmapped));
                learned = await GatedAsync(context, ActivityNames.Delta, new LearnInput(work, route));
            }
            else
            {
                await context.Entities.SignalEntityAsync(LearnGate.Id, nameof(LearnGate.Release), context.InstanceId);
                Status("replay", route.CostSoFar, $"{route.Badge} (learned while queued)");
            }
        }
        else
        {
            Status("replay", route.CostSoFar, route.Badge);
        }

        Status("validate", learned?.CostSoFar ?? route.CostSoFar);
        var outcome = await context.CallActivityAsync<DocumentOutcome>(ActivityNames.ValidateAndPersist, new PersistInput(work, route, learned), Storage("validate"));
        Status("done", outcome.CostUsd, outcome.ReviewKind is null ? outcome.FinalTier : $"{outcome.FinalTier} · {outcome.ReviewKind}");
        return outcome;
    }

    /// <summary>Waits (durable timer, no busy loop) until <see cref="LearnGate"/> grants this instance a model slot; true when it had to wait.</summary>
    private static async Task<bool> AcquireLearnSlotAsync(TaskOrchestrationContext context, Action queued)
    {
        var first = true;
        while (!await context.Entities.CallEntityAsync<bool>(LearnGate.Id, nameof(LearnGate.TryAcquire), new LearnSlotRequest(context.InstanceId, context.CurrentUtcDateTime)))
        {
            if (first)
            {
                queued();
                first = false;
            }

            await context.CreateTimer(LearnGate.RetryEvery, CancellationToken.None);
        }

        return !first;
    }

    /// <summary>Runs a learn/delta activity in an acquired slot and releases the slot even when the activity fails.</summary>
    private static async Task<LearnResult> GatedAsync(TaskOrchestrationContext context, string activity, LearnInput input)
    {
        try
        {
            return await context.CallActivityAsync<LearnResult>(activity, input);
        }
        finally
        {
            await context.Entities.SignalEntityAsync(LearnGate.Id, nameof(LearnGate.Release), context.InstanceId);
        }
    }
}
