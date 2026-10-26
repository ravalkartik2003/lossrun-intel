using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LossRun.Analytics;

/// <summary>One computed metric: an integer at the metric's scale (null when undefined), an optional breakdown, lineage.</summary>
/// <param name="Contributors">Claim ids the metric reads; null = every included claim.</param>
/// <param name="Overrides">Ids of overrides on contributing claims that changed one of the metric's inputs.</param>
/// <param name="NotMeasurable">Why <paramref name="Value"/> is null (an input the submission or the carrier did not supply, or no contributing claims).</param>
public sealed record MetricResult(string Name, string Unit, int Scale, long? Value, IReadOnlyList<(string Key, long? Value)>? Breakdown,
    IReadOnlyList<string>? Contributors, IReadOnlyList<string> Overrides, string? NotMeasurable = null);

public sealed record PeriodAggregate(string? PolicyNumber, int? PolicyYear, int ClaimCount, int OpenCount, long[] Indemnity, long[] Medical, long[] Expense, long[] Total);

/// <summary>A feature snapshot (brief 10 §2): every catalog metric for one account under one view and scenario.</summary>
public sealed record FeatureSnapshot(string CatalogVersion, string View, string? QuoteId, string? ValuationDate, Scenario Scenario, string InputsHash,
    int InputClaims, int NonWcExcluded, int ScenarioExcluded, int Included, IReadOnlyList<MetricResult> Metrics, IReadOnlyList<PeriodAggregate> PerPeriod,
    IReadOnlyList<(string ClaimId, IReadOnlyList<string> Flags)> PerClaimFlags)
{
    public MetricResult this[string name] => Metrics.First(m => m.Name == name);

    public string ToJson() => AnalyticsJson.WriteSnapshot(this);
}

/// <summary>
/// Computes every catalog metric for an account (brief 10 §2–3). Pure and deterministic: integer arithmetic only, fixed
/// orderings, no clock. The TypeScript twin (src/web/packages/metrics/src/engine.ts) follows this file line for line; the
/// golden tests assert byte-identical snapshots.
/// </summary>
public static partial class FeatureEngine
{
    private sealed class Row
    {
        public required AnalyticsClaim Claim { get; init; }
        public long IndPaid, IndRes, MedPaid, MedRes, ExpPaid, ExpRes;
        public bool Ct, PostTermination, NonCA, Open;
        public int? Doi, Reported, Closed, Ldw;

        public long Paid => IndPaid + MedPaid + ExpPaid;
        public long Reserve => IndRes + MedRes + ExpRes;
        public long Incurred => Paid + Reserve;
    }

    public static FeatureSnapshot Compute(AnalyticsInput input, Scenario? scenario = null, string view = "adjusted", Catalog? catalog = null)
    {
        catalog ??= Catalog.Embedded;
        scenario ??= new Scenario();
        var lastDayWorked = IntMath.Day(input.Context.LastDayWorked);
        var valuation = IntMath.Day(input.Context.ValuationDate);

        // 1. Non-WC rows are always excluded.
        var nonWc = input.Claims.Where(c => c.LineOfBusiness is { } lob && !string.Equals(lob.Trim(), "WC", StringComparison.OrdinalIgnoreCase)).ToList();
        var nonWcIds = nonWc.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var rows = input.Claims.Where(c => !nonWcIds.Contains(c.Id)).Select(c => ToRow(c, lastDayWorked)).ToList();

        // 2. Scenario exclusions (catalog.yaml scenario.order), on as-reported amounts.
        var excludedIds = scenario.ExcludeClaimIds.ToHashSet(StringComparer.Ordinal);
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (excludedIds.Contains(r.Claim.Id) || !scenario.IncludeNonCA && r.NonCA || !scenario.IncludeCT && r.Ct
                || scenario.ExcludeCatastrophicAboveCents is { } cat && r.Incurred > cat)
            {
                excluded.Add(r.Claim.Id);
            }
        }

        var included = rows.Where(r => !excluded.Contains(r.Claim.Id)).ToList();

