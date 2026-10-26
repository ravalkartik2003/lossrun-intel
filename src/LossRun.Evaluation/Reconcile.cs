using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Storage.Blobs;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;
using LossRun.Ingestion;
using LossRun.Ingestion.Metering;
using Microsoft.EntityFrameworkCore;

namespace LossRun.Evaluation;

/// <summary>
/// <c>lossrun eval reconcile</c> (brief 13 §1, cost-tracking.md §5): metered usage against the Azure bill, per UTC day and per
/// line. Metered = every <see cref="UsageEvent"/> the system recorded: the deployed API's <c>dbo.UsageEvent</c> plus the local
/// evaluation runs' per-document results (they called the same AI resource), deduplicated by correlation id, priced with
/// <c>config/azure-prices.json</c>. Billed = Cost Management actual cost (the daily export CSV, or a Cost Query JSON). AI services
/// (Content Understanding, model and embedding tokens, Document Intelligence) are reconciled line by line, target ±10 %;
/// platform services (orchestration, compute, database, storage, search, …) are standing costs, listed beside them.
/// </summary>
public static class Reconcile
{
    /// <summary>
    /// A reconciled AI line: billed meter name fragments ↔ the metered quantity (in the bill's unit) and cost. <paramref name="Relevant"/> picks the
    /// events priced for it; <paramref name="PairMeter"/> names the meter reported when an input pair is priced together (cached ⊂ input).
    /// </summary>
    private sealed record Line(string Name, string Service, Func<string, bool> Bills, decimal BillUnit, Func<IReadOnlyList<UsageEvent>, decimal> Quantity,
        Func<UsageEvent, bool> Relevant, string? PairMeter = null);

    /// <summary>Brief 31 item 22: the model lines come from config/models.json (learn, delta, embedding deployments), the bill meters from the price table.</summary>
    private static readonly Lazy<IReadOnlyList<Line>> Configured = new(() =>
    {
        var models = LossRun.Learning.LearnOptions.FromConfiguration();
        return LinesFor([models.LearnDeployment, models.DeltaDeployment], models.EmbeddingModel, PriceTable.Embedded);
    });

