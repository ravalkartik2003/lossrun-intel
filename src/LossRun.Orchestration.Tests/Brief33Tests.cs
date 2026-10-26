using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Data;
using Microsoft.Data.SqlClient;
using Microsoft.DurableTask;

namespace LossRun.Orchestration.Tests;

/// <summary>Brief 33: failure details captured by tools/capture-failures, and real SDK exceptions built the same way.</summary>
internal static class Captured
{
    private static readonly Lazy<JsonElement> Cases = new(() =>
        JsonDocument.Parse(File.ReadAllText(Fixture.File("fixtures", "captured-failures.json"))).RootElement.GetProperty("cases").Clone());

    /// <summary>The <see cref="TaskFailureDetails"/> the orchestrator received for a captured case (property numbers as the wire carries them).</summary>
    public static TaskFailureDetails Failure(string name) => FromJson(Cases.Value.GetProperty(name).GetProperty("OrchestratorSees"));

    public static IEnumerable<string> Names => Cases.Value.EnumerateObject().Select(p => p.Name);

    private static TaskFailureDetails FromJson(JsonElement e) => new(e.GetProperty("ErrorType").GetString()!, e.GetProperty("ErrorMessage").GetString()!, null,
        e.GetProperty("InnerFailure") is { ValueKind: JsonValueKind.Object } inner ? FromJson(inner) : null,
        e.GetProperty("Properties") is { ValueKind: JsonValueKind.Object } props
            ? props.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Number ? (object?)p.Value.GetDouble() : p.Value.ToString())
            : null);

    /// <summary>A <see cref="SqlException"/> through SqlClient's own factory (as the capture builds it).</summary>
    public static SqlException Sql(int number)
    {
        var ctor = typeof(SqlError).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).OrderByDescending(c => c.GetParameters().Length).First();
        var error = (SqlError)ctor.Invoke([.. ctor.GetParameters().Select(p => p.Name switch
        {
            "infoNumber" => (object?)number, "errorState" => (byte)1, "errorClass" => (byte)20, "server" => "example.database.windows.net",
            "errorMessage" => $"<server message for {number}>", "procedure" => "", "lineNumber" => 0,
            "win32ErrorCode" => p.ParameterType == typeof(uint) ? (object)0u : (object)0, "exception" => null, "batchIndex" => -1,
            _ => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null,
        })]);
        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), true)!;
        typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(collection, [error]);
        var create = typeof(SqlException).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name == "CreateException")
            .OrderBy(m => m.GetParameters().Length).First(m => m.GetParameters()[0].ParameterType == typeof(SqlErrorCollection));
        return (SqlException)create.Invoke(null, [.. create.GetParameters().Select((p, i) => i == 0 ? collection
            : p.ParameterType == typeof(string) ? "12.0.2000.8" : p.ParameterType == typeof(Guid) ? Guid.Empty : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)])!;
    }
}

public partial class OrchestratorTests
{
    private static readonly RetryConfig Policy = RetryConfig.Embedded;

    /// <summary>Brief 30's policy, as it was: three attempts at 5 s × 2 for every failure (the "before" of the attempt counts).</summary>
    private static readonly TaskOptions Brief30Storage = TaskOptions.FromRetryPolicy(new RetryPolicy(3, TimeSpan.FromSeconds(5), 2.0));

    [Theory]
    [InlineData("sql-40613", FailureClass.Transient)]
    [InlineData("efcore-dbupdate-over-sql-40613", FailureClass.Transient)]
    [InlineData("requestfailed-503", FailureClass.Transient)]
    [InlineData("requestfailed-429", FailureClass.Transient)]
    [InlineData("requestfailed-404", FailureClass.Permanent)] // brief 34 A1.4 (was Unknown)
    [InlineData("efcore-retry-limit-exceeded-over-sql-40613", FailureClass.Permanent)] // brief 34 A1.1
    [InlineData("httpclient-timeout", FailureClass.Transient)]
    [InlineData("http-connection-refused", FailureClass.Transient)]
    [InlineData("invalidoperation", FailureClass.Permanent)]
    public void Brief33_captured_failures_classify_as_the_policy_says(string captured, FailureClass expected) =>
        Assert.Equal(expected, FailureClassifier.Classify(Captured.Failure(captured), Policy));

