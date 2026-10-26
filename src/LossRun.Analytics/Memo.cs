using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace LossRun.Analytics;

/// <summary>
/// The sample UW report package (brief 10 §6): a pricing memo built from quote payload v2. <see cref="Build"/> makes the
/// structured memo (the JSON twin); <see cref="WriteDocx"/> renders the same memo with the Open XML SDK (MIT; QuestPDF's
/// community licence excludes public-sector bodies, underwriter-analytics.md KQ6).
/// </summary>
public static class Memo
{
    private static readonly (string Metric, string Label)[] Kpis =
    [
        ("claim_count", "Claims"), ("open_claim_count", "Open claims"), ("lost_time_share", "Lost-time share"), ("incurred_total", "Total incurred"),
        ("paid_total", "Total paid"), ("reserve_total", "Case reserves"), ("paid_to_incurred", "Paid-to-incurred"), ("severity_all", "Severity (all)"),
        ("severity_lost_time", "Severity (lost time)"), ("large_loss_count", "Large losses ≥ $100K"), ("capped_incurred", "Incurred capped at $250K"),
        ("reporting_lag_median_days", "Median reporting lag (days)"), ("closure_median_days", "Median days to close"),
    ];

    private static readonly (string Metric, string Label)[] California =
    [
        ("ct_share", "Cumulative-trauma share (all claims)"), ("ct_indemnity_share", "CT share of lost-time claims"),
        ("post_termination_ct_count", "Post-termination CT claims"), ("litigation_rate", "Litigation rate (lost time)"),
        ("non_ca_share", "Non-CA share"), ("non_wc_excluded_count", "Non-WC rows excluded"),
    ];

