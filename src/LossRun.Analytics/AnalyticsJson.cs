using System.Globalization;
using System.Text.Json;

namespace LossRun.Analytics;

/// <summary>
/// Canonical JSON for the engine's input (the per-account claims payload the client recomputes from) and output (feature
/// snapshots). Both sides of the parity test read and write exactly these shapes; see src/web/packages/metrics/src/json.ts.
/// </summary>
public static class AnalyticsJson
{
    // ------------------------------------------------------------------ input

    public static string WriteInput(AnalyticsInput input)
    {
        var w = new CanonicalWriter();
        w.StartObject();
        w.Name("context");
        WriteContext(w, input.Context);
        w.Name("claims").StartArray();
        foreach (var c in input.Claims)
        {
            WriteClaim(w, c);
        }

        w.EndArray();
        w.Name("overrides").StartArray();
        foreach (var o in input.Overrides)
        {
            w.StartObject().Name("id").String(o.Id).Name("claimId").String(o.ClaimId).Name("field").String(o.Field).EndObject();
        }

        w.EndArray();
        w.EndObject();
        return w.ToString();
    }

    public static void WriteClaim(CanonicalWriter w, AnalyticsClaim c)
    {
        w.StartObject()
            .Name("id").String(c.Id)
            .Name("claimNumber").String(c.ClaimNumber)
            .Name("policyNumber").String(c.PolicyNumber)
            .Name("policyYear").Scaled(c.PolicyYear, 0)
            .Name("dateOfInjury").String(c.DateOfInjury)
            .Name("dateReported").String(c.DateReported)
            .Name("dateClosed").String(c.DateClosed)
            .Name("status").String(c.Status)
            .Name("claimType").String(c.ClaimType)
            .Name("natureCode").String(c.NatureCode)
            .Name("causeCode").String(c.CauseCode)
            .Name("bodyPartCode").String(c.BodyPartCode)
            .Name("typeOfLossCode").String(c.TypeOfLossCode)
            .Name("description").String(c.Description)
            .Name("litigation").Bool(c.Litigation)
            .Name("cumulativeTrauma").Bool(c.CumulativeTrauma)
            .Name("jurisdiction").String(c.Jurisdiction)
            .Name("lineOfBusiness").String(c.LineOfBusiness)
            .Name("indemnityPaidCents").Scaled(c.IndemnityPaidCents, 0)
            .Name("indemnityReserveCents").Scaled(c.IndemnityReserveCents, 0)
            .Name("medicalPaidCents").Scaled(c.MedicalPaidCents, 0)
            .Name("medicalReserveCents").Scaled(c.MedicalReserveCents, 0)
            .Name("expensePaidCents").Scaled(c.ExpensePaidCents, 0)
            .Name("expenseReserveCents").Scaled(c.ExpenseReserveCents, 0)
            .Name("recoveryCents").Scaled(c.RecoveryCents, 0)
            .Name("lastDayWorked").String(c.LastDayWorked);
        if (c.ReportOnly is not null)
        {
            // Brief 22 Answers 4: written only when printed, so snapshots of runs without the flag are unchanged.
            w.Name("reportOnly").Bool(c.ReportOnly);
        }

        w.EndObject();
    }

    private static void WriteContext(CanonicalWriter w, AnalyticsContext c)
    {
        w.StartObject()
            .Name("quoteId").String(c.QuoteId)
            .Name("valuationDate").String(c.ValuationDate)
            .Name("payrollCentsByYear");
        WriteYearMap(w, c.PayrollCentsByYear);
        w.Name("fteByYear");
        WriteYearMap(w, c.FteByYear);
        w.Name("lastDayWorked").String(c.LastDayWorked).EndObject();
    }

    private static void WriteYearMap<T>(CanonicalWriter w, IReadOnlyDictionary<int, T>? map) where T : struct, IConvertible
    {
        if (map is null)
        {
            w.Null();
            return;
        }

        w.StartObject();
        foreach (var (year, value) in map.OrderBy(kv => kv.Key))
        {
            w.Name(year.ToString(CultureInfo.InvariantCulture)).Scaled(value.ToInt64(CultureInfo.InvariantCulture), 0);
        }

        w.EndObject();
    }