    [Fact]
    public void Brief35_the_synthetic_40613_shape_classifies_by_its_property_path_never_by_the_server_text()
    {
        // Synthetic: shape only; no real capture yet. Brief 34's live resume never reached the Durable layer (EF's retries absorb it), so this
        // case proves the property path the classifier reads (Properties.sqlErrorNumber), not a server's wording.
        var shape = Captured.Failure("sql-40613");
        Assert.StartsWith("<synthetic: shape only; no real capture yet", shape.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(40613.0, Convert.ToDouble(shape.Properties!["sqlErrorNumber"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(FailureClass.Transient, FailureClassifier.Classify(shape, Policy));
        Assert.Equal(FailureClass.Transient, FailureClassifier.Classify(shape with { ErrorMessage = "any server text at all" }, Policy));
    }

    [Fact]
    public void Brief33_every_policy_rule_classifies_and_the_fixture_covers_the_captured_cases()
    {
        Assert.Equal(9, Captured.Names.Count());
        foreach (var rule in Policy.Permanent.Where(r => r.WhenMessageMatches is null && r.WhenPropertyIn is null))
        {
            Assert.Equal(FailureClass.Permanent, FailureClassifier.Classify(new FailureInfo(rule.Type, "x", new Dictionary<string, object?>(), null), Policy));
        }

        foreach (var rule in Policy.Transient.Where(r => r.WhenMessageMatches is null && r.WhenPropertyIn is null && !r.InnerIsTransient))
        {
            Assert.Equal(FailureClass.Transient, FailureClassifier.Classify(new FailureInfo(rule.Type, "x", new Dictionary<string, object?>(), null), Policy));
        }

        // Narrowed rules: the captured shape matches, anything else does not.
        Assert.Equal(FailureClass.Unknown, FailureClassifier.Classify(new FailureInfo("Azure.RequestFailedException", "Status: 409 (Conflict)", new Dictionary<string, object?>(), null), Policy));
        var sql = new FailureInfo("Microsoft.Data.SqlClient.SqlException", "<server text>", new Dictionary<string, object?> { ["sqlErrorNumber"] = 40613.0 }, null);
        Assert.Equal(FailureClass.Transient, FailureClassifier.Classify(sql, Policy));
        Assert.Equal(FailureClass.Unknown, FailureClassifier.Classify(sql with { Properties = new Dictionary<string, object?> { ["sqlErrorNumber"] = 207 } }, Policy)); // invalid column
        Assert.Equal(FailureClass.Unknown, FailureClassifier.Classify(sql with { Properties = new Dictionary<string, object?>() }, Policy)); // no provider: never guessed from text
        Assert.Equal(FailureClass.Unknown, FailureClassifier.Classify(new FailureInfo("System.Threading.Tasks.TaskCanceledException", "A task was canceled.",
            new Dictionary<string, object?>(), null), Policy)); // a real cancellation is not a timeout

        // DbUpdateException decides by what it wraps.
        var wrapped = new FailureInfo("Microsoft.EntityFrameworkCore.DbUpdateException", "x", new Dictionary<string, object?>(), sql);
        Assert.Equal(FailureClass.Transient, FailureClassifier.Classify(wrapped, Policy));
        Assert.Equal(FailureClass.Permanent, FailureClassifier.Classify(wrapped with { Inner = new FailureInfo("System.InvalidOperationException", "x", new Dictionary<string, object?>(), null) }, Policy));

        Assert.Equal(FailureClass.Unknown, FailureClassifier.Classify(new FailureInfo("Contoso.Unlisted", "x", new Dictionary<string, object?>(), null), Policy));
        Assert.Equal("permanent: InvalidOperationException — Graph doc-1 is missing.",
            FailureClassifier.Describe(FailureClass.Permanent, "System.InvalidOperationException", "Graph doc-1 is missing.\nat …"));
    }

    [Fact]
    public void Brief34_EF_exhaustion_is_not_retried_and_its_reason_names_the_inner_sql_number()
    {
        var exhausted = Captured.Failure("efcore-retry-limit-exceeded-over-sql-40613");
        Assert.Equal("permanent: RetryLimitExceededException (EF retries exhausted on 40613)", FailureClassifier.Reason(exhausted, Policy));
        Assert.Equal("permanent: RequestFailedException", FailureClassifier.Reason(Captured.Failure("requestfailed-404"), Policy));
        Assert.Equal("transient: SqlException", FailureClassifier.Reason(Captured.Failure("sql-40613"), Policy));
        Assert.Equal("unknown: Unlisted", FailureClassifier.Reason(new FailureInfo("Contoso.Unlisted", "secret value 123", new Dictionary<string, object?>(), null), Policy)); // never the message
    }

    [Fact]
    public async Task Brief34_an_upload_failed_by_EF_exhaustion_escalates_with_the_sql_number()
    {
        var h = Harness();
        h.Activity<DocumentWork, RouteResult>(ActivityNames.Route, w => w.DocumentId == "d1"
            ? throw new Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException("The maximum number of retries (8) was exceeded.", Captured.Sql(40613))
            : new RouteResult(w.DocumentId, "ReplayAccepted", "f@1", false, [], [], "badge", true, 0));
        h.Activity<FailInput, object?>(ActivityNames.FailUpload, _ => null);

        await Assert.ThrowsAsync<TaskFailedException>(() => UploadOrchestration.RunAsync(new FakeOrchestrationContext("up-1", h), new UploadInput("up-1", TimeSpan.FromDays(7))));

        Assert.Single(h.ActivityCalls, c => c.Activity == ActivityNames.Route && ((DocumentWork)c.Input!).DocumentId == "d1"); // not retried
        Assert.Equal("permanent: RetryLimitExceededException (EF retries exhausted on 40613)",
            ((FailInput)h.ActivityCalls.Single(c => c.Activity == ActivityNames.FailUpload).Input!).Classification);
    }

    [Fact]
    public void Brief33_the_app_s_exception_properties_carry_the_sql_number_and_nothing_else()
    {
        Assert.Equal(40613, Captured.Sql(40613) is var e && ExceptionProperties.Of(e) is { } p ? p[ExceptionProperties.SqlErrorNumber] : null);
        Assert.Null(ExceptionProperties.Of(new InvalidOperationException("x")));
        Assert.Equal(FailureClass.Transient, FailureClassifier.Classify(new Microsoft.EntityFrameworkCore.DbUpdateException("save", Captured.Sql(40613)), Policy));
    }

    /// <summary>One Route call through the fake's retry loop, with <paramref name="options"/>; returns the attempts and the waits.</summary>
    private static async Task<(int Attempts, IReadOnlyList<TimeSpan> Waits, bool Failed)> RouteAsync(TaskOptions options, Func<int, Exception?> failure)
    {
        var h = Harness();
        var attempts = 0;
        h.Activity<DocumentWork, RouteResult>(ActivityNames.Route, w => failure(++attempts) is { } e ? throw e
            : new RouteResult(w.DocumentId, "ReplayAccepted", "f@1", false, [], [], "badge", true, 0));
        try
        {
            await new FakeOrchestrationContext("i", h).CallActivityAsync<RouteResult>(ActivityNames.Route, new DocumentWork("up-1", "d1", "a.pdf", true), options);
            return (attempts, h.RetryTimers, false);
        }
        catch (TaskFailedException)
        {
            return (attempts, h.RetryTimers, true);
        }
    }

    [Theory]
    [InlineData("missing graph")]
    [InlineData("format exception on a fingerprint hit")]
    public async Task Brief33_a_permanent_failure_is_attempted_once_where_it_was_attempted_three_times(string kind)
    {
        Exception Permanent() => kind == "missing graph" ? new InvalidOperationException("Graph doc-1 is missing.") : new FormatException("The input string 'n/a' was not in a correct format.");

        var before = await RouteAsync(Brief30Storage, _ => Permanent());
        var after = await RouteAsync(ActivityRetry.Storage(Policy), _ => Permanent());

        Assert.Equal((3, true), (before.Attempts, before.Failed)); // brief 30: three attempts, 15 s of waits, three error logs for one defect
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], before.Waits);
        Assert.Equal((1, true), (after.Attempts, after.Failed));
        Assert.Empty(after.Waits);
    }

    [Fact]
    public async Task Brief33_a_transient_failure_retries_with_the_configured_delays_and_stops_at_the_caps()
    {
        var once = await RouteAsync(ActivityRetry.Storage(Policy), n => n == 1 ? new HttpRequestException("refused") : null);
        Assert.Equal((2, false), (once.Attempts, once.Failed));
        Assert.Equal([TimeSpan.FromSeconds(5)], once.Waits);

        var always = await RouteAsync(ActivityRetry.Storage(Policy), _ => new HttpRequestException("refused"));
        Assert.Equal((3, true), (always.Attempts, always.Failed)); // maxAttempts
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], always.Waits);

        var shortBudget = Policy with { Storage = Policy.Storage with { MaxTotalSeconds = 4 } };
        var capped = await RouteAsync(ActivityRetry.Storage(shortBudget), _ => new HttpRequestException("refused"));
        Assert.Equal((2, true), (capped.Attempts, capped.Failed)); // maxTotalSeconds: 5.1 s spent after the first retry
    }

