using System.Globalization;
using System.Text.Json.Nodes;
using LossRun.Contracts;

namespace LossRun.Analytics;

/// <summary>One summary-only policy period (ADR 0017): what the run prints for a fiscal, report or policy year.</summary>
public sealed record PeriodSummaryRow(string DocumentId, int PeriodIndex, string? PeriodKind, string? PeriodLabel, string? PolicyNumber, string? EffectiveDate,
    string? ExpirationDate, int? ClaimCount, int? OpenCount, int? ClosedCount, decimal? Paid, decimal? Reserve, decimal? Incurred, decimal? Subrogation,
    RecoveryAmounts? Recoveries);

/// <summary>
/// ADR 0017: a summary-only loss run carries no claim rows, so the claim-level engine (<see cref="FeatureEngine"/>, and its
/// TypeScript twin) sees none of it. Its periods are read here instead, and only the metrics catalog.yaml marks
/// <c>fromSummary: yes</c> are computed from them; every other metric is not measurable for those periods. The what-if
/// scenario does not apply to period rows (it edits claims).
/// </summary>
public static class PeriodSummaries
{
    public const string NotMeasurableReason = "summary-only loss run: claim-level metric, no claim rows printed";

    /// <summary>Summary-only periods of the account's documents, in document then period order.</summary>
    public static IReadOnlyList<PeriodSummaryRow> Rows(IEnumerable<AccountDocument> documents) =>
        [.. documents.OrderBy(d => d.DocumentId, StringComparer.Ordinal).SelectMany(d => d.Current.PolicyPeriods
            .Select((p, i) => (Period: p, Index: i))
            .Where(x => x.Period.DetailLevel == DetailLevel.SummaryOnly && x.Period.Subtotals is not null)
            .Select(x => Row(d.DocumentId, x.Index, x.Period)))];

    private static PeriodSummaryRow Row(string documentId, int index, PolicyPeriod p)
    {
        var t = p.Subtotals!;
        var components = new[] { t.Indemnity, t.Medical, t.Expense };
        decimal? Sum(Func<MoneyTriplet, decimal?> part) => components.Any(c => c is not null && part(c) is not null) ? components.Sum(c => c is null ? 0 : part(c) ?? 0) : null;
        var paid = t.Total?.Paid ?? Sum(c => c.Paid);
        var reserve = t.Total?.Reserve ?? Sum(c => c.Reserve);
        var incurred = t.Total?.Incurred ?? Sum(c => c.Incurred ?? (c.Paid is null && c.Reserve is null ? null : (c.Paid ?? 0) + (c.Reserve ?? 0)));
        return new PeriodSummaryRow(documentId, index, p.PeriodKind is { } k ? JsonNamingPolicyCamel(k.ToString()) : null, p.PeriodLabel, p.PolicyNumber, Iso(p.EffectiveDate),
            Iso(p.ExpirationDate), t.ClaimCount, t.OpenCount, t.ClosedCount, paid, reserve, incurred, t.RecoverySubrogation, t.Recoveries);
    }

    /// <summary>
    /// The account-level metrics catalog.yaml marks <c>fromSummary: yes</c>, over the summary rows, as
    /// <c>{ name: { value, unit } }</c> or <c>{ name: { notMeasurable } }</c>; by-year breakdowns are keyed by the period label.
    /// </summary>
    public static JsonObject Metrics(IReadOnlyList<PeriodSummaryRow> rows, Catalog? catalog = null)
    {
        catalog ??= Catalog.Embedded;
        var result = new JsonObject();
        foreach (var metric in catalog.Json["metrics"]!.AsArray().OfType<JsonObject>())
        {
            var name = metric["name"]!.GetValue<string>();
            var fromSummary = metric["fromSummary"]?.GetValue<string>() == "yes";
            result[name] = !fromSummary ? new JsonObject { ["notMeasurable"] = NotMeasurableReason } : Value(name, metric["unit"]!.GetValue<string>(), rows);
        }

        return result;
    }

