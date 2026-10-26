using LossRun.Contracts;
using LossRun.Testing;

namespace LossRun.Rules.Tests;

/// <summary>
/// Every catalog rule has at least one synthetic passing case and one failing case. The base synthetic document
/// is consistent, so "pass" cases usually need no mutation; rules that are not applicable on the base document
/// (no-losses, reopened, net-vs-gross) get a mutation that makes them applicable and true.
/// </summary>
public class RuleCaseTests
{
    private sealed record RuleCase(Action<LossRunDocument, Box<RuleContext>>? Pass, Action<LossRunDocument, Box<RuleContext>> Fail);

    private sealed class Box<T>(T value)
    {
        public T Value { get; set; } = value;
    }

    private static readonly Dictionary<string, RuleCase> Cases = new()
    {
        ["R-01"] = new(null, (d, _) => d.ClaimAt(0, 0).Indemnity!.Paid = 13000m),
        ["R-02"] = new(null, (d, _) => d.ClaimAt(0, 0).Indemnity!.Incurred = 12500m),
        ["R-03"] = new(null, (d, _) => d.ClaimAt(0, 1).Medical!.Reserve = -50m),
        ["R-04"] = new(null, (d, _) => d.ClaimAt(0, 0).Indemnity!.Reserve = 500m),
        ["R-05"] = new(null, (d, _) => d.ClaimAt(0, 1).Indemnity!.Paid = 100m),
        ["R-06"] = new(null, (d, _) => d.ClaimAt(1, 0).DateOfInjury = new DateOnly(2024, 10, 5)),
        ["R-07"] = new(null, (d, _) => d.ClaimAt(0, 1).DateReported = new DateOnly(2023, 2, 1)),
        ["R-08"] = new(null, (d, _) => d.ClaimAt(0, 0).DateOfInjury = new DateOnly(2023, 8, 1)),
        ["R-09"] = new(null, (d, _) => d.ClaimAt(1, 0).ClaimNumber = " wc 22 0001"),
        ["R-10"] = new(null, (d, _) => d.PolicyPeriods[0].Subtotals!.Medical!.Paid = 9300m),
        ["R-11"] = new(null, (d, _) => d.PolicyPeriods[0].Subtotals!.OpenCount = 2),
        ["R-12"] = new(null, (d, _) => d.ReportTotals!.Total!.Paid = 23000m),
        ["R-13"] = new(null, (d, _) => d.ReportTotals!.ClaimCount = 4),
        ["R-14"] = new(
            (d, _) =>
            {
                // ADR 0016: the statement is per policy period; the other period keeps its claim rows.
                var p = d.PolicyPeriods[0];
                p.NoLossesStatement = true;
                p.Claims.Clear();
                p.Subtotals = null;
            },
            (d, _) => d.PolicyPeriods[0].NoLossesStatement = true),
        ["R-15"] = new(null, (_, c) => c.Value = c.Value with { TargetPolicyEffectiveDate = new DateOnly(2025, 3, 1) }),
        ["R-16"] = new(
            (d, _) =>
            {
                var claim = d.ClaimAt(0, 1);
                claim.Status = ClaimStatus.Reopened;
                claim.DateClosed = new DateOnly(2023, 6, 1);
            },
            (d, _) =>
            {
                // Reopened with nothing reserved (a missing close date alone no longer fails: brief 08).
                var claim = d.ClaimAt(0, 1);
                claim.Status = ClaimStatus.Reopened;
                foreach (var money in new[] { claim.Indemnity, claim.Medical, claim.Expense, claim.Total }.OfType<MoneyTriplet>())
                {
                    money.Reserve = 0;
                }
            }),
        ["R-17"] = new(null, (d, _) => d.ClaimAt(1, 0).JurisdictionState = "NV"),
        ["R-18"] = new(null, (d, _) => d.ClaimAt(0, 1).LineOfBusiness = "GL"),
        ["R-19"] = new(null, (d, _) => d.ClaimAt(0, 0).ClaimNumber = "  "),
        ["R-20"] = new(null, (d, _) => d.ClaimAt(0, 0).Expense = new MoneyTriplet(1400m, 0m, 1400m)),
        ["R-21"] = new(null, (d, _) => d.ClaimAt(1, 0).DateClosed = new DateOnly(2024, 10, 15)),
        ["R-22"] = new(
            (d, _) =>
            {
                d.ClaimAt(0, 0).NetIncurred = 20000m;
                d.ClaimAt(0, 0).RecoverySubrogation = 1500m;
            },
            (d, _) => d.ClaimAt(0, 0).NetIncurred = 20000m),
        ["R-23"] = new(null, (d, _) => d.ClaimAt(0, 0).DateReported = new DateOnly(2022, 11, 15)),
        ["R-24"] = new(null, (d, _) => d.ClaimAt(0, 0).Total!.Incurred = 150000m),
        ["R-25"] = new(
            (_, c) => c.Value = c.Value with { RequestedPolicyYears = [2022, 2023] },
            (_, c) => c.Value = c.Value with { RequestedPolicyYears = [2021, 2022, 2023] }),
        ["R-26"] = new(null, (d, _) => d.PolicyPeriods[1].Subtotals!.Medical!.Paid = 700m),
        ["R-28"] = new(null, (d, _) => d.UnattributedNoLossesStatement = true),
        ["R-29"] = new(
            (d, _) =>
            {
                d.TotalComposition = TotalComposition.IndemnityMedical;
                foreach (var claim in d.PolicyPeriods.SelectMany(p => p.Claims))
                {
                    claim.Total = new MoneyTriplet((claim.Indemnity?.Paid ?? 0) + (claim.Medical?.Paid ?? 0), (claim.Indemnity?.Reserve ?? 0) + (claim.Medical?.Reserve ?? 0),
                        (claim.Indemnity?.Incurred ?? 0) + (claim.Medical?.Incurred ?? 0));
                }
            },
            (d, _) => d.TotalComposition = TotalComposition.IndemnityMedical),
        ["R-30"] = new(null, (d, _) => d.ClaimAt(0, 0).Status = ClaimStatus.Unknown),
        // Brief 23 Answers 1: kept rows of another line and no printed total to reconcile against.
        ["R-31"] = new(
            (d, _) => d.PolicyPeriods[0].KeptRows = [new Claim { ClaimNumber = "GL-1", KeptReason = "line of business: general liability" }],
            (d, _) =>
            {
                d.PolicyPeriods[0].KeptRows = [new Claim { ClaimNumber = "GL-1", KeptReason = "line of business: general liability" }];
                d.ReportTotals = null;
                d.PolicyPeriods.ForEach(p => p.Subtotals = null);
            }),
        // Brief 23 Answers 2: the valuation date as stated by the submitter.
        ["R-32"] = new((d, _) => d.ValuationDateSource = "printed", (d, _) => d.ValuationDateSource = "submission"),
        // Brief 35 §4: the insured read as the carrier (punctuation and spacing ignored, as the c8 values printed them).
        ["R-34"] = new(
            (d, _) => (d.CarrierName, d.InsuredName) = ("Illustrative Comp Assurance", "Kappa Foods (synthetic)"),
            (d, _) => (d.CarrierName, d.InsuredName) = ("Kappa Foods ( synthetic )", "Kappa Foods (synthetic)")),
        ["R-27"] = new(
            (d, _) =>
            {
                d.PolicyPeriods[0].Claims.Clear();
                d.PolicyPeriods[0].Subtotals = null;
                d.PolicyPeriods[0].NoLossesStatement = true;
            },
            (d, _) =>
            {
                d.PolicyPeriods[0].Claims.Clear();
                d.PolicyPeriods[0].Subtotals = null;
            }),
    };

