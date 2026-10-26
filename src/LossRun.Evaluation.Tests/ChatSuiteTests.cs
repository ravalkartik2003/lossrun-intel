using LossRun.Analytics;
using LossRun.Contracts;
using LossRun.Ingestion;

namespace LossRun.Evaluation.Tests;

/// <summary>Brief 24 step 6: the multi-turn suite builds offline (its run waits for step 7).</summary>
public class ChatSuiteTests
{
    private static AnalyticsInput Account()
    {
        var period = new PolicyPeriod { PolicyNumber = "SYN-1", EffectiveDate = new DateOnly(2023, 1, 1), ExpirationDate = new DateOnly(2024, 1, 1) };
        decimal[] incurred = [500m, 25_000m, 1_200m, 9_000m, 70m];
        for (var i = 0; i < incurred.Length; i++)
        {
            period.Claims.Add(new Claim
            {
                ClaimNumber = $"WC-{i + 1:000}", DateOfInjury = new DateOnly(2023, 2, 1 + i), Status = i % 2 == 0 ? ClaimStatus.Open : ClaimStatus.Closed,
                ClaimType = ClaimType.Indemnity, Indemnity = new MoneyTriplet(incurred[i] / 2, incurred[i] / 2, incurred[i]),
            });
        }

        return AccountBuilder.Build("Q-T", [new AccountDocument("doc-t", new LossRunDocument { DocumentId = "doc-t", PolicyPeriods = [period] }, [])]);
    }

    [Fact]
    public void The_corrected_rubric_accepts_the_right_answers_the_first_run_failed()
    {
        // Brief 24 step 7 Answers 1: the step-7 answers that were right, verbatim in substance.
        var unchanged = new ChatCorrectionsEval.Fact("as printed", "not corrected|no corrections|unchanged|was not|wasn't|has not|hasn't|no correction");
        Assert.True(unchanged.In("No. WC-23-0001's indemnity paid is unchanged from the loss run: $76,293.69 [S1]. There are no corrections [C1]."));
        Assert.False(unchanged.In("Yes, it was changed to $80,000.00 [C1]."));
        Assert.True(new ChatMultiTurnEval.Fact("status", "reopened|open").In("It is open [C1]."));
    }

    [Fact]
    public void The_rubric_strips_the_evals_own_tag_before_comparing_a_rationale()
    {
        // Brief 24 step 7 re-run, who-approved: the rationale quoted without the eval's "eval24:" tag is the reviewer's words.
        var fact = new ChatCorrectionsEval.Fact("rationale b", "eval24: adjuster raised the reserve for a second surgery");
        Assert.True(fact.In("They said: “adjuster raised the reserve for a second surgery” for WC-23-0002 [C3]."));
        Assert.True(fact.In("\"eval24: adjuster raised the reserve for a second surgery\" [C3]"));
        Assert.False(fact.In("The adjuster increased the reserve because of a second operation [C3].")); // a paraphrase still fails
        Assert.True(new ChatCorrectionsEval.Fact("rationale", "eval24: carrier letter supersedes the broker's figure").In("“carrier letter supersedes the broker’s figure”"));
    }

    [Fact]
    public void Cached_input_is_priced_at_the_cached_rate_and_the_reconciliation_splits_the_pair()
    {
        // The 24 Oct reconciliation (docs/evaluation/results/2026-10-25-cost-reconciliation): 5,270,135 uncached + 13,059,840
        // cached gpt-5.4-mini input tokens, metered $5.4253 for the pair. Cached is a subset of input in the usage event.
        var at = new DateTimeOffset(2026, 10, 24, 15, 0, 0, TimeSpan.Zero);
        UsageEvent Event(string meter, decimal quantity) =>
            new("d", "r", "learn", "AzureOpenAI", meter, quantity, "token", "gpt-5.4-mini", "DataZoneStandard", "eastus2", at, "call-1");
        IReadOnlyList<UsageEvent> events = [Event("inputTokens", 5_270_135 + 13_059_840), Event("cachedInputTokens", 13_059_840)];

        var cost = LossRun.Ingestion.Metering.CostCalculator.Compute("d", "r", "learn", events, LossRun.Ingestion.Metering.PriceTable.Embedded);
        Assert.Equal(4.3479m, Math.Round(cost.Breakdown.Single(l => l.Meter.StartsWith("inputTokens", StringComparison.Ordinal)).CostUsd, 4)); // $0.825 / 1M
        Assert.Equal(1.0774m, Math.Round(cost.Breakdown.Single(l => l.Meter.StartsWith("cachedInputTokens", StringComparison.Ordinal)).CostUsd, 4)); // $0.0825 / 1M

        var uncached = Reconcile.LineCost("gpt-5.4-mini input (uncached)", events);
        var cached = Reconcile.LineCost("gpt-5.4-mini cached input", events);
        Assert.Equal((4.3479m, 1.0774m), (Math.Round(uncached, 4), Math.Round(cached, 4)));
        Assert.Equal(5.4253m, Math.Round(uncached + cached, 4)); // the pair's metered $ in the committed reconciliation
    }

    [Fact]
    public void The_multi_turn_suite_is_five_conversations_of_eight_turns_with_facts_from_the_record()
    {
        var conversations = ChatMultiTurnEval.Conversations(Account());

        Assert.Equal(5, conversations.Count);
        Assert.All(conversations, c => Assert.Equal(8, c.Steps.Count));
        var largest = conversations.Single(c => c.Id == "largest");
        Assert.Equal("WC-002", largest.Steps[0].Facts.Single().Text);
        Assert.Equal("WC-004", largest.Steps[3].Facts.Single().Text);
        Assert.Equal("12500.00", largest.Steps[6].Facts.Single().Text); // paid on the first claim discussed
        Assert.Equal("WC-005", conversations.Single(c => c.Id == "smallest").Steps[0].Facts.Single().Text);
        Assert.Equal("35770.00", conversations.Single(c => c.Id == "metrics").Steps[0].Facts.Single().Text);
    }
}
