using LossRun.Contracts;
using LossRun.Testing;

namespace LossRun.Rules.Tests;

/// <summary>Brief 24 step 2: the field-status matrix (review plan 2026-10-25 §1 acceptance tests).</summary>
public class FieldStatusEvaluatorTests
{
    private static IReadOnlyDictionary<string, FieldStatus> Statuses(LossRunDocument document, string? reviewState) =>
        FieldStatusEvaluator.Evaluate(document, reviewState, RuleEvaluator.Default.Evaluate(document).Outcomes);

    /// <summary>Claim 0's medical paid read wrong: the period subtotal no longer reconciles (a hard failure).</summary>
    private static LossRunDocument WithDroppedCell()
    {
        var document = SyntheticLossRuns.ThreeClaims();
        document.ClaimAt(0, 0).Medical = new MoneyTriplet(800m, 0m, 8000m);
        return document;
    }

    [Fact]
    public void R33_is_a_quote_rule_the_document_evaluator_never_runs()
    {
        var catalog = RuleCatalog.Embedded;

        Assert.Equal("R-33", Assert.Single(catalog.QuoteRules).Id);
        Assert.DoesNotContain(catalog.Rules, r => r.Id == "R-33");
        Assert.Contains("policyPeriods[*].claims[*].claimNumber", catalog.RequiredForQuoting.Select(r => r.Path));
        Assert.Equal(RequiredField.SummaryOnlyLabelledPeriod, catalog.RequiredForQuoting.Single(r => r.Path == "policyPeriods[*].effectiveDate").Unless);
        Assert.DoesNotContain(RuleEvaluator.Default.Evaluate(SyntheticLossRuns.ThreeClaims()).Outcomes, o => o.RuleId == "R-33");
    }

    [Fact]
    public void Required_fields_expand_over_every_period_and_claim()
    {
        var statuses = Statuses(SyntheticLossRuns.ThreeClaims(), null);

        // 2 document fields + 2 periods × 2 dates + 3 claims × 4 claim fields, plus the two lineage-only paths.
        Assert.Equal(2 + 4 + 12 + 1, statuses.Count);
        Assert.Contains("policyPeriods[1].claims[0].total.incurred", statuses.Keys);
        Assert.Contains("policyPeriods[0].claims[0].indemnity.paid", statuses.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ReviewStateValues.Accept)]
    [InlineData(ReviewStateValues.Override)]
    public void A_clean_document_straight_through_or_decided_is_all_approved(string? reviewState)
    {
        Assert.All(Statuses(SyntheticLossRuns.ThreeClaims(), reviewState).Values, s => Assert.Equal(new FieldStatus(FieldStates.Approved), s));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("reopened")]
    public void A_document_waiting_for_review_is_all_unresolved(string reviewState)
    {
        Assert.All(Statuses(SyntheticLossRuns.ThreeClaims(), reviewState).Values, s => Assert.Equal(new FieldStatus(FieldStates.Unresolved, "review-pending"), s));
    }

    [Fact]
    public void A_rejected_document_is_all_rejected_even_with_failures()
    {
        Assert.All(Statuses(WithDroppedCell(), ReviewStateValues.Reject).Values, s => Assert.Equal(new FieldStatus(FieldStates.Rejected, "document-rejected"), s));
    }