    public static AnalyticsInput ReadInput(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var ctx = root.GetProperty("context");
        var context = new AnalyticsContext(Str(ctx, "quoteId"), Str(ctx, "valuationDate"), YearMap(ctx, "payrollCentsByYear"), YearMap(ctx, "fteByYear"),
            Str(ctx, "lastDayWorked"));
        var claims = root.GetProperty("claims").EnumerateArray().Select(ReadClaim).ToList();
        var overrides = root.TryGetProperty("overrides", out var o) && o.ValueKind == JsonValueKind.Array
            ? o.EnumerateArray().Select(x => new OverrideRef(Str(x, "id")!, Str(x, "claimId")!, Str(x, "field")!)).ToList()
            : [];
        return new AnalyticsInput(claims, context, overrides);
    }

    public static AnalyticsClaim ReadClaim(JsonElement e) => new(
        Str(e, "id")!, Str(e, "claimNumber"), Str(e, "policyNumber"), Int(e, "policyYear"), Str(e, "dateOfInjury"), Str(e, "dateReported"),
        Str(e, "dateClosed"), Str(e, "status"), Str(e, "claimType"), Str(e, "natureCode"), Str(e, "causeCode"), Str(e, "bodyPartCode"),
        Str(e, "typeOfLossCode"), Str(e, "description"), Bool(e, "litigation"), Bool(e, "cumulativeTrauma"), Str(e, "jurisdiction"),
        Str(e, "lineOfBusiness"), Long(e, "indemnityPaidCents") ?? 0, Long(e, "indemnityReserveCents") ?? 0, Long(e, "medicalPaidCents") ?? 0,
        Long(e, "medicalReserveCents") ?? 0, Long(e, "expensePaidCents") ?? 0, Long(e, "expenseReserveCents") ?? 0, Long(e, "recoveryCents"), Str(e, "lastDayWorked"),
        Bool(e, "reportOnly"));

    // ------------------------------------------------------------------ scenario

    public static void WriteScenario(CanonicalWriter w, Scenario s)
    {
        w.StartObject().Name("excludeClaimIds").StartArray();
        foreach (var id in s.ExcludeClaimIds.Order(StringComparer.Ordinal))
        {
            w.String(id);
        }

        w.EndArray()
            .Name("includeNonCA").Bool(s.IncludeNonCA)
            .Name("includeCT").Bool(s.IncludeCT)
            .Name("excludeCatastrophicAbove").Scaled(s.ExcludeCatastrophicAboveCents, 2)
            .Name("trendFactorBpsByYear");
        WriteYearMap(w, s.TrendFactorBpsByYear);
        w.Name("reserveFactorBps").Scaled(s.ReserveFactorBps, 0)
            .Name("capPerClaim").Scaled(s.CapPerClaimCents, 2)
            .EndObject();
    }

    public static string WriteScenario(Scenario s)
    {
        var w = new CanonicalWriter();
        WriteScenario(w, s);
        return w.ToString();
    }

    /// <summary>Reads a scenario: <c>excludeCatastrophicAbove</c> and <c>capPerClaim</c> in dollars (or <c>…Cents</c>), factors in basis points.</summary>
    public static Scenario ReadScenario(JsonElement e) => new()
    {
        ExcludeClaimIds = e.TryGetProperty("excludeClaimIds", out var ids) && ids.ValueKind == JsonValueKind.Array ? [.. ids.EnumerateArray().Select(x => x.GetString()!)] : [],
        IncludeNonCA = Bool(e, "includeNonCA") ?? true,
        IncludeCT = Bool(e, "includeCT") ?? true,
        ExcludeCatastrophicAboveCents = Long(e, "excludeCatastrophicAboveCents") ?? DollarsToCents(e, "excludeCatastrophicAbove"),
        TrendFactorBpsByYear = YearMap(e, "trendFactorBpsByYear")?.ToDictionary(kv => kv.Key, kv => (int)kv.Value) ?? [],
        ReserveFactorBps = (int)(Long(e, "reserveFactorBps") ?? 0),
        CapPerClaimCents = Long(e, "capPerClaimCents") ?? DollarsToCents(e, "capPerClaim"),
    };

