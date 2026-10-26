using System.Globalization;
using LossRun.Contracts;

namespace LossRun.Evaluation;

/// <summary>A synthetic carrier: name, layout, numbering schemes and policy anniversary. Everything is fictitious.</summary>
public sealed record CarrierProfile(
    string Key,
    string Name,
    string Layout,
    string ClaimantStem,
    int PolicyMonth,
    int PolicyDay,
    Func<int, int, string> PolicyNumber,
    Func<int, int, string> ClaimNumber);

public static class Carriers
{
    public static readonly CarrierProfile SyntheticMutual = new("c1", "Synthetic Mutual Insurance Co.", "landscape-blocks", "Synthetic Claimant", 7, 1,
        (year, n) => $"SYN-WC-{year % 100:00}{n:00}", (year, seq) => $"WC-{year % 100:00}-{seq:0000}");

    public static readonly CarrierProfile FictionalCasualty = new("c2", "Fictional Casualty Group", "portrait-codes", "Synthetic Employee", 1, 1,
        (year, n) => $"FCG-WC-{year % 100:00}{n:00}", (year, seq) => $"{year % 100:00}-FC-{seq:00000}");

    public static readonly CarrierProfile ImaginaryIndemnity = new("c3", "Imaginary Indemnity Mutual", "narrative", "Synthetic Worker", 4, 1,
        (year, n) => $"IIM-{year % 100:00}-{n:000}", (year, seq) => $"IIM{year % 100:00}{seq:0000}");

    public static readonly CarrierProfile NotionalExchange = new("c4", "Notional Workers Comp Exchange", "xlsx-stacked", "Synthetic Party", 10, 1,
        (year, n) => $"NWX-{year}-{n:00}", (year, seq) => $"N{year % 100:00}-{seq:00000}");

    public static readonly CarrierProfile PlaceholderAssurance = new("c5", "Placeholder Assurance Co.", "csv-wide", "Synthetic Insured Employee", 3, 15,
        (year, n) => $"PAC-WC-{year}{n:0}", (year, seq) => $"PA{year % 100:00}{seq:000000}");

    public static readonly CarrierProfile[] All = [SyntheticMutual, FictionalCasualty, ImaginaryIndemnity, NotionalExchange, PlaceholderAssurance];

    // Brief 19 (ADR 0018) construct carriers, generated only by the constructs plan (corpus-gen 0.4.0 --constructs).
    public static readonly CarrierProfile HypotheticalMutual = new("c6", "Hypothetical Mutual Casualty", "two-line-records", "Synthetic Worker", 7, 1,
        (year, n) => $"HMC-{year % 100:00}{n:00}", (year, seq) => $"HM{year % 100:00}{seq:0000}");

    public static readonly CarrierProfile SpecimenIndemnity = new("c7", "Specimen Indemnity Co.", "claim-per-table", "Synthetic Employee", 9, 1,
        (year, n) => $"SIC-WC-{year % 100:00}{n:0}", (year, seq) => (seq % 3) switch
        {
            // Brief 20 §2 (corpus-gen 0.6.0): three claim-number shapes in one run, so a pattern learned from one example fails.
            1 => $"SPC-{year % 100:00}-{seq:00000}",
            2 => $"SPC{year % 100:00}{seq:000000}",
            _ => $"WC{year % 100:00}-{seq:0000}-A",
        });

    public static readonly CarrierProfile IllustrativeComp = new("c8", "Illustrative Comp Assurance", "stacked-columns", "Synthetic Claimant", 1, 1,
        (year, n) => $"ICA-{year}-{n:00}", (year, seq) => $"IC{year % 100:00}{seq:00000}");

    public static readonly CarrierProfile SampleComposite = new("c9", "Sample Composite Mutual", "csv-composite", "Synthetic Party", 4, 1,
        (year, n) => $"SCM-WC-{year}{n:0}", (year, seq) => $"SC{year % 100:00}{seq:000000}");