    [Fact]
    public void A_hard_failure_left_after_the_decision_keeps_its_fields_unresolved_and_an_override_approves_them()
    {
        var document = WithDroppedCell();
        var failing = RuleEvaluator.Default.Evaluate(document).HardFailures
            .Where(f => f.AffectedFields.Contains("policyPeriods[0].claims[*].medical.paid") || f.AffectedFields.Contains("policyPeriods[0].claims[0].medical.paid"))
            .Select(f => f.RuleId).ToList();
        Assert.NotEmpty(failing);
        const string untouched = "policyPeriods[1].claims[0].claimNumber";
        document.Lineage!["policyPeriods[0].claims[0].medical.paid"] = new LineageRecord { DocumentId = document.DocumentId, Extractor = "extracted" };

        var accepted = Statuses(document, ReviewStateValues.Accept);
        Assert.Equal(FieldStates.Unresolved, accepted["policyPeriods[0].claims[0].medical.paid"].Status);
        Assert.Contains(accepted["policyPeriods[0].claims[0].medical.paid"].Reason, failing);
        Assert.Equal(FieldStates.Approved, accepted[untouched].Status);

        document.Lineage["policyPeriods[0].claims[0].medical.paid"] = new LineageRecord { DocumentId = document.DocumentId, Extractor = "manual" };
        Assert.Equal(new FieldStatus(FieldStates.Approved, "override"), Statuses(document, ReviewStateValues.Override)["policyPeriods[0].claims[0].medical.paid"]);
    }

    [Fact]
    public void A_required_field_that_is_null_is_unresolved_even_after_approval()
    {
        var document = SyntheticLossRuns.ThreeClaims();
        document.ClaimAt(0, 1).Status = null;
        document.PolicyPeriods[1].EffectiveDate = null;

        var statuses = Statuses(document, ReviewStateValues.Accept);

        Assert.Equal(new FieldStatus(FieldStates.Unresolved, "R-33"), statuses["policyPeriods[0].claims[1].status"]);
        Assert.Equal(new FieldStatus(FieldStates.Unresolved, "R-33"), statuses["policyPeriods[1].effectiveDate"]);
        Assert.Equal(FieldStates.Approved, statuses["policyPeriods[0].claims[0].status"].Status);
    }

    [Fact]
    public void Period_dates_are_not_required_for_a_labelled_summary_only_period_but_are_when_the_document_has_claim_rows()
    {
        // Brief 24 Answers 1: a claim without a policy period cannot be allocated; a summary row that prints its year can.
        var summary = new LossRunDocument
        {
            DocumentId = "syn-summary", CarrierName = "Synthetic Mutual Insurance Co.", ValuationDate = new DateOnly(2024, 9, 30),
            PolicyPeriods = [new PolicyPeriod { DetailLevel = DetailLevel.SummaryOnly, PeriodLabel = "2022" }, new PolicyPeriod { DetailLevel = DetailLevel.SummaryOnly }],
        };

        var statuses = Statuses(summary, "accept");
        Assert.DoesNotContain("policyPeriods[0].effectiveDate", statuses.Keys);
        Assert.Equal(new FieldStatus(FieldStates.Unresolved, "R-33"), statuses["policyPeriods[1].effectiveDate"]); // no label: required

        var withClaims = SyntheticLossRuns.ThreeClaims();
        withClaims.PolicyPeriods[1].EffectiveDate = null;
        withClaims.PolicyPeriods[1].PeriodLabel = "2023";
        Assert.Equal(new FieldStatus(FieldStates.Unresolved, "R-33"), Statuses(withClaims, "accept")["policyPeriods[1].effectiveDate"]);
    }

    [Fact]
    public void A_bad_required_field_path_fails_the_catalog_load()
    {
        var json = $$"""
        { "catalogVersion": "t", "schemaVersion": "{{CanonicalData.SchemaVersion}}",
          "rules": [ { "id": "R-01", "status": "provisional", "name": "n", "severity": "hard", "scope": "claim",
                       "assert": { "notNull": { "path": "claim.claimNumber" } }, "message": "m", "source": "s" } ],
          "quoteRules": [ { "id": "R-02", "status": "provisional", "name": "n", "severity": "hard", "scope": "quote",
                            "requiredFields": ["policyPeriods[x].claims"], "message": "m", "source": "s" } ] }
        """;

        Assert.Contains("requiredFields", Assert.Throws<RuleCatalogException>(() => RuleCatalog.Load(json)).Message);
    }

    private static class ReviewStateValues
    {
        public const string Accept = "accept";
        public const string Override = "override";
        public const string Reject = "reject";
    }
}
