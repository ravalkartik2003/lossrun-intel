using System.Text;
using System.Reflection;
using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;

namespace LossRun.Orchestration.Tests;

/// <summary>Brief 30 items 6 and 7: decisions and completions applied once, and a failure after the 202 never loses the review.</summary>
public partial class EndToEndTests
{
    /// <summary>Forwards every call to the real store except the ones a test makes throw.</summary>
    public class FaultyRecords : DispatchProxy
    {
        public IRecordStore Inner { get; set; } = null!;
        public HashSet<string> Throw { get; } = new(StringComparer.Ordinal);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Throw.Contains(targetMethod!.Name) ? throw new InvalidOperationException($"injected fault in {targetMethod.Name}") : targetMethod.Invoke(Inner, args);

        public static IRecordStore Wrap(IRecordStore inner, params string[] methods)
        {
            var proxy = Create<IRecordStore, FaultyRecords>();
            var faulty = (FaultyRecords)(object)proxy;
            faulty.Inner = inner;
            faulty.Throw.UnionWith(methods);
            return proxy;
        }
    }

    /// <summary>Brief 32 item 1: runs <see cref="Before"/> (the winning decision) when the loser reaches the store, as two concurrent POSTs interleave.</summary>
    public class RacingRecords : DispatchProxy
    {
        public IRecordStore Inner { get; set; } = null!;
        public Func<Task> Before { get; set; } = () => Task.CompletedTask;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != nameof(IRecordStore.ApplyDecisionAsync))
            {
                return targetMethod.Invoke(Inner, args);
            }

            async Task<DecisionApplyResult> Raced()
            {
                await Before();
                return await (Task<DecisionApplyResult>)targetMethod.Invoke(Inner, args)!;
            }