    /// <summary>Scenario identity for snapshot caching (dbo.FeatureSnapshot.ScenarioHash): SHA-256 of its canonical JSON; "baseline" for the default.</summary>
    public static string ScenarioHash(Scenario s) => s.IsBaseline ? "baseline"
        : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(WriteScenario(s))));

    // ------------------------------------------------------------------ snapshot

    public static string WriteSnapshot(FeatureSnapshot s)
    {
        var w = new CanonicalWriter();
        w.StartObject()
            .Name("catalogVersion").String(s.CatalogVersion)
            .Name("view").String(s.View)
            .Name("quoteId").String(s.QuoteId)
            .Name("valuationDate").String(s.ValuationDate)
            .Name("scenario");
        if (s.Scenario.IsBaseline)
        {
            w.Null();
        }
        else
        {
            WriteScenario(w, s.Scenario);
        }

        w.Name("inputsHash").String(s.InputsHash)
            .Name("claims").StartObject()
            .Name("input").Scaled(s.InputClaims, 0)
            .Name("nonWcExcluded").Scaled(s.NonWcExcluded, 0)
            .Name("scenarioExcluded").Scaled(s.ScenarioExcluded, 0)
            .Name("included").Scaled(s.Included, 0)
            .EndObject();

        w.Name("metrics").StartArray();
        foreach (var m in s.Metrics)
        {
            w.StartObject().Name("name").String(m.Name).Name("unit").String(m.Unit).Name("value").Scaled(m.Value, m.Scale);
            if (m.NotMeasurable is { } reason)
            {
                w.Name("notMeasurable").String(reason);
            }
            if (m.Breakdown is { } b)
            {
                w.Name("breakdown").StartArray();
                foreach (var (key, value) in b)
                {
                    w.StartObject().Name("key").String(key).Name("value").Scaled(value, m.Scale).EndObject();
                }

                w.EndArray();
            }

            w.Name("contributors");
            if (m.Contributors is null)
            {
                w.String("all");
            }
            else
            {
                w.StartArray();
                foreach (var id in m.Contributors)
                {
                    w.String(id);
                }

                w.EndArray();
            }

            w.Name("overrides").StartArray();
            foreach (var id in m.Overrides)
            {
                w.String(id);
            }

            w.EndArray().EndObject();
        }

        w.EndArray();
        w.Name("perPeriod").StartArray();
        foreach (var p in s.PerPeriod)
        {
            w.StartObject().Name("policyNumber").String(p.PolicyNumber).Name("policyYear").Scaled(p.PolicyYear, 0)
                .Name("claimCount").Scaled(p.ClaimCount, 0).Name("openCount").Scaled(p.OpenCount, 0);
            foreach (var (name, t) in new[] { ("indemnity", p.Indemnity), ("medical", p.Medical), ("expense", p.Expense), ("total", p.Total) })
            {
                w.Name(name).StartObject().Name("paid").Scaled(t[0], 2).Name("reserve").Scaled(t[1], 2).Name("incurred").Scaled(t[2], 2).EndObject();
            }

            w.EndObject();
        }

        w.EndArray();
        w.Name("perClaimFlags").StartArray();
        foreach (var (id, flags) in s.PerClaimFlags)
        {
            w.StartObject().Name("claimId").String(id).Name("flags").StartArray();
            foreach (var f in flags)
            {
                w.String(f);
            }

            w.EndArray().EndObject();
        }

        w.EndArray();
        w.EndObject();
        return w.ToString();
    }

    // ------------------------------------------------------------------ helpers

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static long? Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    private static bool? Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static long? DollarsToCents(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)Math.Round(v.GetDecimal() * 100, MidpointRounding.AwayFromZero) : null;

    private static Dictionary<int, long>? YearMap(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v.EnumerateObject().ToDictionary(p => int.Parse(p.Name, CultureInfo.InvariantCulture), p => p.Value.GetInt64())
            : null;
}
