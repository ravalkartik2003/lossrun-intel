using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;

namespace LossRun.Chat.Tests;

/// <summary>
/// Brief 24 step 7 Answers 1–3: corrections listed without a claim number, "open" includes reopened, per-component totals, a
/// doubled answer, and follow-up resolution through the "Last result" of the conversation facts.
/// </summary>
public class FollowUpTests
{
    private static string Json(object o) => JsonSerializer.Serialize(o);

    [Fact]
    public async Task Explain_value_without_a_claim_number_lists_every_correction_in_scope_newest_first()
    {
        var f = await ChatFixture.CreateAsync();
        foreach (var (value, why, user, hours) in new[] { ("12500", "Carrier letter adds a late payment", "uw-1", 1), ("12750", "Second carrier letter corrects the late payment", "uw-2", 2) })
        {
            var before = (await f.Records.GetResultAsync("doc-a1"))!;
            var decision = new ReviewDecision("doc-a1", ReviewActions.Override, [new FieldOverride("policyPeriods[0].claims[0].indemnity.paid", JsonDocument.Parse(value).RootElement, why)],
                false, why, user, Roles.Underwriter, f.Clock.GetUtcNow().AddHours(hours));
            var (after, changes) = DocumentEdits.Apply(before, decision);
            await f.Records.ApplyDecisionAsync(new DecisionRecord("up-a", before, after, decision, changes, false));
        }

        var (tools, _) = await f.ToolsAsync(ChatScope.Parse(ChatScopes.Quote, ChatFixture.QuoteA, null));
        var result = JsonDocument.Parse(Json(await tools.ExplainValueAsync())).RootElement;

        var corrections = result.GetProperty("corrections").EnumerateArray().ToList();
        Assert.Equal(["uw-2", "uw-1"], corrections.Select(c => c.GetProperty("reviewer").GetString()));
        Assert.Equal(("WC-A-001", "indemnity.paid"), (corrections[0].GetProperty("claimNumber").GetString(), corrections[0].GetProperty("field").GetString()));
        Assert.Equal("Second carrier letter corrects the late payment", corrections[0].GetProperty("rationale").GetString());
        Assert.Equal(0, result.GetProperty("more").GetInt32());

        var (other, _) = await f.ToolsAsync(ChatScope.Parse(ChatScopes.Quote, ChatFixture.QuoteB, null));
        Assert.Contains("No value in scope has been corrected", Json(await other.ExplainValueAsync())); // quote B: none, and none of quote A's
    }