            return Raced();
        }

        public static IRecordStore Wrap(IRecordStore inner, Func<Task> before)
        {
            var proxy = Create<IRecordStore, RacingRecords>();
            ((RacingRecords)(object)proxy).Inner = inner;
            ((RacingRecords)(object)proxy).Before = before;
            return proxy;
        }
    }

    [Fact]
    public async Task Brief32_item1_the_loser_of_two_concurrent_decisions_on_a_reopened_review_leaves_it_decided()
    {
        var (rig, doc) = await InReviewAsync("Q-B32-1");
        await rig.Activities.ApplyDecisionAsync(OverrideDecision(doc, "dec-b32-0"));
        var commands = new DocumentCommands(rig.Runtime);
        Assert.Equal(201, (await commands.ReopenAsync(Underwriter, doc, new ReopenRequest("broker sent a corrected run"))).Status);
        var reopened = (await rig.Runtime.RecordStore.GetReviewAsync(doc))!;
        Assert.True(reopened.Reopened);

        // Both POSTs passed the pending check; the winner commits while the loser is between computing its edit and writing it.
        var winner = OverrideDecision(doc, "dec-b32-winner");
        var racing = rig.Runtime with { Records = RacingRecords.Wrap(rig.Runtime.RecordStore, async () => Assert.Equal(200, (await commands.DecideReopenedAsync(winner)).Status)) };
        var lost = await new DocumentCommands(racing).DecideReopenedAsync(OverrideDecision(doc, "dec-b32-loser"));

        Assert.Equal((409, "not-pending"), (lost.Status, ((ApiError)lost.Body!).Code)); // before brief 32: 409 decision-not-applied
        Assert.Equal("decided", (await rig.Runtime.RecordStore.GetReviewAsync(doc))!.State);
        var manifest = await ManifestAsync(rig, reopened.UploadId);
        Assert.DoesNotContain(manifest.Items, i => i.Status == ItemStatuses.AwaitingReview); // before: AwaitingReview, "decide again", and later pushes suppressed
        Assert.DoesNotContain(await rig.Runtime.RecordStore.ListEscalationsAsync(), e => e.ReasonCode == EscalationReasons.DecisionApplyFailed);
        Assert.NotNull(await rig.Runtime.RecordStore.GetOutboxAsync(OutboxMessage.KeyedId(winner.Key, OutboxChannels.Event, OutboxKinds.ReviewDecided)));
        Assert.Null(await rig.Runtime.RecordStore.GetOutboxAsync(OutboxMessage.KeyedId(OverrideDecision(doc, "dec-b32-loser").Key, OutboxChannels.Event, OutboxKinds.ReviewDecided)));
    }

    /// <summary>A registry whose stats writes always fail (format approval after a committed decision).</summary>
    public class StatsFailRegistry : DispatchProxy
    {
        public LossRun.Formats.IFormatRegistry Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name == nameof(LossRun.Formats.IFormatRegistry.UpdateStatsAsync)
            ? throw new InvalidOperationException("injected: stats write failed")
            : targetMethod.Invoke(Inner, args);

        public static LossRun.Formats.IFormatRegistry Wrap(LossRun.Formats.IFormatRegistry inner)
        {
            var proxy = Create<LossRun.Formats.IFormatRegistry, StatsFailRegistry>();
            ((StatsFailRegistry)(object)proxy).Inner = inner;
            return proxy;
        }
    }

    [Fact]
    public async Task Brief32_item3_a_persistent_failure_after_the_commit_leaves_the_decision_standing_and_escalates()
    {
        var rig = await RigAsync();
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile("lossrun2-native.pdf", Fixture.Bytes("lossrun2-native.pdf"))], "Q-B32-3", null, "b32-3")).Body!;
        var item = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items);
        Assert.Equal(ReviewKinds.ConfirmFormat, item.ReviewKind);
        var faulty = new UploadActivities(rig.Runtime with { Registry = StatsFailRegistry.Wrap(rig.Runtime.Registry) });

        var outcome = await faulty.ApplyDecisionAsync(new ReviewDecision(item.DocumentId, ReviewActions.Accept, [], true, "format confirmed", "uw-1", Roles.Underwriter,
            DateTimeOffset.UnixEpoch.AddDays(20000), DecisionId: "dec-b32-3")); // before brief 32: the exception escaped the activity and, after its retries, the orchestration

        Assert.Null(outcome.ReviewKind);
        Assert.Equal("decided", (await rig.Runtime.RecordStore.GetReviewAsync(item.DocumentId))!.State);
        var escalation = Assert.Single(await rig.Runtime.RecordStore.ListEscalationsAsync(), e => e.ReasonCode == EscalationReasons.DecisionPostCommitFailed);
        Assert.Equal("format-approve", escalation.TraceSummary.Step);
        Assert.Equal(ItemStatuses.Processed, Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items).Status); // the later steps still ran
    }

    [Fact]
    public async Task Brief32_item5_an_override_on_an_index_the_document_lacks_is_a_400_before_the_202()
    {
        var (rig, doc) = await InReviewAsync("Q-B32-5");
        var missing = new FieldOverride("policyPeriods[0].claims[99].medical.paid", JsonDocument.Parse("1").RootElement, "adjuster");

        var decided = await rig.Api.DecideAsync(Underwriter, doc, new ReviewDecisionRequest(ReviewActions.Override, [missing], Rationale: "fix"));
        Assert.Equal((400, "bad-override-path"), (decided.Status, ((ApiError)decided.Body!).Code)); // before brief 32: 202, then failed in the activity
        Assert.Equal("pending", (await rig.Runtime.RecordStore.GetReviewAsync(doc))!.State);

        var direct = await new DocumentCommands(rig.Runtime).OverrideAsync(Underwriter, doc, new OverrideRequest([missing], "fix"));
        Assert.Equal((400, "bad-override-path"), (direct.Status, ((ApiError)direct.Body!).Code));
    }

    /// <summary>Brief 32 item 6: an object store that fails once, on the first call matching (method, path): a crash between two writes.</summary>
    public class CrashOnceStore : DispatchProxy
    {
        public IObjectStore Inner { get; set; } = null!;
        public Func<string, string, bool> When { get; set; } = (_, _) => false;
        public bool Fired { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (!Fired && args is { Length: > 1 } && args[1] is string path && When(targetMethod!.Name, path))
            {
                Fired = true;
                throw new IOException($"injected crash before {targetMethod.Name} {path}");
            }

            return targetMethod!.Invoke(Inner, args);
        }

        public static (IObjectStore Store, CrashOnceStore Crash) Wrap(IObjectStore inner, Func<string, string, bool> when)
        {
            var proxy = Create<IObjectStore, CrashOnceStore>();
            var crash = (CrashOnceStore)(object)proxy;
            (crash.Inner, crash.When) = (inner, when);
            return (proxy, crash);
        }
    }

    [Theory]
    [InlineData("override lines written, result not saved")]
    [InlineData("result saved, review and outbox not written")]
    public async Task Brief32_item6_a_blob_decision_retried_after_a_crash_is_completed_once(string crashPoint)
    {
        var (rig, doc) = await InReviewAsync("Q-B32-6");
        var uploadId = (await rig.Runtime.RecordStore.UploadOfDocumentAsync(doc))!;
        var revision = await rig.Runtime.RecordStore.QuoteRevisionAsync("Q-B32-6");
        var before = (await rig.Runtime.RecordStore.GetResultAsync(doc))!;
        var decision = OverrideDecision(doc, "dec-b32-6");
        var (after, changes) = DocumentEdits.Apply(before, decision);
        var record = new DecisionRecord(uploadId, before, after, decision, changes, false, new QuoteRevisionBump("Q-B32-6", QuoteRevisions.ReviewDecided));
        IReadOnlyList<OutboxMessage> outbox =
            [OutboxMessage.Event(OutboxKinds.ReviewDecided, "Q-B32-6", $"documents/{doc}", new { quoteRevision = QuoteRevisions.Token }, "uw-1", decision.At).KeyedBy(decision.Key)];
        var (crashing, crash) = CrashOnceStore.Wrap(rig.Runtime.Store, crashPoint.StartsWith("override", StringComparison.Ordinal)
            ? (method, path) => method == nameof(IObjectStore.PutAsync) && path.EndsWith("result.json", StringComparison.Ordinal)
            : (method, path) => method.StartsWith(nameof(IObjectStore.UpdateAsync), StringComparison.Ordinal) && path == StorePaths.Review(doc));
        var store = new BlobRecordStore(crashing);

        await Assert.ThrowsAsync<IOException>(() => store.ApplyDecisionAsync(record, default, outbox));
        Assert.True(crash.Fired);
        // The retry (Durable re-executes the activity): before brief 32 a duplicated override line, or a Conflict with the outbox and the bump lost.
        Assert.Equal(DecisionApplyResult.Applied, await store.ApplyDecisionAsync(record, default, outbox));
        Assert.Equal(DecisionApplyResult.AlreadyApplied, await store.ApplyDecisionAsync(record, default, outbox));

        Assert.Single(await rig.Runtime.RecordStore.OverridesOfDocumentAsync(doc));
        Assert.Equal("decided", (await rig.Runtime.RecordStore.GetReviewAsync(doc))!.State);
        Assert.NotNull(await rig.Runtime.RecordStore.GetOutboxAsync(OutboxMessage.KeyedId(decision.Key, OutboxChannels.Event, OutboxKinds.ReviewDecided)));
        Assert.Equal(revision + 1, await rig.Runtime.RecordStore.QuoteRevisionAsync("Q-B32-6"));
        var audit = (await rig.Runtime.RecordStore.AuditTrailAsync(doc, null, 100)).Entries;
        Assert.Single(audit, e => e.Action == "override");
        Assert.Single(audit, e => e.Action == $"review.{decision.Action}");
    }

    private static async Task<(Rig Rig, string DocumentId)> InReviewAsync(string quoteId)
    {
        var rig = await RigAsync(tamper: true);
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile("lossrun-native.pdf", Fixture.Bytes("lossrun-native.pdf"))], quoteId, null, quoteId)).Body!;
        var item = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items);
        Assert.Equal("pending", (await rig.Runtime.RecordStore.GetReviewAsync(item.DocumentId))!.State);
        return (rig, item.DocumentId);
    }

    private static ReviewDecision OverrideDecision(string documentId, string id) => new(documentId, ReviewActions.Override,
        [new FieldOverride("policyPeriods[0].claims[0].medical.paid", JsonDocument.Parse("1234").RootElement, "adjuster")], false, "fix", "uw-1", Roles.Underwriter,
        DateTimeOffset.UnixEpoch.AddDays(20000), DecisionId: id);

    [Fact]
    public async Task Item6_an_activity_retried_with_the_same_decision_applies_it_once()
    {
        var (rig, doc) = await InReviewAsync("Q-I6");
        var revision = await rig.Runtime.RecordStore.QuoteRevisionAsync("Q-I6");
        var decision = OverrideDecision(doc, "dec-i6");

        await rig.Activities.ApplyDecisionAsync(decision);
        await rig.Activities.ApplyDecisionAsync(decision); // Durable re-executes the activity

        Assert.Single(await rig.Runtime.RecordStore.OverridesOfDocumentAsync(doc));
        var outbox = await rig.Runtime.RecordStore.OutboxAsync("Q-I6", 50);
        Assert.Single(outbox, o => o.Kind == OutboxKinds.ReviewDecided);
        Assert.Single(outbox, o => o.Kind == OutboxKinds.QuotePayload);
        Assert.Equal(revision + 1, await rig.Runtime.RecordStore.QuoteRevisionAsync("Q-I6"));
        Assert.Equal(1234m, (await rig.Runtime.RecordStore.GetResultAsync(doc))!.Document.PolicyPeriods[0].Claims[0].Medical!.Paid);
    }

    [Fact]
    public async Task Item6_a_failure_after_the_acknowledgement_leaves_the_document_in_review_with_an_escalation()
    {
        var (rig, doc) = await InReviewAsync("Q-I6B");
        var revision = (await rig.Runtime.RecordStore.GetResultAsync(doc))!.Revision;
        var faulty = new UploadActivities(rig.Runtime with { Records = FaultyRecords.Wrap(rig.Runtime.RecordStore, nameof(IRecordStore.ApplyDecisionAsync)) });

        var outcome = await faulty.ApplyDecisionAsync(OverrideDecision(doc, "dec-i6b")); // does not throw: the orchestration stays alive

        Assert.Equal(ReviewKinds.DocumentReview, outcome.ReviewKind);
        Assert.Equal("pending", (await rig.Runtime.RecordStore.GetReviewAsync(doc))!.State);
        Assert.Equal(revision, (await rig.Runtime.RecordStore.GetResultAsync(doc))!.Revision);
        var escalation = Assert.Single(await rig.Runtime.RecordStore.ListEscalationsAsync(), e => e.ReasonCode == EscalationReasons.DecisionApplyFailed);
        Assert.Contains(doc, escalation.DocumentIds);

        // The reviewer decides again once it is fixed.
        await rig.Activities.ApplyDecisionAsync(OverrideDecision(doc, "dec-i6b-2"));
        Assert.Equal("decided", (await rig.Runtime.RecordStore.GetReviewAsync(doc))!.State);
    }

    [Fact]
    public async Task Item6_a_completion_retried_bumps_the_quote_once_and_an_unreachable_webhook_does_not_fail_it()
    {
        var rig = await RigAsync();
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile("lossrun-export.csv", Fixture.Bytes("lossrun-export.csv"))], "Q-I6C", null, "i6c")).Body!;
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));
        var doc = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items).DocumentId;
        var revision = await rig.Runtime.RecordStore.QuoteRevisionAsync("Q-I6C");

        var timingOut = new UploadActivities(rig.Runtime with { Webhooks = new HmacWebhookSender(new Uri("https://hooks.invalid/x"), "s", new HttpClient(new TimeoutHandler())) });
        var completion = await timingOut.CompleteUploadAsync(new CompleteInput(accepted.UploadId, [new DocumentOutcome(doc, "ReplayAccepted", null, 3, 0)]));

        Assert.Equal(UploadStatuses.Completed, completion.Status);
        Assert.Equal(revision, await rig.Runtime.RecordStore.QuoteRevisionAsync("Q-I6C")); // the retried completion was a no-op
        Assert.Single(await rig.Runtime.RecordStore.OutboxAsync("Q-I6C", 50), o => o.Kind == OutboxKinds.UploadCompleted);
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.");
    }
}

