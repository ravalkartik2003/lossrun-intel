using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;
using LossRun.Ingestion;

namespace LossRun.Chat.Tests;

/// <summary>
/// Brief 24 step 5 (review plan 2026-10-25 §5): corrections and review status in chat, the tool policy, the fact guard,
/// and that a decision never rewrites the indexed page text.
/// </summary>
public class ReviewToolsTests
{
    private const string Paid = "policyPeriods[0].claims[0].indemnity.paid";

    /// <summary>Two corrections of WC-A-001's indemnity paid on doc-a1 (quote A), as the record store keeps them.</summary>
    private static async Task CorrectTwiceAsync(ChatFixture f)
    {
        foreach (var (value, why, user, at) in new[] { ("12500", "Carrier letter dated 2024-10-01 adds a late payment", "uw-1", 1), ("12750", "Second carrier letter corrects the late payment", "uw-2", 2) })
        {
            var before = (await f.Records.GetResultAsync("doc-a1"))!;
            var decision = new ReviewDecision("doc-a1", ReviewActions.Override, [new FieldOverride(Paid, JsonDocument.Parse(value).RootElement, why)], false, why, user,
                Roles.Underwriter, f.Clock.GetUtcNow().AddHours(at));
            var (after, changes) = DocumentEdits.Apply(before, decision);
            await f.Records.ApplyDecisionAsync(new DecisionRecord("up-a", before, after, decision, changes, false));
        }
    }

    private static string Json(object o) => JsonSerializer.Serialize(o);

    [Fact]
    public async Task Explain_value_returns_the_printed_value_and_the_corrections_chain_with_the_reviewers_verbatim_rationale()
    {
        var f = await ChatFixture.CreateAsync();
        await CorrectTwiceAsync(f);
        var (tools, recorder) = await f.ToolsAsync(ChatScope.Parse(ChatScopes.Quote, ChatFixture.QuoteA, null));

        var result = JsonDocument.Parse(Json(await tools.ExplainValueAsync("WC-A-001", "indemnity.paid"))).RootElement;
        var value = Assert.Single(result.GetProperty("values").EnumerateArray().ToList());
        Assert.Equal("12000", value.GetProperty("original").GetProperty("value").GetString());
        Assert.Equal("12750", value.GetProperty("current").GetProperty("value").GetString());
        Assert.Equal((3, "approved"), (value.GetProperty("current").GetProperty("revision").GetInt32(), value.GetProperty("current").GetProperty("status").GetString()));
        var corrections = value.GetProperty("corrections").EnumerateArray().ToList();
        Assert.Equal(["uw-1", "uw-2"], corrections.Select(c => c.GetProperty("reviewer").GetString()));
        Assert.Equal("Second carrier letter corrects the late payment", corrections[1].GetProperty("rationale").GetString());
        Assert.Equal(corrections[0].GetProperty("id").GetString(), corrections[1].GetProperty("supersedes").GetString()); // the chain
        Assert.Equal(JsonValueKind.Null, corrections[0].GetProperty("supersedes").ValueKind);
        Assert.Equal(("Underwriter", 2), (corrections[0].GetProperty("role").GetString(), corrections[0].GetProperty("revision").GetInt32()));

        // The printed value's evidence is the unmodified page chunk of doc-a1 page 1.
        var source = recorder.Refs[value.GetProperty("original").GetProperty("ref").GetString()!];
        Assert.Equal(("page", "doc-a1", 1), (source.Kind, source.DocumentId, source.Page));
        Assert.Contains(f.Search.Chunks, c => c.DocumentId == source.DocumentId && c.Page == source.Page);
    }

    [Fact]
    public async Task An_applicant_is_not_offered_explain_value_and_a_call_is_refused_and_traced()
    {
        var f = await ChatFixture.CreateAsync();
        var recorder = new TurnRecorder();
        var scope = ChatScope.Parse(ChatScopes.Workspace, null, ChatFixture.Workspace);
        var documents = await f.Records.DocumentsInScopeAsync(scope.Kind, null, ChatFixture.Workspace);
        var tools = new ChatTools(scope, documents, f.Search, new FakeEmbeddings(recorder.Meter), f.Records, recorder, "turn-t", "centralus", Roles.Applicant);

        Assert.DoesNotContain(tools.AsAITools(), t => t.Name == ToolPolicy.ExplainValue);
        Assert.Contains(tools.AsAITools(), t => t.Name == ToolPolicy.QueryClaims);
        Assert.Contains("Not allowed", Json(await tools.ExplainValueAsync("WC-W-001")));
        Assert.Contains("Not allowed", Json(await tools.QueryClaimsAsync(view: "reported")));
        Assert.Equal(2, recorder.Trace.Count(t => t.Note?.StartsWith("denied by tool policy", StringComparison.Ordinal) == true));
        Assert.Equal(0, recorder.ToolCalls); // a refusal is not a tool call
    }

