using LossRun.Contracts;

namespace LossRun.Testing;

/// <summary>
/// Hand-written synthetic loss runs (no real carrier or claimant data). The base document is internally
/// consistent: every hard and soft rule passes or is not applicable. Tests mutate a fresh copy per case.
/// </summary>
internal static class SyntheticLossRuns
{
    /// <summary>Two CA WC policy periods, three claims (closed indemnity, open medical-only, closed medical-only).</summary>
    public static LossRunDocument ThreeClaims() => new()
    {
        DocumentId = "syn-doc-0001",
        CarrierName = "Synthetic Mutual Insurance Co.",
        InsuredName = "Acme Widgets (synthetic)",
        LineOfBusiness = "WC",
        JurisdictionState = "CA",
        ValuationDate = new DateOnly(2024, 9, 30),
        ReportRunDate = new DateOnly(2024, 10, 2),
        NoLossesStatement = false,
        ExpenseTreatment = ExpenseTreatment.Separate,
        PolicyPeriods =
        [
            new PolicyPeriod
            {
                PolicyNumber = "SYN-WC-1001",
                EffectiveDate = new DateOnly(2022, 7, 1),
                ExpirationDate = new DateOnly(2023, 7, 1),
                Claims =
                [
                    new Claim
                    {
                        ClaimNumber = "WC-22-0001",
                        ClaimantName = "Synthetic Claimant A",
                        DateOfInjury = new DateOnly(2022, 9, 15),
                        DateReported = new DateOnly(2022, 9, 16),
                        DateClosed = new DateOnly(2023, 3, 1),
                        Status = ClaimStatus.Closed,
                        RawStatus = "C",
                        ClaimType = ClaimType.Indemnity,
                        InjuryTypeCode = "05",
                        TypeOfLossCode = "01",
                        CumulativeTrauma = false,
                        NatureOfInjury = new CodedValue { Code = "52", Text = "Strain", CodeSystem = "WCIO-Nature" },
                        CauseOfInjury = new CodedValue { Code = "56", Text = "Lifting", CodeSystem = "WCIO-Cause" },
                        PartOfBody = new CodedValue { Code = "42", Text = "Lower back", CodeSystem = "WCIO-PartOfBody" },
                        LitigationFlag = false,
                        JurisdictionState = "CA",
                        ClassCode = "8810",
                        Indemnity = new MoneyTriplet(12000m, 0m, 12000m),
                        Medical = new MoneyTriplet(8000m, 0m, 8000m),
                        Expense = new MoneyTriplet(1500m, 0m, 1500m),
                        Total = new MoneyTriplet(21500m, 0m, 21500m),
                    },
                    new Claim
                    {
                        ClaimNumber = "WC-23-0002",
                        ClaimantName = "Synthetic Claimant B",
                        DateOfInjury = new DateOnly(2023, 2, 10),
                        DateReported = new DateOnly(2023, 2, 13),
                        Status = ClaimStatus.Open,
                        RawStatus = "O",
                        ClaimType = ClaimType.MedicalOnly,
                        InjuryTypeCode = "06",
                        TypeOfLossCode = "01",
                        NatureOfInjury = new CodedValue { Code = "40", Text = "Laceration", CodeSystem = "WCIO-Nature" },
                        JurisdictionState = "CA",
                        ClassCode = "8810",
                        Indemnity = new MoneyTriplet(0m, 0m, 0m),
                        Medical = new MoneyTriplet(1200m, 800m, 2000m),
                        Expense = new MoneyTriplet(0m, 0m, 0m),
                        Total = new MoneyTriplet(1200m, 800m, 2000m),
                    },
                ],
                Subtotals = new Totals
                {
                    ClaimCount = 2,
                    OpenCount = 1,
                    ClosedCount = 1,
                    Indemnity = new MoneyTriplet(12000m, 0m, 12000m),
                    Medical = new MoneyTriplet(9200m, 800m, 10000m),
                    Expense = new MoneyTriplet(1500m, 0m, 1500m),
                    Total = new MoneyTriplet(22700m, 800m, 23500m),
                },
            },
            new PolicyPeriod
            {
                PolicyNumber = "SYN-WC-1002",
                EffectiveDate = new DateOnly(2023, 7, 1),
                ExpirationDate = new DateOnly(2024, 7, 1),
                Claims =
                [
                    new Claim
                    {
                        ClaimNumber = "WC-24-0003",
                        ClaimantName = "Synthetic Claimant C",
                        DateOfInjury = new DateOnly(2024, 1, 20),
                        DateReported = new DateOnly(2024, 1, 22),
                        DateClosed = new DateOnly(2024, 5, 10),
                        Status = ClaimStatus.Closed,
                        ClaimType = ClaimType.MedicalOnly,
                        JurisdictionState = "CA",
                        Indemnity = new MoneyTriplet(0m, 0m, 0m),
                        Medical = new MoneyTriplet(650m, 0m, 650m),
                        Expense = new MoneyTriplet(0m, 0m, 0m),
                        Total = new MoneyTriplet(650m, 0m, 650m),
                        RecoverySubrogation = 0m,
                    },
                ],
                Subtotals = new Totals
                {
                    ClaimCount = 1,
                    OpenCount = 0,
                    ClosedCount = 1,
                    Indemnity = new MoneyTriplet(0m, 0m, 0m),
                    Medical = new MoneyTriplet(650m, 0m, 650m),
                    Expense = new MoneyTriplet(0m, 0m, 0m),
                    Total = new MoneyTriplet(650m, 0m, 650m),
                },
            },
        ],
        ReportTotals = new Totals
        {
            ClaimCount = 3,
            OpenCount = 1,
            ClosedCount = 2,
            Indemnity = new MoneyTriplet(12000m, 0m, 12000m),
            Medical = new MoneyTriplet(9850m, 800m, 10650m),
            Expense = new MoneyTriplet(1500m, 0m, 1500m),
            Total = new MoneyTriplet(23350m, 800m, 24150m),
        },
        Lineage = new Dictionary<string, LineageRecord>
        {
            ["policyPeriods[0].claims[0].indemnity.paid"] = new()
            {
                DocumentId = "syn-doc-0001", Page = 1, Polygon = [5.1, 2.0, 5.9, 2.0, 5.9, 2.2, 5.1, 2.2],
                SourceText = "12,000.00", FormatId = "fmt-synthetic-pdf", FormatVersion = "1", Confidence = 0.98,
            },
            ["valuationDate"] = new()
            {
                DocumentId = "syn-doc-0001", Sheet = "Loss Run", Cell = "B2", SourceText = "09/30/2024",
                FormatId = "fmt-synthetic-xlsx", FormatVersion = "1", Model = "gpt-5.4-mini", PromptVersion = "learn-v1", Confidence = 0.91,
            },
        },
    };