    [Fact]
    public async Task Status_open_includes_reopened_claims_and_the_totals_carry_each_component()
    {
        var f = await ChatFixture.CreateAsync();
        var result = (await f.Records.GetResultAsync("doc-a1"))!;
        result.Document.PolicyPeriods[0].Claims[1].Status = ClaimStatus.Reopened; // WC-A-002
        await f.Records.SaveResultAsync(result, Actor.System);
        var (tools, recorder) = await f.ToolsAsync(ChatScope.Parse(ChatScopes.Quote, ChatFixture.QuoteA, null));

        var open = JsonDocument.Parse(Json(await tools.QueryClaimsAsync(new ClaimFilter { Status = "open" }))).RootElement;
        Assert.Equal(["WC-A-001", "WC-A-002"], open.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("claimNumber").GetString()).Order());
        var totals = open.GetProperty("totals");
        Assert.Equal(2, totals.GetProperty("open").GetInt32());
        Assert.Equal(15_500.50m, totals.GetProperty("indemnityPaid").GetDecimal());
        Assert.Equal(0m, totals.GetProperty("medicalIncurred").GetDecimal());
        Assert.Empty(JsonDocument.Parse(Json(await tools.QueryClaimsAsync(new ClaimFilter { Status = "closed" }))).RootElement.GetProperty("rows").EnumerateArray());
        Assert.Contains("\"Status\":\"closed\"", JsonSerializer.Serialize(recorder.LastResult)); // the last call's filter is what the next turn sees
    }

    [Fact]
    public void A_doubled_answer_keeps_the_last_json_object()
    {
        var raw = "{\"text\":\"first try\",\"citations\":[\"S5\"],\"notFound\":true}{\"text\":\"Only the 2023 total is verified [S5].\",\"citations\":[\"S5\"],\"notFound\":true}";

        var (text, citations, notFound) = ChatService.ParseAnswer(raw);

        Assert.Equal(("Only the 2023 total is verified [S5].", true), (text, notFound));
        Assert.Equal(["S5"], citations);
        Assert.Equal("Plain words", ChatService.ParseAnswer("Plain words").Text);
    }

    private static ChatTurnResult Turn(int n, string question, object? last, params ChatCitation[] citations) =>
        new($"turn-{n}", "conv-1", "uw-1", Roles.Underwriter, ChatScopes.Quote, "Q-A", null, question, "…", false, citations,
            last is null ? [] : [new ChatToolCall(ChatMemory.LastResultTool, JsonSerializer.Serialize(last), 0, 0)], new ChatTurnUsage(1, 0, 0, 0, 0, 0, 0, 0, 0, []),
            DateTimeOffset.UnixEpoch);

    private static ChatCitation Claim(string number) => new("C1", "claim", $"{number} · doc-a1.pdf", "doc-a1", QuoteId: "Q-A");

    private static object Rows(object? filter, params (string Number, string Carrier, string Status, string Incurred)[] rows) => new
    {
        tool = "query_claims", filter, matches = rows.Length,
        rows = rows.Select(r => new { claimNumber = r.Number, carrier = r.Carrier, status = r.Status, policyYear = "2023", incurred = r.Incurred }),
    };

    private static readonly ChatScope QuoteA = ChatScope.Parse(ChatScopes.Quote, "Q-A", null);

    [Fact]
    public void Those_two_finds_the_two_claims_of_the_last_turns_not_their_carriers()
    {
        // "Which claim has the largest incurred?" → WC-1; "And the second largest?" → WC-2; then "How do those two compare?"
        var history = new[]
        {
            Turn(1, "Which claim has the largest total incurred?", Rows(new { Status = (string?)null }, ("WC-1", "Carrier A", "open", "11900")), Claim("WC-1")),
            Turn(2, "And the second largest?", Rows(null, ("WC-2", "Carrier B", "closed", "9000")), Claim("WC-2")),
        };

        var facts = ChatMemory.Facts(history, QuoteA)!;

        Assert.Contains("Last result (turn 2): query_claims, 1 match(es): WC-2 (Carrier B, closed, 2023, incurred 9000).", facts);
        Assert.Contains("- claim WC-2 in doc-a1 (turn 2)", facts);
        Assert.Contains("- claim WC-1 in doc-a1 (turn 1)", facts);
    }

    [Fact]
    public void Of_those_carries_the_last_filter()
    {
        var history = new[] { Turn(1, "How many claims are open?", Rows(new { Status = "open" }, ("WC-1", "Carrier A", "open", "11900"), ("WC-3", "Carrier A", "reopened", "700"))) };

        Assert.Contains("Last result (turn 1): query_claims, filter Status=open, 2 match(es): WC-1 (Carrier A, open, 2023, incurred 11900); WC-3 (Carrier A, reopened, 2023, incurred 700).",
            ChatMemory.Facts(history, QuoteA));
    }

    [Fact]
    public void Its_reserve_points_at_the_single_claim_of_the_last_result()
    {
        var history = new[]
        {
            Turn(1, "What is the total incurred?", new { tool = "compute_metric", metric = "incurred_total", value = 35770 }),
            Turn(2, "Of those, which open claim is largest?", Rows(new { Status = "open" }, ("WC-4", "Carrier A", "open", "9000")), Claim("WC-4")),
        };

        var facts = ChatMemory.Facts(history, QuoteA)!;

        Assert.StartsWith(ChatMemory.FactsHeader + "\nScope: quote Q-A.\nLast result (turn 2): query_claims, filter Status=open, 1 match(es): WC-4", facts.Replace("\r", ""));
        Assert.DoesNotContain("Last result (turn 1)", facts); // only the newest result
    }

    [Fact]
    public async Task The_next_turn_sees_the_last_result_and_its_filter()
    {
        var f = await ChatFixture.CreateAsync();
        var first = new ScriptedModel(
            new ScriptedModel.Step(Calls: [("query_claims", "{\"filter\":{\"status\":\"open\"}}")]),
            new ScriptedModel.Step(Answer: JsonSerializer.Serialize(new { text = "One open claim, WC-A-001 [C1].", citations = new[] { "C1" }, notFound = false })));
        var turn1 = (await f.Service(first).RunTurnAsync("uw-1", Roles.Underwriter, new ChatTurnRequest(ChatScopes.Quote, "Which claims are open?", ChatFixture.QuoteA))).Result!;
        Assert.Contains(turn1.ToolTrace, t => t.Tool == ChatMemory.LastResultTool);

        var second = new ScriptedModel(new ScriptedModel.Step(Answer: JsonSerializer.Serialize(new { text = "Its incurred is $12,000.00.", citations = Array.Empty<string>(), notFound = false })));
        await f.Service(second).RunTurnAsync("uw-1", Roles.Underwriter, new ChatTurnRequest(ChatScopes.Quote, "What is its incurred?", ChatFixture.QuoteA, ConversationId: turn1.ConversationId));

        Assert.Contains(second.SystemSeen, s => s.Contains("Last result (turn 1): query_claims, filter Status=open, 1 match(es): WC-A-001 (Acme Mutual, open, 2023, incurred 12000", StringComparison.Ordinal));
    }
}
