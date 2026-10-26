using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;

namespace LossRun.Chat.Tests;

/// <summary>Brief 30 item 8: a rejected document's pages are not cited as fact unless history is asked for, and then they are labelled.</summary>
public class AuditFixTests
{
    private static async Task RejectAsync(ChatFixture f, string documentId, string uploadId)
    {
        var before = (await f.Records.GetResultAsync(documentId))!;
        var decision = new ReviewDecision(documentId, ReviewActions.Reject, [], false, "not this insured", "uw-1", Roles.Underwriter, f.Clock.GetUtcNow());
        var (after, changes) = DocumentEdits.Apply(before, decision);
        await f.Records.ApplyDecisionAsync(new DecisionRecord(uploadId, before, after, decision, changes, false));
    }

    private static List<JsonElement> Results(object output) =>
        [.. JsonDocument.Parse(JsonSerializer.Serialize(output)).RootElement.GetProperty("results").EnumerateArray()];

    [Fact]
    public async Task Item8_search_documents_leaves_out_a_rejected_document_unless_history_is_asked_for()
    {
        var f = await ChatFixture.CreateAsync();
        var scope = ChatScope.Parse(ChatScopes.Quote, ChatFixture.QuoteA, null);
        var (tools, _) = await f.ToolsAsync(scope);
        Assert.Contains(Results(await tools.SearchDocumentsAsync("lifting injury loss run")), r => r.GetProperty("fileName").GetString() == "doc-a1.pdf");

        await RejectAsync(f, "doc-a1", "up-a");
        var (after, recorder) = await f.ToolsAsync(scope);
        var hits = Results(await after.SearchDocumentsAsync("lifting injury loss run"));
        Assert.DoesNotContain(hits, r => r.GetProperty("fileName").GetString() == "doc-a1.pdf");
        Assert.Contains(hits, r => r.GetProperty("fileName").GetString() == "broker-letter.pdf"); // kept items stay in scope

        // Asked for explicitly: returned, and marked as coming from a rejected document.
        var (history, _) = await f.ToolsAsync(scope);
        var rejected = Assert.Single(Results(await history.SearchDocumentsAsync("lifting injury loss run", includeRejected: true)), r => r.GetProperty("fileName").GetString() == "doc-a1.pdf");
        Assert.True(rejected.GetProperty("fromRejectedDocument").GetBoolean());
    }

    [Fact]
    public async Task Brief32_item7_a_reopened_document_is_superseded_until_it_is_decided_again()
    {
        var f = await ChatFixture.CreateAsync();
        var scope = ChatScope.Parse(ChatScopes.Quote, ChatFixture.QuoteA, null);
        var result = (await f.Records.GetResultAsync("doc-a1"))!;
        result.ReviewState = ReviewStates.Reopened; // what ReopenAsync writes; it then enqueues the upload for the indexer
        await f.Records.SaveResultAsync(result, Actor.System);
        await ReindexAsync(f, "up-a", "doc-a1");

        var (reopened, _) = await f.ToolsAsync(scope);
        Assert.DoesNotContain(Results(await reopened.SearchDocumentsAsync("lifting injury loss run")), r => r.GetProperty("fileName").GetString() == "doc-a1.pdf"); // before: still cited

        var before = (await f.Records.GetResultAsync("doc-a1"))!;
        var decision = new ReviewDecision("doc-a1", ReviewActions.Accept, [], false, "checked again", "uw-1", Roles.Underwriter, f.Clock.GetUtcNow());
        var (after, changes) = DocumentEdits.Apply(before, decision);
        await f.Records.ApplyDecisionAsync(new DecisionRecord("up-a", before, after, decision, changes, false));
        await ReindexAsync(f, "up-a", "doc-a1"); // the decision on a reopened review enqueues it again
        var (approved, _) = await f.ToolsAsync(scope);
        Assert.Contains(Results(await approved.SearchDocumentsAsync("lifting injury loss run")), r => r.GetProperty("fileName").GetString() == "doc-a1.pdf");
    }

    /// <summary>What <see cref="ChatIndexer.IndexUploadAsync"/> does for one item: build its chunks from its pages and result, and upsert them.</summary>
    private static async Task ReindexAsync(ChatFixture f, string uploadId, string documentId)
    {
        var manifest = (await f.Records.GetManifestAsync(uploadId))!;
        var item = manifest.Items.Single(i => i.DocumentId == documentId);
        var chunks = ChatIndexer.Build(manifest, item, [new GraphPage { Number = 1, Markdown = "Loss run: lifting injury, claim WC-1001, indemnity paid." }],
            await f.Records.GetResultAsync(documentId), null);
        await f.Search.UpsertAsync([.. chunks.Select(c => c with { Vector = FakeEmbeddings.Vector(c.Markdown) })]);
    }