    /// <summary>The structured memo (JSON twin of the DOCX).</summary>
    public static JsonObject Build(JsonObject payload)
    {
        var adjusted = payload["features"]!["adjusted"]!;
        var reported = payload["features"]!["reported"]!;
        string Value(JsonNode snapshot, string metric) => Format(Metric(snapshot, metric));
        var catalog = Catalog.Embedded;

        var kpis = new JsonArray();
        foreach (var (metric, label) in Kpis)
        {
            kpis.Add(new JsonObject { ["metric"] = metric, ["label"] = label, ["asReported"] = Value(reported, metric), ["asAdjusted"] = Value(adjusted, metric) });
        }

        var years = Breakdown(adjusted, "claim_count_by_policy_year").Select(b => b.Key).ToList();
        var trend = new JsonArray();
        foreach (var y in years)
        {
            trend.Add(new JsonObject
            {
                ["policyYear"] = y,
                ["claims"] = BreakdownValue(adjusted, "claim_count_by_policy_year", y),
                ["incurred"] = BreakdownValue(adjusted, "incurred_by_policy_year", y),
                ["paid"] = BreakdownValue(adjusted, "paid_by_policy_year", y),
                ["severity"] = BreakdownValue(adjusted, "severity_by_policy_year", y),
                ["paidToIncurred"] = BreakdownValue(adjusted, "paid_to_incurred_by_policy_year", y),
            });
        }

        var large = new JsonArray();
        foreach (var c in payload["claims"]!.AsArray().Where(c => c!["flags"]!.AsArray().Any(f => f!.GetValue<string>() == "large"))
                     .OrderByDescending(c => Incurred(c!)))
        {
            large.Add(new JsonObject
            {
                ["claimNumber"] = c!["claimNumber"]?.DeepClone(), ["dateOfInjury"] = c["dateOfInjury"]?.DeepClone(), ["status"] = c["status"]?.DeepClone(),
                ["incurred"] = Money(Incurred(c)), ["flags"] = string.Join(", ", c["flags"]!.AsArray().Select(f => f!.GetValue<string>())),
            });
        }

        var drivers = new JsonArray();
        foreach (var (metric, label) in new[] { ("loss_drivers_cause_incurred", "Cause of injury"), ("loss_drivers_body_part_incurred", "Part of body"), ("loss_drivers_nature_incurred", "Nature of injury") })
        {
            var b = Breakdown(adjusted, metric);
            drivers.Add(new JsonObject
            {
                ["dimension"] = label,
                ["top"] = b.Count == 0 ? "not printed on these loss runs" : string.Join("; ", b.Select(x => $"{x.Key} {Format(x.Value, "money")}")),
            });
        }

        var ca = new JsonArray();
        foreach (var (metric, label) in California)
        {
            ca.Add(new JsonObject { ["metric"] = metric, ["label"] = label, ["asAdjusted"] = Value(adjusted, metric), ["note"] = Note(catalog, metric) });
        }

        var lineage = payload["lineage"]!;
        var overrides = new JsonArray([.. lineage["overrides"]!.AsArray().Select(o => (JsonNode)new JsonObject
        {
            ["field"] = o!["fieldPath"]?.DeepClone(), ["from"] = o["oldValue"]?.DeepClone(), ["to"] = o["newValue"]?.DeepClone(), ["rationale"] = o["rationale"]?.DeepClone(),
            ["user"] = o["userId"]?.DeepClone(), ["atUtc"] = o["atUtc"]?.DeepClone(),
        })]);
        var docs = payload["documents"]!.AsArray();
        return new JsonObject
        {
            ["title"] = $"Underwriting loss analysis — quote {payload["quoteId"]}",
            ["generatedUtc"] = payload["generatedUtc"]?.DeepClone(),
            ["account"] = new JsonObject
            {
                ["quoteId"] = payload["quoteId"]?.DeepClone(),
                ["insured"] = string.Join("; ", docs.Select(d => d!["insuredName"]?.GetValue<string>()).OfType<string>().Distinct()),
                ["carriers"] = string.Join("; ", docs.Select(d => d!["carrierName"]?.GetValue<string>()).OfType<string>().Distinct()),
                ["valuationDate"] = payload["valuationDate"]?.DeepClone(),
                ["documents"] = docs.Count,
                ["claims"] = Value(adjusted, "claim_count"),
                // Brief 24 Answers 2: the memo is built from the verified-only payload.
                ["basis"] = payload["featuresView"]?.GetValue<string>() == "verified-only"
                    ? "Verified values only: documents pending review and values a validation rule still flags are left out."
                    : null,
            },
            ["kpis"] = kpis,
            ["trend"] = trend,
            ["largeLosses"] = large,
            ["lossDrivers"] = drivers,
            ["california"] = ca,
            ["dataQuality"] = new JsonObject
            {
                ["documents"] = new JsonArray([.. docs.Select(d => (JsonNode)new JsonObject
                {
                    ["documentId"] = d!["documentId"]?.DeepClone(), ["routing"] = d["routingTier"]?.DeepClone(), ["reviewState"] = d["reviewState"]?.DeepClone(),
                    ["revision"] = d["revision"]?.DeepClone(), ["claims"] = d["claimCount"]?.DeepClone(),
                })]),
                ["reviewRate"] = lineage["reviewRate"] is { } rr ? (rr.GetValue<decimal>() * 100).ToString("0.#", CultureInfo.InvariantCulture) + " % of documents routed to review" : "—",
                ["overrides"] = overrides,
                ["note"] = overrides.Count == 0
                    ? "No values were overridden: as reported equals as adjusted."
                    : $"{overrides.Count} override(s); every one is in the append-only ledger with its rationale. As reported is the value before each field's first override.",
            },
            ["lineage"] = new JsonObject
            {
                ["documents"] = string.Join(", ", docs.Select(d => $"{d!["documentId"]} r{d["revision"]}")),
                ["valuationDates"] = string.Join(", ", lineage["valuationDates"]!.AsArray().Select(v => v!.GetValue<string>()).Select(v =>
                    lineage["valuationDatesStatedBySubmitter"] is JsonArray stated && stated.Any(s => s!.GetValue<string>() == v) ? $"{v} (valuation date as stated by the submitter)" : v)),
                ["catalogVersion"] = payload["catalogVersion"]?.DeepClone(),
                ["formatVersions"] = string.Join(", ", lineage["formatVersions"]!.AsArray().Select(v => v!.GetValue<string>())),
                ["extractors"] = string.Join(", ", lineage["extractors"]!.AsObject().Select(kv => $"{kv.Key} {kv.Value}")),
                ["inputsHash"] = adjusted["inputsHash"]?.DeepClone(),
            },
        };
    }