public partial class EndToEndTests
{
    [Fact]
    public async Task Item15_a_direct_override_on_a_confirm_format_document_leaves_the_format_decision_open()
    {
        var rig = await RigAsync();
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile("lossrun2-native.pdf", Fixture.Bytes("lossrun2-native.pdf"))], "Q-I15", null, "i15")).Body!;
        var item = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items);
        Assert.Equal(ReviewKinds.ConfirmFormat, item.ReviewKind);
        var before = (await rig.Runtime.RecordStore.GetResultAsync(item.DocumentId))!.Document.PolicyPeriods[0].Claims[0].Medical?.Paid ?? 0;

        var overridden = await new DocumentCommands(rig.Runtime).OverrideAsync(Underwriter, item.DocumentId, new OverrideRequest(
            [new FieldOverride("policyPeriods[0].claims[0].medical.paid", JsonDocument.Parse((before + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement, "letter")], "letter"));
        Assert.Equal(200, overridden.Status);

        // Before brief 30 the proc marked the confirm-format item decided: the shadow spec was neither confirmed nor rejected.
        var review = (await rig.Runtime.RecordStore.GetReviewAsync(item.DocumentId))!;
        Assert.Equal(("pending", ReviewKinds.ConfirmFormat), (review.State, review.Kind));
        Assert.Equal(before + 1, (await rig.Runtime.RecordStore.GetResultAsync(item.DocumentId))!.Document.PolicyPeriods[0].Claims[0].Medical!.Paid);
        Assert.Equal(202, (await rig.Api.DecideAsync(Underwriter, item.DocumentId, new ReviewDecisionRequest(ReviewActions.Accept, Rationale: "format confirmed"))).Status);
    }
}

