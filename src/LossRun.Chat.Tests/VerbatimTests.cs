using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Data;

namespace LossRun.Chat.Tests;

/// <summary>Brief 24 step 7: rationales are quoted exactly; the fact guard checks it and the service restates once when not.</summary>
public class VerbatimTests
{
    private const string Rationale = "eval24: adjuster raised the reserve for a second surgery";

    [Fact]
    public void A_quotation_must_be_an_exact_substring_of_the_tool_output_case_included()
    {
        string[] tool = [Rationale];

        Assert.Empty(FactGuard.Unverified($"The reason given was “{Rationale}” [C1].", tool));
        Assert.Equal(["quote \"Eval24: Adjuster raised the reserve for a second surgery\""],
            FactGuard.Unverified("The reason given was \"Eval24: Adjuster raised the reserve for a second surgery\" [C1].", tool));
    }

    [Fact]
    public void Every_rationale_returned_must_be_quoted_whole_up_to_five_then_at_least_one()
    {
        Assert.Empty(FactGuard.MissingRationales($"Changed because \"{Rationale}\" [C1].", [Rationale]));
        Assert.Equal([Rationale], FactGuard.MissingRationales("Changed because \"adjuster raised the reserve for a second surgery\" [C1].", [Rationale])); // tag dropped
        Assert.Equal([Rationale], FactGuard.MissingRationales("It changed because the adjuster raised the reserve after a second surgery [C1].", [Rationale])); // paraphrase
        Assert.Empty(FactGuard.MissingRationales("No corrections.", []));

        var six = Enumerable.Range(1, 6).Select(i => $"reason number {i} for the change").ToList();
        Assert.Empty(FactGuard.MissingRationales($"One of them: \"{six[2]}\".", six));
        Assert.Equal(6, FactGuard.MissingRationales("Several reasons were given.", six).Count);
    }

    [Fact]
    public async Task A_paraphrased_rationale_is_restated_once_with_the_exact_quotation()
    {
        var f = await ChatFixture.CreateAsync();
        var before = (await f.Records.GetResultAsync("doc-a1"))!;
        var decision = new ReviewDecision("doc-a1", ReviewActions.Override, [new FieldOverride("policyPeriods[0].claims[0].indemnity.paid", JsonDocument.Parse("12500").RootElement, Rationale)],
            false, Rationale, "uw-1", Roles.Underwriter, f.Clock.GetUtcNow());
        var (after, changes) = DocumentEdits.Apply(before, decision);
        await f.Records.ApplyDecisionAsync(new DecisionRecord("up-a", before, after, decision, changes, false));

        var model = new ScriptedModel(
            new ScriptedModel.Step(Calls: [("explain_value", "{\"claimNumber\":\"WC-A-001\",\"field\":\"indemnity.paid\"}")]),
            new ScriptedModel.Step(Answer: JsonSerializer.Serialize(new { text = "It went up because the adjuster raised the reserve after surgery [C1].", citations = new[] { "C1" }, notFound = false })),
            new ScriptedModel.Step(Answer: JsonSerializer.Serialize(new { text = $"It went up; the reviewer wrote \"{Rationale}\" [C1].", citations = new[] { "C1" }, notFound = false })));

        var turn = (await f.Service(model).RunTurnAsync("uw-1", Roles.Underwriter, new ChatTurnRequest(ChatScopes.Quote, "Why did WC-A-001 indemnity paid change?", ChatFixture.QuoteA))).Result!;

        Assert.Contains(Rationale, turn.Text);
        Assert.Null(turn.Unverified);
        Assert.Null(turn.InvalidCitations);
        var restate = Assert.Single(turn.ToolTrace, t => t.Tool == "restate");
        Assert.Equal(1, restate.Results);
        Assert.Contains("rationaleMisses", restate.Arguments);
        Assert.Equal(3, turn.Usage.ModelCalls); // tool round, first answer, one restate: never more
    }