    [Fact]
    public async Task Brief33_a_permanent_route_failure_fails_the_upload_once_and_says_permanent()
    {
        var h = Harness();
        var routes = 0;
        h.Activity<DocumentWork, RouteResult>(ActivityNames.Route, w => w.DocumentId == "d1" && ++routes > 0 ? throw new InvalidOperationException("Graph d1 is missing.")
            : new RouteResult(w.DocumentId, "ReplayAccepted", "f@1", false, [], [], "badge", true, 0));
        h.Activity<FailInput, object?>(ActivityNames.FailUpload, _ => null);

        await Assert.ThrowsAsync<TaskFailedException>(() => UploadOrchestration.RunAsync(new FakeOrchestrationContext("up-1", h), new UploadInput("up-1", TimeSpan.FromDays(7))));

        Assert.Equal(1, routes); // before brief 33: 3
        Assert.Empty(h.RetryTimers);
        var failed = (FailInput)h.ActivityCalls.Single(c => c.Activity == ActivityNames.FailUpload).Input!;
        Assert.Equal("permanent: InvalidOperationException", failed.Classification);
        var status = Assert.IsType<UploadProgress>(h.Statuses["up-1"][^1]);
        Assert.StartsWith("permanent: InvalidOperationException — Graph d1 is missing.", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Brief33_a_transient_route_failure_then_success_is_one_route_result_and_the_progress_line_says_retry()
    {
        var h = Harness();
        var routes = 0;
        h.Activity<DocumentWork, RouteResult>(ActivityNames.Route, w => w.DocumentId == "d1" && ++routes == 1 ? throw new HttpRequestException("refused")
            : new RouteResult(w.DocumentId, "ReplayAccepted", "f@1", false, [], [], "badge", true, 0));

        var summary = await UploadOrchestration.RunAsync(new FakeOrchestrationContext("up-1", h), new UploadInput("up-1", TimeSpan.FromDays(7)));

        Assert.Equal((2, "completed"), (routes, summary.Status));
        Assert.Contains(h.Statuses["up-1~d1"].OfType<UploadProgress>(), p => p.Detail == "retry 2/3: HttpRequestException");
    }

    [Fact]
    public async Task Brief33_storage_activities_get_the_classified_handler_and_paid_model_calls_get_nothing()
    {
        var h = Harness(new DocumentOutcome("d1", "Review", ReviewKinds.DocumentReview, 3, 0.01m));
        h.Events.Enqueue(Decision("d1"));
        await UploadOrchestration.RunAsync(new FakeOrchestrationContext("up-1", h), new UploadInput("up-1", TimeSpan.FromDays(7)));

        foreach (var storage in new[] { ActivityNames.ExpandUpload, ActivityNames.ParseFile, ActivityNames.RecordItems, ActivityNames.Route, ActivityNames.ValidateAndPersist,
                     ActivityNames.CompleteUpload, ActivityNames.ApplyDecision })
        {
            Assert.NotNull(h.ActivityOptions[storage]?.Retry?.Handler); // ParseFile: since its CU results are reused (brief 33)
            Assert.Null(h.ActivityOptions[storage]!.Retry!.Policy);
        }

        foreach (var paid in new[] { ActivityNames.Learn, ActivityNames.Delta })
        {
            Assert.Null(h.ActivityOptions[paid]); // their own cap and 429 loop are the policy; a Durable retry would bill again
        }
    }
}

public partial class EndToEndTests
{
    /// <summary>Forwards to the real store; <see cref="Fail"/> decides, per call, whether to throw instead.</summary>
    public class FailWhenRecords : DispatchProxy
    {
        public IRecordStore Inner { get; set; } = null!;
        public Func<MethodInfo, Exception?> Fail { get; set; } = _ => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Fail(targetMethod!) is { } e ? throw e : targetMethod!.Invoke(Inner, args);

        public static IRecordStore Wrap(IRecordStore inner, Func<MethodInfo, Exception?> fail)
        {
            var proxy = Create<IRecordStore, FailWhenRecords>();
            ((FailWhenRecords)(object)proxy).Inner = inner;
            ((FailWhenRecords)(object)proxy).Fail = fail;
            return proxy;
        }
    }

    [Fact]
    public async Task Brief33_a_retried_persist_sends_format_learned_once()
    {
        IRecordStore? inner = null;
        var failed = false;
        var rig = await RigAsync(wrapRecords: records =>
        {
            inner = records;
            // The manifest update right after the format.learned event fails once with a transient fault: the activity is retried.
            return FailWhenRecords.Wrap(records, m => !failed && m.Name == nameof(IRecordStore.UpdateManifestAsync)
                && inner!.OutboxAsync("Q-B33-6", 50).GetAwaiter().GetResult().Any(o => o.Kind == OutboxKinds.FormatLearned) && (failed = true)
                    ? new HttpRequestException("injected: storage connection reset") : null);
        });

        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile("lossrun2-native.pdf", Fixture.Bytes("lossrun2-native.pdf"))], "Q-B33-6", null, "b33-6")).Body!;
        // The upload stays open (it waits for the confirm-format review); wait for the persist step instead.
        for (var i = 0; i < 200 && rig.Orchestrations.Harness.ActivityCalls.Count(c => c.Activity == ActivityNames.CompleteUpload) == 0; i++)
        {
            await Task.Delay(50);
        }

