using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;
using LossRun.Formats;
using LossRun.Ingestion;
using LossRun.Ingestion.ContentUnderstanding;
using LossRun.Learning;
using LossRun.Rules;

namespace LossRun.Orchestration.Tests;

/// <summary>Orchestrator logic with fake activities (brief 07 acceptance: fan-out, review wait + timer, custom status).</summary>
public partial class OrchestratorTests
{
    private static FakeOrchestrationContext.Harness Harness(params DocumentOutcome[] outcomes)
    {
        var h = new FakeOrchestrationContext.Harness();
        h.Activity<string, IReadOnlyList<FileWork>>(ActivityNames.ExpandUpload, id => [new FileWork(id, "a.pdf"), new FileWork(id, "b.xlsx")]);
        h.Activity<FileWork, ParseResult>(ActivityNames.ParseFile, f => f.FileName == "a.pdf"
            ? new ParseResult([new ManifestItem { DocumentId = "d1" }, new ManifestItem { DocumentId = "d1-p2-2", Status = ItemStatuses.KeptNotProcessed }],
                [new DocumentWork(f.UploadId, "d1", f.FileName, true)])
            : new ParseResult([new ManifestItem { DocumentId = "d2" }, new ManifestItem { DocumentId = "d3" }],
                [new DocumentWork(f.UploadId, "d2", f.FileName, false), new DocumentWork(f.UploadId, "d3", f.FileName, false)]));
        h.Activity<ItemsInput, object?>(ActivityNames.RecordItems, _ => null);
        h.Activity<DocumentWork, RouteResult>(ActivityNames.Route, w => new RouteResult(w.DocumentId, w.DocumentId switch { "d2" => "ReplayWithDelta", "d3" => "Learn", _ => "ReplayAccepted" },
            "f@1", false, w.DocumentId == "d2" ? ["Lit"] : [], ["reason"], "badge", true, 0.005m));
        h.Activity<LearnInput, LearnResult>(ActivityNames.Delta, i => new LearnResult(i.Work.DocumentId, true, "f@2", "active", [], 1, 0.01m));
        h.Activity<LearnInput, LearnResult>(ActivityNames.Learn, i => new LearnResult(i.Work.DocumentId, true, "g@1", "shadow", [], 2, 0.03m));
        h.Activity<PersistInput, DocumentOutcome>(ActivityNames.ValidateAndPersist, p => outcomes.FirstOrDefault(o => o.DocumentId == p.Work.DocumentId)
            ?? new DocumentOutcome(p.Work.DocumentId, p.Route.Tier, null, 3, 0.01m));
        h.Activity<CompleteInput, UploadCompletion>(ActivityNames.CompleteUpload, c =>
            new UploadCompletion(c.UploadId, c.Outcomes.Any(o => o.ReviewKind == ReviewKinds.DocumentReview) ? UploadStatuses.AwaitingReview : UploadStatuses.Completed, 0.05m,
                c.Outcomes.Count(o => o.ReviewKind == ReviewKinds.DocumentReview)));
        h.Activity<ReviewDecision, DocumentOutcome>(ActivityNames.ApplyDecision, d => new DocumentOutcome(d.DocumentId, d.Action, null, 3, 0));
        h.Activity<ExpireInput, object?>(ActivityNames.ExpireReviews, _ => null);
        return h;
    }

    private static ReviewDecision Decision(string documentId) => new(documentId, ReviewActions.Accept, [], false, "ok", "uw", Roles.Underwriter, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Files_and_loss_runs_fan_out_with_document_instance_ids()
    {
        var h = Harness();

        var summary = await UploadOrchestration.RunAsync(new FakeOrchestrationContext("up-1", h), new UploadInput("up-1", TimeSpan.FromDays(7)));

        Assert.Equal(2, h.ActivityCalls.Count(c => c.Activity == ActivityNames.ParseFile));
        Assert.Equal(["up-1~d1", "up-1~d2", "up-1~d3"], h.SubOrchestrations);
        var recorded = (ItemsInput)h.ActivityCalls.Single(c => c.Activity == ActivityNames.RecordItems).Input!;
        Assert.Equal(4, recorded.Items.Count); // the kept-not-processed segment is in the manifest, not in processing
        Assert.Equal((UploadStatuses.Completed, 3), (summary.Status, summary.Documents));
    }

    [Fact]
    public async Task A_failed_parse_marks_the_upload_failed_and_the_orchestration_still_fails()
    {
        // Brief 23 follow-up: the missing-changeset outage left every manifest "processing" for good.
        var h = Harness();
        h.Activity<FileWork, ParseResult>(ActivityNames.ParseFile, _ =>
            throw new Microsoft.DurableTask.TaskFailedException(ActivityNames.ParseFile, 1, new Microsoft.DurableTask.TaskFailureDetails("SqlException", "Invalid object name 'dbo.X'.", null, null, null)));
        h.Activity<FailInput, object?>(ActivityNames.FailUpload, _ => null);

        await Assert.ThrowsAsync<Microsoft.DurableTask.TaskFailedException>(() => UploadOrchestration.RunAsync(new FakeOrchestrationContext("up-1", h), new UploadInput("up-1", TimeSpan.FromDays(7))));

        var failed = (FailInput)h.ActivityCalls.Single(c => c.Activity == ActivityNames.FailUpload).Input!;
        Assert.Equal("up-1", failed.UploadId);
        Assert.DoesNotContain(h.ActivityCalls, c => c.Activity == ActivityNames.CompleteUpload);
    }

    [Fact]
    public async Task Custom_status_follows_the_steps_for_each_path()
    {
        var h = Harness();

        await UploadOrchestration.RunAsync(new FakeOrchestrationContext("up-1", h), new UploadInput("up-1", TimeSpan.FromDays(7)));

        Assert.Equal(["expand", "parse", "process", "completed", "completed"], h.Steps("up-1"));
        Assert.Equal(["fingerprint", "route", "replay", "validate", "done"], h.Steps("up-1~d1"));
        Assert.Equal(["fingerprint", "route", "delta", "validate", "done"], h.Steps("up-1~d2"));
        Assert.Equal(["fingerprint", "route", "learn", "validate", "done"], h.Steps("up-1~d3"));
        var done = h.Statuses["up-1~d3"].OfType<UploadProgress>().Last();
        Assert.Equal("d3", done.DocumentId);
        Assert.True(h.Statuses["up-1~d3"].OfType<UploadProgress>().Select(p => p.ElapsedMs).SequenceEqual(h.Statuses["up-1~d3"].OfType<UploadProgress>().Select(p => p.ElapsedMs).Order()));
    }

    [Fact]
    public async Task Review_decision_is_applied_and_the_upload_completes()
    {
        var h = Harness(new DocumentOutcome("d1", "Review", ReviewKinds.DocumentReview, 3, 0.01m));
        h.Events.Enqueue(Decision("other-doc")); // not open: ignored
        h.Events.Enqueue(Decision("d1"));

        var summary = await UploadOrchestration.RunAsync(new FakeOrchestrationContext("up-1", h), new UploadInput("up-1", TimeSpan.FromDays(7)));

        Assert.Equal(("completed", 1, 0), (summary.Status, summary.ReviewsDecided, summary.ReviewsExpired));
        Assert.Equal("d1", ((ReviewDecision)h.ActivityCalls.Single(c => c.Activity == ActivityNames.ApplyDecision).Input!).DocumentId);
        Assert.Contains(UploadStatuses.AwaitingReview, h.Steps("up-1"));
    }

    [Fact]
    public async Task Review_timer_expires_open_items()
    {
        var h = Harness(new DocumentOutcome("d1", "Review", ReviewKinds.DocumentReview, 3, 0.01m), new DocumentOutcome("d3", "Learned", ReviewKinds.ConfirmFormat, 4, 0.03m));
        h.FireTimers = true;

        var summary = await UploadOrchestration.RunAsync(new FakeOrchestrationContext("up-1", h), new UploadInput("up-1", TimeSpan.FromDays(7)));

        Assert.Equal(("reviews-expired", 0, 2), (summary.Status, summary.ReviewsDecided, summary.ReviewsExpired));
        Assert.Equal(["d1", "d3"], ((ExpireInput)h.ActivityCalls.Single(c => c.Activity == ActivityNames.ExpireReviews).Input!).DocumentIds);
        Assert.DoesNotContain(h.ActivityCalls, c => c.Activity == ActivityNames.ApplyDecision);
    }
}

/// <summary>Offline end to end: the real activities on an in-memory store, recorded CU responses and a fake learn model.</summary>
public partial class EndToEndTests
{
    private sealed class FakeCu(bool tamper = false) : IContentUnderstandingService
    {
        private static readonly (string File, string Recorded)[] Recordings =
        [
            ("lossrun-native.pdf", "prebuilt-layout.lossrun-native.json"), ("lossrun2-native.pdf", "prebuilt-layout.lossrun2-native.json"),
            ("lossrun-drifted.pdf", "prebuilt-layout.lossrun-drifted.json"),
        ];

        /// <summary>The recording for a fixture, also when bytes were appended to it (distinct uploads of the same pages).</summary>
        private static string Recorded(byte[] content) =>
            Recordings.First(r => content.AsSpan().StartsWith(Fixture.Bytes(r.File))).Recorded;

        public Task<CuCallResult> AnalyzeAsync(string analyzerId, BinaryData content, string contentType, CancellationToken cancellationToken)
        {
            var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content.ToArray()));
            var json = analyzerId == "prebuilt-layout" ? Fixture.Recorded(Recorded(content.ToArray())) : Fixture.Recorded("lossrun_classifier_v1.bundle.json");
            if (tamper)
            {
                json = json.Replace("9,200.00", "9,300.00", StringComparison.Ordinal); // period 1 medical paid subtotal no longer reconciles
            }

            return Task.FromResult(new CuCallResult(analyzerId, "op-" + sha[..8], json, TimeSpan.FromMilliseconds(3)));
        }

        public Task EnsureAnalyzerAsync(string analyzerId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeModel : ILearnModel
    {
        public int Calls { get; private set; }

        /// <summary>Brief 26: a reply that never verifies (the learn runs out of attempts).</summary>
        public string? Reply { get; set; }

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            var json = Reply ?? File.ReadAllText(Fixture.File("Specs", "fictional-casualty.proposal.json"));
            return Task.FromResult(new ModelResponse(json, 1000, 0, 500, 0, TimeSpan.Zero, $"m-{Calls}", ModelUsage.Events(request, 1000, 0, 500, $"m-{Calls}", "DataZoneStandard", "eastus2", DateTimeOffset.UnixEpoch)));
        }
    }

