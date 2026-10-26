using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using LossRun.Contracts;

namespace LossRun.Ingestion.Metering;

/// <summary>Turns a Content Understanding <c>usage</c> block into UsageEvents (docs/architecture/cost-tracking.md §1).</summary>
public static class CuUsage
{
    /// <summary>
    /// One event per CU meter (documentPagesStandard, contextualizationTokens, …) and one per model token meter in
    /// <c>usage.tokens</c> (e.g. <c>gpt-5.4-mini-input</c> → AzureOpenAI inputTokens on model gpt-5.4-mini).
    /// </summary>
    public static IReadOnlyList<UsageEvent> ToEvents(JsonElement operation, string documentId, string runId, string stage, string analyzerId,
        string region, string modelDeploymentType, string? correlationId, DateTimeOffset timestamp)
    {
        var events = new List<UsageEvent>();
        if (!operation.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return events;
        }

        foreach (var meter in usage.EnumerateObject())
        {
            if (meter.Name == "tokens" && meter.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var token in meter.Value.EnumerateObject())
                {
                    var (model, tokenMeter) = SplitTokenKey(token.Name);
                    events.Add(new UsageEvent(documentId, runId, stage, "AzureOpenAI", tokenMeter, token.Value.GetDecimal(), "token",
                        model, modelDeploymentType, region, timestamp, correlationId));
                }
            }
            else if (meter.Value.ValueKind == JsonValueKind.Number)
            {
                events.Add(new UsageEvent(documentId, runId, stage, "ContentUnderstanding", meter.Name, meter.Value.GetDecimal(), UnitOf(meter.Name),
                    analyzerId, null, region, timestamp, correlationId));
            }
        }

        return events;
    }

    private static (string Model, string Meter) SplitTokenKey(string key)
    {
        foreach (var (suffix, meter) in new[] { ("-cachedInput", "cachedInputTokens"), ("-cached-input", "cachedInputTokens"), ("-input", "inputTokens"), ("-output", "outputTokens") })
        {
            if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return (key[..^suffix.Length], meter);
            }
        }

        return (key, "inputTokens"); // embedding models report a single count
    }

    private static string UnitOf(string meter) =>
        meter.Contains("Pages", StringComparison.OrdinalIgnoreCase) ? "page"
        : meter.Contains("Tokens", StringComparison.OrdinalIgnoreCase) ? "token"
        : meter.Contains("Hours", StringComparison.OrdinalIgnoreCase) ? "hour"
        : "unit";
}

/// <summary>Keeps events in memory (tests, the CLI, and per-run cost roll-up).</summary>
public sealed class InMemoryCostMeter : ICostMeter
{
    private readonly ConcurrentQueue<UsageEvent> _events = new();

    public IReadOnlyList<UsageEvent> Events => [.. _events];