        Assert.True(failed);
        Assert.Equal(2, rig.Orchestrations.Harness.ActivityCalls.Count(c => c.Activity == ActivityNames.ValidateAndPersist)); // retried
        Assert.Single(await rig.Runtime.RecordStore.OutboxAsync("Q-B33-6", 50), o => o.Kind == OutboxKinds.FormatLearned); // before brief 33: two rows, two sends
        Assert.Equal(ReviewKinds.ConfirmFormat, Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items).ReviewKind);
    }

    private static async Task<(Rig Rig, string DocumentId, string UploadId)> DecisionRigAsync(string quoteId, Func<MethodInfo, Exception?> fail)
    {
        var rig = await RigAsync(tamper: true, wrapRecords: records => FailWhenRecords.Wrap(records, fail));
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile("lossrun-native.pdf", Fixture.Bytes("lossrun-native.pdf"))], quoteId, null, quoteId)).Body!;
        var item = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items);
        return (rig, item.DocumentId, accepted.UploadId);
    }

    private static async Task<string> DecidedStateAsync(Rig rig, string documentId)
    {
        for (var i = 0; i < 100; i++)
        {
            var state = (await rig.Runtime.RecordStore.GetReviewAsync(documentId))!.State;
            if (state != "pending" || rig.Orchestrations.Harness.ActivityCalls.Count(c => c.Activity == ActivityNames.ApplyDecision) > 0 && i > 20)
            {
                return state;
            }

            await Task.Delay(50);
        }

        return "pending";
    }

    [Fact]
    public async Task Brief33_a_sql_resume_during_a_decision_is_retried_and_applied_once()
    {
        var thrown = 0;
        var (rig, doc, _) = await DecisionRigAsync("Q-B33-9", m => m.Name == nameof(IRecordStore.ApplyDecisionAsync) && thrown++ == 0 ? Captured.Sql(40613) : null);

        Assert.Equal(202, (await rig.Api.DecideAsync(Underwriter, doc, new ReviewDecisionRequest(ReviewActions.Override,
            [new FieldOverride("policyPeriods[0].claims[0].medical.paid", JsonDocument.Parse("1234").RootElement, "adjuster")], Rationale: "fix"))).Status);

        Assert.Equal("decided", await DecidedStateAsync(rig, doc));
        Assert.Equal(2, rig.Orchestrations.Harness.ActivityCalls.Count(c => c.Activity == ActivityNames.ApplyDecision)); // before brief 33: 1, then decision-apply-failed
        Assert.Single(await rig.Runtime.RecordStore.OverridesOfDocumentAsync(doc));
        Assert.DoesNotContain(await rig.Runtime.RecordStore.ListEscalationsAsync(), e => e.ReasonCode == EscalationReasons.DecisionApplyFailed);
        Assert.Equal([TimeSpan.FromSeconds(5)], rig.Orchestrations.Harness.RetryTimers);
    }

    [Fact]
    public async Task Brief33_a_permanent_failure_during_a_decision_is_attempted_once_and_escalated()
    {
        var (rig, doc, _) = await DecisionRigAsync("Q-B33-9P", m => m.Name == nameof(IRecordStore.ApplyDecisionAsync) ? new InvalidOperationException("store refused") : null);

        Assert.Equal(202, (await rig.Api.DecideAsync(Underwriter, doc, new ReviewDecisionRequest(ReviewActions.Accept, Rationale: "ok"))).Status);
        for (var i = 0; i < 100 && !(await rig.Runtime.RecordStore.ListEscalationsAsync()).Any(e => e.ReasonCode == EscalationReasons.DecisionApplyFailed); i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(1, rig.Orchestrations.Harness.ActivityCalls.Count(c => c.Activity == ActivityNames.ApplyDecision));
        Assert.Contains(await rig.Runtime.RecordStore.ListEscalationsAsync(), e => e.ReasonCode == EscalationReasons.DecisionApplyFailed);
        Assert.Equal("pending", (await rig.Runtime.RecordStore.GetReviewAsync(doc))!.State);
    }

    [Fact]
    public async Task Brief33_the_fingerprint_embedding_is_reused_and_recomputed_when_stale()
    {
        var store = new InMemoryObjectStore();
        var inner = new CountingEmbeddings();
        var cached = new CachedEmbeddingService(inner, store, "up-1", "text-embedding-3-large");

        var first = await cached.EmbedAsync("carrier header text", "d1", "run", CancellationToken.None);
        var second = await cached.EmbedAsync("carrier header text", "d1", "run", CancellationToken.None); // a retried Route, or the re-route after a learn slot
        Assert.Equal(1, inner.Calls);
        Assert.Single(first.Usage);
        Assert.Empty(second.Usage); // metered only on a real call
        Assert.Equal(first.Vector, second.Vector);

        await cached.EmbedAsync("other text", "d1", "run", CancellationToken.None); // the text changed
        Assert.Equal(2, inner.Calls);

        var path = StorePaths.Document("up-1", "d1", "fingerprint.json");
        var stored = (await store.GetJsonAsync<CachedEmbeddingService.StoredEmbedding>(Containers.Extractions, path))!;
        await store.PutJsonAsync(Containers.Extractions, path, stored with { Normalization = "an older normalization" });
        await cached.EmbedAsync("other text", "d1", "run", CancellationToken.None); // stale normalization version
        Assert.Equal(3, inner.Calls);
    }

    private sealed class CountingEmbeddings : LossRun.Formats.IEmbeddingService
    {
        public int Calls { get; private set; }

        public Task<LossRun.Formats.EmbeddingResult> EmbedAsync(string text, string documentId, string runId, CancellationToken cancellationToken)
        {
            Calls++;
            var usage = new LossRun.Contracts.UsageEvent(documentId, runId, "fingerprint", "AzureOpenAI", "inputTokens", text.Length, "token", "text-embedding-3-large",
                "DataZoneStandard", "eastus2", DateTimeOffset.UnixEpoch, $"e-{Calls}");
            return Task.FromResult(new LossRun.Formats.EmbeddingResult([text.Length, Calls], "text-embedding-3-large", TimeSpan.Zero, [usage]));
        }
    }
}

