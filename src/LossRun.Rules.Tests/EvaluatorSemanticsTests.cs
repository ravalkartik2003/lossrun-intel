using LossRun.Contracts;
using LossRun.Testing;

namespace LossRun.Rules.Tests;

public class EvaluatorSemanticsTests
{
    private static readonly RuleContext Context = new() { TargetPolicyEffectiveDate = SyntheticLossRuns.TargetPolicyEffectiveDate };

    [Fact]
    public void Cumulative_trauma_downgrades_the_policy_period_rule_to_soft()
    {
        var document = SyntheticLossRuns.ThreeClaims();
        var claim = document.ClaimAt(0, 0);
        claim.DateOfInjury = new DateOnly(2023, 8, 1);

        var hard = RuleEvaluator.Default.Evaluate(document, Context).Single(o => o.RuleId == "R-08" && o.ClaimId == "WC-22-0001");
        claim.CumulativeTrauma = true;
        var soft = RuleEvaluator.Default.Evaluate(document, Context).Single(o => o.RuleId == "R-08" && o.ClaimId == "WC-22-0001");

        Assert.Equal((RuleStatus.Fail, RuleSeverity.Hard), (hard.Status, hard.Severity));
        Assert.Equal((RuleStatus.Fail, RuleSeverity.Soft), (soft.Status, soft.Severity));
    }

    [Fact]
    public void Type_of_loss_code_also_marks_cumulative_trauma()
    {
        var document = SyntheticLossRuns.ThreeClaims();
        document.ClaimAt(0, 0).DateOfInjury = new DateOnly(2023, 8, 1);
        document.ClaimAt(0, 0).TypeOfLossCode = "03";

        var outcome = RuleEvaluator.Default.Evaluate(document, Context).Single(o => o.RuleId == "R-08" && o.ClaimId == "WC-22-0001");

        Assert.Equal(RuleSeverity.Soft, outcome.Severity);
    }

    [Fact]
    public void Missing_values_make_a_rule_not_applicable_rather_than_failing()
    {
        var document = SyntheticLossRuns.ThreeClaims();
        document.ClaimAt(0, 0).Indemnity = new MoneyTriplet(12000m, null, 12000m);

        var r02 = RuleEvaluator.Default.Evaluate(document, Context)
            .Single(o => o.RuleId == "R-02" && o.ClaimId == "WC-22-0001" && o.Bindings!["c"] == "indemnity");

        Assert.Equal(RuleStatus.NotApplicable, r02.Status);
    }

    [Fact]
    public void Stale_valuation_rule_needs_submission_context()
    {
        var outcome = RuleEvaluator.Default.Evaluate(SyntheticLossRuns.ThreeClaims()).Single(o => o.RuleId == "R-15");

        Assert.Equal(RuleStatus.NotApplicable, outcome.Status);
    }

    [Fact]
    public void A_dropped_cell_fails_the_subtotal_reconciliation()
    {
        // Extraction loses claim 2's medical paid: the printed subtotal (the free oracle) catches it.
        var document = SyntheticLossRuns.ThreeClaims();
        document.ClaimAt(0, 1).Medical!.Paid = null;

        var outcome = RuleEvaluator.Default.Evaluate(document, Context)
            .Single(o => o.RuleId == "R-10" && o.PeriodIndex == 0 && o.Bindings!["c"] == "medical" && o.Bindings["m"] == "paid");

        Assert.Equal(RuleStatus.Fail, outcome.Status);
    }

    [Fact]
    public void Failures_point_at_canonical_field_paths_and_explain_the_values()
    {
        var document = SyntheticLossRuns.ThreeClaims();
        document.ClaimAt(0, 0).Indemnity!.Paid = 13000m;

        var failure = RuleEvaluator.Default.Evaluate(document, Context)
            .Single(o => o.RuleId == "R-01" && o.Status == RuleStatus.Fail);

        Assert.Equal(["policyPeriods[0].claims[0].indemnity.paid", "policyPeriods[0].claims[0].indemnity.incurred"], failure.AffectedFields);
        Assert.Equal("WC-22-0001", failure.ClaimId);
        Assert.Equal(0, failure.PeriodIndex);
        Assert.Equal("indemnity paid exceeds indemnity incurred on claim WC-22-0001.", failure.Message);
        Assert.Contains("claim.indemnity.paid=13000", failure.Detail);
    }