public partial class EndToEndTests
{
    [Fact]
    public async Task Item16_reopen_bumps_the_quote_revision_and_the_verified_view_drops_the_reopened_document()
    {
        var rig = await RigAsync();
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile("lossrun-export.csv", Fixture.Bytes("lossrun-export.csv"))], "Q-I16", null, "i16")).Body!;
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));
        var doc = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items).DocumentId;
        var analytics = new QuoteAnalytics(rig.Runtime);
        var verified = Encoding.UTF8.GetString((await analytics.FeaturesAsync(Underwriter, "Q-I16", "verified-only")).Bytes!);
        var revision = await rig.Runtime.RecordStore.QuoteRevisionAsync("Q-I16");

        Assert.Equal(201, (await new DocumentCommands(rig.Runtime).ReopenAsync(Underwriter, doc, new ReopenRequest("broker sent a corrected run"))).Status);

        // Before brief 30: same revision, and the cached verified snapshot still counted the reopened document.
        Assert.Equal(revision + 1, await rig.Runtime.RecordStore.QuoteRevisionAsync("Q-I16"));
        var after = await analytics.FeaturesAsync(Underwriter, "Q-I16", "verified-only");
        Assert.True(verified != Encoding.UTF8.GetString(after.Bytes!), $"{after.Headers?.GetValueOrDefault(QuoteAnalytics.SnapshotHeader)} {verified[..Math.Min(600, verified.Length)]}");
        var payload = System.Text.Json.Nodes.JsonNode.Parse((await analytics.QuotePayloadAsync(Underwriter, "Q-I16")).Bytes!)!;
        Assert.Equal((1, false, 1), (payload["lineage"]!["pendingReview"]!.GetValue<int>(), payload["periodSummaries"]!["complete"]!.GetValue<bool>(),
            payload["periodSummaries"]!["excludedDocuments"]!.GetValue<int>())); // item 13: a reopened document is pending, and the summary says it is incomplete
        Assert.Empty(payload["claims"]!.AsArray());
        var outbox = await rig.Runtime.RecordStore.OutboxAsync("Q-I16", 50);
        Assert.Contains(outbox, o => o.Kind == OutboxKinds.QuoteRevised && o.PayloadJson.Contains(QuoteRevisions.Reopened, StringComparison.Ordinal));
        Assert.Contains(outbox, o => o.Kind == OutboxKinds.QuotePayload && o.PayloadJson.Contains(QuoteRevisions.Reopened, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Item16_two_files_with_the_same_name_are_both_listed_with_distinct_blob_paths()
    {
        var rig = await RigAsync();
        var csv = Fixture.Bytes("lossrun-export.csv");
        var other = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(csv).Replace("WC-22-0001", "WC-22-0901", StringComparison.Ordinal));
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile("run.csv", csv), new UploadFile("run.csv", other)], "Q-I16B", null, "i16b")).Body!;
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));

        var manifest = await ManifestAsync(rig, accepted.UploadId);
        Assert.Equal(2, manifest.Files.Count);
        Assert.Equal(2, manifest.Files.Select(f => f.BlobPath).Distinct().Count()); // before brief 30: the second overwrote nothing and vanished
        Assert.Equal(2, manifest.Items.Count(i => i.Status == ItemStatuses.Processed));
    }
}