public partial class EndToEndTests
{
    /// <summary>The seen CSV export with the second claim's policy cell blank: the fill-down question.</summary>
    private static byte[] BlankPolicyCsv()
    {
        var lines = System.Text.Encoding.UTF8.GetString(Fixture.Bytes("lossrun-export.csv")).Split('\n');
        lines[2] = "," + lines[2][(lines[2].IndexOf(',') + 1)..];
        return System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines));
    }

    private static readonly IReadOnlyDictionary<string, JsonElement> FillDown =
        new Dictionary<string, JsonElement> { ["periods.fillDown"] = JsonDocument.Parse("true").RootElement };

    [Fact]
    public async Task Brief34_A3_confirming_fill_down_publishes_a_shadow_version_and_replays_the_document_with_it()
    {
        var rig = await RigAsync();
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile("blank-policy.csv", BlankPolicyCsv())], "Q-B34-3", null, "b34-3")).Body!;
        var item = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items);
        var review = (await rig.Runtime.RecordStore.GetReviewAsync(item.DocumentId))!;
        Assert.Contains(review.Reasons, r => r.StartsWith(LossRun.Formats.UnnormalizedReasonText.FillDownReasonPrefix, StringComparison.Ordinal));
        var before = (await rig.Runtime.RecordStore.GetResultAsync(item.DocumentId))!;
        Assert.Equal("synthetic-mutual-csv@1", before.FormatRef ?? before.Routing?.FormatRef);

        // What the API refuses (before brief 34 there was no way to answer the question in the review).
        Assert.Equal(400, (await rig.Api.DecideAsync(Underwriter, item.DocumentId, new ReviewDecisionRequest(ReviewActions.Accept, Rationale: "x",
            FormatChanges: new Dictionary<string, JsonElement> { ["columnMap"] = JsonDocument.Parse("[]").RootElement }))).Status);
        Assert.Equal(400, (await rig.Api.DecideAsync(Underwriter, item.DocumentId, new ReviewDecisionRequest(ReviewActions.Accept, Rationale: "x",
            FormatChanges: new Dictionary<string, JsonElement> { ["periods.fillDown"] = JsonDocument.Parse("false").RootElement }))).Status);
        Assert.Equal(400, (await rig.Api.DecideAsync(Underwriter, item.DocumentId, new ReviewDecisionRequest(ReviewActions.Reject, Rationale: "not ours", FormatChanges: FillDown))).Status);

        Assert.Equal(202, (await rig.Api.DecideAsync(Underwriter, item.DocumentId, new ReviewDecisionRequest(ReviewActions.Accept, Rationale: "broker confirms the export fills down",
            FormatChanges: FillDown))).Status);
        for (var i = 0; i < 100 && (await rig.Runtime.RecordStore.GetReviewAsync(item.DocumentId))!.State == "pending"; i++)
        {
            await Task.Delay(50);
        }

        var v2 = (await rig.Registry.GetAsync("synthetic-mutual-csv", 2))!;
        Assert.True(v2.Periods.FillDown);
        Assert.Equal(LossRun.Formats.FormatLifecycle.Shadow, await LossRun.Formats.FormatLifecycle.StatusAsync(rig.Registry, v2)); // approval or 3 clean replays
        Assert.False((await rig.Registry.GetAsync("synthetic-mutual-csv", 1))!.Periods.FillDown); // the active version is unchanged
        var after = (await rig.Runtime.RecordStore.GetResultAsync(item.DocumentId))!;
        Assert.Equal("synthetic-mutual-csv@2", after.FormatRef);
        Assert.Equal(2, after.Document.PolicyPeriods[0].Claims.Count);
        Assert.Contains("filled-down", after.Document.Lineage!["policyPeriods[0].claims[1].periodAssignment"].SourceText, StringComparison.Ordinal);
        Assert.Contains((await rig.Runtime.RecordStore.AuditTrailAsync(item.DocumentId, null, 50)).Entries,
            e => e.Action == "format.change" && e.FieldPath == "periods.fillDown" && e.NewValue!.Contains("true", StringComparison.Ordinal));

        // A decision on a review without the fill-down question, or on a document without a format, cannot carry the change.
        var noQuestion = new ReviewItem { DocumentId = "d", UploadId = "u", Reasons = ["4 hard rule failure(s) with every header mapped"] };
        Assert.Equal("bad-format-change", ((ApiError)DocumentCommands.FormatChangesError(FillDown, ReviewActions.Accept, noQuestion, "f@1")!.Body!).Code);
        Assert.NotNull(DocumentCommands.FormatChangesError(FillDown, ReviewActions.Accept, review, null));
        Assert.Null(DocumentCommands.FormatChangesError(FillDown, ReviewActions.Override, review, "f@1"));
        Assert.Null(DocumentCommands.FormatChangesError(null, ReviewActions.Accept, noQuestion, null)); // no change, nothing to check
    }

    [Fact]
    public async Task Brief34_Answers4_the_next_document_of_the_format_replays_on_the_shadow_version_and_asks_to_confirm_the_format()
    {
        var rig = await RigAsync();
        var first = (UploadAccepted)(await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile("blank-policy.csv", BlankPolicyCsv())], "Q-B34-4", null, "b34-4a")).Body!;
        var doc1 = Assert.Single((await ManifestAsync(rig, first.UploadId)).Items).DocumentId;

        // Answers 3: the change needs a role that decides reviews (Underwriter and above), like a confirm-format decision.
        foreach (var role in new[] { Applicant, new Caller("eng-1", Roles.Engineer) })
        {
            Assert.Equal(403, (await rig.Api.DecideAsync(role, doc1, new ReviewDecisionRequest(ReviewActions.Accept, Rationale: "x", FormatChanges: FillDown))).Status);
        }

        Assert.Equal(202, (await rig.Api.DecideAsync(Underwriter, doc1, new ReviewDecisionRequest(ReviewActions.Accept, Rationale: "fills down", FormatChanges: FillDown))).Status);
        for (var i = 0; i < 100 && (await rig.Runtime.RecordStore.GetReviewAsync(doc1))!.State == "pending"; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(LossRun.Formats.FormatLifecycle.Shadow, await LossRun.Formats.FormatLifecycle.StatusAsync(rig.Registry, (await rig.Registry.GetAsync("synthetic-mutual-csv", 2))!));

        // Document 2: the same format with the same blank policy cell. It replays clean on v2 and gets the confirm-format review that
        // activates the version (one of three clean replays), not the blank-cell document review again.
        var bytes = BlankPolicyCsv();
        var second = (UploadAccepted)(await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile("blank-policy-2.csv", [.. bytes, (byte)'\n'])], "Q-B34-4b", null, "b34-4b")).Body!;
        var doc2 = Assert.Single((await ManifestAsync(rig, second.UploadId)).Items).DocumentId;
        var review2 = (await rig.Runtime.RecordStore.GetReviewAsync(doc2))!;
        var result2 = (await rig.Runtime.RecordStore.GetResultAsync(doc2))!;
        Assert.Equal("synthetic-mutual-csv@2", result2.FormatRef ?? result2.Routing?.FormatRef);
        Assert.Equal(ReviewKinds.ConfirmFormat, review2.Kind);
        Assert.DoesNotContain(review2.Reasons, r => r.StartsWith(LossRun.Formats.UnnormalizedReasonText.FillDownReasonPrefix, StringComparison.Ordinal));
        Assert.Equal(2, result2.Document.PolicyPeriods[0].Claims.Count);
    }
}