    /// <summary>
    /// Second fictitious carrier for the learn path (brief 06): two calendar-year policies, four claims including a
    /// reopened one (prior close date + open reserve). The run prints no claim type and no expense reserve; "O/S" is the
    /// total outstanding (indemnity + medical reserve). Everything is invented.
    /// </summary>
    public static LossRunDocument SecondCarrier() => new()
    {
        DocumentId = "syn-doc-0002",
        CarrierName = "Fictional Casualty Group",
        InsuredName = "Beta Tooling (synthetic)",
        LineOfBusiness = "WC",
        ValuationDate = new DateOnly(2024, 8, 31),
        ReportRunDate = new DateOnly(2024, 9, 3),
        NoLossesStatement = false,
        ExpenseTreatment = ExpenseTreatment.Separate,
        PolicyPeriods =
        [
            new PolicyPeriod
            {
                PolicyNumber = "FCG-WC-2201",
                EffectiveDate = new DateOnly(2022, 1, 1),
                ExpirationDate = new DateOnly(2023, 1, 1),
                Claims =
                [
                    Second("22-FC-00117", "Synthetic Employee D", new(2022, 3, 14), new(2022, 3, 15), new(2022, 11, 30), ClaimStatus.Closed, "C",
                        new(4500m, 0m, 4500m), new(6200m, 0m, 6200m), 900m),
                    Second("22-FC-00342", "Synthetic Employee E", new(2022, 8, 2), new(2022, 8, 9), new(2023, 2, 15), ClaimStatus.Reopened, "R",
                        new(2000m, 3000m, 5000m), new(5000m, 1500m, 6500m), 400m),
                ],
                Subtotals = SecondTotals(2, new(6500m, 3000m, 9500m), new(11200m, 1500m, 12700m), 1300m),
            },
            new PolicyPeriod
            {
                PolicyNumber = "FCG-WC-2301",
                EffectiveDate = new DateOnly(2023, 1, 1),
                ExpirationDate = new DateOnly(2024, 1, 1),
                Claims =
                [
                    Second("23-FC-00051", "Synthetic Employee F", new(2023, 5, 20), new(2023, 5, 22), null, ClaimStatus.Open, "O",
                        new(0m, 0m, 0m), new(850m, 400m, 1250m), 0m),
                    Second("23-FC-00198", "Synthetic Employee G", new(2023, 10, 11), new(2023, 10, 12), new(2023, 12, 1), ClaimStatus.Closed, "C",
                        new(0m, 0m, 0m), new(320m, 0m, 320m), 0m),
                ],
                Subtotals = SecondTotals(2, new(0m, 0m, 0m), new(1170m, 400m, 1570m), 0m),
            },
        ],
        ReportTotals = SecondTotals(4, new(6500m, 3000m, 9500m), new(12370m, 1900m, 14270m), 1300m),
    };

    private static Claim Second(string number, string name, DateOnly injury, DateOnly reported, DateOnly? closed, ClaimStatus status, string raw,
        MoneyTriplet indemnity, MoneyTriplet medical, decimal expensePaid) => new()
    {
        ClaimNumber = number,
        ClaimantName = name,
        DateOfInjury = injury,
        DateReported = reported,
        DateClosed = closed,
        Status = status,
        RawStatus = raw,
        Indemnity = indemnity,
        Medical = medical,
        Expense = new MoneyTriplet(expensePaid, null, null),
        Total = new MoneyTriplet(indemnity.Paid + medical.Paid + expensePaid, indemnity.Reserve + medical.Reserve,
            indemnity.Paid + medical.Paid + expensePaid + indemnity.Reserve + medical.Reserve),
    };

    private static Totals SecondTotals(int count, MoneyTriplet indemnity, MoneyTriplet medical, decimal expensePaid) => new()
    {
        ClaimCount = count,
        Indemnity = indemnity,
        Medical = medical,
        Expense = new MoneyTriplet(expensePaid, null, null),
        Total = new MoneyTriplet(indemnity.Paid + medical.Paid + expensePaid, indemnity.Reserve + medical.Reserve,
            indemnity.Paid + medical.Paid + expensePaid + indemnity.Reserve + medical.Reserve),
    };

    /// <summary>Quote context 62 days after the valuation date (inside State Fund's 100-day window).</summary>
    public static DateOnly TargetPolicyEffectiveDate => new(2024, 12, 1);

    public static Claim ClaimAt(this LossRunDocument document, int period, int claim) => document.PolicyPeriods[period].Claims[claim];
}