    [Fact]
    public async Task A_printed_value_answer_need_not_quote_a_rationale()
    {
        // Brief 25 step 0: brief 24's known issue — a question about the printed value was forced into a restate.
        Assert.Empty(FactGuard.MissingRationales("The loss run printed $0.00 for the indemnity reserve [S1].", [Rationale]));

        var f = await ChatFixture.CreateAsync();
        await CorrectAsync(f);
        var model = new ScriptedModel(
            new ScriptedModel.Step(Calls: [("explain_value", "{\"claimNumber\":\"WC-A-001\",\"field\":\"indemnity.paid\"}")]),
            new ScriptedModel.Step(Answer: JsonSerializer.Serialize(new { text = "The loss run printed $12,000.00 for the indemnity paid [S1].", citations = new[] { "S1" }, notFound = false })));

        var turn = (await f.Service(model).RunTurnAsync("uw-1", Roles.Underwriter, new ChatTurnRequest(ChatScopes.Quote, "What did the loss run print for WC-A-001 indemnity paid?", ChatFixture.QuoteA))).Result!;

        Assert.DoesNotContain(turn.ToolTrace, t => t.Tool == "restate");
        Assert.Equal("The loss run printed $12,000.00 for the indemnity paid [S1].", turn.Text);
        Assert.Null(turn.Unverified);
    }

    [Fact]
    public async Task A_restate_that_loses_facts_or_answers_not_found_is_discarded_and_the_misquote_flagged()
    {
        var f = await ChatFixture.CreateAsync();
        await CorrectAsync(f);
        const string first = "It changed from $12,000.00 to $12,500.00 because the adjuster raised it after surgery [C1].";
        var model = new ScriptedModel(
            new ScriptedModel.Step(Calls: [("explain_value", "{\"claimNumber\":\"WC-A-001\",\"field\":\"indemnity.paid\"}")]),
            new ScriptedModel.Step(Answer: JsonSerializer.Serialize(new { text = first, citations = new[] { "C1" }, notFound = false })),
            new ScriptedModel.Step(Answer: JsonSerializer.Serialize(new { text = "It is not in these documents.", citations = Array.Empty<string>(), notFound = true })));

        var turn = (await f.Service(model).RunTurnAsync("uw-1", Roles.Underwriter, new ChatTurnRequest(ChatScopes.Quote, "Why did WC-A-001 indemnity paid change?", ChatFixture.QuoteA))).Result!;

        Assert.Equal(first, turn.Text);
        Assert.False(turn.NotFound);
        Assert.Contains(turn.Unverified!, u => u.StartsWith("rationale not quoted verbatim", StringComparison.Ordinal));
        Assert.Equal(0, Assert.Single(turn.ToolTrace, t => t.Tool == "restate").Results);
    }

    private static async Task CorrectAsync(ChatFixture f)
    {
        var before = (await f.Records.GetResultAsync("doc-a1"))!;
        var decision = new ReviewDecision("doc-a1", ReviewActions.Override, [new FieldOverride("policyPeriods[0].claims[0].indemnity.paid", JsonDocument.Parse("12500").RootElement, Rationale)],
            false, Rationale, "uw-1", Roles.Underwriter, f.Clock.GetUtcNow());
        var (after, changes) = DocumentEdits.Apply(before, decision);
        await f.Records.ApplyDecisionAsync(new DecisionRecord("up-a", before, after, decision, changes, false));
    }

    [Fact]
    public async Task An_exact_answer_is_not_restated()
    {
        var f = await ChatFixture.CreateAsync();
        var model = new ScriptedModel(
            new ScriptedModel.Step(Calls: [("query_claims", "{}")]),
            new ScriptedModel.Step(Answer: JsonSerializer.Serialize(new { text = "Two claims [C1].", citations = new[] { "C1" }, notFound = false })));

        var turn = (await f.Service(model).RunTurnAsync("uw-1", Roles.Underwriter, new ChatTurnRequest(ChatScopes.Quote, "How many claims?", ChatFixture.QuoteA))).Result!;

        Assert.DoesNotContain(turn.ToolTrace, t => t.Tool == "restate");
    }
}