    private static string? Note(Catalog catalog, string metric) =>
        catalog.Json["metrics"]!.AsArray().FirstOrDefault(m => m!["name"]!.GetValue<string>() == metric)?["caNote"]?.GetValue<string>();

    private static JsonNode Metric(JsonNode snapshot, string name) => snapshot["metrics"]!.AsArray().First(m => m!["name"]!.GetValue<string>() == name)!;

    private static List<(string Key, JsonNode? Value)> Breakdown(JsonNode snapshot, string metric) =>
        Metric(snapshot, metric)["breakdown"] is JsonArray b ? [.. b.Select(x => (x!["key"]!.GetValue<string>(), x["value"]))] : [];

    private static string BreakdownValue(JsonNode snapshot, string metric, string key)
    {
        var m = Metric(snapshot, metric);
        var v = Breakdown(snapshot, metric).FirstOrDefault(b => b.Key == key).Value;
        return Format(v, m["unit"]!.GetValue<string>());
    }

    private static string Format(JsonNode metric) => Format(metric["value"], metric["unit"]!.GetValue<string>());

    private static string Format(JsonNode? value, string unit)
    {
        if (value is null)
        {
            return "—";
        }

        var d = value.GetValue<decimal>();
        return unit switch
        {
            "money" => d.ToString("$#,##0", CultureInfo.InvariantCulture),
            "ratio" => (d * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %",
            "rate" => d.ToString("0.######", CultureInfo.InvariantCulture),
            "days" => d.ToString("0.#", CultureInfo.InvariantCulture),
            _ => d.ToString("#,##0", CultureInfo.InvariantCulture),
        };
    }

    private static decimal Incurred(JsonNode claim)
    {
        decimal Part(string name) => claim[name] is JsonObject t
            ? t["incurred"]?.GetValue<decimal>() ?? (t["paid"]?.GetValue<decimal>() ?? 0) + (t["reserve"]?.GetValue<decimal>() ?? 0)
            : 0;
        var parts = Part("indemnity") + Part("medical") + Part("expense");
        return parts != 0 ? parts : claim["total"]?["incurred"]?.GetValue<decimal>() ?? 0;
    }

    private static string Money(decimal d) => d.ToString("$#,##0", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ DOCX

    public static void WriteDocx(JsonObject memo, string path)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;

        body.Append(Heading(memo["title"]!.GetValue<string>(), 32));
        var account = memo["account"]!.AsObject();
        body.Append(Table(["Quote", "Insured", "Carrier(s)", "Valuation date", "Documents", "Claims"],
            [[Text(account["quoteId"]), Text(account["insured"]), Text(account["carriers"]), Text(account["valuationDate"]), Text(account["documents"]), Text(account["claims"])]]));
        if (account["basis"]?.GetValue<string>() is { } basis)
        {
            body.Append(Paragraph(basis, italic: true));
        }

        body.Append(Heading("Key indicators", 26));
        body.Append(Table(["Indicator", "As reported", "As adjusted"],
            [.. memo["kpis"]!.AsArray().Select(k => new[] { Text(k!["label"]), Text(k["asReported"]), Text(k["asAdjusted"]) })]));

        body.Append(Heading("Trend by policy year (as adjusted)", 26));
        body.Append(Table(["Policy year", "Claims", "Incurred", "Paid", "Severity", "Paid-to-incurred"],
            [.. memo["trend"]!.AsArray().Select(t => new[] { Text(t!["policyYear"]), Text(t["claims"]), Text(t["incurred"]), Text(t["paid"]), Text(t["severity"]), Text(t["paidToIncurred"]) })]));
        body.Append(Paragraph("Each policy year is at a different maturity; apply development factors before comparing years (catalog: valuation caveats).", italic: true));

        body.Append(Heading("Large losses (≥ $100,000)", 26));
        var large = memo["largeLosses"]!.AsArray();
        body.Append(large.Count == 0 ? Paragraph("None.")
            : Table(["Claim", "Date of injury", "Status", "Incurred", "Flags"],
                [.. large.Select(l => new[] { Text(l!["claimNumber"]), Text(l["dateOfInjury"]), Text(l["status"]), Text(l["incurred"]), Text(l["flags"]) })]));

        body.Append(Heading("Loss drivers", 26));
        body.Append(Table(["Dimension", "Top five by incurred"], [.. memo["lossDrivers"]!.AsArray().Select(d => new[] { Text(d!["dimension"]), Text(d["top"]) })]));

        body.Append(Heading("California: cumulative trauma, litigation, jurisdiction", 26));
        body.Append(Table(["Indicator", "Value", "Context"], [.. memo["california"]!.AsArray().Select(c => new[] { Text(c!["label"]), Text(c["asAdjusted"]), Text(c["note"]) })]));

        body.Append(Heading("Data quality", 26));
        var dq = memo["dataQuality"]!;
        body.Append(Table(["Document", "Routing", "Review state", "Revision", "Claims"],
            [.. dq["documents"]!.AsArray().Select(d => new[] { Text(d!["documentId"]), Text(d["routing"]), Text(d["reviewState"]), Text(d["revision"]), Text(d["claims"]) })]));
        body.Append(Paragraph($"Review rate: {Text(dq["reviewRate"])}. {Text(dq["note"])}"));
        var overrides = dq["overrides"]!.AsArray();
        if (overrides.Count > 0)
        {
            body.Append(Table(["Field", "From", "To", "Rationale", "By", "When (UTC)"],
                [.. overrides.Select(o => new[] { Text(o!["field"]), Text(o["from"]), Text(o["to"]), Text(o["rationale"]), Text(o["user"]), Text(o["atUtc"]) })]));
        }

        body.Append(Heading("Lineage", 22));
        var lineage = memo["lineage"]!;
        body.Append(Paragraph($"Documents {Text(lineage["documents"])}; valuation dates {Text(lineage["valuationDates"])}; analytics catalog {Text(lineage["catalogVersion"])}; " +
            $"carrier formats {Text(lineage["formatVersions"])}; value sources {Text(lineage["extractors"])}; inputs hash {Text(lineage["inputsHash"])}. " +
            "Every figure traces to cited cells of the source documents (GET /documents/{id}) and every override to the ledger.", italic: true, size: 16));
        body.Append(Paragraph($"Generated {Text(memo["generatedUtc"])} from synthetic data.", italic: true, size: 16));
        main.Document.Save();
    }

    private static string Text(JsonNode? node) => node switch
    {
        null => "—",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };

    private static Paragraph Heading(string text, int halfPoints) =>
        new(new ParagraphProperties(new SpacingBetweenLines { Before = "240", After = "120" }),
            new Run(new RunProperties(new Bold(), new FontSize { Val = halfPoints.ToString(CultureInfo.InvariantCulture) }), new Text(text)));

    private static Paragraph Paragraph(string text, bool italic = false, int size = 20)
    {
        var props = new RunProperties(new FontSize { Val = size.ToString(CultureInfo.InvariantCulture) });
        if (italic)
        {
            props.Append(new Italic());
        }

        return new Paragraph(new Run(props, new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static Table Table(string[] header, IEnumerable<string[]> rows)
    {
        var border = new TableBorders(
            new TopBorder { Val = BorderValues.Single, Size = 4 }, new BottomBorder { Val = BorderValues.Single, Size = 4 },
            new LeftBorder { Val = BorderValues.Single, Size = 4 }, new RightBorder { Val = BorderValues.Single, Size = 4 },
            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 });
        var table = new Table(new TableProperties(border, new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }));
        table.Append(Row(header, bold: true));
        foreach (var r in rows)
        {
            table.Append(Row(r, bold: false));
        }

        return table;
    }

    private static TableRow Row(string[] cells, bool bold)
    {
        var row = new TableRow();
        if (bold)
        {
            row.Append(new TableRowProperties(new TableHeader()));
        }

        foreach (var c in cells)
        {
            var props = new RunProperties(new FontSize { Val = "18" });
            if (bold)
            {
                props.Append(new Bold());
            }

            row.Append(new TableCell(new Paragraph(new Run(props, new Text(c) { Space = SpaceProcessingModeValues.Preserve }))));
        }

        return row;
    }

    /// <summary>Writes memo.docx and its JSON twin (memo.json) from a quote payload v2.</summary>
    public static JsonObject Write(JsonObject payload, string docxPath)
    {
        var memo = Build(payload);
        WriteDocx(memo, docxPath);
        File.WriteAllText(Path.ChangeExtension(docxPath, ".json"), memo.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return memo;
    }
}