public partial class EndToEndTests
{
    /// <summary>The CSV fixture with a Carrier column: the carrier's name as each export prints it.</summary>
    private static byte[] CarrierCsv(string carrier)
    {
        var lines = System.Text.Encoding.UTF8.GetString(Fixture.Bytes("lossrun-export.csv")).TrimEnd('\n', '\r').Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        lines[0] += ",Carrier";
        for (var i = 1; i < lines.Count; i++)
        {
            lines[i] += $",\"{carrier}\"";
        }

        return System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
    }

    /// <summary>A format that reads the carrier from the file (the hand CSV format states it as a constant), fingerprinted from the file itself.</summary>
    private static async Task PublishCarrierColumnFormatAsync(Rig rig)
    {
        var graph = (await new LossRun.Ingestion.IngestionPipeline(rig.Runtime.ContentUnderstanding, new LossRun.Ingestion.Metering.InMemoryCostMeter(),
            new StoreRawResultStore(new InMemoryObjectStore(), "fp"), rig.Runtime.PriceTable, rig.Runtime.GlossaryOrSeed, rig.Runtime.Ingestion)
            .IngestAsync("carrier.csv", CarrierCsv("Synthetic Mutual Insurance Co."))).Documents[0];
        var fp = LossRun.Formats.FingerprintText.Compute(graph);
        var spec = (await rig.Registry.GetAsync("synthetic-mutual-csv", 1))!;
        spec = LossRun.Formats.FormatJson.Parse(LossRun.Formats.FormatJson.Write(spec));
        spec.FormatId = "carrier-column-csv";
        spec.DocumentFields.RemoveAll(f => f.Target == "document.carrierName");
        spec.ColumnMap.Add(new LossRun.Formats.ColumnMapping { Source = "Carrier", Target = "document.carrierName" });
        spec.Fingerprint = new LossRun.Formats.FormatFingerprintSpec { IdentityKey = fp.IdentityKey, HeaderKey = fp.HeaderKey, IdentityText = fp.IdentityText, HeaderText = fp.HeaderText };
        spec.Status = LossRun.Formats.FormatLifecycle.Active;
        await rig.Registry.PublishAsync(spec);
        await LossRun.Formats.FormatLifecycle.InitializeAsync(rig.Registry, spec, "test", DateTimeOffset.UtcNow);
    }