    /// <summary>The reconciliation lines for these chat models and embedding model: the fixed Content Understanding and Document Intelligence lines, then per model.</summary>
    private static IReadOnlyList<Line> LinesFor(IEnumerable<string> chatModels, string? embeddingModel, PriceTable prices)
    {
        var lines = new List<Line>(FixedLines);
        foreach (var model in chatModels.Where(m => m.Length > 0).Select(m => ModelName(prices, m)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (input, cached, output) = (Price(prices, model, "inputTokens"), Price(prices, model, "cachedInputTokens"), Price(prices, model, "outputTokens"));
            bool Model(UsageEvent e) => e.Service == "AzureOpenAI" && string.Equals(prices.ModelOf(e), model, StringComparison.OrdinalIgnoreCase);
            lines.Add(new($"{model} input (uncached)", "Model tokens", Bills(input), BillUnit(input),
                e => Sum(e, prices, "inputTokens", model) - Sum(e, prices, "cachedInputTokens", model), e => Model(e) && e.Meter is "inputTokens" or "cachedInputTokens", "inputTokens"));
            lines.Add(new($"{model} cached input", "Model tokens", Bills(cached), BillUnit(cached),
                e => Sum(e, prices, "cachedInputTokens", model), e => Model(e) && e.Meter is "inputTokens" or "cachedInputTokens", "cachedInputTokens"));
            lines.Add(new($"{model} output", "Model tokens", Bills(output), BillUnit(output), e => Sum(e, prices, "outputTokens", model), e => Model(e) && e.Meter == "outputTokens"));
        }

        if (embeddingModel is { Length: > 0 } embedding)
        {
            var model = ModelName(prices, embedding);
            var entry = Price(prices, model, "inputTokens");
            lines.Add(new(model, "Embeddings", Bills(entry), BillUnit(entry), e => Sum(e, prices, "inputTokens", model),
                e => e.Service == "AzureOpenAI" && string.Equals(prices.ModelOf(e), model, StringComparison.OrdinalIgnoreCase)));
        }

        return lines;
    }

    /// <summary>A deployment's model (Terraform's deployment → model map), else the deployment name itself.</summary>
    private static string ModelName(PriceTable prices, string deployment) => prices.Deployments.TryGetValue(deployment, out var model) ? model : deployment;

    private static PriceEntry Price(PriceTable prices, string model, string meter) =>
        prices.Prices.FirstOrDefault(p => p.Service == "AzureOpenAI" && p.Meter == meter && string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException($"config/models.json names {model}, but config/azure-prices.json has no AzureOpenAI {meter} price for it");

    private static Func<string, bool> Bills(PriceEntry entry) => entry.BillMeterPrefixes is { Count: > 0 } prefixes
        ? m => prefixes.Any(p => m.StartsWith(p, StringComparison.OrdinalIgnoreCase))
        : throw new InvalidDataException($"config/azure-prices.json: {entry.Model} {entry.Meter} has no billMeterPrefixes (the Cost Management meter names it is billed under)");

    private static decimal BillUnit(PriceEntry entry) => entry.BillUnit
        ?? throw new InvalidDataException($"config/azure-prices.json: {entry.Model} {entry.Meter} has no billUnit");

    private static IReadOnlyList<Line> Lines => Configured.Value;

    private static readonly Line[] FixedLines =
    [
        new("CU standard pages (async)", "Content Understanding", m => m == "Doc Content Extraction Standard Pages", 1000,
            e => Sum(e, "ContentUnderstanding", "documentPagesStandard"), e => e.Meter == "documentPagesStandard"),
        new("CU standard pages (inline)", "Content Understanding", m => m == "Doc Co Extr Sync Standard Pages", 1000,
            e => Sum(e, "ContentUnderstanding", "documentPagesStandardInline"), e => e.Meter == "documentPagesStandardInline"),
        new("CU contextualization tokens", "Content Understanding", m => m == "Std Contextualization Tokens", 1000,
            e => Sum(e, "ContentUnderstanding", "contextualizationTokens"), e => e.Meter == "contextualizationTokens"),
        new("Document Intelligence pages", "Document Intelligence", m => m.Contains("Pre-built", StringComparison.OrdinalIgnoreCase) || m.Contains("S0 Transactions", StringComparison.Ordinal), 1000,
            e => Sum(e, "DocumentIntelligence", "prebuiltPages"), e => e.Service == "DocumentIntelligence"),
    ];

    /// <summary>One billed row (export CSV or Cost Query): UTC date, service, sub-category, meter, quantity in the meter's unit, cost.</summary>
    public sealed record BillRow(DateOnly Date, string Service, string Subcategory, string Meter, decimal Quantity, decimal Cost);

    public static async Task<int> RunAsync(string billingPath, string runsDir, string? sql, DateOnly from, DateOnly to, string outPath, bool publish, string? note = null)
    {
        var bill = ReadBill(billingPath).Where(r => r.Date >= from && r.Date <= to).ToList();
        var metered = Dedupe([.. ReadRuns(runsDir), .. sql is null ? [] : await ReadSqlAsync(sql)]).Where(e => Day(e) >= from && Day(e) <= to).ToList();
        var prices = PriceTable.Embedded;
        var days = bill.Select(b => b.Date).Concat(metered.Select(Day)).Distinct().Order().ToList();

        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"# Cost reconciliation — metered vs billed, {from:yyyy-MM-dd} to {to:yyyy-MM-dd}").AppendLine();
        md.AppendLine("Generated by `lossrun eval reconcile` (`docs/poc-plan/closeout/README.md`). Metered: `dbo.UsageEvent` (deployed API, chat, indexing) plus the local evaluation runs' per-document usage, deduplicated by correlation id and priced with `config/azure-prices.json`. Billed: Azure Cost Management actual cost for the resource group (UTC days). Target: AI-services ratio within ±10 % (cost-tracking.md §5).").AppendLine();
        var summary = new JsonArray();
        decimal aiMeteredAll = 0, aiBilledAll = 0, platformAll = 0;
        foreach (var day in days)
        {
            var dayBill = bill.Where(b => b.Date == day).ToList();
            var dayEvents = metered.Where(e => Day(e) == day).ToList();
            md.AppendLine(CultureInfo.InvariantCulture, $"## {day:yyyy-MM-dd}").AppendLine();
            md.AppendLine("| AI line | Metered qty | Billed qty | Qty ratio | Metered $ | Billed $ | $ ratio |");
            md.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
            decimal aiMetered = 0, aiBilled = 0;
            var claimed = new HashSet<BillRow>();
            foreach (var line in Lines)
            {
                var rows = dayBill.Where(b => line.Bills(b.Meter)).ToList();
                claimed.UnionWith(rows);
                var mq = line.Quantity(dayEvents);
                var bq = rows.Sum(r => r.Quantity) * line.BillUnit;
                var mc = Cost(line, dayEvents, prices);
                var bc = rows.Sum(r => r.Cost);
                if (mq == 0 && bq == 0 && bc == 0)
                {
                    continue;
                }

                aiMetered += mc;
                aiBilled += bc;
                md.AppendLine(CultureInfo.InvariantCulture, $"| {line.Name} | {mq:#,##0} | {bq:#,##0} | {Ratio(mq, bq)} | {mc:0.0000} | {bc:0.0000} | {Ratio(mc, bc)} |");
            }

            md.AppendLine(CultureInfo.InvariantCulture, $"| **AI services** | | | | **{aiMetered:0.0000}** | **{aiBilled:0.0000}** | **{Ratio(aiMetered, aiBilled)}** |").AppendLine();
            var platform = dayBill.Where(b => !claimed.Contains(b)).GroupBy(b => b.Service).Select(g => (Service: g.Key, Cost: g.Sum(r => r.Cost), Meters: g.Where(r => r.Cost > 0)
                .Select(r => $"{r.Meter} ({r.Quantity:0.###})").Distinct().ToList())).OrderByDescending(p => p.Cost).ToList();
            md.AppendLine("| Platform (standing, not per document) | Billed $ | Billed meters |").AppendLine("|---|---:|---|");
            foreach (var p in platform)
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"| {p.Service} | {p.Cost:0.0000} | {string.Join("; ", p.Meters.Take(4))} |");
            }

            var platformTotal = platform.Sum(p => p.Cost);
            md.AppendLine(CultureInfo.InvariantCulture, $"| **Platform** | **{platformTotal:0.0000}** | |").AppendLine();
            aiMeteredAll += aiMetered;
            aiBilledAll += aiBilled;
            platformAll += platformTotal;
            summary.Add(new JsonObject
            {
                ["date"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["aiMeteredUsd"] = Math.Round(aiMetered, 4), ["aiBilledUsd"] = Math.Round(aiBilled, 4),
                ["platformBilledUsd"] = Math.Round(platformTotal, 4), ["meteredEvents"] = dayEvents.Count,
            });
        }

        md.AppendLine(CultureInfo.InvariantCulture, $"## Period: AI services metered ${aiMeteredAll:0.00} vs billed ${aiBilledAll:0.00} ({Ratio(aiMeteredAll, aiBilledAll)}); platform billed ${platformAll:0.00}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllTextAsync(outPath, md.ToString());
        var reconciliation = new CostReconciliation(Math.Round(aiMeteredAll, 4), Math.Round(aiBilledAll, 4), aiBilledAll == 0 ? 0 : Math.Round((double)(aiMeteredAll / aiBilledAll), 4), to,
            from, Math.Round(platformAll, 4), note ?? "AI services (Content Understanding, model and embedding tokens); platform costs are standing, not per document.");
        var json = new JsonObject { ["summary"] = JsonSerializer.SerializeToNode(reconciliation, CanonicalJson.Options), ["days"] = summary };
        await File.WriteAllTextAsync(Path.ChangeExtension(outPath, ".json"), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(md.ToString().Split('\n').Last(l => l.StartsWith("## Period", StringComparison.Ordinal)));
        if (publish)
        {
            var endpoint = Settings.Required("LOSSRUN_BLOB_ENDPOINT", "the storage Blob endpoint: terraform output storage_blob_endpoint");
            var blob = new BlobServiceClient(new Uri(endpoint), AzureCredentials.Default()).GetBlobContainerClient("extractions").GetBlobClient(CostReconciliationPath);
            await blob.UploadAsync(BinaryData.FromString(JsonSerializer.Serialize(reconciliation, CanonicalJson.Options)), overwrite: true);
            Console.WriteLine($"published → extractions/{CostReconciliationPath}");
        }

        return 0;
    }

    /// <summary>Where the API reads the latest reconciliation for the Costs screen (extractions container).</summary>
    public const string CostReconciliationPath = "reconciliation/latest.json";

    private static decimal Cost(Line line, IReadOnlyList<UsageEvent> events, PriceTable prices)
    {
        // Price each metered event through the same table as DocumentCost (cached input at the cached price).
        var relevant = events.Where(line.Relevant).ToList();
        // Compute prices one document's events: relabel so the whole day is one "document".
        if (relevant.Count == 0)
        {
            return 0;
        }

        var cost = CostCalculator.Compute("reconcile", "reconcile", "reconcile", relevant.Select(e => e with { DocumentId = "reconcile" }), prices);
        // Brief 24 step 7: the input pair is priced together (cached tokens are a subset of input) but reported on its own rows:
        // uncached input at the input price, cached input at the cached price, as the bill splits them.
        return line.PairMeter is { } pair
            ? cost.Breakdown.Where(l => l.Meter.StartsWith(pair, StringComparison.Ordinal)).Sum(l => l.CostUsd)
            : cost.CostUsd;
    }

    /// <summary>The configured lines' names, in report order (tests).</summary>
    internal static IReadOnlyList<string> LineNames(IEnumerable<string> chatModels, string? embeddingModel, PriceTable prices) =>
        [.. LinesFor(chatModels, embeddingModel, prices).Select(l => l.Name)];

    /// <summary>The configured line a billed meter name is claimed by, if any (tests).</summary>
    internal static string? LineBilling(string billMeter, IEnumerable<string> chatModels, string? embeddingModel, PriceTable prices) =>
        LinesFor(chatModels, embeddingModel, prices).FirstOrDefault(l => l.Bills(billMeter))?.Name;

    /// <summary>The metered cost of one reconciliation line (tests and the report use the same pricing).</summary>
    public static decimal LineCost(string lineName, IReadOnlyList<UsageEvent> events) =>
        Cost(Lines.Single(l => l.Name == lineName), events, PriceTable.Embedded);

    private static decimal Sum(IReadOnlyList<UsageEvent> events, string service, string meter) =>
        events.Where(e => e.Service == service && e.Meter == meter).Sum(e => e.Quantity);

    private static decimal Sum(IReadOnlyList<UsageEvent> events, PriceTable prices, string meter, string model) =>
        events.Where(e => e.Service == "AzureOpenAI" && e.Meter == meter && string.Equals(prices.ModelOf(e), model, StringComparison.OrdinalIgnoreCase)).Sum(e => e.Quantity);

    private static DateOnly Day(UsageEvent e) => DateOnly.FromDateTime(e.TimestampUtc.UtcDateTime);

    private static string Ratio(decimal metered, decimal billed) => billed == 0 ? (metered == 0 ? "—" : "∞") : (metered / billed).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>One event per (correlation id, service, meter, model): result folders copy documents between runs, and the API rows overlap nothing else.</summary>
    private static IEnumerable<UsageEvent> Dedupe(IEnumerable<UsageEvent> events) =>
        events.GroupBy(e => (e.CorrelationId, e.Service, e.Meter, e.ModelOrAnalyzer, e.DocumentId)).Select(g => g.First());

    private static IEnumerable<UsageEvent> ReadRuns(string runsDir)
    {
        foreach (var file in Directory.EnumerateFiles(runsDir, "*.json", SearchOption.AllDirectories).Where(f => f.Contains($"{Path.DirectorySeparatorChar}docs{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            RunResult? result;
            try
            {
                result = JsonSerializer.Deserialize<RunResult>(File.ReadAllText(file), CanonicalJson.Options);
            }
            catch (JsonException)
            {
                continue;
            }

            foreach (var e in result?.Usage ?? [])
            {
                yield return e;
            }
        }
    }

    private static async Task<IReadOnlyList<UsageEvent>> ReadSqlAsync(string connection)
    {
        await using var db = new LossRunDbContext(LossRunDbContext.Options(connection));
        var rows = await db.UsageEvents.AsNoTracking().ToListAsync();
        return [.. rows.Select(r => new UsageEvent(r.DocumentId, r.RunId, r.Stage, r.Service, r.Meter, r.Quantity, r.Unit, r.ModelOrAnalyzer, r.DeploymentType, r.Region,
            new DateTimeOffset(DateTime.SpecifyKind(r.TimestampUtc, DateTimeKind.Utc)), r.CorrelationId ?? $"row-{r.Id}") { ModelVersion = r.ModelVersion })];
    }

    /// <summary>The bill: a Cost Management export CSV (or a folder of them), or a Cost Query API JSON response.</summary>
    public static IReadOnlyList<BillRow> ReadBill(string path)
    {
        if (Directory.Exists(path))
        {
            return [.. Directory.EnumerateFiles(path, "*.csv", SearchOption.AllDirectories).SelectMany(ReadBill)];
        }

        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var props = JsonNode.Parse(File.ReadAllText(path))!["properties"]!;
            var cols = props["columns"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToList();
            return [.. props["rows"]!.AsArray().Select(r =>
            {
                var a = r!.AsArray();
                string V(string name) => a[cols.IndexOf(name)]?.ToString() ?? "";
                var d = int.Parse(V("UsageDate"), CultureInfo.InvariantCulture);
                return new BillRow(new DateOnly(d / 10000, d / 100 % 100, d % 100), V("ServiceName"), V("MeterSubcategory"), V("Meter"),
                    decimal.Parse(V("UsageQuantity"), NumberStyles.Float, CultureInfo.InvariantCulture), decimal.Parse(V("Cost"), NumberStyles.Float, CultureInfo.InvariantCulture));
            })];
        }

        // Export CSV (ActualCost schema): date, meterCategory, meterSubCategory, meterName, quantity, costInBillingCurrency.
        var lines = File.ReadAllLines(path);
        var header = SplitCsv(lines[0]).Select(h => h.Trim('"', '﻿')).ToList();
        int Col(params string[] names) => names.Select(n => header.FindIndex(h => h.Equals(n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(i => i >= 0, -1);
        var (date, cat, sub, meter, qty, cost) = (Col("date", "UsageDate"), Col("meterCategory", "ServiceName"), Col("meterSubCategory"), Col("meterName", "Meter"),
            Col("quantity", "UsageQuantity"), Col("costInBillingCurrency", "CostInBillingCurrency", "cost", "Cost"));
        return [.. lines.Skip(1).Where(l => l.Length > 0).Select(SplitCsv).Select(f => new BillRow(
            DateOnly.FromDateTime(DateTime.Parse(f[date], CultureInfo.InvariantCulture)), f[cat], sub >= 0 ? f[sub] : "", f[meter],
            decimal.Parse(f[qty], NumberStyles.Float, CultureInfo.InvariantCulture), decimal.Parse(f[cost], NumberStyles.Float, CultureInfo.InvariantCulture)))];
    }

    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (ch == ',' && !quoted)
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(ch);
            }
        }

        fields.Add(sb.ToString());
        return fields;
    }
}