    // Brief 22 Answers 1 (corpus v5): measures as rows, one row per measure under component columns.
    public static readonly CarrierProfile ExemplarCasualty = new("c10", "Exemplar Casualty Mutual", "measures-rows", "Synthetic Worker", 6, 1,
        (year, n) => $"ECM-WC-{year % 100:00}{n:00}", (year, seq) => $"EC{year % 100:00}{seq:00000}");

    public static readonly CarrierProfile[] Constructs = [HypotheticalMutual, SpecimenIndemnity, IllustrativeComp, SampleComposite, ExemplarCasualty];

    /// <summary>Carriers whose format is in the registry before the run (seen); the others are first-seen templates.</summary>
    public static readonly string[] Seen = [SyntheticMutual.Key];
}

/// <summary>Builds internally consistent canonical loss runs (truth) from a seed. Every hard rule passes unless a stratum says otherwise.</summary>
public sealed class TruthFactory(Random random)
{
    private static readonly string[] Insureds =
    [
        "Acme Widgets (synthetic)", "Beta Tooling (synthetic)", "Gamma Logistics (synthetic)", "Delta Bakery (synthetic)", "Epsilon Plumbing (synthetic)",
        "Zeta Landscaping (synthetic)", "Eta Printing (synthetic)", "Theta Clinics (synthetic)", "Iota Staffing (synthetic)", "Kappa Foods (synthetic)",
    ];

    private static readonly string[] OtherStates = ["NV", "AZ", "OR", "WA"];
    private static readonly string[] OtherLines = ["GL", "AUTO"];

    /// <summary>
    /// Brief 23 §2 (ADR 0020 amendment): rows of another line of business are kept, not claims. The printed run lists them
    /// among its claims; the truth moves them to the period's keptRows (subtotals, which cover every line, are unchanged).
    /// </summary>
    public static void KeepOtherLines(LossRunDocument truth)
    {
        foreach (var period in truth.PolicyPeriods)
        {
            var other = period.Claims.Where(c => c.LineOfBusiness is { } line && line is not ("WC" or "EL")).ToList();
            if (other.Count == 0)
            {
                continue;
            }

            foreach (var claim in other)
            {
                claim.KeptReason = $"line of business: {(claim.LineOfBusiness == "GL" ? "general liability" : "auto")} (line \"{claim.LineOfBusiness}\")";
            }

            period.Claims = [.. period.Claims.Except(other)];
            period.KeptRows = other;
        }
    }