    [Fact]
    public async Task Rows_of_a_pending_document_say_so_and_a_rejected_document_is_out_of_scope()
    {
        var f = await ChatFixture.CreateAsync();
        var result = (await f.Records.GetResultAsync("doc-a1"))!;
        result.ReviewState = "pending";
        await f.Records.SaveResultAsync(result, Actor.System);
        var (tools, _) = await f.ToolsAsync(ChatScope.Parse(ChatScopes.Quote, ChatFixture.QuoteA, null));

        var row = JsonDocument.Parse(Json(await tools.QueryClaimsAsync())).RootElement.GetProperty("rows")[0];
        Assert.Equal("pending", row.GetProperty("reviewState").GetString());
        Assert.Equal("unresolved (review-pending)", row.GetProperty("fieldStatus").GetProperty("claimNumber").GetString());
        var status = JsonDocument.Parse(Json(await tools.GetReviewStatusAsync())).RootElement;
        Assert.Equal((1, false), (status.GetProperty("pendingDocuments").GetInt32(), status.GetProperty("ready").GetBoolean()));

        result.ReviewState = ReviewActions.Reject;
        await f.Records.SaveResultAsync(result, Actor.System);
        Assert.Empty(await f.Records.DocumentsInScopeAsync(ChatScopes.Quote, ChatFixture.QuoteA, null));
    }

    [Fact]
    public async Task The_fact_guard_flags_a_name_date_or_quotation_no_tool_returned_and_the_trace_keeps_it()
    {
        var f = await ChatFixture.CreateAsync();
        await CorrectTwiceAsync(f);
        var model = new ScriptedModel(
            new ScriptedModel.Step(Calls: [("explain_value", "{\"claimNumber\":\"WC-A-001\",\"field\":\"indemnity.paid\"}")]),
            new ScriptedModel.Step(Answer: JsonSerializer.Serialize(new
            {
                text = "The run printed $12,000.00 [S1]. It was changed by uw-2 on 2026-10-23 [C1]: \"Second carrier letter corrects the late payment\". "
                    + "Before that, approved by M. Chen on 4 Nov 2026: \"the broker phoned to confirm\".",
                citations = new[] { "S1", "C1" }, notFound = false,
            })));

        var outcome = await f.Service(model).RunTurnAsync("uw-1", Roles.Underwriter, new ChatTurnRequest(ChatScopes.Quote, "Why is WC-A-001 paid 12,750?", ChatFixture.QuoteA));

        var flagged = outcome.Result!.Unverified!;
        Assert.Equal(["date 4 Nov 2026", "quote \"the broker phoned to confirm\"", "reviewer M. Chen"], flagged.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(flagged, x => x.Contains("uw-2", StringComparison.Ordinal) || x.Contains("Second carrier", StringComparison.Ordinal) || x.Contains("2026-10-23", StringComparison.Ordinal));
        Assert.Contains(outcome.Result.ToolTrace, t => t.Tool == "fact-guard" && t.Results == 3);
    }

    [Fact]
    public void The_fact_guard_reads_dates_in_several_forms()
    {
        string[] tool = ["2026-10-23T18:04Z", "uw-1", "Medical bill received on the 3rd"];

        Assert.Empty(FactGuard.Unverified("Changed by uw-1 on 23 Oct 2026, October 23, 2026 and 10/23/2026: \"Medical bill received on the 3rd\".", tool));
        Assert.Equal(["date 2026-10-24"], FactGuard.Unverified("Changed on 2026-10-24.", tool));
        Assert.Empty(FactGuard.Unverified("A \"short\" word and $1,234.50 are not checked.", tool));
    }

    [Fact]
    public async Task A_decision_changes_no_indexed_page_text()
    {
        // The chat index holds the pages as printed; a decision changes the result, never the page markdown it is built from.
        var f = await ChatFixture.CreateAsync();
        var manifest = (await f.Records.GetManifestAsync("up-a"))!;
        var item = manifest.Items.Single();
        GraphPage[] pages = [new GraphPage { Number = 1, Markdown = "Acme Mutual loss run. Claim WC-A-001 indemnity paid 12,000.00." }];
        var before = ChatIndexer.Build(manifest, item, pages, await f.Records.GetResultAsync("doc-a1"), null);

        await CorrectTwiceAsync(f);
        var after = ChatIndexer.Build(manifest, item, pages, await f.Records.GetResultAsync("doc-a1"), null);

        Assert.Equal(before.Select(c => (c.ChunkId, c.Markdown)), after.Select(c => (c.ChunkId, c.Markdown)));
    }
}
