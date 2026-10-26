using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LossRun.Contracts;

namespace LossRun.Analytics;

/// <summary>An override as recorded (brief 09 <c>dbo.Override</c> / Blob decision): canonical field path, old and new value.</summary>
public sealed record OverrideRecord(string Id, string DocumentId, string FieldPath, string? OldValue, string? NewValue, string? Rationale, string? UserId,
    DateTimeOffset? AtUtc);

/// <summary>One loss-run document of an account: its current (as-adjusted) canonical document and the overrides applied to it.</summary>
public sealed record AccountDocument(string DocumentId, LossRunDocument Current, IReadOnlyList<OverrideRecord> Overrides);

/// <summary>
/// Builds the engine input for a quote from its documents (brief 10 §2). As-adjusted = the current documents. As-reported =
/// the same documents with every overridden field put back to the value before its first override — equivalent to
/// <c>vClaimAsReported</c> (brief 09) and available in both record-store modes. Claim ids are lineage paths
/// (<c>{documentId}/{period}/{claim}</c>); the overrides that explain the difference ride along for metric lineage.
/// </summary>
public static partial class AccountBuilder
{
    public const string AsReported = "reported";
    public const string AsAdjusted = "adjusted";

    public static AnalyticsInput Build(string? quoteId, IEnumerable<AccountDocument> documents, string view = AsAdjusted, AnalyticsContext? extras = null)
    {
        var claims = new List<AnalyticsClaim>();
        var overrides = new List<OverrideRef>();
        string? valuation = null;
        foreach (var d in documents.OrderBy(d => d.DocumentId, StringComparer.Ordinal))
        {
            var unrevertable = new HashSet<string>(StringComparer.Ordinal);
            var doc = view == AsReported ? Revert(d.Current, d.Overrides, unrevertable) : d.Current;
            if (doc.ValuationDate is { } v && (valuation is null || string.CompareOrdinal(Iso(v), valuation) > 0))
            {
                valuation = Iso(v);
            }

            for (var p = 0; p < doc.PolicyPeriods.Count; p++)
            {
                var period = doc.PolicyPeriods[p];
                for (var c = 0; c < period.Claims.Count; c++)
                {
                    claims.Add(ToClaim($"{d.DocumentId}/{p}/{c}", period, period.Claims[c], doc));
                }
            }

            foreach (var o in d.Overrides)
            {
                if (ClaimPath().Match(o.FieldPath) is { Success: true } m)
                {
                    overrides.Add(new OverrideRef(o.Id, $"{d.DocumentId}/{m.Groups["p"].Value}/{m.Groups["c"].Value}", m.Groups["field"].Value,
                        unrevertable.Contains(o.FieldPath) ? true : null));
                }
            }
        }

        var context = (extras ?? new AnalyticsContext(quoteId, null)) with { QuoteId = quoteId, ValuationDate = extras?.ValuationDate ?? valuation };
        return new AnalyticsInput(claims, context, overrides);
    }

    public static AnalyticsClaim ToClaim(string id, PolicyPeriod period, Claim c, LossRunDocument doc)
    {
        var (ip, ir) = Component(c.Indemnity);
        var (mp, mr) = Component(c.Medical);
        var (ep, er) = Component(c.Expense);
        if (c.Indemnity is null && c.Medical is null && c.Expense is null && c.Total is { } total)
        {
            // Only totals printed: carried under indemnity so incurred, severity and large-loss metrics stay right
            // (catalog.yaml conventions.money; component breakdowns then over-state indemnity for such rows).
            (ip, ir) = Component(total);
        }

        return new AnalyticsClaim(id, c.ClaimNumber, period.PolicyNumber, period.EffectiveDate?.Year,
            Iso(c.DateOfInjury), Iso(c.DateReported), Iso(c.DateClosed),
            c.Status is { } s ? JsonNamingPolicy.CamelCase.ConvertName(s.ToString()) : null,
            c.ClaimType is { } t ? JsonNamingPolicy.CamelCase.ConvertName(t.ToString()) : null,
            c.NatureOfInjury?.Code ?? c.NatureOfInjury?.Text, c.CauseOfInjury?.Code ?? c.CauseOfInjury?.Text, c.PartOfBody?.Code ?? c.PartOfBody?.Text,
            c.TypeOfLossCode, c.AccidentDescription, c.LitigationFlag, c.CumulativeTrauma,
            c.JurisdictionState ?? doc.JurisdictionState, c.LineOfBusiness,
            ip, ir, mp, mr, ep, er, Cents(c.RecoverySubrogation), Iso(c.LastDayWorked), c.ReportOnly);
    }

    private static (long Paid, long Reserve) Component(MoneyTriplet? t)
    {
        var paid = Cents(t?.Paid) ?? 0;
        var reserve = Cents(t?.Reserve) ?? (Cents(t?.Incurred) is { } incurred ? incurred - paid : 0);
        return (paid, reserve);
    }

    private static long? Cents(decimal? amount) => amount is { } a ? (long)Math.Round(a * 100, MidpointRounding.AwayFromZero) : null;

    private static string? Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The document with each overridden field set back to the value before its first override.</summary>
    /// <param name="unrevertable">Brief 32 item 5: receives the paths whose stored old value is not a value of the field's type; they keep the current value.</param>
    public static LossRunDocument Revert(LossRunDocument current, IReadOnlyList<OverrideRecord> overrides, ISet<string>? unrevertable = null)
    {
        if (overrides.Count == 0)
        {
            return current;
        }

        var node = JsonSerializer.SerializeToNode(current, CanonicalJson.Options)!.AsObject();
        node.Remove("lineage");
        foreach (var first in overrides.GroupBy(o => o.FieldPath).Select(g => g.OrderBy(o => o.AtUtc).ThenBy(o => o.Id, StringComparer.Ordinal).First()))
        {
            if (!SetPath(node, first.FieldPath, first.OldValue))
            {
                unrevertable?.Add(first.FieldPath);
            }
        }

        return node.Deserialize<LossRunDocument>(CanonicalJson.Options)!;
    }

    private static bool SetPath(JsonObject root, string path, string? value)
    {
        JsonNode? node = root;
        var parts = path.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            var name = parts[i].Split('[')[0];
            var indexes = parts[i].Split('[').Skip(1).Select(x => int.Parse(x.TrimEnd(']'), CultureInfo.InvariantCulture)).ToList();
            var obj = node as JsonObject;
            if (obj is null)
            {
                return true;
            }

            if (i == parts.Length - 1 && indexes.Count == 0)
            {
                // Brief 30 item 3: by the field's declared type, never by the text's shape (a digit-only claim number stays a string).
                // Brief 32 item 5: a stored value that is not of that type leaves the field as it is (and is reported), never a 500.
                if (!OverrideTypes.TryFromStoredText(path, value, out var typed))
                {
                    return false;
                }

                obj[name] = typed;
                return true;
            }

            if (obj[name] is null)
            {
                obj[name] = new JsonObject();
            }

            node = obj[name];
            foreach (var index in indexes)
            {
                node = (node as JsonArray)?[index];
            }
        }

        return true;
    }

    [GeneratedRegex(@"^policyPeriods\[(?<p>\d+)\]\.claims\[(?<c>\d+)\]\.(?<field>.+)$")]
    private static partial Regex ClaimPath();
}