    public LossRunDocument Create(CarrierProfile carrier, string documentId, IReadOnlyCollection<string> strata, int minClaims = 5, int maxClaims = 60)
    {
        var firstYear = 2021 + random.Next(0, 2);
        var years = new List<int> { firstYear, firstYear + 1, firstYear + 2 };
        var lastEffective = new DateOnly(years[^1], carrier.PolicyMonth, carrier.PolicyDay);
        var valuation = strata.Contains(Strata.PartialRun)
            ? lastEffective.AddMonths(random.Next(4, 9)).AddDays(random.Next(0, 20))
            : lastEffective.AddYears(1).AddDays(random.Next(20, 120));
        if (strata.Contains(Strata.MissingYear))
        {
            years.RemoveAt(random.Next(0, 2)); // the oldest or the middle year is not on the run
        }

        var document = new LossRunDocument
        {
            DocumentId = documentId,
            CarrierName = carrier.Name,
            InsuredName = Insureds[random.Next(Insureds.Length)],
            LineOfBusiness = "WC",
            ValuationDate = valuation,
            ReportRunDate = valuation.AddDays(random.Next(1, 6)),
            NoLossesStatement = false,
            ExpenseTreatment = ExpenseTreatment.Separate,
        };

        // Policies: one per year; multi-policy splits one year into two policy numbers (same dates).
        var policies = new List<(string Number, DateOnly Effective, DateOnly Expiration)>();
        foreach (var year in years)
        {
            var effective = new DateOnly(year, carrier.PolicyMonth, carrier.PolicyDay);
            policies.Add((carrier.PolicyNumber(year, 1), effective, effective.AddYears(1)));
            if (strata.Contains(Strata.MultiPolicy) && year == years[^1])
            {
                policies.Add((carrier.PolicyNumber(year, 2), effective, effective.AddYears(1)));
            }
        }

        var summaryOnly = strata.Contains(Strata.SummaryOnlyPeriod) ? random.Next(0, policies.Count) : -1;
        var hidden = new Dictionary<PolicyPeriod, List<Claim>>();
        var claimCount = random.Next(minClaims, maxClaims + 1);
        var perPolicy = Distribute(claimCount, policies.Count, summaryOnly);
        var sequence = 1;
        var claimant = random.Next(10, 90);
        for (var p = 0; p < policies.Count; p++)
        {
            var (number, effective, expiration) = policies[p];
            var period = new PolicyPeriod
            {
                PolicyNumber = number, EffectiveDate = effective, ExpirationDate = expiration,
                DetailLevel = p == summaryOnly ? DetailLevel.SummaryOnly : DetailLevel.Claims,
            };
            for (var i = 0; i < perPolicy[p]; i++)
            {
                period.Claims.Add(NewClaim(carrier, effective, expiration, valuation, sequence++, claimant++, strata));
            }

            // A summary-only period prints totals but no claim rows: its claims exist (they make the subtotals and the
            // grand total) but are not in the truth's claim list (corpus v2, 08 Answers 6).
            if (p == summaryOnly)
            {
                hidden[period] = [.. Enumerable.Range(0, random.Next(3, 9)).Select(_ => NewClaim(carrier, effective, expiration, valuation, sequence++, claimant++, strata))];
            }

            document.PolicyPeriods.Add(period);
        }

        var all = document.PolicyPeriods.SelectMany(p => p.Claims).ToList();
        if (strata.Contains(Strata.Reopened) && !all.Any(c => c.Status == ClaimStatus.Reopened) && all.Count > 0)
        {
            MakeReopened(all[random.Next(all.Count)], valuation);
        }

        if (strata.Contains(Strata.CatOutlier) && all.Count > 0)
        {
            var big = all[random.Next(all.Count)];
            big.ClaimType = ClaimType.Indemnity;
            big.Indemnity = Triplet(Money(380_000, 520_000), big.Status == ClaimStatus.Closed ? 0 : Money(120_000, 250_000));
            big.Medical = Triplet(Money(90_000, 180_000), big.Status == ClaimStatus.Closed ? 0 : Money(20_000, 60_000));
            big.Total = Sum(big.Indemnity, big.Medical, big.Expense!);
        }

        foreach (var period in document.PolicyPeriods)
        {
            period.Subtotals = Totalize([.. period.Claims, .. hidden.GetValueOrDefault(period) ?? []]);
        }

        document.ReportTotals = Totalize([.. all, .. hidden.Values.SelectMany(h => h)]);
        return document;
    }