    [Theory]
    [InlineData(ItemStatuses.Processed, null, null)]
    [InlineData(ItemStatuses.Processed, ReviewStates.Reopened, ChatReviewStatus.Superseded)]
    [InlineData(ItemStatuses.Processed, ReviewActions.Accept, ChatReviewStatus.Approved)]
    [InlineData(ItemStatuses.Processed, ReviewActions.Override, ChatReviewStatus.Approved)]
    [InlineData(ItemStatuses.Rejected, ReviewActions.Accept, ChatReviewStatus.Rejected)]
    [InlineData(ItemStatuses.Processed, ReviewActions.Reject, ChatReviewStatus.Rejected)]
    public void Brief32_item7_the_chunk_status_follows_the_review_state(string itemStatus, string? reviewState, string? expected)
    {
        Assert.Equal(expected, ChatReviewStatus.Of(itemStatus, reviewState));
        Assert.Equal(expected is not (ChatReviewStatus.Rejected or ChatReviewStatus.Superseded),
            ChatScope.ForQuote(ChatFixture.QuoteA).Admits(ChatFixture.Chunk("d", "u", ChatFixture.QuoteA, null, "LossRun", "a.pdf", 1, "t") with { ReviewStatus = expected }));
    }

    [Fact]
    public void Item8_the_index_filter_and_the_post_filter_leave_out_rejected_and_superseded_chunks()
    {
        var scope = ChatScope.ForQuote(ChatFixture.QuoteA);
        var chunk = ChatFixture.Chunk("doc-a1", "up-a", ChatFixture.QuoteA, null, "LossRun", "a.pdf", 1, "text");
        Assert.True(scope.Admits(chunk));
        Assert.False(scope.Admits(chunk with { ReviewStatus = ChatReviewStatus.Rejected }));
        Assert.False(scope.Admits(chunk with { ReviewStatus = ChatReviewStatus.Superseded }));
        Assert.True(scope.Admits(chunk with { ReviewStatus = ChatReviewStatus.Rejected }, includeRejected: true));
        Assert.Contains("reviewStatus", scope.ODataFilter(), StringComparison.Ordinal);
        Assert.DoesNotContain("reviewStatus", scope.ODataFilter(includeRejected: true), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Item24_explain_value_says_originally_not_present_when_the_loss_run_printed_nothing()
    {
        var f = await ChatFixture.CreateAsync();
        var before = (await f.Records.GetResultAsync("doc-a1"))!;
        var decision = new ReviewDecision("doc-a1", ReviewActions.Override,
            [new FieldOverride("policyPeriods[0].claims[0].medical.paid", JsonDocument.Parse("500").RootElement, "medical bill received")], false, "added", "uw-1",
            Roles.Underwriter, f.Clock.GetUtcNow());
        var (after, changes) = DocumentEdits.Apply(before, decision);
        await f.Records.ApplyDecisionAsync(new DecisionRecord("up-a", before, after, decision, changes, false));
        var (tools, _) = await f.ToolsAsync(ChatScope.Parse(ChatScopes.Quote, ChatFixture.QuoteA, null));

        var result = JsonDocument.Parse(JsonSerializer.Serialize(await tools.ExplainValueAsync("WC-A-001", "medical.paid"))).RootElement;
        var original = result.GetProperty("values")[0].GetProperty("original");
        Assert.Equal("originally not present", original.GetProperty("value").GetString()); // before brief 30: "500", the current value
        Assert.False(original.GetProperty("present").GetBoolean());
    }

    /// <summary>A model that streams its usage and then fails mid-answer (a dropped connection, a 500 from the deployment).</summary>
    private sealed class FailingModel : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new HttpRequestException("deployment returned 500");

        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant,
                [new Microsoft.Extensions.AI.UsageContent(new Microsoft.Extensions.AI.UsageDetails { InputTokenCount = 4000, OutputTokenCount = 10 })]) { ResponseId = "resp-fail" };
            throw new HttpRequestException("connection reset mid-stream");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Item24_a_turn_that_fails_after_the_model_was_called_is_still_metered()
    {
        var f = await ChatFixture.CreateAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service(new FailingModel()).RunTurnAsync("uw-1", Roles.Underwriter,
            new ChatTurnRequest(ChatScopes.Quote, "How many claims?", ChatFixture.QuoteA)));

        var usage = await f.Records.ReadUsageAsync(await FailingTurnId(f)); // before brief 30: nothing recorded, the 4,000 input tokens unbilled in the report
        Assert.Contains(usage, e => e.Meter == "inputTokens" && e.Quantity == 4000);
    }

    private static async Task<string> FailingTurnId(ChatFixture f) =>
        (await f.Store.ListAsync("extractions", "uploads/turn-")).Select(k => k.Split('/').First(p => p.StartsWith("turn-", StringComparison.Ordinal))).Distinct().Single();
}
