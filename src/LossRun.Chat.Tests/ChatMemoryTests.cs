using System.Text.Json;
using LossRun.Api.Contracts;

namespace LossRun.Chat.Tests;

/// <summary>Brief 24 step 6 (review plan 2026-10-25 §4): conversation facts and the token-budgeted verbatim window.</summary>
public class ChatMemoryTests
{
    private static ChatTurnResult Turn(string question, string text, params ChatCitation[] citations) =>
        new($"turn-{Guid.NewGuid():N}", "conv-1", "uw-1", Roles.Underwriter, ChatScopes.Quote, "Q-A", null, question, text, false, citations, [],
            new ChatTurnUsage(1, 0, 0, 0, 0, 0, 0, 0, 0, []), DateTimeOffset.UnixEpoch);

    private static readonly ChatScope QuoteA = ChatScope.Parse(ChatScopes.Quote, "Q-A", null);

    [Fact]
    public void Facts_list_every_document_claim_and_metric_cited_before_newest_first_with_their_turns_and_no_refs()
    {
        var history = new[]
        {
            Turn("total?", "Total is $1 [M1].", new ChatCitation("M1", "metric", "Incurred total · catalog 1.3.0", Metric: "incurred_total")),
            Turn("largest?", "WC-A-001 [C1].", new ChatCitation("C1", "claim", "WC-A-001 · doc-a1.pdf · p.1", "doc-a1", 1, FieldPath: "policyPeriods[0].claims[0].total.incurred",
                ClaimId: "doc-a1/0/0", FileName: "doc-a1.pdf", QuoteId: "Q-A")),
            Turn("other quote?", "…", new ChatCitation("C1", "claim", "B-SECRET-001 · doc-b1.pdf", "doc-b1", QuoteId: "Q-B")), // impossible; filtered anyway
            Turn("again?", "WC-A-001 [C1].", new ChatCitation("C1", "claim", "WC-A-001 · doc-a1.pdf · p.1", "doc-a1", 1, FieldPath: "policyPeriods[0].claims[0].total.incurred",
                ClaimId: "doc-a1/0/0", FileName: "doc-a1.pdf", QuoteId: "Q-A")),
        };

        var facts = ChatMemory.Facts(history, QuoteA)!;
        var lines = facts.Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal)).ToList();

        Assert.Equal(["- claim WC-A-001 in doc-a1 (turn 4, 2)", "- document doc-a1 (doc-a1.pdf) (turn 4, 2)", "- metric incurred_total (turn 1)"], lines);
        Assert.DoesNotContain("B-SECRET", facts);
        Assert.DoesNotContain("[C1]", facts);
        Assert.Contains("not valid in this turn", facts);
        Assert.Null(ChatMemory.Facts([Turn("hi", "hello")], QuoteA));
    }

    [Fact]
    public void The_facts_block_respects_its_size_cap()
    {
        var citations = Enumerable.Range(1, 400).Select(i => new ChatCitation($"C{i}", "claim", $"WC-{i:0000} · doc-a1.pdf", "doc-a1", QuoteId: "Q-A")).ToArray();
        var facts = ChatMemory.Facts([Turn("all?", "…", citations)], QuoteA, tokenBudget: 300)!;

        Assert.True(ChatMemory.EstimateTokens(facts) <= 300, $"{ChatMemory.EstimateTokens(facts)} tokens");
        Assert.Contains("older fact(s) left out", facts);
    }

    [Fact]
    public void The_verbatim_window_takes_the_newest_pairs_that_fit_at_least_two_and_at_most_the_ceiling()
    {
        var history = Enumerable.Range(1, 8).Select(i => Turn($"question {i}", new string('x', 400))).ToList(); // ~100 tokens each

        Assert.Equal([6, 7, 8], ChatMemory.Verbatim(history, tokenBudget: 330, ceiling: 12).Select(v => v.Turn));
        Assert.Equal([7, 8], ChatMemory.Verbatim(history, tokenBudget: 10, ceiling: 12).Select(v => v.Turn)); // the floor of two
        Assert.Equal([5, 6, 7, 8], ChatMemory.Verbatim(history, tokenBudget: 100_000, ceiling: 4).Select(v => v.Turn)); // the ceiling
        Assert.Empty(ChatMemory.Verbatim([], 6000, 12));
    }

    [Fact]
    public async Task Turn_ten_resolves_the_claim_from_earlier_through_the_facts_block_and_the_ledger_records_what_was_verbatim()
    {
        var f = await ChatFixture.CreateAsync();
        string? conversation = null;
        // Turn 1 cites WC-A-002; turns 2–9 are long small talk, so with a small budget only the last two are replayed verbatim.
        var first = new ScriptedModel(
            new ScriptedModel.Step(Calls: [("query_claims", "{\"filter\":{\"claimNumber\":\"WC-A-002\"}}")]),
            new ScriptedModel.Step(Answer: JsonSerializer.Serialize(new { text = "WC-A-002 is closed [C1].", citations = new[] { "C1" }, notFound = false })));
        conversation = (await f.Service(first, historyTokenBudget: 200).RunTurnAsync("uw-1", Roles.Underwriter,
            new ChatTurnRequest(ChatScopes.Quote, "Tell me about claim WC-A-002.", ChatFixture.QuoteA))).Result!.ConversationId;
        for (var i = 2; i <= 9; i++)
        {
            var chat = new ScriptedModel(new ScriptedModel.Step(Answer: JsonSerializer.Serialize(new { text = $"Noted ({i}). " + new string('-', 600), citations = Array.Empty<string>(), notFound = true })));
            await f.Service(chat, historyTokenBudget: 200).RunTurnAsync("uw-1", Roles.Underwriter, new ChatTurnRequest(ChatScopes.Quote, $"Small talk {i}.", ChatFixture.QuoteA, ConversationId: conversation));
        }

        var model = new ScriptedModel(
            new ScriptedModel.Step(Calls: [("query_claims", "{\"filter\":{\"claimNumber\":\"WC-A-002\"}}")]),
            new ScriptedModel.Step(Answer: JsonSerializer.Serialize(new { text = "Claim WC-A-002 is still closed [C1].", citations = new[] { "C1" }, notFound = false })));
        var turn10 = (await f.Service(model, historyTokenBudget: 200).RunTurnAsync("uw-1", Roles.Underwriter,
            new ChatTurnRequest(ChatScopes.Quote, "Is the claim we discussed at the start still closed?", ChatFixture.QuoteA, ConversationId: conversation))).Result!;

        Assert.Contains(model.SystemSeen, s => s.Contains("claim WC-A-002 in doc-a1 (turn 1)", StringComparison.Ordinal));
        Assert.Equal(2 * 2 + 1, model.HistoryMessagesSeen[0]); // two verbatim pairs + the question
        Assert.Null(turn10.InvalidCitations);
        var history = Assert.Single(turn10.ToolTrace, t => t.Tool == "history");
        Assert.Equal([8, 9], JsonDocument.Parse(history.Arguments).RootElement.GetProperty("verbatimTurns").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal("turns 1–7 as conversation facts only", history.Note);
        // The ledger keeps it: the stored turn's trace has the same entry.
        var stored = (await f.Records.ConversationAsync(conversation)).Last();
        Assert.Contains(stored.ToolTrace, t => t.Tool == "history" && t.Results == 2);
    }
}