    private Claim NewClaim(CarrierProfile carrier, DateOnly effective, DateOnly expiration, DateOnly valuation, int sequence, int claimant, IReadOnlyCollection<string> strata)
    {
        var lastDay = (expiration < valuation ? expiration : valuation).AddDays(-1);
        var span = Math.Max(1, lastDay.DayNumber - effective.DayNumber);
        var injury = effective.AddDays(random.Next(0, span));
        var reported = injury.AddDays(random.NextDouble() < 0.1 ? random.Next(31, 90) : random.Next(0, 15));
        if (reported >= valuation)
        {
            reported = valuation.AddDays(-1);
        }

        if (reported < injury)
        {
            reported = injury;
        }

        var closedOnly = strata.Contains(Strata.ClosedOnly);
        var roll = random.NextDouble();
        var status = closedOnly ? ClaimStatus.Closed : roll < 0.62 ? ClaimStatus.Closed : roll < 0.95 ? ClaimStatus.Open : ClaimStatus.Reopened;
        var medicalOnly = random.NextDouble() < 0.6;
        var claim = new Claim
        {
            ClaimNumber = carrier.ClaimNumber(injury.Year, sequence),
            ClaimantName = $"{carrier.ClaimantStem} {claimant}",
            DateOfInjury = injury,
            DateReported = reported,
            Status = status,
            ClaimType = medicalOnly ? ClaimType.MedicalOnly : ClaimType.Indemnity,
            JurisdictionState = strata.Contains(Strata.NonCa) && random.NextDouble() < 0.25 ? OtherStates[random.Next(OtherStates.Length)] : "CA",
            LineOfBusiness = strata.Contains(Strata.NonWcRows) && random.NextDouble() < 0.15 ? OtherLines[random.Next(OtherLines.Length)] : "WC",
        };

        var open = status != ClaimStatus.Closed;
        claim.Indemnity = medicalOnly ? Triplet(0, 0) : Triplet(Money(1_000, 90_000), open ? Money(500, 60_000) : 0);
        claim.Medical = Triplet(Money(150, 40_000), open ? Money(100, 25_000) : 0);
        claim.Expense = Triplet(random.NextDouble() < 0.4 ? Money(100, 12_000) : 0, 0);
        claim.Total = Sum(claim.Indemnity, claim.Medical, claim.Expense);
        if (status == ClaimStatus.Closed)
        {
            claim.DateClosed = ClosedDate(reported, valuation);
        }
        else if (status == ClaimStatus.Reopened)
        {
            MakeReopened(claim, valuation);
        }

        claim.RawStatus = status.ToString();
        return claim;
    }

    private void MakeReopened(Claim claim, DateOnly valuation)
    {
        claim.Status = ClaimStatus.Reopened;
        claim.RawStatus = "Reopened";
        claim.DateClosed ??= ClosedDate(claim.DateReported!.Value, valuation);
        if ((claim.Medical?.Reserve ?? 0) == 0)
        {
            claim.Medical = Triplet(claim.Medical?.Paid ?? Money(150, 40_000), Money(500, 15_000));
            claim.Total = Sum(claim.Indemnity!, claim.Medical, claim.Expense!);
        }
    }

    private DateOnly ClosedDate(DateOnly reported, DateOnly valuation)
    {
        var span = valuation.DayNumber - reported.DayNumber - 1;
        return span < 1 ? reported : reported.AddDays(random.Next(1, span + 1));
    }

    private List<int> Distribute(int total, int buckets, int empty)
    {
        var counts = Enumerable.Repeat(0, buckets).ToList();
        var targets = Enumerable.Range(0, buckets).Where(b => b != empty).ToList();
        for (var i = 0; i < total; i++)
        {
            counts[targets[random.Next(targets.Count)]]++;
        }

        // Every non-empty period keeps at least one claim so period boundaries stay observable.
        foreach (var b in targets.Where(b => counts[b] == 0))
        {
            var donor = targets.MaxBy(t => counts[t]);
            if (counts[donor] > 1)
            {
                counts[donor]--;
                counts[b]++;
            }
        }

        return counts;
    }

    private decimal Money(int min, int max) => Math.Round(random.Next(min, max) + random.Next(0, 100) / 100m, 2);

    private static MoneyTriplet Triplet(decimal paid, decimal reserve) => new(paid, reserve, paid + reserve);

    private static MoneyTriplet Sum(params MoneyTriplet[] parts) =>
        new(parts.Sum(p => p.Paid ?? 0), parts.Sum(p => p.Reserve ?? 0), parts.Sum(p => p.Incurred ?? 0));

    public static Totals Totalize(IReadOnlyCollection<Claim> claims) => new()
    {
        ClaimCount = claims.Count,
        OpenCount = claims.Count(c => c.Status is ClaimStatus.Open or ClaimStatus.Reopened),
        ClosedCount = claims.Count(c => c.Status == ClaimStatus.Closed),
        Indemnity = Sum([.. claims.Select(c => c.Indemnity!)]),
        Medical = Sum([.. claims.Select(c => c.Medical!)]),
        Expense = Sum([.. claims.Select(c => c.Expense!)]),
        Total = Sum([.. claims.Select(c => c.Total!)]),
    };

    // ------------------------------------------------------------------ corpus v3 (brief 13; 10 and 12 Answers)