    private sealed class CapturingWebhooks(string secret) : IWebhookSender
    {
        public List<(byte[] Body, string Signature)> Sent { get; } = [];

        public Task SendAsync(WebhookEnvelope envelope, CancellationToken cancellationToken = default)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(envelope, CanonicalJson.Options);
            Sent.Add((body, HmacWebhookSender.Sign(secret, body)));
            return Task.CompletedTask;
        }
    }

    /// <summary>Runs the orchestrations synchronously on StartAsync, with pre-queued review decisions.</summary>
    private sealed class InlineOrchestrations(UploadActivities activities) : IOrchestrationClient
    {
        public FakeOrchestrationContext.Harness Harness { get; } = Wire(activities);
        public HashSet<string> Started { get; } = [];
        public Task? Running { get; private set; }

        private static FakeOrchestrationContext.Harness Wire(UploadActivities a)
        {
            var h = new FakeOrchestrationContext.Harness();
            h.ActivityAsync<string, IReadOnlyList<FileWork>>(ActivityNames.ExpandUpload, id => a.ExpandUploadAsync(id));
            h.ActivityAsync<FileWork, ParseResult>(ActivityNames.ParseFile, f => a.ParseFileAsync(f));
            h.ActivityAsync<ItemsInput, object?>(ActivityNames.RecordItems, async i => { await a.RecordItemsAsync(i); return null; });
            h.ActivityAsync<DocumentWork, RouteResult>(ActivityNames.Route, w => a.RouteAsync(w));
            h.ActivityAsync<LearnInput, LearnResult>(ActivityNames.Delta, i => a.DeltaAsync(i));
            h.ActivityAsync<LearnInput, LearnResult>(ActivityNames.Learn, i => a.LearnAsync(i));
            h.ActivityAsync<PersistInput, DocumentOutcome>(ActivityNames.ValidateAndPersist, p => a.ValidateAndPersistAsync(p));
            h.ActivityAsync<CompleteInput, UploadCompletion>(ActivityNames.CompleteUpload, c => a.CompleteUploadAsync(c));
            h.ActivityAsync<ReviewDecision, DocumentOutcome>(ActivityNames.ApplyDecision, d => a.ApplyDecisionAsync(d));
            h.ActivityAsync<ExpireInput, object?>(ActivityNames.ExpireReviews, async e => { await a.ExpireReviewsAsync(e); return null; });
            return h;
        }

        public async Task<bool> StartAsync(UploadInput input, CancellationToken cancellationToken = default)
        {
            if (!Started.Add(input.UploadId))
            {
                return false;
            }

            Running = UploadOrchestration.RunAsync(new FakeOrchestrationContext(input.UploadId, Harness), input);
            await Task.WhenAny(Running, Task.Delay(2000, cancellationToken)); // reviews keep it running; the test raises decisions
            return true;
        }

        public Task<(string RuntimeStatus, string? CustomStatus)?> GetAsync(string instanceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<(string, string?)?>(Harness.Statuses.TryGetValue(instanceId, out var s)
                ? ("Running", JsonSerializer.Serialize(s[^1], CanonicalJson.Options)) : null);

        public Task RaiseAsync(string instanceId, string eventName, object payload, CancellationToken cancellationToken = default)
        {
            Harness.Raise(payload);
            return Task.CompletedTask;
        }
    }

    private sealed record Rig(UploadApi Api, InlineOrchestrations Orchestrations, InMemoryObjectStore Store, InMemoryFormatRegistry Registry, CapturingWebhooks Webhooks,
        FakeModel Model, UploadActivities Activities, LossRunRuntime Runtime);

    /// <param name="wrapRecords">Brief 33: wraps the rig's Blob record store (a fault-injecting proxy) before anything uses it.</param>
    private static async Task<Rig> RigAsync(bool tamper = false, LossRun.Data.IRecordStore? records = null, ApiOptions? options = null,
        Func<LossRun.Data.IRecordStore, LossRun.Data.IRecordStore>? wrapRecords = null)
    {
        var store = new InMemoryObjectStore();
        records ??= wrapRecords?.Invoke(new BlobRecordStore(store));
        var registry = new InMemoryFormatRegistry();
        await FormatDirectory.LoadIntoAsync(Fixture.File("formats"), registry, maxVersion: 1);
        var webhooks = new CapturingWebhooks("hook-secret");
        var model = new FakeModel();
        var runtime = new LossRunRuntime(store, registry, new FakeCu(tamper), new IngestionOptions(),
            new LearnOptions { LearnDeployment = "learn", DeltaDeployment = "delta" }, _ => null, _ => model, webhooks, TimeProvider.System, Records: records);
        var activities = new UploadActivities(runtime);
        var orchestrations = new InlineOrchestrations(activities);
        return new Rig(new UploadApi(runtime, orchestrations, options), orchestrations, store, registry, webhooks, model, activities, runtime);
    }

    private static readonly Caller Applicant = new("applicant-1", Roles.Applicant);
    private static readonly Caller Underwriter = new("uw-1", Roles.Underwriter);

    private static async Task<UploadManifest> ManifestAsync(Rig rig, string uploadId) =>
        (UploadManifest)(await rig.Api.GetUploadAsync(Underwriter, uploadId)).Body!;

    [Fact]
    public async Task Pdf_csv_and_an_unsupported_file_produce_the_manifest_results_cost_and_webhook()
    {
        var rig = await RigAsync();
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant,
            [new UploadFile("lossrun-native.pdf", Fixture.Bytes("lossrun-native.pdf")), new UploadFile("lossrun-export.csv", Fixture.Bytes("lossrun-export.csv")),
             new UploadFile("notes.docx", Encoding.UTF8.GetBytes("not a real docx"))], "Q-100", "Synthetic Mutual Insurance Co.", null)).Body!;
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));

        var manifest = await ManifestAsync(rig, accepted.UploadId);
        Assert.Equal(UploadStatuses.Completed, manifest.Status);
        Assert.Equal(2, manifest.Items.Count(i => i.Status == ItemStatuses.Processed));
        Assert.Single(manifest.Items, i => i.Status == ItemStatuses.Unsupported && i.FileName == "notes.docx");
        Assert.All(manifest.Items.Where(i => i.Status == ItemStatuses.Processed), i => Assert.Equal("ReplayAccepted", i.RoutingTier));
        Assert.NotNull(manifest.FirstClaimMs);
        Assert.True(manifest.Cost!.TotalUsd > 0); // CU layout for the PDF; the CSV is free
        Assert.Equal(0, manifest.Cost.LlmCalls);

        var pdf = manifest.Items.Single(i => i.FileName == "lossrun-native.pdf");
        var result = (DocumentResult)(await rig.Api.GetDocumentAsync(Applicant, pdf.DocumentId)).Body!;
        Assert.Equal(3, result.Document.PolicyPeriods.Sum(p => p.Claims.Count));
        Assert.Equal("synthetic-mutual-pdf@1", result.FormatRef);
        Assert.Contains(result.Document.Lineage!, l => l.Value.Page == 1 && l.Value.Polygon is not null);
        Assert.True(RuleEvaluator.PassesHardRules(result.RuleOutcomes));
        Assert.Equal(0.005m, result.Cost!.CostUsd);

        var quote = (QuotePayload)(await rig.Api.GetQuotePayloadAsync(Applicant, pdf.DocumentId)).Body!;
        Assert.Equal(2, quote.Periods.Count);
        Assert.Equal(23500m, quote.Periods[0].Total.Incurred);

        var hook = Assert.Single(rig.Webhooks.Sent);
        Assert.Equal(200, (await rig.Api.ReceiveWebhookAsync(hook.Body, hook.Signature, "hook-secret")).Status);
        Assert.Equal(401, (await rig.Api.ReceiveWebhookAsync(hook.Body, hook.Signature, "wrong-secret")).Status);
    }

    [Fact]
    public async Task Bundle_segments_that_are_not_loss_runs_are_kept_not_processed()
    {
        var rig = await RigAsync();
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("bundle.pdf", Fixture.Bytes("bundle.pdf"))], null, null, null)).Body!;
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));

        var manifest = await ManifestAsync(rig, accepted.UploadId);
        Assert.Contains(manifest.Items, i => i.Classification == "LossRun" && i.PageRange == "1-1" && i.Status == ItemStatuses.Processed);
        Assert.Equal(2, manifest.Items.Count(i => i.Status == ItemStatuses.KeptNotProcessed));
        Assert.All(manifest.Items.Where(i => i.Status == ItemStatuses.KeptNotProcessed), i => Assert.False(string.IsNullOrEmpty(i.Reason)));

        // Chat cites kept segments by their own id: their pages come from the parent document's graph (brief 12).
        var kept = manifest.Items.First(i => i.Status == ItemStatuses.KeptNotProcessed);
        var page = int.Parse(kept.PageRange!.Split('-')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(200, (await rig.Api.GetLayoutAsync(Applicant, kept.DocumentId, page)).Status);
        Assert.Equal(404, (await rig.Api.GetLayoutAsync(new Caller("someone-else", Roles.Applicant), kept.DocumentId, page)).Status);
    }

    [Fact]
    public async Task A_processed_document_can_be_reopened_decided_and_overridden_with_audit_and_outbox_rows()
    {
        var rig = await RigAsync();
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("lossrun-export.csv", Fixture.Bytes("lossrun-export.csv"))], "Q-12B", null, null)).Body!;
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));
        var doc = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items).DocumentId;
        var commands = new DocumentCommands(rig.Runtime);

        // Reopen (11 Answers 2): role, reason, then once only.
        Assert.Equal(403, (await commands.ReopenAsync(Applicant, doc, new ReopenRequest("why"))).Status);
        Assert.Equal(400, (await commands.ReopenAsync(Underwriter, doc, new ReopenRequest(" "))).Status);
        var reopened = await commands.ReopenAsync(Underwriter, doc, new ReopenRequest("Broker sent a corrected 2023 run"));
        Assert.Equal(201, reopened.Status);
        Assert.Equal(409, (await commands.ReopenAsync(Underwriter, doc, new ReopenRequest("again"))).Status);
        var review = (await rig.Runtime.RecordStore.GetReviewAsync(doc))!;
        Assert.True(review.Reopened);
        Assert.Equal(("pending", "reopened"), (review.State, (await rig.Runtime.RecordStore.GetResultAsync(doc))!.ReviewState));
        Assert.Equal(ItemStatuses.AwaitingReview, Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items).Status);
        Assert.Equal(409, (await commands.OverrideAsync(Underwriter, doc, Override("1.00"))).Status); // decide the open review first

        // The decision applies at once (the upload orchestration finished long ago).
        var decided = await rig.Api.DecideAsync(Underwriter, doc, new ReviewDecisionRequest(ReviewActions.Accept, Rationale: "checked against the corrected run"));
        Assert.Equal(200, decided.Status);
        Assert.Equal("decided", (await rig.Runtime.RecordStore.GetReviewAsync(doc))!.State);
        Assert.Equal(ItemStatuses.Processed, Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items).Status);

        // A direct override: new revision, the value changed, review.decided announced.
        var before = (await rig.Runtime.RecordStore.GetResultAsync(doc))!.Revision;
        var overridden = await commands.OverrideAsync(Underwriter, doc, Override("950.00"));
        var result = Assert.IsType<OverrideResult>(overridden.Body);
        Assert.Equal((before + 1, 1), (result.Revision, result.Changed));
        Assert.Equal(950.00m, (await rig.Runtime.RecordStore.GetResultAsync(doc))!.Document.PolicyPeriods[0].Claims[1].Medical!.Reserve);

        var trail = (await rig.Runtime.RecordStore.AuditTrailAsync(doc, null, 100)).Entries.Select(e => e.Action).ToList();
        Assert.Contains("review.reopened", trail);
        Assert.Contains("review.accept", trail);
        Assert.Contains("override", trail);
        // Brief 24 step 3: reopened again and decided, so the quote moved upload → decision → override → reopen → decision.
        Assert.Equal(201, (await commands.ReopenAsync(Underwriter, doc, new ReopenRequest("one more check"))).Status);
        Assert.Equal(200, (await rig.Api.DecideAsync(Underwriter, doc, new ReviewDecisionRequest(ReviewActions.Accept, Rationale: "still right"))).Status);

        var outbox = await rig.Runtime.RecordStore.OutboxAsync("Q-12B", 50);
        Assert.Equal(3, outbox.Count(o => o.Kind == LossRun.Data.OutboxKinds.ReviewDecided));
        Assert.Single(outbox, o => o.Kind == LossRun.Data.OutboxKinds.UploadCompleted && o.Channel == LossRun.Data.OutboxChannels.Event);
        // One quote.payload per change (ADR 0024), each pinned to a strictly higher revision, each with its quote.revised.
        static int Revision(LossRun.Data.OutboxMessage o) => JsonNode.Parse(o.PayloadJson)!["quoteRevision"]!.GetValue<int>();
        var pushes = outbox.Where(o => o.Kind == LossRun.Data.OutboxKinds.QuotePayload && o.Channel == LossRun.Data.OutboxChannels.Command)
            .OrderBy(o => o.CreatedAt).ToList();
        Assert.Equal([1, 2, 3, 4, 5, 6], pushes.Select(Revision)); // brief 30 item 16: each reopen moves the quote too
        Assert.Equal(["upload-completed", "reopened", "reopen-decided", "override", "reopened", "reopen-decided"], pushes.Select(o => JsonNode.Parse(o.PayloadJson)!["cause"]!.GetValue<string>()));
        Assert.All(pushes, p => Assert.EndsWith($"?revision={Revision(p)}", JsonNode.Parse(p.PayloadJson)!["quotePayloadUrl"]!.GetValue<string>()));
        Assert.Equal([1, 2, 3, 4, 5, 6], outbox.Where(o => o.Kind == LossRun.Data.OutboxKinds.QuoteRevised).Select(Revision).Order());
        Assert.DoesNotContain(outbox, o => o.PayloadJson.Contains(LossRun.Data.QuoteRevisions.Token, StringComparison.Ordinal));
        Assert.Equal(6, await rig.Runtime.RecordStore.QuoteRevisionAsync("Q-12B"));

        // The payload names the revision; a pinned fetch of an older one is superseded; If-None-Match on the current one is 304.
        var analytics = new QuoteAnalytics(rig.Runtime);
        var current = await analytics.QuotePayloadAsync(Underwriter, "Q-12B");
        Assert.Equal(("\"q-6\"", 6), (current.Headers!["ETag"], JsonNode.Parse(current.Bytes!)!["quoteRevision"]!.GetValue<int>()));
        Assert.Equal(200, (await analytics.QuotePayloadAsync(Underwriter, "Q-12B", revision: 6)).Status);
        var stale = await analytics.QuotePayloadAsync(Underwriter, "Q-12B", revision: 5);
        Assert.Equal((409, "revision-superseded"), (stale.Status, ((ApiError)stale.Body!).Code));
        Assert.Equal(404, (await analytics.QuotePayloadAsync(Underwriter, "Q-12B", revision: 7)).Status);
        Assert.Equal(304, (await analytics.QuotePayloadAsync(Underwriter, "Q-12B", ifNoneMatch: "\"q-6\"")).Status);
        Assert.Equal(200, (await analytics.QuotePayloadAsync(Underwriter, "Q-12B", ifNoneMatch: "\"q-5\"")).Status);

        static OverrideRequest Override(string reserve) => new(
            [new FieldOverride("policyPeriods[0].claims[1].medical.reserve", JsonDocument.Parse(reserve).RootElement, "adjuster note: reserve reduced")], "reserve update");
    }

    [Fact]
    public async Task Quote_actions_write_their_outbox_command_with_the_audit_row()
    {
        var rig = await RigAsync();
        await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("lossrun-export.csv", Fixture.Bytes("lossrun-export.csv"))], "Q-12B-2", null, null);
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));
        var workbench = new WorkbenchApi(rig.Runtime);

        var flags = (FlagPushResult)(await workbench.PushFlagsAsync(Underwriter, "Q-12B-2", new FlagPushRequest())).Body!;
        await workbench.AssignFollowUpAsync(Underwriter, "Q-12B-2", new FollowUpRequest("uw-2", "Request the 2021 run"));
        Assert.Equal(201, (await workbench.AnnotateAsync(Underwriter, "Q-12B-2", new AnnotationRequest("Reserve trend looks adverse."))).Status);
        Assert.Equal(403, (await workbench.AnnotateAsync(Applicant, "Q-12B-2", new AnnotationRequest("x"))).Status);

        var outbox = await rig.Runtime.RecordStore.OutboxAsync("Q-12B-2", 50);
        var flagCommand = Assert.Single(outbox, o => o.Kind == LossRun.Data.OutboxKinds.Flags);
        Assert.Contains(flagCommand.Id, flags.Delivery);
        Assert.Single(outbox, o => o.Kind == LossRun.Data.OutboxKinds.FollowUp);
        Assert.Single(outbox, o => o.Kind == LossRun.Data.OutboxKinds.Annotation);
        Assert.All(outbox.Where(o => o.Channel == LossRun.Data.OutboxChannels.Command), o => Assert.Equal("Q-12B-2", o.QuoteId));
        var actions = (await rig.Runtime.RecordStore.AuditTrailAsync(null, "Q-12B-2", 100)).Entries.Select(e => e.Action).ToList();
        Assert.Contains("quote.flags.pushed", actions);
        Assert.Contains("quote.followup", actions);
        Assert.Contains("quote.annotation", actions);
    }

    [Fact]
    public async Task Same_content_returns_the_existing_upload_and_applicants_see_only_their_own()
    {
        var rig = await RigAsync();
        UploadFile[] files = [new("lossrun-export.csv", Fixture.Bytes("lossrun-export.csv"))];

        var first = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, files, null, null, null)).Body!;
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));
        var again = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, files, null, null, null)).Body!;
        var keyed = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, files, null, null, "sim-run-2")).Body!;

        Assert.Equal((first.UploadId, false, true), (again.UploadId, first.Existing, again.Existing));
        Assert.NotEqual(first.UploadId, keyed.UploadId);
        Assert.Equal(404, (await rig.Api.GetUploadAsync(new Caller("someone-else", Roles.Applicant), first.UploadId)).Status);
        var other = (UploadAccepted)(await rig.Api.CreateUploadAsync(new Caller("someone-else", Roles.Applicant), files, null, null, null)).Body!;
        Assert.NotEqual(first.UploadId, other.UploadId); // same bytes, different uploader: a separate upload
        Assert.Equal(200, (await rig.Api.GetUploadAsync(Underwriter, first.UploadId)).Status);
    }

    [LossRun.Data.Tests.SqlFact]
    public async Task With_sql_as_system_of_record_the_review_flow_writes_ledger_rows_and_the_audit_proof_passes()
    {
        var sql = new LossRun.Data.Tests.SqlFixture();
        await sql.InitializeAsync();
        try
        {
            var connection = await sql.DatabaseAsync("e2e");
            var rig = await RigAsync(tamper: true, records: LossRun.Data.SqlRecordStore.Create(connection));
            var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("lossrun-native.pdf", Fixture.Bytes("lossrun-native.pdf"))], "Q-7", null, null)).Body!;
            // The orchestration keeps running while the item awaits review, so wait for the item instead: with SQL as the record
            // store on a busy machine it can take longer than the 2 s StartAsync gives it.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while ((await ManifestAsync(rig, accepted.UploadId)).Items is not [{ Status: not ItemStatuses.Processing }] && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }

            var item = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items);
            Assert.Equal(ItemStatuses.AwaitingReview, item.Status);
            Assert.Single((List<ReviewItem>)(await rig.Api.ListReviewsAsync(Underwriter, "pending")).Body!);

            var fix = new ReviewDecisionRequest(ReviewActions.Override,
            [
                new FieldOverride("policyPeriods[0].subtotals.medical.paid", JsonDocument.Parse("9200").RootElement, "printed subtotal mis-read; 8,000 + 1,200"),
                new FieldOverride("policyPeriods[0].claims[0].claimantName", JsonDocument.Parse("\"Synthetic Claimant One\"").RootElement, "name per carrier letter"),
            ]);
            Assert.Equal(202, (await rig.Api.DecideAsync(Underwriter, item.DocumentId, fix)).Status);
            await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));

            var result = (DocumentResult)(await rig.Api.GetDocumentAsync(Underwriter, item.DocumentId)).Body!;
            Assert.Equal((2, "override"), (result.Revision, result.ReviewState));
            Assert.Equal(UploadStatuses.Completed, (await ManifestAsync(rig, accepted.UploadId)).Status);
            Assert.Empty(await rig.Store.ListAsync(Containers.Extractions, "audit/")); // the audit went to SQL, not Blob
            Assert.Equal(409, (await rig.Api.DecideAsync(Underwriter, item.DocumentId, fix)).Status);

            await using var db = new LossRun.Data.LossRunDbContext(LossRun.Data.LossRunDbContext.Options(connection));
            var actions = await db.AuditEvents.Select(a => a.Action).ToListAsync();
            Assert.Contains("upload.create", actions);
            Assert.Contains("extraction.persist", actions);
            Assert.Equal(2, actions.Count(a => a == "override"));
            Assert.Contains("review.override", actions);
            Assert.Equal(2, await db.Overrides.CountAsync());
            Assert.True(await db.UsageEvents.AnyAsync());
            Assert.Equal("Q-7", (await db.Documents.SingleAsync()).QuoteId);

            // Brief 24 step 3: the completion waited for the review (no push), so the decision's transaction wrote the quote's one
            // quote.payload command, with the revision dbo.ApplyDecision bumped to (1 completion, 2 decision).
            var push = Assert.Single(await db.Outbox.Where(o => o.Kind == LossRun.Data.OutboxKinds.QuotePayload).ToListAsync());
            Assert.Equal((2, "review-decided"), (JsonNode.Parse(push.PayloadJson)!["quoteRevision"]!.GetValue<int>(), JsonNode.Parse(push.PayloadJson)!["cause"]!.GetValue<string>()));
            Assert.Equal(2, await rig.Runtime.RecordStore.QuoteRevisionAsync("Q-7"));
            Assert.Equal(2, await db.Outbox.CountAsync(o => o.Kind == LossRun.Data.OutboxKinds.QuoteRevised));
            Assert.DoesNotContain(await db.Outbox.Select(o => o.PayloadJson).ToListAsync(), p => p.Contains(LossRun.Data.QuoteRevisions.Token));
            var proof = await LossRun.Data.AuditProof.RunAsync(connection);
            Assert.True(proof.Pass, string.Join("; ", proof.Checks.Where(c => !c.Pass).Select(c => $"{c.Name}: {c.Detail}")));
            Assert.Equal(1, proof.ClaimsWithHistory);
        }
        finally
        {
            await sql.DisposeAsync();
        }
    }

    [LossRun.Data.Tests.SqlFact]
    public async Task With_sql_a_rejected_document_leaves_the_chat_scope_and_the_payload_and_the_audit_proof_passes()
    {
        // Brief 24 step 2 on the ledger: the reject is an ordinary decision (ledger rows, audit proof), and the SQL chat scope
        // and the quote payload both drop the document.
        var sql = new LossRun.Data.Tests.SqlFixture();
        await sql.InitializeAsync();
        try
        {
            var connection = await sql.DatabaseAsync("reject");
            var rig = await RigAsync(records: LossRun.Data.SqlRecordStore.Create(connection));
            var keep = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("lossrun-native.pdf", Fixture.Bytes("lossrun-native.pdf"))], "Q-8", null, "sql-keep")).Body!;
            var drop = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("lossrun2-native.pdf", Fixture.Bytes("lossrun2-native.pdf"))], "Q-8", null, "sql-drop")).Body!;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while ((await ManifestAsync(rig, drop.UploadId)).Items is not [{ Status: not ItemStatuses.Processing }] && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }

            var kept = Assert.Single((await ManifestAsync(rig, keep.UploadId)).Items).DocumentId!;
            var rejected = Assert.Single((await ManifestAsync(rig, drop.UploadId)).Items).DocumentId!;
            Assert.Equal(2, (await rig.Runtime.RecordStore.DocumentsInScopeAsync(ChatScopes.Quote, "Q-8", null)).Count);

            Assert.Equal(202, (await rig.Api.DecideAsync(Underwriter, rejected, new ReviewDecisionRequest(ReviewActions.Reject, Rationale: "another account's run"))).Status);
            deadline = DateTime.UtcNow.AddSeconds(30);
            while ((await rig.Runtime.RecordStore.DocumentHeadAsync(rejected))?.ReviewState != ReviewActions.Reject && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }

            Assert.Equal(kept, Assert.Single(await rig.Runtime.RecordStore.DocumentsInScopeAsync(ChatScopes.Quote, "Q-8", null)).DocumentId);
            var payload = JsonNode.Parse(System.Text.Encoding.UTF8.GetString((await new QuoteAnalytics(rig.Runtime).QuotePayloadAsync(Underwriter, "Q-8")).Bytes!))!;
            Assert.Equal(rejected, Assert.Single(payload["excluded"]!.AsArray())!["documentId"]!.GetValue<string>());
            Assert.DoesNotContain(payload["claims"]!.AsArray(), c => c!["id"]!.GetValue<string>().StartsWith(rejected + "/", StringComparison.Ordinal));

            await using var db = new LossRun.Data.LossRunDbContext(LossRun.Data.LossRunDbContext.Options(connection));
            Assert.Contains("review.reject", await db.AuditEvents.Select(a => a.Action).ToListAsync());
            var proof = await LossRun.Data.AuditProof.RunAsync(connection);
            Assert.True(proof.Pass, string.Join("; ", proof.Checks.Where(c => !c.Pass).Select(c => $"{c.Name}: {c.Detail}")));
        }
        finally
        {
            await sql.DisposeAsync();
        }
    }

    [Fact]
    public async Task Quote_analytics_serve_reported_and_adjusted_features_and_an_override_invalidates_the_cache()
    {
        var rig = await RigAsync(tamper: true);
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("lossrun-native.pdf", Fixture.Bytes("lossrun-native.pdf"))], "Q-UC2", null, null)).Body!;
        var item = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items);
        var analytics = new QuoteAnalytics(rig.Runtime);
        static JsonNode Body(ApiResult r) => JsonNode.Parse(System.Text.Encoding.UTF8.GetString(r.Bytes!))!;
        static decimal Metric(JsonNode snapshot, string name) => snapshot["metrics"]!.AsArray().Single(m => m!["name"]!.GetValue<string>() == name)!["value"]!.GetValue<decimal>();

        Assert.Equal(403, (await analytics.FeaturesAsync(Applicant, "Q-UC2", null)).Status);
        Assert.Equal(404, (await analytics.FeaturesAsync(Underwriter, "Q-NONE", null)).Status);
        Assert.Equal(400, (await analytics.FeaturesAsync(Underwriter, "Q-UC2", "sideways")).Status);

        var account = (await analytics.LoadAsync(Underwriter, "Q-UC2", CancellationToken.None)).Account!;
        Assert.Equal("computed", (await analytics.BaselineAsync(account, "adjusted", CancellationToken.None)).Source);
        Assert.Equal("memory", (await analytics.BaselineAsync(account, "adjusted", CancellationToken.None)).Source);
        var before = Body(await analytics.FeaturesAsync(Underwriter, "Q-UC2", "adjusted"));
        var reserveBefore = Metric(before, "reserve_total");

        // An override of claim 0's indemnity reserve (+ the subtotal fix the tampered fixture needs).
        var original = (DocumentResult)(await rig.Api.GetDocumentAsync(Underwriter, item.DocumentId)).Body!;
        var oldReserve = original.Document.PolicyPeriods[0].Claims[0].Indemnity?.Reserve ?? 0m;
        var fix = new ReviewDecisionRequest(ReviewActions.Override,
        [
            new FieldOverride("policyPeriods[0].subtotals.medical.paid", JsonDocument.Parse("9200").RootElement, "printed subtotal mis-read; 8,000 + 1,200"),
            new FieldOverride("policyPeriods[0].claims[0].indemnity.reserve", JsonDocument.Parse("12345.67").RootElement, "adjuster's updated reserve"),
        ]);
        Assert.Equal(202, (await rig.Api.DecideAsync(Underwriter, item.DocumentId, fix)).Status);
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));

        var adjusted = Body(await analytics.FeaturesAsync(Underwriter, "Q-UC2", "adjusted"));
        var reported = Body(await analytics.FeaturesAsync(Underwriter, "Q-UC2", "reported"));
        Assert.Equal(reserveBefore + 12345.67m - oldReserve, Metric(adjusted, "reserve_total")); // the new revision misses L1 and is recomputed
        Assert.Equal(reserveBefore, Metric(reported, "reserve_total"));                          // as-reported = before the override
        var overrideIds = adjusted["metrics"]!.AsArray().Single(m => m!["name"]!.GetValue<string>() == "reserve_total")!["overrides"]!.AsArray();
        Assert.Single(overrideIds);

        var whatIf = Body(await analytics.WhatIfAsync(Underwriter, "Q-UC2", JsonDocument.Parse("""{"scenario":{"reserveFactorBps":1000}}""").RootElement));
        var delta = whatIf["deltas"]!.AsArray().Single(d => d!["name"]!.GetValue<string>() == "reserve_total")!;
        Assert.InRange(delta["delta"]!.GetValue<decimal>() - Metric(adjusted, "reserve_total") * 0.1m, -1m, 1m); // per-claim cent rounding

        var claims = Body(await analytics.ClaimsAsync(Underwriter, "Q-UC2", "adjusted"));
        var input = LossRun.Analytics.AnalyticsJson.ReadInput(claims["input"]!.ToJsonString());
        Assert.Equal(original.Document.PolicyPeriods.Sum(p => p.Claims.Count), input.Claims.Count);
        Assert.Equal(1_234_567L, input.Claims[0].IndemnityReserveCents);
        Assert.Equal(adjusted.ToJsonString(), JsonNode.Parse(LossRun.Analytics.FeatureEngine.Compute(input).ToJson())!.ToJsonString()); // the client's recompute = the server's

        var payload = Body(await analytics.QuotePayloadAsync(Underwriter, "Q-UC2"));
        Assert.Equal("2", payload["payloadVersion"]!.GetValue<string>());
        Assert.Equal(2, payload["lineage"]!["overrides"]!.AsArray().Count);
        Assert.NotNull(payload["features"]!["reported"]);
        Assert.Equal(input.Claims.Count, payload["claims"]!.AsArray().Count);
        Assert.Contains("\"catalogVersion\"", System.Text.Encoding.UTF8.GetString(QuoteAnalytics.CatalogJson().Bytes!));

        // The sample UW report package from the same payload: DOCX + JSON twin.
        var path = Path.Combine(Path.GetTempPath(), $"memo-{Guid.NewGuid():N}.docx");
        try
        {
            var memo = LossRun.Analytics.Memo.Write(payload.AsObject(), path);
            using var docx = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(path, false);
            var text = docx.MainDocumentPart!.Document.Body!.InnerText;
            Assert.Contains("Key indicators", text);
            Assert.Contains("adjuster's updated reserve", text);
            Assert.Equal(2, memo["dataQuality"]!["overrides"]!.AsArray().Count);
            Assert.True(File.Exists(Path.ChangeExtension(path, ".json")));
        }
        finally
        {
            File.Delete(path);
            File.Delete(Path.ChangeExtension(path, ".json"));
        }
    }

    [Fact]
    public async Task Workbench_context_rekeys_snapshots_and_quote_actions_are_audited()
    {
        var rig = await RigAsync();
        var payroll = new List<PeriodExposure> { new(new DateOnly(2022, 7, 1), 1_250_000m) };
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile("lossrun-native.pdf", Fixture.Bytes("lossrun-native.pdf"))], "Q-WB", null, null,
            payrollByPeriod: payroll)).Body!;
        await ManifestAsync(rig, accepted.UploadId);
        var workbench = new WorkbenchApi(rig.Runtime);
        var analytics = new QuoteAnalytics(rig.Runtime);
        static JsonNode Body(ApiResult r) => JsonNode.Parse(System.Text.Encoding.UTF8.GetString(r.Bytes!))!;
        static JsonNode Metric(JsonNode snapshot, string name) => snapshot["metrics"]!.AsArray().Single(m => m!["name"]!.GetValue<string>() == name)!;

        Assert.Equal(403, (await workbench.QuotesAsync(Applicant)).Status);
        var quote = Assert.Single((IReadOnlyList<QuoteSummary>)(await workbench.QuotesAsync(Underwriter)).Body!);
        Assert.Equal(("Q-WB", 1), (quote.QuoteId, quote.Documents));
        Assert.Equal(Roles.Underwriter, ((MeView)WorkbenchApi.Me(Underwriter).Body!).Role);

        // The upload's payroll made frequency measurable; FTE is still missing and says so.
        var first = Body(await analytics.FeaturesAsync(Underwriter, "Q-WB", null));
        Assert.NotNull(Metric(first, "frequency_per_100_payroll")["value"]);
        Assert.Equal("no FTE count in the submission context", Metric(first, "frequency_per_100_fte")["notMeasurable"]!.GetValue<string>());

        // A context change re-keys the cached snapshot (no stale hit), and is audited at quote level.
        var put = await workbench.PutContextAsync(Underwriter, "Q-WB", new QuoteContext("Q-WB", payroll, [new PeriodExposure(new DateOnly(2022, 7, 1), 25m)]));
        Assert.Equal(200, put.Status);
        var second = Body(await analytics.FeaturesAsync(Underwriter, "Q-WB", null));
        Assert.NotNull(Metric(second, "frequency_per_100_fte")["value"]);
        Assert.Equal(400, (await workbench.PutContextAsync(Underwriter, "Q-WB", new QuoteContext("Q-WB", [new PeriodExposure(new DateOnly(2022, 7, 1), -1m)]))).Status);

        var flags = (FlagPushResult)(await workbench.PushFlagsAsync(Underwriter, "Q-WB", new FlagPushRequest(Rationale: "bind review"))).Body!;
        Assert.True(flags.Claims > 0);
        var followUp = (FollowUp)(await workbench.AssignFollowUpAsync(Underwriter, "Q-WB", new FollowUpRequest("uw-2", "Request policy year 2024 loss run"))).Body!;
        Assert.Equal(400, (await workbench.AssignFollowUpAsync(Underwriter, "Q-WB", new FollowUpRequest("", ""))).Status);
        Assert.Equal(followUp.Id, Assert.Single((List<FollowUp>)(await workbench.FollowUpsAsync(Underwriter, "Q-WB")).Body!).Id);

        var memo = await workbench.MemoAsync(Underwriter, "Q-WB", "docx");
        Assert.Equal((200, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"), (memo.Status, memo.ContentType));
        Assert.Equal((byte)'P', memo.Bytes![0]); // a zip (OOXML) package
        Assert.Equal(400, (await workbench.MemoAsync(Underwriter, "Q-WB", "pdf")).Status);

        // Demo hygiene (brief 13): rename and retire are manager actions with a rationale; retired quotes leave the list, nothing is deleted.
        var manager = new Caller("mgr-1", Roles.UWManager);
        Assert.Equal(403, (await workbench.PatchQuoteAsync(Underwriter, "Q-WB", new QuoteLabelRequest("tidy", "Demo"))).Status);
        Assert.Equal(400, (await workbench.PatchQuoteAsync(manager, "Q-WB", new QuoteLabelRequest(" ", "Demo"))).Status);
        Assert.Equal(404, (await workbench.PatchQuoteAsync(manager, "Q-NONE", new QuoteLabelRequest("tidy", "Demo"))).Status);
        var renamed = (QuoteLabel)(await workbench.PatchQuoteAsync(manager, "Q-WB", new QuoteLabelRequest("demo name", "Q-WB · Workbench (synthetic)"))).Body!;
        Assert.Equal(("Q-WB · Workbench (synthetic)", (DateTimeOffset?)null), (renamed.DisplayName, renamed.RetiredAt));
        Assert.Equal("Q-WB · Workbench (synthetic)", Assert.Single((IReadOnlyList<QuoteSummary>)(await workbench.QuotesAsync(Underwriter)).Body!).DisplayName);
        await workbench.PatchQuoteAsync(manager, "Q-WB", new QuoteLabelRequest("smoke data", Retired: true));
        Assert.Empty((IReadOnlyList<QuoteSummary>)(await workbench.QuotesAsync(Underwriter)).Body!);
        var retired = Assert.Single((IReadOnlyList<QuoteSummary>)(await workbench.QuotesAsync(Underwriter, includeRetired: true)).Body!);
        Assert.Equal("Q-WB · Workbench (synthetic)", retired.DisplayName); // the name survives a retire
        Assert.NotNull(retired.RetiredAt);
        await workbench.PatchQuoteAsync(manager, "Q-WB", new QuoteLabelRequest("back for the test", Retired: false));

        var trail = (AuditTrailView)(await workbench.QuoteAuditAsync(Underwriter, "Q-WB")).Body!;
        var actions = trail.Entries.Select(e => e.Action).ToList();
        Assert.Equal(1, actions.Count(a => a == "quote.rename")); // one row per change, not per call
        Assert.Contains("quote.retire", actions);
        Assert.Contains("quote.restore", actions);
        Assert.Contains("quote.context", actions);
        Assert.Contains("quote.flags.pushed", actions);
        Assert.Contains("quote.followup", actions);
        Assert.Contains("quote.memo.exported", actions);
        Assert.Equal("append-only-blob", trail.Protection);
        Assert.True(trail.Entries.Zip(trail.Entries.Skip(1)).All(p => p.First.At >= p.Second.At)); // newest first

        var costs = (CostSummary)(await workbench.CostsAsync(Underwriter)).Body!;
        Assert.Equal(1, costs.DocumentsProcessed);
        Assert.Equal(costs.PocToDateUsd, costs.ByPath.Sum(p => p.TotalUsd));
        Assert.Null(costs.Reconciliation);
    }

    [Fact]
    public async Task Hard_rule_failure_goes_to_review_and_an_override_re_validates()
    {
        var rig = await RigAsync(tamper: true);
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("lossrun-native.pdf", Fixture.Bytes("lossrun-native.pdf"))], null, null, null)).Body!;

        var manifest = await ManifestAsync(rig, accepted.UploadId);
        Assert.Equal(UploadStatuses.AwaitingReview, manifest.Status);
        var item = Assert.Single(manifest.Items);
        Assert.Equal((ItemStatuses.AwaitingReview, "Review"), (item.Status, item.RoutingTier));
        var reviews = (List<ReviewItem>)(await rig.Api.ListReviewsAsync(Underwriter, "pending")).Body!;
        var review = Assert.Single(reviews);
        Assert.Equal(ReviewKinds.DocumentReview, review.Kind);
        Assert.Equal(403, (await rig.Api.ListReviewsAsync(Applicant, "pending")).Status);

        var path = "policyPeriods[0].subtotals.medical.paid";
        var bad = new ReviewDecisionRequest(ReviewActions.Override, [new FieldOverride(path, JsonDocument.Parse("9200").RootElement, "")]);
        Assert.Equal(400, (await rig.Api.DecideAsync(Underwriter, item.DocumentId, bad)).Status);
        Assert.Equal(403, (await rig.Api.DecideAsync(Applicant, item.DocumentId, bad)).Status);
        var fix = new ReviewDecisionRequest(ReviewActions.Override, [new FieldOverride(path, JsonDocument.Parse("9200").RootElement, "printed subtotal mis-read; 8,000 + 1,200")]);
        Assert.Equal(202, (await rig.Api.DecideAsync(Underwriter, item.DocumentId, fix)).Status);
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));

        var result = (DocumentResult)(await rig.Api.GetDocumentAsync(Underwriter, item.DocumentId)).Body!;
        Assert.Equal((2, "override"), (result.Revision, result.ReviewState));
        Assert.True(RuleEvaluator.PassesHardRules(result.RuleOutcomes));
        Assert.Equal("manual", result.Document.Lineage![path].Extractor);
        Assert.Equal(UploadStatuses.Completed, (await ManifestAsync(rig, accepted.UploadId)).Status);
        var audit = (await rig.Store.GetAsync(Containers.Extractions, (await rig.Store.ListAsync(Containers.Extractions, "audit/")).Single()))!.ToString();
        Assert.Contains("\"action\":\"override\"", audit);
        Assert.Contains("\"oldValue\":9300", audit);
        Assert.Equal(409, (await rig.Api.DecideAsync(Underwriter, item.DocumentId, fix)).Status); // already decided
    }

    [Fact]
    public async Task Retiring_a_format_needs_a_manager_and_a_reason_and_is_audited()
    {
        var rig = await RigAsync();

        Assert.Equal(403, (await rig.Api.RetireFormatAsync(Underwriter, "synthetic-mutual-pdf", 1, "dup")).Status);
        Assert.Equal(400, (await rig.Api.RetireFormatAsync(new Caller("boss", Roles.UWManager), "synthetic-mutual-pdf", 1, " ")).Status);
        var ok = await rig.Api.RetireFormatAsync(new Caller("boss", Roles.UWManager), "synthetic-mutual-pdf", 1, "test copy");

        Assert.Equal(FormatLifecycle.Retired, ((FormatSummary)ok.Body!).Status);
        var audit = (await rig.Store.GetAsync(Containers.Extractions, (await rig.Store.ListAsync(Containers.Extractions, "audit/")).Single()))!.ToString();
        Assert.Contains("format.retire", audit);
    }

    [Fact]
    public async Task The_pre_bake_off_reset_retires_every_learned_version_and_keeps_the_hand_specs()
    {
        var rig = await RigAsync();
        var admin = new Caller("admin", Roles.Admin);
        await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("lossrun2-native.pdf", Fixture.Bytes("lossrun2-native.pdf"))], null, null, null);
        Assert.Contains((List<FormatSummary>)(await rig.Api.ListFormatsAsync()).Body!, f => f.FormatId == "fictional-casualty-group-pdf" && f.Status == FormatLifecycle.Shadow);

        Assert.Equal(403, (await rig.Api.ResetLearnedFormatsAsync(new Caller("boss", Roles.UWManager), "bake-off", false)).Status);
        Assert.Equal(400, (await rig.Api.ResetLearnedFormatsAsync(admin, " ", false)).Status);
        var dry = (FormatResetResult)(await rig.Api.ResetLearnedFormatsAsync(admin, "bake-off reset", true)).Body!;
        Assert.Equal(["fictional-casualty-group-pdf@1"], dry.Retired);
        Assert.Empty(await rig.Store.ListAsync(Containers.Extractions, "audit/")); // a dry run changes nothing

        var done = (FormatResetResult)(await rig.Api.ResetLearnedFormatsAsync(admin, "bake-off reset", false)).Body!;

        Assert.Equal(["fictional-casualty-group-pdf@1"], done.Retired);
        var formats = (List<FormatSummary>)(await rig.Api.ListFormatsAsync()).Body!;
        Assert.All(formats.Where(f => f.CreatedBy != "hand"), f => Assert.Equal(FormatLifecycle.Retired, f.Status));
        Assert.All(formats.Where(f => f.CreatedBy == "hand"), f => Assert.NotEqual(FormatLifecycle.Retired, f.Status));
        var audit = (await rig.Store.GetAsync(Containers.Extractions, (await rig.Store.ListAsync(Containers.Extractions, "audit/")).Single()))!.ToString();
        Assert.Contains("formats.reset", audit);
        Assert.Contains("fictional-casualty-group-pdf@1", audit);
        Assert.Empty(((FormatResetResult)(await rig.Api.ResetLearnedFormatsAsync(admin, "again", true)).Body!).Retired); // idempotent
    }

    [Fact]
    public async Task A_burst_of_one_new_format_learns_once_and_the_queued_documents_replay_it()
    {
        var rig = await RigAsync();
        rig.Orchestrations.Harness.LearnSlots = 1; // the live cap (08d Answers 1)
        rig.Orchestrations.Harness.TimerDelay = TimeSpan.FromMilliseconds(5);
        var bytes = Fixture.Bytes("lossrun2-native.pdf");
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant,
            [new UploadFile("run-a.pdf", bytes), new UploadFile("run-b.pdf", [.. bytes, .. "%b"u8]), new UploadFile("run-c.pdf", [.. bytes, .. "%c"u8])], null, null, null)).Body!;
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));

        var items = (await ManifestAsync(rig, accepted.UploadId)).Items;
        Assert.Equal(1, rig.Model.Calls); // 12b: learned once, not once per queued document
        Assert.Single(items, i => i.RoutingTier == "Learned");
        Assert.True(2 == items.Count(i => i.RoutingTier is "ReplayAccepted" && i.FormatRef == items.Single(l => l.RoutingTier == "Learned").FormatRef),
            string.Join("; ", items.Select(i => $"{i.RoutingTier}/{i.FormatRef}/{i.Status}/{i.ReviewKind}/{i.Badge}")));
    }

    [Fact]
    public async Task The_confirm_format_review_decides_the_learned_aliases_and_a_correction_retires_the_one_it_read_through()
    {
        // ADR 0019. The learned spec maps headers the seed resolves; two aliases the format introduced are recorded as candidates.
        var rig = await RigAsync();
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("lossrun2-native.pdf", Fixture.Bytes("lossrun2-native.pdf"))], null, null, null)).Body!;
        var item = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items);
        var spec = (await rig.Registry.GetAsync("fictional-casualty-group-pdf"))!;
        var paid = spec.ColumnMap.First(c => c.Target.StartsWith("claim.", StringComparison.Ordinal) && c.Target.EndsWith(".paid", StringComparison.Ordinal));
        LearnedMapping Alias(string header, string field) => new()
        {
            Header = header, CanonicalField = field, CarrierName = spec.CarrierName, FormatId = spec.FormatId, FormatVersion = "1", FirstSeenDocumentId = item.DocumentId,
            LearnedBy = new LearnedBy { Model = "learn", PromptVersion = "learn-v8" }, Confidence = 0.9,
            Evidence = [new AliasEvidence { DocumentId = item.DocumentId, FormatRef = spec.Ref, Route = "learn", TotalsTie = true }],
        };
        await rig.Registry.AppendLearnedMappingsAsync([Alias(paid.Source, paid.Target), Alias("Mystery Column", "claim.status")]);

        Assert.Equal(400, (await rig.Api.DecideAsync(Underwriter, item.DocumentId, new ReviewDecisionRequest(ReviewActions.Accept,
            Aliases: [new AliasDecision("Mystery Column", "claim.status", AliasDecision.Edit, "claim.notAField")]))).Status);
        await rig.Api.DecideAsync(Underwriter, item.DocumentId, new ReviewDecisionRequest(ReviewActions.Accept, Rationale: "format looks right",
            Aliases: [new AliasDecision("Mystery Column", "claim.status", AliasDecision.Reject)]));
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));

        var aliases = (List<LearnedMapping>)(await rig.Api.ListAliasesAsync(spec.FormatId)).Body!;
        Assert.Equal(AliasStatus.Active, aliases.Single(a => a.Header == paid.Source).Status); // accepted with the format
        Assert.Equal(AliasStatus.Retired, aliases.Single(a => a.Header == "Mystery Column").Status);

        // A correction of a value read through the accepted alias retires it, linked to the override.
        var doc = (await rig.Runtime.RecordStore.GetResultAsync(item.DocumentId))!;
        var path = $"policyPeriods[0].claims[0].{paid.Target["claim.".Length..]}";
        var overridden = await new DocumentCommands(rig.Runtime).OverrideAsync(Underwriter, item.DocumentId,
            new OverrideRequest([new FieldOverride(path, JsonDocument.Parse("1234.00").RootElement, "the printed value was misread")], "correction"));
        Assert.Equal(200, overridden.Status);

        var retired = ((List<LearnedMapping>)(await rig.Api.ListAliasesAsync(spec.FormatId, AliasStatus.Retired)).Body!).Single(a => a.Header == paid.Source);
        Assert.Equal($"override:{item.DocumentId}@{doc.Revision + 1}:{path}", retired.RetiredAuditEventId);
        var audit = string.Join("\n", await Task.WhenAll((await rig.Store.ListAsync(Containers.Extractions, "audit/")).Select(async k => (await rig.Store.GetAsync(Containers.Extractions, k))!.ToString())));
        Assert.Contains("glossary.alias.accepted", audit);
        Assert.Contains("glossary.alias.rejected", audit);
        Assert.Contains("glossary.alias.retired", audit);
    }

    [Fact]
    public async Task Accept_or_reject_with_overrides_is_refused_never_dropped()
    {
        // Brief 24 step 1: typed corrections sent with "Accept extracted values" were silently discarded.
        var rig = await RigAsync(tamper: true);
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("lossrun-native.pdf", Fixture.Bytes("lossrun-native.pdf"))], null, null, null)).Body!;
        var item = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items);
        var change = new FieldOverride("policyPeriods[0].subtotals.medical.paid", JsonDocument.Parse("9200").RootElement, "printed subtotal mis-read");

        foreach (var action in new[] { ReviewActions.Accept, ReviewActions.Reject })
        {
            var refused = await rig.Api.DecideAsync(Underwriter, item.DocumentId, new ReviewDecisionRequest(action, [change], Rationale: "looks fine overall"));
            Assert.Equal(400, refused.Status);
            Assert.Contains("overrides-not-allowed", JsonSerializer.Serialize(refused.Body));
        }

        Assert.Contains(((List<ReviewItem>)(await rig.Api.ListReviewsAsync(Underwriter, "pending")).Body!), r => r.DocumentId == item.DocumentId); // nothing was decided
    }

    [Fact]
    public async Task Rejecting_a_document_marks_it_rejected_and_never_activates_a_format()
    {
        var rig = await RigAsync();
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("lossrun2-native.pdf", Fixture.Bytes("lossrun2-native.pdf"))], null, null, null)).Body!;
        var item = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items);
        Assert.Equal(ReviewKinds.ConfirmFormat, item.ReviewKind);

        // promoteToFormat: true with reject is ignored: the server never honours it with a reject.
        Assert.Equal(202, (await rig.Api.DecideAsync(Underwriter, item.DocumentId,
            new ReviewDecisionRequest(ReviewActions.Reject, PromoteToFormat: true, Rationale: "not a loss run of this account"))).Status);
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));

        var result = (DocumentResult)(await rig.Api.GetDocumentAsync(Underwriter, item.DocumentId)).Body!;
        Assert.Equal(("reject", 2), (result.ReviewState, result.Revision));
        Assert.Equal(ItemStatuses.Rejected, (await ManifestAsync(rig, accepted.UploadId)).Items.Single().Status);
        var formats = (List<FormatSummary>)(await rig.Api.ListFormatsAsync()).Body!;
        Assert.Contains(formats, f => f.FormatId == "fictional-casualty-group-pdf" && f.Status == FormatLifecycle.Shadow);
    }

    [Fact]
    public async Task Approve_document_only_leaves_a_confirm_format_item_in_shadow()
    {
        // Brief 24 step 1: on a confirm-format item promoteToFormat: false approves the document and keeps the format in shadow.
        var rig = await RigAsync();
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("lossrun2-native.pdf", Fixture.Bytes("lossrun2-native.pdf"))], null, null, null)).Body!;
        var item = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items);

        await rig.Api.DecideAsync(Underwriter, item.DocumentId, new ReviewDecisionRequest(ReviewActions.Accept, PromoteToFormat: false, Rationale: "values right; format not yet"));
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("accept", ((DocumentResult)(await rig.Api.GetDocumentAsync(Underwriter, item.DocumentId)).Body!).ReviewState);
        var formats = (List<FormatSummary>)(await rig.Api.ListFormatsAsync()).Body!;
        Assert.Contains(formats, f => f.FormatId == "fictional-casualty-group-pdf" && f.Status == FormatLifecycle.Shadow);
    }

    [Fact]
    public async Task The_dated_demo_csv_replays_with_resolved_periods_and_the_plain_csv_still_needs_its_dates()
    {
        // Brief 25 Answers 3: the demo's first upload fills values (policy term on every row, valuation date from the upload);
        // the second, the plain export, shows the R-33 markers.
        var rig = await RigAsync();
        var dated = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("lossrun-export-dated.csv", Fixture.Bytes("lossrun-export-dated.csv"))], "Q-DATED", null,
            null, valuationDate: new DateOnly(2024, 6, 30))).Body!;
        var item = Assert.Single((await ManifestAsync(rig, dated.UploadId)).Items);
        Assert.Equal(("ReplayAccepted", "synthetic-mutual-csv-dated@1"), (item.RoutingTier, item.FormatRef));
        var payload = JsonNode.Parse(System.Text.Encoding.UTF8.GetString((await new QuoteAnalytics(rig.Runtime).QuotePayloadAsync(Underwriter, "Q-DATED")).Bytes!))!.AsObject();
        var periods = payload["policyPeriods"]!.AsArray();
        Assert.Equal(2, periods.Count);
        Assert.All(periods, p => Assert.NotNull(p!["effectiveDate"]));
        Assert.All(periods, p => Assert.NotNull(p!["valuationDate"]));

        var plain = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("lossrun-export.csv", Fixture.Bytes("lossrun-export.csv"))], "Q-PLAIN", null, null)).Body!;
        Assert.Equal("synthetic-mutual-csv@1", Assert.Single((await ManifestAsync(rig, plain.UploadId)).Items).FormatRef);
    }

    [Fact]
    public async Task R15_and_R25_evaluate_once_the_quote_supplies_a_target_date_and_requested_years()
    {
        // Brief 29 §3: no production path supplied a RuleContext, so both rules were always not-applicable.
        var rig = await RigAsync();
        RuleOutcome Rule(DocumentResult r, string id) => r.RuleOutcomes.Single(o => o.RuleId == id);
        async Task<DocumentResult> Upload(string quote, DateOnly? target, int[]? years)
        {
            var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("lossrun-export-dated.csv", Fixture.Bytes("lossrun-export-dated.csv"))], quote, null,
                $"r15-{quote}", valuationDate: new DateOnly(2024, 6, 30), targetPolicyEffectiveDate: target, requestedPolicyYears: years)).Body!; // own key: same bytes
            var doc = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items).DocumentId;
            return (DocumentResult)(await rig.Api.GetDocumentAsync(Underwriter, doc)).Body!;
        }

        var none = await Upload("Q-NOCTX", null, null); // the synthetic corpus case: no context, both n/a, routing unchanged
        Assert.Equal((RuleStatus.NotApplicable, RuleStatus.NotApplicable), (Rule(none, "R-15").Status, Rule(none, "R-25").Status));

        // Valued 2024-06-30, quoting 2025-01-01 (185 days): stale. Periods 2022 and 2023, 2024 requested: missing. Both soft.
        var stale = await Upload("Q-CTX", new DateOnly(2025, 1, 1), [2022, 2023, 2024]);
        Assert.Equal((RuleStatus.Fail, RuleSeverity.Soft), (Rule(stale, "R-15").Status, Rule(stale, "R-15").Severity));
        Assert.Equal((RuleStatus.Fail, RuleSeverity.Soft), (Rule(stale, "R-25").Status, Rule(stale, "R-25").Severity));
        Assert.Equal("ReplayAccepted", stale.Routing!.Tier.ToString()); // context rules never push a document to delta
        var context = (QuoteContext)(await new WorkbenchApi(rig.Runtime).GetContextAsync(Underwriter, "Q-CTX")).Body!;
        Assert.Equal((new DateOnly(2025, 1, 1), 3), (context.TargetPolicyEffectiveDate, context.RequestedPolicyYears!.Count)); // merged from the upload

        // PUT /quotes/{q}/context, then a value change re-validates with the new context: both pass.
        Assert.Equal(200, (await new WorkbenchApi(rig.Runtime).PutContextAsync(Underwriter, "Q-CTX", context with { TargetPolicyEffectiveDate = new DateOnly(2024, 7, 15), RequestedPolicyYears = [2022, 2023] })).Status);
        var path = stale.Document.Lineage!.Keys.First(k => k.EndsWith(".claimantName", StringComparison.Ordinal));
        Assert.Equal(200, (await new DocumentCommands(rig.Runtime).OverrideAsync(Underwriter, stale.DocumentId,
            new OverrideRequest([new FieldOverride(path, JsonDocument.Parse("\"Synthetic Claimant A\"").RootElement, "spelling per the broker")], "context changed"))).Status);
        var after = (DocumentResult)(await rig.Api.GetDocumentAsync(Underwriter, stale.DocumentId)).Body!;
        Assert.Equal((RuleStatus.Pass, RuleStatus.Pass), (Rule(after, "R-15").Status, Rule(after, "R-25").Status));
    }

    [Fact]
    public async Task Engineer_reads_but_never_decides_and_the_quote_system_reads_only_the_payload()
    {
        var rig = await RigAsync(tamper: true);
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("lossrun-native.pdf", Fixture.Bytes("lossrun-native.pdf"))], "Q-ROLES", null, null)).Body!;
        var doc = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items).DocumentId;
        var engineer = new Caller("eng-1", Roles.Engineer);
        var quoteSystem = new Caller("quotesystem", Roles.QuoteSystem);
        var analytics = new QuoteAnalytics(rig.Runtime);
        var workbench = new WorkbenchApi(rig.Runtime);

        // Brief 26 Answers 3: everything a reviewer reads; no decision, override, memo or flag push.
        Assert.Equal(200, (await rig.Api.ListReviewsAsync(engineer, "pending")).Status);
        Assert.Equal(200, (await rig.Api.GetDocumentAsync(engineer, doc)).Status);
        Assert.Equal(200, (await workbench.DocumentAuditAsync(engineer, doc)).Status);
        Assert.Equal(200, (await analytics.QuotePayloadAsync(engineer, "Q-ROLES")).Status);
        Assert.Equal(403, (await rig.Api.DecideAsync(engineer, doc, new ReviewDecisionRequest(ReviewActions.Accept))).Status);
        Assert.Equal(403, (await workbench.PushFlagsAsync(engineer, "Q-ROLES", new FlagPushRequest([]))).Status);
        Assert.Equal(403, (await workbench.MemoAsync(engineer, "Q-ROLES", "json")).Status);
        Assert.Equal(200, (await new EscalationApi(rig.Runtime).ListAsync(engineer, null, null, null)).Status);
        Assert.True(((MeView)WorkbenchApi.Me(engineer).Body!) is { CanReview: false, CanRead: true });

        // Brief 25 Answers 2: the quote payload, manifests and integration status; nothing else, nothing that writes.
        Assert.Equal(200, (await analytics.QuotePayloadAsync(quoteSystem, "Q-ROLES")).Status);
        Assert.Equal(200, (await rig.Api.GetUploadAsync(quoteSystem, accepted.UploadId)).Status);
        Assert.Equal(200, (await workbench.IntegrationAsync(quoteSystem, "Q-ROLES")).Status);
        Assert.Equal(403, (await rig.Api.GetDocumentAsync(quoteSystem, doc)).Status);
        Assert.Equal(403, (await analytics.FeaturesAsync(quoteSystem, "Q-ROLES", null)).Status);
        Assert.Equal(403, (await rig.Api.ListReviewsAsync(quoteSystem, "pending")).Status);
        Assert.Equal(403, (await rig.Api.DecideAsync(quoteSystem, doc, new ReviewDecisionRequest(ReviewActions.Accept))).Status);
        Assert.Equal(403, (await rig.Api.CreateUploadAsync(quoteSystem, [new UploadFile("x.csv", [1])], "Q-ROLES", null, null)).Status);
        Assert.Equal(403, (await workbench.QuotesAsync(quoteSystem)).Status);
    }

    [Fact]
    public async Task Readiness_follows_field_status_and_a_rejected_document_leaves_the_payload()
    {
        // Brief 24 step 2, the review plan's "8 of 10" example: accepted with a hard failure left, the failing fields stay
        // unresolved (null in the payload, named in readiness); the override resolves them; a rejected document is excluded.
        var rig = await RigAsync(tamper: true);
        var analytics = new QuoteAnalytics(rig.Runtime);
        async Task<JsonObject> Payload() =>
            JsonNode.Parse(System.Text.Encoding.UTF8.GetString((await analytics.QuotePayloadAsync(Underwriter, "Q-RDY")).Bytes!))!.AsObject();

        var first = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("lossrun-native.pdf", Fixture.Bytes("lossrun-native.pdf"))], "Q-RDY", null, "readiness-d1")).Body!;
        // Idempotency keys give these uploads their own document ids (QuoteAnalytics caches results by id and revision across rigs).
        var d1 = Assert.Single((await ManifestAsync(rig, first.UploadId)).Items).DocumentId!;

        int d1Periods() => rig.Runtime.RecordStore.GetResultAsync(d1).GetAwaiter().GetResult()!.Document.PolicyPeriods.Count;
        var waitingPayload = await Payload();
        var waiting = waitingPayload["readiness"]!;
        Assert.False(waiting["ready"]!.GetValue<bool>());
        // Brief 24 Answers 2: the payload is verified-only — a document waiting for review is excluded, none of its claims travel.
        Assert.Equal("verified-only", waitingPayload["featuresView"]!.GetValue<string>());
        Assert.Equal("pending-review", Assert.Single(waitingPayload["excluded"]!.AsArray())!["reason"]!.GetValue<string>());
        // Brief 25: the quote system's columns — every period, a pending one with its reason and its identity nulled.
        var pendingPeriods = waitingPayload["policyPeriods"]!.AsArray();
        Assert.Equal(d1Periods(), pendingPeriods.Count);
        Assert.All(pendingPeriods, p => Assert.Equal("pending-review", p!["excludedReason"]!.GetValue<string>()));
        var pendingPeriod = pendingPeriods[0]!;
        Assert.Null(pendingPeriod["effectiveDate"]);
        Assert.Equal("review-pending", pendingPeriod["fieldStatus"]!["effectiveDate"]!["reason"]!.GetValue<string>());
        Assert.Empty(waitingPayload["claims"]!.AsArray());
        Assert.Equal(d1, Assert.Single(waiting["pendingDocuments"]!.AsArray())!["documentId"]!.GetValue<string>());

        Assert.Equal(202, (await rig.Api.DecideAsync(Underwriter, d1, new ReviewDecisionRequest(ReviewActions.Accept, Rationale: "looks right"))).Status);
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));

        var accepted = (DocumentResult)(await rig.Api.GetDocumentAsync(Underwriter, d1)).Body!;
        var unresolved = accepted.FieldStatus!.Where(s => s.Value.Status == FieldStates.Unresolved).ToList();
        Assert.NotEmpty(unresolved);
        Assert.Contains(accepted.FieldStatus!, s => s.Value.Status == FieldStates.Approved);
        Assert.All(unresolved, s => Assert.StartsWith("R-", s.Value.Reason));
        var payload = await Payload();
        var readiness = payload["readiness"]!;
        Assert.False(readiness["ready"]!.GetValue<bool>());
        Assert.Empty(readiness["pendingDocuments"]!.AsArray());
        Assert.Equal(unresolved.Count, readiness["blockingFieldCount"]!.GetValue<int>());
        var claimPath = unresolved.Select(s => s.Key).First(k => k.StartsWith("policyPeriods[0].claims[0].", StringComparison.Ordinal));
        var claim0 = payload["claims"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == $"{d1}/0/0")!;
        var field = claimPath["policyPeriods[0].claims[0].".Length..];
        Assert.Equal(FieldStates.Unresolved, claim0["fieldStatus"]![field]!["status"]!.GetValue<string>());
        Assert.Null(field.Split('.').Aggregate((JsonNode?)claim0, (n, part) => n?[part]));
        Assert.NotNull(claim0["claimNumber"]);
        // The payload's features are the verified-only snapshot (the R-10 values left out); the Analytics view keeps them.
        static string Json(ApiResult r) => System.Text.Encoding.UTF8.GetString(r.Bytes!);
        var verifiedFeatures = JsonNode.Parse(Json(await analytics.FeaturesAsync(Underwriter, "Q-RDY", "verified-only")))!;
        Assert.Equal(payload["features"]!["adjusted"]!.ToJsonString(), verifiedFeatures.ToJsonString());
        Assert.NotEqual(verifiedFeatures["inputsHash"]!.GetValue<string>(), JsonNode.Parse(Json(await analytics.FeaturesAsync(Underwriter, "Q-RDY", "adjusted")))!["inputsHash"]!.GetValue<string>());

        // The fix the tampered fixture needs (the printed subtotal) resolves every remaining failure: the quote is ready.
        var fix = await new DocumentCommands(rig.Runtime).OverrideAsync(Underwriter, d1, new OverrideRequest(
            [new FieldOverride("policyPeriods[0].subtotals.medical.paid", JsonDocument.Parse("9200").RootElement, "printed subtotal mis-read; 8,000 + 1,200")], "subtotal"));
        Assert.Equal(200, fix.Status);
        Assert.True((await Payload())["readiness"]!["ready"]!.GetValue<bool>());

        // A second document on the quote, rejected: out of claims, listed in excluded[] with its revision, never blocking.
        var second = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("lossrun2-native.pdf", Fixture.Bytes("lossrun2-native.pdf"))], "Q-RDY", null, "readiness-d2")).Body!;
        var d2 = Assert.Single((await ManifestAsync(rig, second.UploadId)).Items).DocumentId!;
        Assert.False((await Payload())["readiness"]!["ready"]!.GetValue<bool>()); // d2 waits for its format confirmation
        await rig.Api.DecideAsync(Underwriter, d2, new ReviewDecisionRequest(ReviewActions.Reject, Rationale: "another account's run"));
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));

        payload = await Payload();
        Assert.DoesNotContain(payload["claims"]!.AsArray(), c => c!["id"]!.GetValue<string>().StartsWith(d2 + "/", StringComparison.Ordinal));
        Assert.DoesNotContain(payload["documents"]!.AsArray(), d => d!["documentId"]!.GetValue<string>() == d2);
        var excluded = Assert.Single(payload["excluded"]!.AsArray())!;
        Assert.Equal((d2, "rejected", 2), (excluded["documentId"]!.GetValue<string>(), excluded["reason"]!.GetValue<string>(), excluded["revision"]!.GetValue<int>()));
        Assert.True(payload["readiness"]!["ready"]!.GetValue<bool>());
        Assert.Equal(d2, Assert.Single(payload["readiness"]!["rejectedDocuments"]!.AsArray())!["documentId"]!.GetValue<string>());
        var periods = payload["policyPeriods"]!.AsArray();
        Assert.Contains(periods, p => p!["documentId"]!.GetValue<string>() == d2 && p["excludedReason"]!.GetValue<string>() == "rejected");
        Assert.Contains(periods, p => p!["documentId"]!.GetValue<string>() == d1 && p["excludedReason"] is null && p["effectiveDate"] is not null);
        Assert.DoesNotContain(await rig.Runtime.RecordStore.DocumentsInScopeAsync(ChatScopes.Quote, "Q-RDY", null), d => d.DocumentId == d2);
    }

    [Fact]
    public async Task A_learn_exhausted_document_creates_exactly_one_escalation_and_one_event()
    {
        var rig = await RigAsync();
        rig.Model.Reply = "{}";
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("lossrun2-native.pdf", Fixture.Bytes("lossrun2-native.pdf"))], null, null, null)).Body!;
        var item = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items);
        Assert.Equal("Review", item.RoutingTier);

        var escalation = Assert.Single(await rig.Runtime.RecordStore.ListEscalationsAsync());
        Assert.Equal((EscalationReasons.LearnExhausted, 1, EscalationStatuses.New, "learn"), (escalation.ReasonCode, escalation.Count, escalation.Status, escalation.TraceSummary.Step));
        Assert.Equal([item.DocumentId], escalation.DocumentIds);
        Assert.StartsWith("fp:", escalation.FormatFingerprint, StringComparison.Ordinal);
        var events = (await rig.Runtime.RecordStore.OutboxAsync(null, 100)).Where(o => o.Kind.StartsWith("escalation.", StringComparison.Ordinal)).ToList();
        Assert.Equal(OutboxKinds.EscalationRaised, Assert.Single(events).Kind);

        // The same document again (a retried activity, the sweep) is not a second occurrence.
        var again = await new EscalationService(rig.Runtime).RaiseAsync(EscalationTriggers.ForDocument(EscalationReasons.LearnExhausted,
            (await rig.Runtime.RecordStore.GetResultAsync(item.DocumentId))!, DateTimeOffset.UtcNow, [], "learn"), LossRun.Data.Actor.System);
        Assert.Equal((1, 1), (again!.Count, again.Version));

        // A reviewer flags it; the queue lists both, and only triage roles see it.
        var api = new EscalationApi(rig.Runtime);
        Assert.Equal(201, (await api.FlagAsync(Underwriter, item.DocumentId, new FlagRequest("totals look doubled"))).Status);
        Assert.Equal(403, (await api.ListAsync(Underwriter, null, null, null)).Status);
        var manager = new Caller("mgr-1", Roles.UWManager);
        Assert.Equal(2, ((IReadOnlyList<Escalation>)(await api.ListAsync(manager, "open", null, null)).Body!).Count);
        Assert.Equal(400, (await api.SetStatusAsync(manager, escalation.Id, new EscalationStatusRequest("resolved", " "))).Status);
        Assert.Equal(200, (await api.SetStatusAsync(manager, escalation.Id, new EscalationStatusRequest("investigating", "looking at the learn trace"))).Status);
        Assert.Equal(400, (await api.CreateTuningItemAsync(manager, escalation.Id, new TuningItemRequest("z", "x"))).Status);
        Assert.Equal(201, (await api.CreateTuningItemAsync(manager, escalation.Id, new TuningItemRequest("b", "two-line records the grammar cannot express"))).Status);
        var tuning = Assert.Single((IReadOnlyList<TuningItem>)(await api.TuningItemsAsync(manager)).Body!);
        Assert.Equal(("b", escalation.Id), (tuning.Category, tuning.EscalationId));
        Assert.Equal(tuning.Id, (await rig.Runtime.RecordStore.GetEscalationAsync(escalation.Id))!.TuningItemRef);
        var audit = (await rig.Runtime.RecordStore.AuditTrailAsync(null, null, 200)).Entries.Select(e => e.Action).ToList();
        Assert.Contains("escalation.raised", audit);
        Assert.Contains("escalation.status", audit);
        Assert.Contains("escalation.tuning-item", audit);
    }

    [Fact]
    public async Task New_carrier_is_learned_and_confirmed_by_an_underwriter()
    {
        var rig = await RigAsync();
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Applicant, [new UploadFile("lossrun2-native.pdf", Fixture.Bytes("lossrun2-native.pdf"))], null, null, null)).Body!;

        var item = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items);
        Assert.Equal(("Learned", ItemStatuses.Processed, ReviewKinds.ConfirmFormat, FormatLifecycle.Shadow), (item.RoutingTier, item.Status, item.ReviewKind, item.FormatStatus));
        Assert.Equal(1, rig.Model.Calls);
        var formats = (List<FormatSummary>)(await rig.Api.ListFormatsAsync()).Body!;
        Assert.Contains(formats, f => f.FormatId == "fictional-casualty-group-pdf" && f.Status == FormatLifecycle.Shadow && f.VerificationGate == "pdf-totals");

        await rig.Api.DecideAsync(Underwriter, item.DocumentId, new ReviewDecisionRequest(ReviewActions.Accept, Rationale: "format looks right"));
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));

        formats = (List<FormatSummary>)(await rig.Api.ListFormatsAsync()).Body!;
        Assert.Contains(formats, f => f.FormatId == "fictional-casualty-group-pdf" && f.Status == FormatLifecycle.Active);
    }
}