    [Fact]
    public void Aggregate_failures_reference_the_subtotal_and_the_rows()
    {
        var document = SyntheticLossRuns.ThreeClaims();
        document.PolicyPeriods[0].Subtotals!.Medical!.Paid = 9300m;

        var failure = RuleEvaluator.Default.Evaluate(document, Context).Single(o => o.RuleId == "R-10" && o.Status == RuleStatus.Fail);

        // `when` paths are included: the rule only ran because a subtotal block exists on a claims-level period.
        Assert.Equal(["policyPeriods[0].subtotals", "policyPeriods[0].detailLevel", "policyPeriods[0].subtotals.medical.paid", "policyPeriods[0].claims[*].medical.paid"], failure.AffectedFields);
        Assert.Equal("Printed medical paid subtotal for policy SYN-WC-1001 does not equal the sum of its claim rows.", failure.Message);
    }

    [Fact]
    public void Duplicate_claim_numbers_list_every_duplicate_row()
    {
        var document = SyntheticLossRuns.ThreeClaims();
        document.ClaimAt(1, 0).ClaimNumber = "WC 22 0001";

        var failure = RuleEvaluator.Default.Evaluate(document, Context).Single(o => o.RuleId == "R-09");

        Assert.Equal(RuleStatus.Fail, failure.Status);
        Assert.Contains("policyPeriods[0].claims[0].claimNumber", failure.AffectedFields);
        Assert.Contains("policyPeriods[1].claims[0].claimNumber", failure.AffectedFields);
    }

    [Fact]
    public void Flags_and_soft_failures_do_not_block_straight_through_processing()
    {
        var document = SyntheticLossRuns.ThreeClaims();
        document.ClaimAt(1, 0).JurisdictionState = "NV";
        document.ClaimAt(0, 0).Indemnity!.Reserve = 0.5m; // within ±$1 for R-02, but a soft R-04 failure

        var result = RuleEvaluator.Default.Evaluate(document, Context);

        var flag = Assert.Single(result.Flags);
        Assert.Equal(("R-17", RuleSeverity.Flag), (flag.RuleId, flag.Severity));
        Assert.Contains(result.SoftFailures, o => o.RuleId == "R-04");
        Assert.Empty(result.HardFailures);
        Assert.True(result.PassesHardRules);
        Assert.True(RuleEvaluator.PassesHardRules(result));
        Assert.Equal("0.12.0", result.CatalogVersion);
    }

    [Fact]
    public void Total_identity_is_hard_only_when_expense_is_separate()
    {
        var document = SyntheticLossRuns.ThreeClaims();
        document.ClaimAt(0, 0).Expense = new MoneyTriplet(1400m, 0m, 1400m);

        var separate = RuleEvaluator.Default.Evaluate(document, Context).First(o => o.RuleId == "R-20" && o.Status == RuleStatus.Fail);
        document.ExpenseTreatment = ExpenseTreatment.Unknown;
        var unknown = RuleEvaluator.Default.Evaluate(document, Context).First(o => o.RuleId == "R-20" && o.Status == RuleStatus.Fail);

        Assert.Equal(RuleSeverity.Hard, separate.Severity);
        Assert.Equal(RuleSeverity.Soft, unknown.Severity);
    }

    [Fact]
    public void Cumulative_trauma_downgrades_the_report_date_rule_to_soft()
    {
        var document = SyntheticLossRuns.ThreeClaims();
        document.ClaimAt(0, 1).DateReported = new DateOnly(2023, 2, 1);
        document.ClaimAt(0, 1).CumulativeTrauma = true;

        var outcome = RuleEvaluator.Default.Evaluate(document, Context).Single(o => o.RuleId == "R-07" && o.ClaimId == "WC-23-0002");

        Assert.Equal((RuleStatus.Fail, RuleSeverity.Soft), (outcome.Status, outcome.Severity));
    }