    private static async Task<(string DocumentId, DocumentResult Result, ReviewItem? Review)> UploadCarrierAsync(Rig rig, string carrier, string key)
    {
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile($"{key}.csv", CarrierCsv(carrier))], "Q-B35-CARRIER", null, key)).Body!;
        var doc = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items).DocumentId;
        return (doc, (await rig.Runtime.RecordStore.GetResultAsync(doc))!, await rig.Runtime.RecordStore.GetReviewAsync(doc));
    }

    [Fact]
    public async Task Brief35_Answers2_one_carriers_legal_names_replay_after_one_same_carrier_answer_and_another_carrier_still_reviews()
    {
        var rig = await RigAsync();
        await PublishCarrierColumnFormatAsync(rig);

        // Corporate words do not tell carriers apart: two more legal names of the format's carrier replay with no alias.
        foreach (var (name, key) in new[] { ("Synthetic Mutual Insurance Company", "b35-a"), ("Synthetic Mutual Indemnity Co", "b35-b") })
        {
            var (_, result, review) = await UploadCarrierAsync(rig, name, key);
            Assert.Equal("ReplayAccepted", result.Routing?.Tier.ToString());
            Assert.Null(review);
            Assert.Equal(name, result.Document.CarrierName);
        }

        // A third legal name differs in a distinguishing word: Review, the reason naming both names.
        const string third = "Synthetic Property Casualty Co of America";
        var (doc, first, pending) = await UploadCarrierAsync(rig, third, "b35-c");
        Assert.NotNull(pending);
        var reason = Assert.Single(pending!.Reasons, r => r.StartsWith(LossRun.Formats.FormatRouter.CarrierOnPageReasonPrefix, StringComparison.Ordinal));
        Assert.Contains($"\"{third}\"", reason, StringComparison.Ordinal);
        Assert.Contains("\"Synthetic Mutual Insurance Co.\"", reason, StringComparison.Ordinal);

        // What the API refuses: a name on a review that did not ask, an empty name, a reject.
        Assert.Equal(400, (await rig.Api.DecideAsync(Underwriter, doc, new ReviewDecisionRequest(ReviewActions.Accept, Rationale: "x",
            FormatChanges: new Dictionary<string, JsonElement> { ["carrierNames.add"] = JsonDocument.Parse("\"  \"").RootElement }))).Status);
        Assert.Equal(400, (await rig.Api.DecideAsync(Underwriter, doc, new ReviewDecisionRequest(ReviewActions.Reject, Rationale: "not ours",
            FormatChanges: new Dictionary<string, JsonElement> { ["carrierNames.add"] = JsonDocument.Parse($"\"{third}\"").RootElement }))).Status);
        var noQuestion = new ReviewItem { DocumentId = "d", UploadId = "u", Reasons = ["4 hard rule failure(s) with every header mapped"] };
        Assert.NotNull(DocumentCommands.FormatChangesError(new Dictionary<string, JsonElement> { ["carrierNames.add"] = JsonDocument.Parse("\"X Co\"").RootElement },
            ReviewActions.Accept, noQuestion, "f@1"));

        // The reviewer answers "same carrier" once.
        Assert.Equal(202, (await rig.Api.DecideAsync(Underwriter, doc, new ReviewDecisionRequest(ReviewActions.Accept, Rationale: "same group, another legal entity",
            FormatChanges: new Dictionary<string, JsonElement> { ["carrierNames.add"] = JsonDocument.Parse($"\"{third}\"").RootElement }))).Status);
        Assert.NotEqual("pending", await DecidedStateAsync(rig, doc));
        var v2 = (await rig.Registry.GetAsync("carrier-column-csv", 2))!;
        Assert.Equal([third], v2.CarrierNames);
        Assert.Equal(LossRun.Formats.FormatLifecycle.Active, await LossRun.Formats.FormatLifecycle.StatusAsync(rig.Registry, v2)); // a name, not a layout: no shadow
        Assert.Contains((await rig.Runtime.RecordStore.AuditTrailAsync(doc, null, 50)).Entries,
            e => e.Action == "format.change" && e.FieldPath == "carrierNames" && e.NewValue!.Contains(third, StringComparison.Ordinal));
        Assert.Equal("carrier-column-csv@2", (await rig.Runtime.RecordStore.GetResultAsync(doc))!.FormatRef);

        // The next document that prints the name replays clean.
        var (_, again, none) = await UploadCarrierAsync(rig, third, "b35-d");
        Assert.Equal("ReplayAccepted", again.Routing?.Tier.ToString());
        Assert.Null(none);
        Assert.Equal("carrier-column-csv@2", again.FormatRef);

        // Another company's name (the c8 case: the insured read as the carrier) is still Review.
        var (_, _, other) = await UploadCarrierAsync(rig, "Kappa Foods (synthetic)", "b35-e");
        Assert.NotNull(other);
        Assert.Contains(other!.Reasons, r => r.StartsWith(LossRun.Formats.FormatRouter.CarrierOnPageReasonPrefix, StringComparison.Ordinal));
    }
}