    private static readonly string[] Natures = ["10", "49", "52", "04", "07"];          // contusion, sprain, strain, burn, concussion
    private static readonly string[] CtNatures = ["78", "80"];                           // carpal tunnel, all other cumulative injury
    private static readonly string[] Causes = ["29", "31", "53", "56", "78", "80"];      // falls, twisting, lifting, machine, object handled
    private static readonly string[] CtCauses = ["97", "98"];                            // repetitive motion, cumulative NOC
    private static readonly string[] Bodies = ["31", "38", "42", "53", "56"];            // upper arm, shoulder, lower back, knee, foot
    private static readonly string[] Descriptions =
        ["Strained lower back lifting cartons", "Slipped on wet floor in loading dock", "Hand caught in press guard", "Struck by falling pallet", "Twisted knee on stairs"];

    /// <summary>
    /// The v3 claim fields, from a random stream of their own so the v2 corpus (claims, amounts, PDFs) is unchanged: WCIO
    /// nature/cause/body codes, ~12 % cumulative-trauma claims (CT cause and nature codes, "cumulative trauma" text), ~10 %
    /// litigated, subrogation recoveries on ~6 % of indemnity claims, and the claimant's last day worked on ~30 % of indemnity
    /// claims (some CT claims filed after it: post-termination CT). Only layouts that print a field are scored on it.
    /// </summary>
    public static void EnrichV3(LossRunDocument document, Random rng)
    {
        foreach (var claim in document.PolicyPeriods.SelectMany(p => p.Claims))
        {
            var ct = rng.NextDouble() < 0.12;
            claim.CumulativeTrauma = ct;
            claim.NatureOfInjury = new CodedValue { Code = ct ? CtNatures[rng.Next(CtNatures.Length)] : Natures[rng.Next(Natures.Length)], CodeSystem = "WCIO-Nature" };
            claim.CauseOfInjury = new CodedValue { Code = ct ? CtCauses[rng.Next(CtCauses.Length)] : Causes[rng.Next(Causes.Length)], CodeSystem = "WCIO-Cause" };
            claim.PartOfBody = new CodedValue { Code = Bodies[rng.Next(Bodies.Length)], CodeSystem = "WCIO-PartOfBody" };
            claim.AccidentDescription = ct ? "Cumulative trauma - repetitive keyboard and assembly work" : Descriptions[rng.Next(Descriptions.Length)];
            claim.LitigationFlag = rng.NextDouble() < 0.10;
            var indemnity = claim.ClaimType == ClaimType.Indemnity;
            if (indemnity && rng.NextDouble() < 0.06 && (claim.Indemnity?.Paid ?? 0) > 1_000)
            {
                claim.RecoverySubrogation = Math.Round(claim.Indemnity!.Paid!.Value * (decimal)(0.1 + rng.NextDouble() * 0.4), 2);
            }

            if (claim.DateOfInjury is { } doi && (indemnity && rng.NextDouble() < 0.30 || ct && rng.NextDouble() < 0.5))
            {
                // Post-termination CT: the claim is filed after the last day worked.
                claim.LastDayWorked = ct && rng.NextDouble() < 0.6 ? doi.AddDays(-rng.Next(5, 90)) : doi.AddDays(rng.Next(0, 3));
            }
        }
    }

    /// <summary>Submission context the broker sends with the loss runs (not printed on them): payroll and FTE per policy year.</summary>
    public static SubmissionTruth Context(LossRunDocument document, Random rng)
    {
        var years = document.PolicyPeriods.Select(p => p.EffectiveDate!.Value.Year).Distinct().Order().ToList();
        var fte = rng.Next(20, 400);
        return new SubmissionTruth(
            years.ToDictionary(y => y, y => (long)(fte * rng.Next(42_000, 78_000)) * 100),
            years.ToDictionary(y => y, y => (long)fte + rng.Next(-5, 6)));
    }

    public static string Date(DateOnly? d) => d?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) ?? "";

    public static string Money(decimal? m) => m?.ToString("#,##0.00", CultureInfo.InvariantCulture) ?? "";
}
