using System.Globalization;
using System.Text.Json.Nodes;

namespace LossRun.Integration;

/// <summary>One cell of the loss-information grid. <paramref name="State"/>: value | needs-input | not-from-loss-run | excluded | none.</summary>
/// <param name="Reason">Why a cell needs input or is excluded (shown on hover and focus, and to screen readers).</param>
/// <param name="Source">Where a value comes from when it is not the run itself (e.g. "submission" for a valuation date the run does not print).</param>
public sealed record LossInfoCell(string? Value, string State, string? Reason = null, string? Source = null);

/// <param name="Key">documentId/periodIndex.</param>
/// <param name="ExcludedReason">rejected | pending-review | null.</param>
/// <param name="ReportOnlyClaims">Report-only claims (no financial information), counted apart from the claim counts.</param>
/// <param name="EffectiveDateNeedsInput">Brief 25 Answers 1: the period's effective date is unresolved (the header says so).</param>
public sealed record LossInfoColumn(string Key, string Title, string? ExcludedReason, IReadOnlyDictionary<string, LossInfoCell> Cells, int ReportOnlyClaims,
    bool EffectiveDateNeedsInput = false);

public sealed record LossInfoAttachment(string FileName, string? Classification, string? Status, string? Reason, string? UploadId);

/// <summary>What the quote system last received from the integration: the command, its idempotency key and revision, and the last event.</summary>
public sealed record LossInfoIntegration(string? CommandId, string? IdempotencyKey, int? QuoteRevision, string? Delivery, string? ReceivedAt, string? LastEventType,
    string? LastEventAt, int? LastEventRevision, int? StaleRefused);

public sealed record LossInfoRow(string Key, string Label);

public sealed record LossInfoGrid(string QuoteId, int? QuoteRevision, bool? Ready, int BlockingFieldCount, string? UpdatedAt,
    IReadOnlyList<LossInfoRow> Rows, IReadOnlyList<LossInfoColumn> Columns, IReadOnlyList<LossInfoAttachment> Attachments, LossInfoIntegration Integration,
    bool OrderedByEffectiveDate = true);

/// <summary>
/// Brief 25: the quote system's "Loss Information" grid. It mirrors how a policy system shows prior-insurance history: one column
/// per policy period, newest left. The grid is built from quote payload v2 (verified-only), the uploads' manifests, and what the
/// mock quote system recorded.
/// <list type="bullet">
/// <item>An approved value is shown.</item>
/// <item>An unresolved one "needs input", with its reason.</item>
/// <item>A rejected document's column is excluded.</item>
/// <item>A pending document's column needs input everywhere.</item>
/// <item>Premium, payroll and experience modification are not on a loss run: they are marked as such, never guessed.</item>
/// </list>
/// Pure: no I/O.
/// </summary>
public static class LossInformation
{
    public static readonly IReadOnlyList<LossInfoRow> Rows =
    [
        new("effectiveDate", "Effective date"), new("expirationDate", "Expiration date"), new("carrier", "Carrier"), new("policyNumber", "Policy number"),
        new("totalPremium", "Total premium"), new("totalPayroll", "Total payroll"), new("exMod", "Ex-Mod %"),
        new("incurredLoss", "Total incurred loss (including expense)"), new("incurredExpense", "Total incurred expense"),
        new("claims", "Total # incurred claims"), new("indemnityClaims", "# indemnity claims"), new("medicalOnlyClaims", "# medical-only claims"),
        new("evaluationDate", "Loss-run evaluation date"),
    ];

    public const string NotFromLossRun = "not from loss run";