    public static TheoryData<string> RuleIds() => [.. RuleCatalog.Embedded.Rules.Select(r => r.Id)];

    private static (LossRunDocument Document, RuleContext Context) Base() =>
        (SyntheticLossRuns.ThreeClaims(), new RuleContext { TargetPolicyEffectiveDate = SyntheticLossRuns.TargetPolicyEffectiveDate });

    private static IReadOnlyList<RuleOutcome> Run(Action<LossRunDocument, Box<RuleContext>>? mutate)
    {
        var (document, context) = Base();
        var box = new Box<RuleContext>(context);
        mutate?.Invoke(document, box);
        return RuleEvaluator.Default.Evaluate(document, box.Value);
    }

    [Fact]
    public void Every_catalog_rule_has_a_pass_and_a_fail_case()
    {
        var ids = RuleCatalog.Embedded.Rules.Select(r => r.Id).ToHashSet();

        Assert.Empty(ids.Except(Cases.Keys));
        Assert.Empty(Cases.Keys.Except(ids));
    }

    [Fact]
    public void Base_synthetic_document_fails_no_rule()
    {
        var failures = Run(null).Where(o => o.Status == RuleStatus.Fail).ToList();

        Assert.True(failures.Count == 0, string.Join("\n", failures.Select(f => $"{f.RuleId} {f.Message} {f.Detail}")));
    }

    [Theory]
    [MemberData(nameof(RuleIds))]
    public void Rule_passes_on_its_passing_case(string ruleId)
    {
        var outcomes = Run(Cases[ruleId].Pass).Where(o => o.RuleId == ruleId).ToList();

        Assert.Contains(outcomes, o => o.Status == RuleStatus.Pass);
        Assert.DoesNotContain(outcomes, o => o.Status == RuleStatus.Fail);
    }

    [Theory]
    [MemberData(nameof(RuleIds))]
    public void Rule_fails_on_its_failing_case(string ruleId)
    {
        var failures = Run(Cases[ruleId].Fail).Where(o => o.RuleId == ruleId && o.Status == RuleStatus.Fail).ToList();

        Assert.NotEmpty(failures);
        Assert.All(failures, f =>
        {
            Assert.NotEmpty(f.AffectedFields);
            Assert.DoesNotContain("{", f.Message);
        });
    }
}