public partial class EndToEndTests
{
    [Fact]
    public async Task Item19_an_accepted_document_becomes_an_exemplar_a_drift_repair_verifies_on()
    {
        var rig = await RigAsync();
        Assert.Empty(await rig.Activities.ExemplarsAsync("synthetic-mutual-csv"));
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile("lossrun-export.csv", Fixture.Bytes("lossrun-export.csv"))], "Q-I19", null, "i19")).Body!;
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));
        var doc = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items);
        Assert.Equal("ReplayAccepted", doc.RoutingTier);

        var exemplar = Assert.Single(await rig.Activities.ExemplarsAsync("synthetic-mutual-csv")); // before brief 30: nothing was kept, the repairer got none
        Assert.Equal(doc.DocumentId, exemplar.DocumentId);
    }
}

public partial class OrchestratorTests
{
    [Fact]
    public async Task Item6_a_decision_that_could_not_be_applied_keeps_the_review_open_for_the_next_one()
    {
        var h = Harness(new DocumentOutcome("d1", "Review", ReviewKinds.DocumentReview, 3, 0.01m));
        var calls = 0;
        h.Activity<ReviewDecision, DocumentOutcome>(ActivityNames.ApplyDecision, d => ++calls == 1
            ? new DocumentOutcome(d.DocumentId, "decision-apply-failed", ReviewKinds.DocumentReview, 3, 0)
            : new DocumentOutcome(d.DocumentId, d.Action, null, 3, 0));
        h.Events.Enqueue(Decision("d1"));
        h.Events.Enqueue(Decision("d1"));

        var summary = await UploadOrchestration.RunAsync(new FakeOrchestrationContext("up-1", h), new UploadInput("up-1", TimeSpan.FromDays(7)));

        Assert.Equal(("completed", 1, 0), (summary.Status, summary.ReviewsDecided, summary.ReviewsExpired));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Brief32_item3_a_failed_decision_activity_does_not_end_the_upload_s_decision_loop()
    {
        var h = Harness(new DocumentOutcome("d1", "Review", ReviewKinds.DocumentReview, 3, 0.01m), new DocumentOutcome("d2", "Review", ReviewKinds.DocumentReview, 2, 0.01m));
        var calls = new List<string>();
        h.Activity<ReviewDecision, DocumentOutcome>(ActivityNames.ApplyDecision, d =>
        {
            calls.Add(d.DocumentId);
            return calls.Count == 1
                ? throw new Microsoft.DurableTask.TaskFailedException(ActivityNames.ApplyDecision, 1, new Microsoft.DurableTask.TaskFailureDetails("RequestFailedException", "storage", null, null, null))
                : new DocumentOutcome(d.DocumentId, d.Action, null, 3, 0);
        });
        h.Events.Enqueue(Decision("d1"));
        h.Events.Enqueue(Decision("d2")); // before brief 32: never applied, the orchestration had faulted on d1
        h.Events.Enqueue(Decision("d1"));

        var summary = await UploadOrchestration.RunAsync(new FakeOrchestrationContext("up-1", h), new UploadInput("up-1", TimeSpan.FromDays(7)));

        Assert.Equal(["d1", "d2", "d1"], calls);
        Assert.Equal(("completed", 2), (summary.Status, summary.ReviewsDecided));
    }

    // Brief 30's Item32 (a fixed three-attempt policy on every storage activity) is replaced by brief 33's classified handler:
    // Brief33_storage_activities_get_the_classified_handler_and_paid_model_calls_get_nothing (Brief33Tests.cs).
}