        // 3. Amount adjustments: trend, reserve factor, cap.
        foreach (var r in included)
        {
            if (r.Claim.PolicyYear is { } year && scenario.TrendFactorBpsByYear.TryGetValue(year, out var trend))
            {
                (r.IndPaid, r.IndRes, r.MedPaid, r.MedRes, r.ExpPaid, r.ExpRes) = (IntMath.MulBps(r.IndPaid, trend), IntMath.MulBps(r.IndRes, trend),
                    IntMath.MulBps(r.MedPaid, trend), IntMath.MulBps(r.MedRes, trend), IntMath.MulBps(r.ExpPaid, trend), IntMath.MulBps(r.ExpRes, trend));
            }

            if (scenario.ReserveFactorBps != 0)
            {
                var f = 10_000 + scenario.ReserveFactorBps;
                (r.IndRes, r.MedRes, r.ExpRes) = (IntMath.MulBps(r.IndRes, f), IntMath.MulBps(r.MedRes, f), IntMath.MulBps(r.ExpRes, f));
            }

            if (scenario.CapPerClaimCents is { } cap && r.Incurred > cap)
            {
                var remaining = cap;
                long Take(long amount)
                {
                    var taken = Math.Min(amount, Math.Max(remaining, 0));
                    remaining -= taken;
                    return taken;
                }

                (r.IndPaid, r.MedPaid, r.ExpPaid) = (Take(r.IndPaid), Take(r.MedPaid), Take(r.ExpPaid));
                (r.IndRes, r.MedRes, r.ExpRes) = (Take(r.IndRes), Take(r.MedRes), Take(r.ExpRes));
            }
        }

        // 4. Metrics, in catalog order.
        var ctx = new Ctx(included, nonWc, input, valuation, lastDayWorked);
        var metrics = catalog.Metrics.Select(m => Metric(m, ctx)).ToList();

        // 5. Per period and per-claim flags.
        var periods = included
            .GroupBy(r => (r.Claim.PolicyYear, r.Claim.PolicyNumber))
            .OrderBy(g => g.Key.PolicyYear is null ? 1 : 0).ThenBy(g => g.Key.PolicyYear ?? 0).ThenBy(g => g.Key.PolicyNumber ?? "", StringComparer.Ordinal)
            .Select(g => new PeriodAggregate(g.Key.PolicyNumber, g.Key.PolicyYear, g.Count(), g.Count(r => r.Open),
                Triplet(g.Sum(r => r.IndPaid), g.Sum(r => r.IndRes)), Triplet(g.Sum(r => r.MedPaid), g.Sum(r => r.MedRes)),
                Triplet(g.Sum(r => r.ExpPaid), g.Sum(r => r.ExpRes)), Triplet(g.Sum(r => r.Paid), g.Sum(r => r.Reserve))))
            .ToList();

        var largeThreshold = catalog["large_loss_count"].MoneyParam("threshold", 100_000);
        var byId = rows.ToDictionary(r => r.Claim.Id, StringComparer.Ordinal);
        var flags = input.Claims.Select(c => c.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(id =>
        {
            var list = new List<string>();
            if (nonWcIds.Contains(id))
            {
                list.Add("nonWc");
            }
            else
            {
                var r = byId[id];
                if (excluded.Contains(id)) { list.Add("excluded"); }
                if (r.Ct) { list.Add("ct"); }
                if (r.PostTermination) { list.Add("postTermination"); }
                if (r.Incurred >= largeThreshold) { list.Add("large"); }
                if (r.Claim.Litigation == true) { list.Add("litigated"); }
                if (r.NonCA) { list.Add("nonCA"); }
                if (r.Open) { list.Add("open"); }
            }

            return (ClaimId: id, Flags: (IReadOnlyList<string>)list);
        }).Where(f => f.Flags.Count > 0).ToList();

        return new FeatureSnapshot(catalog.Version, view, input.Context.QuoteId, input.Context.ValuationDate, scenario, InputsHash(input),
            input.Claims.Count, nonWc.Count, excluded.Count, included.Count, metrics, periods, flags);
    }