    public static LossInfoGrid Build(string quoteId, JsonObject? payload, JsonObject? quoteRecord, IReadOnlyList<JsonObject> manifests)
    {
        var readiness = payload?["readiness"];
        var columns = new List<LossInfoColumn>();
        var claims = payload?["claims"] as JsonArray ?? [];
        var summaries = payload?["periodSummaries"]?["rows"] as JsonArray ?? [];
        // Brief 25 Answers 1: newest left only when every period has a resolved effective date; otherwise the order the documents
        // list them, and the header says which date needs input (a guessed order would hide that the date is missing).
        // Excluded columns (rejected, pending review) carry no approved date: they follow the counted ones, in source order.
        var periods = (payload?["policyPeriods"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var counted = periods.Where(p => S(p["excludedReason"]) is null).ToList();
        var byDate = counted.Count > 0 && counted.All(p => S(p["effectiveDate"]) is not null);
        foreach (var period in byDate
            ? counted.OrderByDescending(p => S(p["effectiveDate"]), StringComparer.Ordinal).ThenBy(p => S(p["documentId"]), StringComparer.Ordinal).ThenBy(p => I(p["periodIndex"]))
                .Concat(periods.Where(p => S(p["excludedReason"]) is not null))
            : periods)
        {
            columns.Add(Column(period, claims, summaries));
        }

        var attachments = manifests.SelectMany(m => (m["items"] as JsonArray ?? []).OfType<JsonObject>().Select(i => new LossInfoAttachment(
            S(i["fileName"]) ?? "(file)", S(i["classification"]), S(i["status"]), S(i["reason"]), S(m["uploadId"])))).ToList();

        var analysis = quoteRecord?["lossRunAnalysis"] as JsonObject;
        var lastEvent = (quoteRecord?["events"] as JsonArray)?.OfType<JsonObject>().LastOrDefault();
        var integration = new LossInfoIntegration(S(analysis?["lossrunOutboxId"]), S(analysis?["idempotencyKey"]), I(analysis?["attributes"]?["quoteRevision"]),
            analysis is null ? null : "applied", S(analysis?["receivedAt"]), S(lastEvent?["type"]), S(lastEvent?["receivedAt"]), I(lastEvent?["data"]?["quoteRevision"]),
            I(quoteRecord?["staleRefused"]));

        return new LossInfoGrid(quoteId, I(payload?["quoteRevision"]), readiness?["ready"]?.GetValue<bool>(), I(readiness?["blockingFieldCount"]) ?? 0,
            S(quoteRecord?["payloadReceivedAt"]) ?? S(analysis?["receivedAt"]), Rows, columns, attachments, integration, byDate);
    }

    private static LossInfoColumn Column(JsonObject period, JsonArray claims, JsonArray summaries)
    {
        var doc = S(period["documentId"])!;
        var index = I(period["periodIndex"]) ?? 0;
        var excluded = S(period["excludedReason"]);
        var status = period["fieldStatus"] as JsonObject ?? [];
        var cells = new Dictionary<string, LossInfoCell>(StringComparer.Ordinal);

        LossInfoCell Identity(string field, string? value)
        {
            if (excluded is not null)
            {
                return Excluded(excluded);
            }

            return status[field] is JsonObject s ? new(null, "needs-input", Why(S(s["reason"]))) : value is null ? new(null, "none", "not printed on the loss run") : new(value, "value");
        }

        cells["effectiveDate"] = Identity("effectiveDate", S(period["effectiveDate"]));
        cells["expirationDate"] = Identity("expirationDate", S(period["expirationDate"]));
        cells["carrier"] = Identity("carrierName", S(period["carrierName"]));
        cells["policyNumber"] = Identity("policyNumber", S(period["policyNumber"]));
        cells["totalPremium"] = new(null, "not-from-loss-run", "A loss run does not print premium.");
        cells["totalPayroll"] = new(null, "not-from-loss-run", "A loss run does not print payroll; it comes with the submission.");
        cells["exMod"] = new(null, "not-from-loss-run", "The experience modification is not on a loss run.");
        var evaluation = Identity("valuationDate", S(period["valuationDate"]));
        cells["evaluationDate"] = S(period["valuationDateSource"]) == "submission" && evaluation.State == "value" ? evaluation with { Source = "submission" } : evaluation;

        var mine = claims.OfType<JsonObject>().Where(c => S(c["id"])?.StartsWith($"{doc}/{index}/", StringComparison.Ordinal) == true).ToList();
        var reportOnly = mine.Count(c => c["reportOnly"]?.GetValue<bool>() == true);
        if (excluded is not null)
        {
            foreach (var key in new[] { "incurredLoss", "incurredExpense", "claims", "indemnityClaims", "medicalOnlyClaims" })
            {
                cells[key] = Excluded(excluded);
            }
        }
        else if (S(period["detailLevel"]) == "SummaryOnly")
        {
            // ADR 0017: a summary row prints counts and totals, not claims.
            var row = summaries.OfType<JsonObject>().FirstOrDefault(r => S(r["documentId"]) == doc && I(r["periodIndex"]) == index);
            cells["incurredLoss"] = Amount(D(row?["incurred"]));
            cells["incurredExpense"] = new(null, "none", "the summary row prints no expense");
            cells["claims"] = row?["claimCount"] is { } n ? new(n.ToString(), "value") : new(null, "none", "not printed on the loss run");
            cells["indemnityClaims"] = new(null, "none", "the summary row prints no claim type");
            cells["medicalOnlyClaims"] = new(null, "none", "the summary row prints no claim type");
        }
        else
        {
            var counted = mine.Where(c => c["reportOnly"]?.GetValue<bool>() != true).ToList();
            cells["incurredLoss"] = Sum(mine, "total.incurred", period);
            cells["incurredExpense"] = Sum(mine, "expense.incurred", period);
            cells["claims"] = Count(counted, _ => true);
            cells["indemnityClaims"] = Count(counted, c => S(c["claimType"]) == "indemnity", "claimType");
            cells["medicalOnlyClaims"] = Count(counted, c => S(c["claimType"]) == "medicalOnly", "claimType");
        }

        var title = S(period["policyNumber"]) ?? S(period["periodLabel"]) ?? (S(period["effectiveDate"]) is { } eff ? eff[..4] : $"{doc} · period {index + 1}");
        return new LossInfoColumn($"{doc}/{index}", title, excluded, cells, reportOnly, excluded is null && S(period["effectiveDate"]) is null);
    }

    private static LossInfoCell Sum(IReadOnlyList<JsonObject> claims, string field, JsonObject period)
    {
        var open = claims.Select(c => c["fieldStatus"]?[field] as JsonObject ?? (field.EndsWith(".incurred", StringComparison.Ordinal)
            ? c["fieldStatus"]?[field.Replace(".incurred", ".paid", StringComparison.Ordinal)] as JsonObject ?? c["fieldStatus"]?[field.Replace(".incurred", ".reserve", StringComparison.Ordinal)] as JsonObject
            : null)).OfType<JsonObject>().ToList();
        if (open.Count > 0)
        {
            return new(null, "needs-input", $"{open.Count} claim value(s) need input: {Why(S(open[0]["reason"]))}");
        }

        var parts = field.Split('.');
        var values = claims.Select(c => D(c[parts[0]]?[parts[1]])).ToList();
        if (claims.Count == 0)
        {
            return new(Money(0), "value", Source: period["noLossesStatement"]?.GetValue<bool>() == true ? "no-losses statement" : "no claims printed");
        }

        if (values.All(v => v is null))
        {
            return new(null, "none", "not printed on the loss run");
        }

        // Brief 30 item 13: a sum over claims some of which print no value is not a total; never read the gap as 0.
        var unprinted = values.Count(v => v is null);
        return unprinted > 0
            ? new(null, "incomplete", $"{unprinted} of {values.Count} claims print no {field}; the total is not known")
            : new(Money(values.Sum(v => v!.Value)), "value");
    }

    private static LossInfoCell Count(IReadOnlyList<JsonObject> claims, Func<JsonObject, bool> where, string? field = null)
    {
        if (field is not null && claims.Any(c => c["fieldStatus"]?[field] is JsonObject))
        {
            return new(null, "needs-input", "a claim type needs input");
        }

        // Brief 30 item 13: a count by claim type when the loss run prints no claim type is not 0, it is not applicable.
        if (field is not null && claims.Count > 0 && claims.All(c => c[field] is null || S(c[field]) is null))
        {
            return new("n/a", "none", "n/a (claim type not on the loss run)");
        }

        if (field is not null && claims.Any(c => c[field] is null || S(c[field]) is null))
        {
            return new(null, "incomplete", $"{claims.Count(c => c[field] is null || S(c[field]) is null)} of {claims.Count} claims print no claim type");
        }

        return new(claims.Count(where).ToString(CultureInfo.InvariantCulture), "value");
    }

    private static LossInfoCell Amount(decimal? value) => value is { } v ? new(Money(v), "value") : new(null, "none", "not printed on the loss run");

    private static LossInfoCell Excluded(string reason) => reason == "pending-review"
        ? new(null, "needs-input", "the document is waiting for review")
        : new(null, "excluded", "excluded from quote: the document was rejected");

    private static string Why(string? reason) => reason switch
    {
        null => "unresolved",
        "R-33" => "required for quoting and missing",
        "review-pending" => "waiting for review",
        "document-rejected" => "document rejected",
        var rule => $"validation rule {rule} still fails",
    };

    private static string Money(decimal v) => v.ToString("$#,##0.00", CultureInfo.InvariantCulture);

    private static string? S(JsonNode? n) => n is JsonValue v ? v.ToString() : null;

    private static int? I(JsonNode? n) => n is JsonValue v && int.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    private static decimal? D(JsonNode? n) => n is JsonValue v && decimal.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
