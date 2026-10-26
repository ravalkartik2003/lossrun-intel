using LossRun.Analytics;
using LossRun.Contracts;

namespace LossRun.Analytics.Tests;

/// <summary>ADR 0017: a summary-only document gives period metrics only; claim-level metrics are not measurable.</summary>
public class PeriodSummaryTests
{
    private static PolicyPeriod Year(string label, int count, int open, decimal paid, decimal reserve) => new()
    {
        PeriodKind = PeriodKind.FiscalYear, PeriodLabel = label, DetailLevel = DetailLevel.SummaryOnly,
        Subtotals = new Totals
        {
            ClaimCount = count, OpenCount = open, ClosedCount = count - open, Total = new MoneyTriplet(paid, reserve, paid + reserve),
            Recoveries = new RecoveryAmounts { Deductible = 100m },
        },
    };

    private static AccountDocument Summary() => new("doc-s", new LossRunDocument
    {
        DocumentId = "doc-s", PolicyPeriods = [Year("FY 2022-23", 4, 1, 30_000m, 10_000m), Year("FY 2023-24", 6, 3, 20_000m, 40_000m)],
    }, []);

    [Fact]
    public void Period_rows_feed_the_from_summary_metrics_and_nothing_else()
    {
        var rows = PeriodSummaries.Rows([Summary()]);
        var metrics = PeriodSummaries.Metrics(rows);

        Assert.Equal(2, rows.Count);
        Assert.Equal("fiscalYear", rows[0].PeriodKind);
        Assert.Equal(10, metrics["claim_count"]!["value"]!.GetValue<int>());
        Assert.Equal(4, metrics["open_claim_count"]!["value"]!.GetValue<int>());
        Assert.Equal(100_000m, metrics["incurred_total"]!["value"]!.GetValue<decimal>());
        Assert.Equal(10_000m, metrics["severity_all"]!["value"]!.GetValue<decimal>());
        Assert.Equal(0.5m, metrics["paid_to_incurred"]!["value"]!.GetValue<decimal>());
        Assert.Equal(60_000m, metrics["incurred_by_policy_year"]!["value"]!["FY 2023-24"]!.GetValue<decimal>());
        Assert.Equal(PeriodSummaries.NotMeasurableReason, metrics["reporting_lag_median_days"]!["notMeasurable"]!.GetValue<string>());

        var catalog = Catalog.Embedded.Json["metrics"]!.AsArray();
        Assert.Equal(catalog.Count, metrics.Count);
        Assert.All(catalog, m => Assert.Contains(m!["fromSummary"]!.GetValue<string>(), new[] { "yes", "no" }));
    }

    [Fact]
    public void A_summary_only_document_adds_no_claims_to_the_claim_level_engine()
    {
        var input = AccountBuilder.Build("q-1", [Summary()]);

        Assert.Empty(input.Claims);
        Assert.Equal("policyPeriods[1].subtotals", PeriodSummaries.ToJson(PeriodSummaries.Rows([Summary()]))[1]!["lineagePath"]!.GetValue<string>());
    }
}