    /// <summary>Lower-case hex SHA-256 of the canonical input JSON (the twin computes the same with its own SHA-256).</summary>
    public static string InputsHash(AnalyticsInput input) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(AnalyticsJson.WriteInput(input))));

    private static long[] Triplet(long paid, long reserve) => [paid, reserve, paid + reserve];

    private static Row ToRow(AnalyticsClaim c, int? lastDayWorked)
    {
        var r = new Row
        {
            Claim = c,
            IndPaid = c.IndemnityPaidCents, IndRes = c.IndemnityReserveCents,
            MedPaid = c.MedicalPaidCents, MedRes = c.MedicalReserveCents,
            ExpPaid = c.ExpensePaidCents, ExpRes = c.ExpenseReserveCents,
            Doi = IntMath.Day(c.DateOfInjury), Reported = IntMath.Day(c.DateReported), Closed = IntMath.Day(c.DateClosed),
            Ct = IsCumulativeTrauma(c),
            NonCA = c.Jurisdiction is { } j && j.Trim().Length > 0 && !string.Equals(j.Trim(), "CA", StringComparison.OrdinalIgnoreCase),
            Open = c.Status is "open" or "reopened",
        };
        r.Ldw = IntMath.Day(c.LastDayWorked) ?? lastDayWorked;
        r.PostTermination = r.Ct && r.Ldw is { } ldw && r.Doi is { } doi && doi >= ldw;
        return r;
    }

    /// <summary>catalog.yaml conventions.ct.</summary>
    public static bool IsCumulativeTrauma(AnalyticsClaim c) =>
        c.CumulativeTrauma == true
        || Code(c.NatureCode) is >= 60 and <= 83
        || Code(c.CauseCode) is 97 or 98
        || Code(c.TypeOfLossCode) is 2 or 3
        || c.Description is { } d && CtText().IsMatch(d);

    private static int? Code(string? code)
    {
        var s = code?.Trim();
        return s is { Length: > 0 and <= 6 } && s.All(char.IsAsciiDigit) ? int.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    [GeneratedRegex("(?<![A-Za-z0-9_])(cumulative|ct|repetitive)(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CtText();

    // ------------------------------------------------------------------ metrics

    private sealed record Ctx(List<Row> Rows, List<AnalyticsClaim> NonWc, AnalyticsInput Input, int? Valuation, int? LastDayWorked);

    private static MetricResult Metric(MetricDef m, Ctx x)
    {
        var rows = x.Rows;
        var all = rows;
        var lostTime = rows.Where(r => r.Claim.ClaimType == "indemnity").ToList();
        long? value;
        List<(string, long?)>? breakdown = null;
        List<Row> contributors = all;
        IReadOnlyList<string>? explicitContributors = null;

        switch (m.Name)
        {
            case "claim_count":
                value = rows.Count;
                break;
            case "claim_count_with_financials":
                contributors = [.. rows.Where(r => r.Claim.ReportOnly != true)];
                value = contributors.Count;
                break;
            case "open_claim_count":
                contributors = [.. rows.Where(r => r.Open)];
                value = contributors.Count;
                break;
            case "closed_claim_count":
                contributors = [.. rows.Where(r => r.Claim.Status == "closed")];
                value = contributors.Count;
                break;
            case "reopened_claim_count":
                contributors = [.. rows.Where(r => r.Claim.Status == "reopened")];
                value = contributors.Count;
                break;
            case "medical_only_count":
                contributors = [.. rows.Where(r => r.Claim.ClaimType == "medicalOnly")];
                value = contributors.Count;
                break;
            case "lost_time_count":
                contributors = lostTime;
                value = lostTime.Count;
                break;
            case "lost_time_share":
                value = Ratio(lostTime.Count, rows.Count);
                break;
            case "paid_total":
                value = rows.Sum(r => r.Paid);
                breakdown = [("indemnity", rows.Sum(r => r.IndPaid)), ("medical", rows.Sum(r => r.MedPaid)), ("expense", rows.Sum(r => r.ExpPaid))];
                break;
            case "reserve_total":
                value = rows.Sum(r => r.Reserve);
                breakdown = [("indemnity", rows.Sum(r => r.IndRes)), ("medical", rows.Sum(r => r.MedRes)), ("expense", rows.Sum(r => r.ExpRes))];
                break;
            case "incurred_total":
                value = rows.Sum(r => r.Incurred);
                breakdown = [("indemnity", rows.Sum(r => r.IndPaid + r.IndRes)), ("medical", rows.Sum(r => r.MedPaid + r.MedRes)), ("expense", rows.Sum(r => r.ExpPaid + r.ExpRes))];
                break;
            case "frequency_per_100_payroll":
                (value, breakdown) = Frequency(rows, x.Input.Context.PayrollCentsByYear, 10_000_000_000L);
                break;
            case "frequency_per_100_fte":
                (value, breakdown) = Frequency(rows, x.Input.Context.FteByYear, 100_000_000L);
                break;
            case "claim_count_by_policy_year":
                breakdown = [.. ByYear(rows).Select(g => (g.Key, (long?)g.Rows.Count))];
                value = breakdown.Count;
                break;
            case "severity_all":
                value = IntMath.RoundDiv(rows.Sum(r => r.Incurred), rows.Count);
                break;
            case "severity_lost_time":
                contributors = lostTime;
                value = IntMath.RoundDiv(lostTime.Sum(r => r.Incurred), lostTime.Count);
                break;
            case "paid_to_incurred":
                value = Ratio(rows.Sum(r => r.Paid), rows.Sum(r => r.Incurred));
                break;
            case "large_loss_count":
            {
                var threshold = m.MoneyParam("threshold", 100_000);
                contributors = [.. rows.Where(r => r.Incurred >= threshold)];
                value = contributors.Count;
                breakdown = [.. m.MoneyParams("thresholds").Select(t => (Dollars(t), (long?)rows.Count(r => r.Incurred >= t)))];
                break;
            }

            case "large_loss_share":
            {
                var threshold = m.MoneyParam("threshold", 100_000);
                var total = rows.Sum(r => r.Incurred);
                value = Ratio(rows.Where(r => r.Incurred >= threshold).Sum(r => r.Incurred), total);
                breakdown = [.. m.MoneyParams("thresholds").Select(t => (Dollars(t), Ratio(rows.Where(r => r.Incurred >= t).Sum(r => r.Incurred), total)))];
                break;
            }

            case "capped_incurred":
            {
                var cap = m.MoneyParam("cap", 250_000);
                value = rows.Sum(r => Math.Min(r.Incurred, cap));
                breakdown = [.. m.MoneyParams("caps").Select(c => (Dollars(c), (long?)rows.Sum(r => Math.Min(r.Incurred, c))))];
                break;
            }

            case "loss_drivers_cause_incurred":
                (value, breakdown) = Drivers(rows, r => r.Claim.CauseCode, byIncurred: true, m.IntParam("top", 5));
                break;
            case "loss_drivers_cause_count":
                (value, breakdown) = Drivers(rows, r => r.Claim.CauseCode, byIncurred: false, m.IntParam("top", 5));
                break;
            case "loss_drivers_body_part_incurred":
                (value, breakdown) = Drivers(rows, r => r.Claim.BodyPartCode, byIncurred: true, m.IntParam("top", 5));
                break;
            case "loss_drivers_body_part_count":
                (value, breakdown) = Drivers(rows, r => r.Claim.BodyPartCode, byIncurred: false, m.IntParam("top", 5));
                break;
            case "loss_drivers_nature_incurred":
                (value, breakdown) = Drivers(rows, r => r.Claim.NatureCode, byIncurred: true, m.IntParam("top", 5));
                break;
            case "loss_drivers_nature_count":
                (value, breakdown) = Drivers(rows, r => r.Claim.NatureCode, byIncurred: false, m.IntParam("top", 5));
                break;
            case "litigation_rate":
                contributors = lostTime;
                value = rows.Any(r => r.Claim.Litigation is not null) ? Ratio(lostTime.Count(r => r.Claim.Litigation == true), lostTime.Count) : null;
                break;
            case "ct_share":
                value = Ratio(rows.Count(r => r.Ct), rows.Count);
                break;
            case "ct_indemnity_share":
                contributors = lostTime;
                value = Ratio(lostTime.Count(r => r.Ct), lostTime.Count);
                break;
            case "post_termination_ct_count":
                contributors = [.. rows.Where(r => r.PostTermination)];
                value = rows.Any(r => r.Ldw is not null) ? contributors.Count : null;
                break;
            case "subrogation_recovery_rate":
            {
                contributors = [.. rows.Where(r => r.Claim.RecoveryCents is > 0)];
                value = contributors.Count == 0 ? null : Ratio(contributors.Sum(r => r.Claim.RecoveryCents!.Value), contributors.Sum(r => r.Incurred));
                break;
            }

            case "closure_median_days":
                contributors = [.. rows.Where(r => r.Claim.Status == "closed" && r.Doi is not null && r.Closed is not null)];
                value = IntMath.MedianTenths([.. contributors.Select(r => (long)(r.Closed!.Value - r.Doi!.Value))]);
                break;
            case "open_claim_age":
            {
                contributors = [.. rows.Where(r => r.Open && r.Doi is not null)];
                if (x.Valuation is { } v)
                {
                    var ages = contributors.Select(r => v - r.Doi!.Value).ToList();
                    breakdown = [("<1y", ages.Count(a => a < 365)), ("1-2y", ages.Count(a => a >= 365 && a < 730)), ("2-3y", ages.Count(a => a >= 730 && a < 1095)), ("3y+", ages.Count(a => a >= 1095))];
                    value = contributors.Count;
                }
                else
                {
                    value = null;
                }

                break;
            }

            case "reporting_lag_median_days":
                contributors = [.. rows.Where(r => r.Doi is not null && r.Reported is not null)];
                value = IntMath.MedianTenths(Lags(contributors));
                break;
            case "reporting_lag_p90_days":
                contributors = [.. rows.Where(r => r.Doi is not null && r.Reported is not null)];
                value = IntMath.PercentileTenths(Lags(contributors), m.IntParam("percentile", 90));
                break;
            case "reporting_lag_over_30_share":
            {
                contributors = [.. rows.Where(r => r.Doi is not null && r.Reported is not null)];
                var days = m.IntParam("days", 30);
                value = Ratio(Lags(contributors).Count(l => l > days), contributors.Count);
                break;
            }

            case "incurred_by_policy_year":
                value = rows.Sum(r => r.Incurred);
                breakdown = [.. ByYear(rows).Select(g => (g.Key, (long?)g.Rows.Sum(r => r.Incurred)))];
                break;
            case "paid_by_policy_year":
                value = rows.Sum(r => r.Paid);
                breakdown = [.. ByYear(rows).Select(g => (g.Key, (long?)g.Rows.Sum(r => r.Paid)))];
                break;
            case "severity_by_policy_year":
                value = IntMath.RoundDiv(rows.Sum(r => r.Incurred), rows.Count);
                breakdown = [.. ByYear(rows).Select(g => (g.Key, IntMath.RoundDiv(g.Rows.Sum(r => r.Incurred), g.Rows.Count)))];
                break;
            case "paid_to_incurred_by_policy_year":
                value = Ratio(rows.Sum(r => r.Paid), rows.Sum(r => r.Incurred));
                breakdown = [.. ByYear(rows).Select(g => (g.Key, Ratio(g.Rows.Sum(r => r.Paid), g.Rows.Sum(r => r.Incurred))))];
                break;
            case "non_ca_share":
                value = Ratio(rows.Count(r => r.NonCA), rows.Count);
                breakdown = [.. rows.GroupBy(r => r.Claim.Jurisdiction is { } j && j.Trim().Length > 0 ? j.Trim().ToUpperInvariant() : "unknown")
                    .Select(g => (g.Key, (long)g.Count())).OrderByDescending(g => g.Item2).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => (g.Key, Ratio(g.Item2, rows.Count)))];
                break;
            case "non_wc_excluded_count":
                value = x.NonWc.Count;
                explicitContributors = [.. x.NonWc.Select(c => c.Id).Order(StringComparer.Ordinal)];
                break;
            default:
                throw new InvalidOperationException($"catalog metric '{m.Name}' has no implementation (catalog {Catalog.Embedded.Version})");
        }

        var contributorIds = explicitContributors ?? (ReferenceEquals(contributors, all) || contributors.Count == all.Count ? null
            : contributors.Select(r => r.Claim.Id).Order(StringComparer.Ordinal).ToList());
        var contributing = (contributorIds ?? all.Select(r => r.Claim.Id)).ToHashSet(StringComparer.Ordinal);
        var overrides = x.Input.Overrides.Where(o => contributing.Contains(o.ClaimId) && m.InputFields.Any(f => FieldMatches(f, o.Field)))
            .Select(o => o.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new MetricResult(m.Name, m.Unit, m.Scale, value, breakdown, contributorIds, overrides, value is null ? NotMeasurable(m.Name, x) : null);
    }

    /// <summary>Why a metric has no value (catalog.yaml conventions.notMeasurable): the missing input, else no contributing claims.</summary>
    private static string NotMeasurable(string name, Ctx x)
    {
        var rows = x.Rows;
        return name switch
        {
            "frequency_per_100_payroll" when x.Input.Context.PayrollCentsByYear is not { Count: > 0 } => "no payroll in the submission context",
            "frequency_per_100_fte" when x.Input.Context.FteByYear is not { Count: > 0 } => "no FTE count in the submission context",
            "post_termination_ct_count" => "no last day worked in the submission context or on the claims",
            "litigation_rate" when rows.All(r => r.Claim.Litigation is null) => "no litigation flag printed",
            "subrogation_recovery_rate" => "no recoveries printed",
            "loss_drivers_cause_incurred" or "loss_drivers_cause_count" => "no cause-of-injury codes printed",
            "loss_drivers_body_part_incurred" or "loss_drivers_body_part_count" => "no body-part codes printed",
            "loss_drivers_nature_incurred" or "loss_drivers_nature_count" => "no nature-of-injury codes printed",
            "open_claim_age" when x.Valuation is null => "no valuation date",
            _ => "no contributing claims",
        };
    }

    private static List<long> Lags(List<Row> rows) => [.. rows.Select(r => (long)(r.Reported!.Value - r.Doi!.Value))];

    /// <summary>numerator / denominator in millionths (catalog conventions.ratio).</summary>
    private static long? Ratio(long numerator, long denominator) => IntMath.RoundDiv((Int128)numerator * 1_000_000, denominator);

    private static string Dollars(long cents) => CanonicalWriter.FormatScaled(cents, 2);

    private static List<(string Key, List<Row> Rows)> ByYear(List<Row> rows) =>
    [
        .. rows.GroupBy(r => r.Claim.PolicyYear)
            .OrderBy(g => g.Key is null ? 1 : 0).ThenBy(g => g.Key ?? 0)
            .Select(g => (g.Key is { } y ? y.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown", g.ToList())),
    ];

    /// <summary>Claims per exposure unit over the years with exposure: claims × <paramref name="scale"/> / exposure (millionths).</summary>
    private static (long?, List<(string, long?)>?) Frequency(List<Row> rows, IReadOnlyDictionary<int, long>? exposure, long scale)
    {
        if (exposure is null || exposure.Count == 0)
        {
            return (null, null);
        }

        var years = exposure.Keys.Order().ToList();
        var claims = rows.Count(r => r.Claim.PolicyYear is { } y && exposure.ContainsKey(y));
        var value = IntMath.RoundDiv((Int128)claims * scale, years.Sum(y => exposure[y]));
        var breakdown = years.Select(y => (y.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IntMath.RoundDiv((Int128)rows.Count(r => r.Claim.PolicyYear == y) * scale, exposure[y]))).ToList();
        return (value, breakdown);
    }

    /// <summary>Top codes by incurred or count ("unknown" for claims without one); value = the top code's amount; null when no claim carries the code.</summary>
    private static (long?, List<(string, long?)>?) Drivers(List<Row> rows, Func<Row, string?> code, bool byIncurred, int top)
    {
        if (!rows.Any(r => code(r) is { Length: > 0 }))
        {
            return (null, null);
        }

        var groups = rows.GroupBy(r => code(r) is { Length: > 0 } c ? c.Trim() : "unknown")
            .Select(g => (Key: g.Key, Value: byIncurred ? g.Sum(r => r.Incurred) : g.Count()))
            .OrderByDescending(g => g.Value).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        return (groups[0].Value, [.. groups.Take(top).Select(g => (g.Key, (long?)g.Value))]);
    }

    /// <summary>Whether an override of claim field <paramref name="field"/> changes catalog input <paramref name="input"/>.</summary>
    public static bool FieldMatches(string input, string field)
    {
        if (!input.StartsWith("claim.", StringComparison.Ordinal))
        {
            return false;
        }

        var i = input["claim.".Length..];
        return i == field || field.StartsWith(i + ".", StringComparison.Ordinal)
            || (i.StartsWith("total.", StringComparison.Ordinal) || i.EndsWith(".incurred", StringComparison.Ordinal)) && IsMoney(field);
    }

    private static bool IsMoney(string field) =>
        field.StartsWith("indemnity.", StringComparison.Ordinal) || field.StartsWith("medical.", StringComparison.Ordinal)
        || field.StartsWith("expense.", StringComparison.Ordinal) || field.StartsWith("total.", StringComparison.Ordinal);
}