    private static JsonObject Value(string name, string unit, IReadOnlyList<PeriodSummaryRow> rows)
    {
        static decimal? Total(IEnumerable<decimal?> values) => values.Any(v => v is not null) ? values.Sum(v => v ?? 0) : null;
        static int? Count(IEnumerable<int?> values) => values.Any(v => v is not null) ? values.Sum(v => v ?? 0) : null;
        var count = Count(rows.Select(r => r.ClaimCount));
        var paid = Total(rows.Select(r => r.Paid));
        var incurred = Total(rows.Select(r => r.Incurred));
        JsonNode? value = name switch
        {
            "claim_count" => count,
            "open_claim_count" => Count(rows.Select(r => r.OpenCount)),
            "closed_claim_count" => Count(rows.Select(r => r.ClosedCount)),
            "paid_total" => paid,
            "reserve_total" => Total(rows.Select(r => r.Reserve)),
            "incurred_total" => incurred,
            "severity_all" => count is > 0 && incurred is { } i ? Math.Round(i / count.Value, 2, MidpointRounding.AwayFromZero) : null,
            "paid_to_incurred" => incurred is > 0 && paid is { } p ? Math.Round(p / incurred.Value, 6, MidpointRounding.AwayFromZero) : null,
            "claim_count_by_policy_year" => ByPeriod(rows, r => r.ClaimCount),
            "incurred_by_policy_year" => ByPeriod(rows, r => r.Incurred),
            "paid_by_policy_year" => ByPeriod(rows, r => r.Paid),
            _ => throw new InvalidOperationException($"catalog.yaml marks {name} fromSummary: yes but PeriodSummaries does not compute it"),
        };
        return value is null ? new JsonObject { ["notMeasurable"] = "no period row prints the input" } : new JsonObject { ["value"] = value, ["unit"] = unit };
    }

    private static JsonObject? ByPeriod<T>(IReadOnlyList<PeriodSummaryRow> rows, Func<PeriodSummaryRow, T?> value) where T : struct
    {
        var map = new JsonObject();
        foreach (var r in rows)
        {
            var key = r.PeriodLabel ?? r.PolicyNumber ?? r.EffectiveDate ?? $"{r.DocumentId}/{r.PeriodIndex}";
            if (value(r) is { } v && !map.ContainsKey(key))
            {
                map[key] = JsonValue.Create(v);
            }
        }

        return map.Count == 0 ? null : map;
    }

    /// <summary>Period rows as JSON for the quote payload and the UI (lineage paths point at each period's subtotals).</summary>
    public static JsonArray ToJson(IReadOnlyList<PeriodSummaryRow> rows) =>
        [.. rows.Select(r => (JsonNode)new JsonObject
        {
            ["documentId"] = r.DocumentId, ["periodIndex"] = r.PeriodIndex, ["periodKind"] = r.PeriodKind, ["periodLabel"] = r.PeriodLabel, ["policyNumber"] = r.PolicyNumber,
            ["effectiveDate"] = r.EffectiveDate, ["expirationDate"] = r.ExpirationDate, ["claimCount"] = r.ClaimCount, ["openCount"] = r.OpenCount,
            ["closedCount"] = r.ClosedCount, ["paid"] = r.Paid, ["reserve"] = r.Reserve, ["incurred"] = r.Incurred, ["recoverySubrogation"] = r.Subrogation,
            ["recoveries"] = r.Recoveries is { } rec
                ? new JsonObject { ["deductible"] = rec.Deductible, ["reinsurance"] = rec.Reinsurance, ["sif"] = rec.Sif, ["other"] = rec.Other }
                : null,
            ["lineagePath"] = $"policyPeriods[{r.PeriodIndex}].subtotals",
        })];

    private static string? Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string JsonNamingPolicyCamel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