    [Fact]
    public void Summary_only_periods_skip_row_reconciliation()
    {
        var document = SyntheticLossRuns.ThreeClaims();
        var period = document.PolicyPeriods[1];
        period.Claims.Clear();
        period.DetailLevel = DetailLevel.SummaryOnly;
        document.ReportTotals = null;

        var outcomes = RuleEvaluator.Default.Evaluate(document, Context).Where(o => o.RuleId is "R-10" or "R-11" && o.PeriodIndex == 1).ToList();

        Assert.NotEmpty(outcomes);
        Assert.All(outcomes, o => Assert.Equal(RuleStatus.NotApplicable, o.Status));
    }

    [Fact]
    public void Summary_only_runs_skip_row_based_grand_totals_but_still_check_subtotals()
    {
        var document = SyntheticLossRuns.ThreeClaims();
        var period = document.PolicyPeriods[1];
        period.Claims.Clear();
        period.DetailLevel = DetailLevel.SummaryOnly;

        var outcomes = RuleEvaluator.Default.Evaluate(document, Context);

        Assert.All(outcomes.Where(o => o.RuleId is "R-12" or "R-13"), o => Assert.Equal(RuleStatus.NotApplicable, o.Status));
        Assert.Contains(outcomes, o => o.RuleId == "R-26" && o.Status == RuleStatus.Pass);
        Assert.DoesNotContain(outcomes, o => o.RuleId == "R-26" && o.Status == RuleStatus.Fail);
    }

    [Fact]
    public void Report_lag_is_emitted_for_analytics_on_every_claim()
    {
        var lags = RuleEvaluator.Default.Evaluate(SyntheticLossRuns.ThreeClaims(), Context)
            .Where(o => o.RuleId == "R-23").ToDictionary(o => o.ClaimId!, o => o.Values!["lagDays"]);

        Assert.Equal(new Dictionary<string, string> { ["WC-22-0001"] = "1", ["WC-23-0002"] = "3", ["WC-24-0003"] = "2" }, lags);
    }

    [Fact]
    public void Missing_requested_years_are_listed_in_the_detail()
    {
        var context = Context with { RequestedPolicyYears = [2020, 2021, 2022, 2023] };

        var outcome = RuleEvaluator.Default.Evaluate(SyntheticLossRuns.ThreeClaims(), context).Single(o => o.RuleId == "R-25");

        Assert.Equal(RuleStatus.Fail, outcome.Status);
        Assert.Contains("2020, 2021", outcome.Detail);
        Assert.Contains("policyPeriods[*].effectiveDate", outcome.AffectedFields);
    }

    [Fact]
    public void Evaluation_is_deterministic()
    {
        var first = RuleEvaluator.Default.Evaluate(SyntheticLossRuns.ThreeClaims(), Context);
        var second = RuleEvaluator.Default.Evaluate(SyntheticLossRuns.ThreeClaims(), Context);

        Assert.Equal(first.Select(Key), second.Select(Key));

        static string Key(RuleOutcome o) =>
            $"{o.RuleId}|{o.Status}|{o.Severity}|{o.ClaimId}|{o.PeriodIndex}|{string.Join(',', o.Bindings ?? new Dictionary<string, string>())}|{string.Join(',', o.AffectedFields)}";
    }

    [Fact]
    public void Each_instance_produces_one_outcome()
    {
        var outcomes = RuleEvaluator.Default.Evaluate(SyntheticLossRuns.ThreeClaims(), Context);

        Assert.Equal(3 * 4, outcomes.Count(o => o.RuleId == "R-01")); // 3 claims × 4 components
        Assert.Equal(2 * 12, outcomes.Count(o => o.RuleId == "R-10")); // 2 periods × 4 components × 3 measures
        Assert.Single(outcomes, o => o.RuleId == "R-09");
    }
}