    public ValueTask RecordAsync(UsageEvent usage, CancellationToken cancellationToken = default)
    {
        _events.Enqueue(usage);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Appends one JSON line per event to <c>extractions/{documentId}/usage.jsonl</c> (append blob, identity auth).</summary>
public sealed class BlobJsonlCostMeter(BlobContainerClient container) : ICostMeter
{
    public async ValueTask RecordAsync(UsageEvent usage, CancellationToken cancellationToken = default)
    {
        var blob = container.GetAppendBlobClient($"{usage.DocumentId}/usage.jsonl");
        await blob.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var line = JsonSerializer.Serialize(usage, CanonicalJson.Options) + "\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(line));
        await blob.AppendBlockAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Fans an event out to several sinks.</summary>
public sealed class CompositeCostMeter(params ICostMeter[] meters) : ICostMeter
{
    public async ValueTask RecordAsync(UsageEvent usage, CancellationToken cancellationToken = default)
    {
        foreach (var meter in meters)
        {
            await meter.RecordAsync(usage, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <param name="BillMeterPrefixes">Brief 31 item 22: the Cost Management meter names this price is billed under (prefix match), for <c>lossrun eval reconcile</c>;
/// the bill's names are not the retail meter names ("5.4 mini Inp", not "gpt-5.4-mini Dz input").</param>
/// <param name="BillUnit">Tokens per billed unit on those meters (1,000,000 for model tokens, 1,000 for embeddings).</param>
public sealed record PriceEntry(string Service, string Meter, string? Model, string? DeploymentType, string Unit, decimal UnitPrice, string? RetailMeter, string Source, string RetrievedOn,
    IReadOnlyList<string>? BillMeterPrefixes = null, decimal? BillUnit = null);

/// <summary>config/azure-prices.json: unit prices per service/meter(/model). Prices are configuration, never code.</summary>
public sealed class PriceTable
{
    private PriceTable(string version, string region, IReadOnlyList<PriceEntry> prices, IReadOnlyDictionary<string, string>? deployments = null)
    {
        Version = version;
        Region = region;
        Prices = prices;
        Deployments = deployments ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Brief 30 item 22: deployment name → model name, for deployments named apart from their model (Terraform's <c>Models__DeploymentModels</c>).</summary>
    public IReadOnlyDictionary<string, string> Deployments { get; }

    public PriceTable WithDeployments(IReadOnlyDictionary<string, string> deployments) =>
        new(Version, Region, Prices, new Dictionary<string, string>(deployments, StringComparer.OrdinalIgnoreCase));

    /// <summary>"deployment=model;deployment=model" (an app setting) → the map.</summary>
    public PriceTable WithDeployments(string? text) => WithDeployments((text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p => p.Split('=', 2, StringSplitOptions.TrimEntries)).Where(p => p.Length == 2 && p[0].Length > 0 && p[1].Length > 0)
        .GroupBy(p => p[0], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First()[1], StringComparer.OrdinalIgnoreCase));

    /// <summary>Brief 30 item 22: config/azure-prices.json from the app's config path when it is there (prices change without a build), else the embedded copy.</summary>
    public static PriceTable FromFileOrEmbedded(string? path)
    {
        if (path is { Length: > 0 } && File.Exists(path))
        {
            try
            {
                return Load(File.ReadAllText(path));
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
            {
                Console.Error.WriteLine($"price table {path} unreadable ({e.GetType().Name}); using the embedded copy");
            }
        }

        return Embedded;
    }

    /// <summary>The model a usage event is priced as: its model name, else the deployment's model, else the deployment name itself.</summary>
    public string? ModelOf(UsageEvent usage) => usage.ModelName
        ?? (usage.ModelOrAnalyzer is { } d && Deployments.TryGetValue(d, out var model) ? model : usage.ModelOrAnalyzer);

    public string Version { get; }
    public string Region { get; }
    public IReadOnlyList<PriceEntry> Prices { get; }

    /// <summary>The table embedded from config/azure-prices.json at build time.</summary>
    public static PriceTable Embedded { get; } = Load(ReadEmbedded());

    public static PriceTable Load(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var prices = root.GetProperty("prices").Deserialize<List<PriceEntry>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Price table has no prices.");
        return new PriceTable(root.GetProperty("priceTableVersion").GetString()!, root.GetProperty("region").GetString()!, prices);
    }

    public PriceEntry? Find(UsageEvent usage)
    {
        var model = ModelOf(usage);
        return Prices.FirstOrDefault(p => p.Service == usage.Service && p.Meter == usage.Meter
            && (p.Model is null || string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase)));
    }

    private static string ReadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LossRun.Config.azure-prices.json")
            ?? throw new InvalidOperationException("Embedded config/azure-prices.json not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>Rolls UsageEvents up into a DocumentCost (cost-tracking.md §3).</summary>
public static class CostCalculator
{
    /// <summary>UsageEvent stage of fingerprint embeddings (brief 05).</summary>
    public const string EmbeddingStage = "fingerprint";

    /// <summary>Chat stages (brief 12): question embeddings, chunk embeddings at indexing, model turns, search queries.</summary>
    public const string ChatEmbeddingStage = "chat-embedding";
    public const string ChatIndexStage = "chat-index";
    public const string ChatStage = "chat";

    /// <summary>Embedding stages: model requests that are not "LLM calls".</summary>
    public static bool IsEmbedding(string stage) => stage is EmbeddingStage or ChatEmbeddingStage or ChatIndexStage;

    public static DocumentCost Compute(string documentId, string runId, string path, IEnumerable<UsageEvent> events, PriceTable prices)
    {
        var list = events.Where(e => e.DocumentId == documentId).ToList();
        var lines = BillableQuantities(list)
            .GroupBy(e => (e.Service, e.Meter, e.ModelOrAnalyzer, e.Unit))
            .Select(g =>
            {
                var quantity = g.Sum(e => e.Quantity);
                var price = prices.Find(g.First());
                var unitPrice = price?.UnitPrice ?? 0m;
                return new CostLine(g.Key.Service, g.Key.ModelOrAnalyzer is { } m ? $"{g.Key.Meter} ({m})" : g.Key.Meter, quantity, g.Key.Unit,
                    unitPrice, quantity * unitPrice, price?.Source ?? DocumentCost.MissingPrice);
            })
            .OrderBy(l => l.Service).ThenBy(l => l.Meter)
            .ToList();

        // Embeddings (fingerprint tier) are model requests but not "LLM calls"; they are counted separately.
        var model = list.Where(e => e.Service == "AzureOpenAI" && !IsEmbedding(e.Stage)).ToList();
        var embeddingCalls = list.Where(e => e.Service == "AzureOpenAI" && IsEmbedding(e.Stage)).Select(e => e.CorrelationId).Distinct().Count();
        return new DocumentCost(
            documentId,
            runId,
            path,
            LlmCalls: model.Select(e => e.CorrelationId).Distinct().Count(),
            Pages: (int)list.Where(e => e.Service == "ContentUnderstanding" && e.Unit == "page").Sum(e => e.Quantity),
            TokensIn: (long)model.Where(e => e.Meter == "inputTokens").Sum(e => e.Quantity), // includes cached tokens
            TokensOut: (long)model.Where(e => e.Meter == "outputTokens").Sum(e => e.Quantity),
            CostUsd: lines.Sum(l => l.CostUsd),
            Breakdown: lines,
            EmbeddingCalls: embeddingCalls);
    }

    /// <summary>
    /// Cached prompt tokens are a subset of input tokens (Azure OpenAI usage semantics, which CU's per-model token
    /// counts are assumed to follow): bill (input − cached) at the input price and cached at the cached price.
    /// </summary>
    private static IEnumerable<UsageEvent> BillableQuantities(List<UsageEvent> events)
    {
        var cached = events.Where(e => e.Meter == "cachedInputTokens")
            .GroupBy(e => (e.CorrelationId, e.ModelOrAnalyzer))
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Quantity));
        return events.Select(e => e.Meter == "inputTokens" && cached.TryGetValue((e.CorrelationId, e.ModelOrAnalyzer), out var c)
            ? e with { Quantity = Math.Max(0, e.Quantity - c) }
            : e);
    }

    /// <summary>One-line summary for the CLI and the UI badge, e.g. "$0.005 · CU layout 1 page · 0 LLM calls".</summary>
    public static string Summary(DocumentCost cost, string engineLabel) =>
        (cost.UnpricedLines > 0 ? $"unpriced ({cost.UnpricedLines} line(s) without a price; ${cost.CostUsd:0.000###} priced)" : $"${cost.CostUsd:0.000###}")
        + $" · {engineLabel} {cost.Pages} page{(cost.Pages == 1 ? "" : "s")} · {cost.LlmCalls} LLM call{(cost.LlmCalls == 1 ? "" : "s")}";
}
