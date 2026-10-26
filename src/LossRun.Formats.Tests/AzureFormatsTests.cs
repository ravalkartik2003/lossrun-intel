using System.Text.Json;
using Azure.Storage.Blobs;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Ingestion;
using LossRun.Ingestion.Metering;
using Xunit.Abstractions;

namespace LossRun.Formats.Tests;

/// <summary>Runs only when LOSSRUN_AZURE_TESTS=1: embeddings on the AI resource and the Blob registry (keyless; LOSSRUN_AOAI_ENDPOINT, LOSSRUN_STORAGE_BLOB_ENDPOINT).</summary>
public sealed class AzureFactAttribute : FactAttribute
{
    public AzureFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LOSSRUN_AZURE_TESTS") != "1")
        {
            Skip = "Azure integration test: set LOSSRUN_AZURE_TESTS=1.";
        }
    }
}

[Trait("Azure", "true")]
public class AzureFormatsTests(ITestOutputHelper output)
{
    private static Uri Endpoint => Settings.RequiredUri("LOSSRUN_AOAI_ENDPOINT", "the Azure OpenAI endpoint: terraform output ai_endpoint");
    private static readonly string Model = Environment.GetEnvironmentVariable("LOSSRUN_EMBEDDING_MODEL") ?? "text-embedding-3-large";
    /// <summary>Opt-in tests write here, never to the live registry container (brief 05 Answers 6).</summary>
    internal const string TestContainer = "formats-test";

    private static Uri Blob => Settings.RequiredUri("LOSSRUN_STORAGE_BLOB_ENDPOINT", "terraform output storage_blob_endpoint");

    [AzureFact]
    public async Task Embedding_fingerprint_matches_native_and_scanned_but_not_payroll()
    {
        var meter = new InMemoryCostMeter();
        var service = new AzureOpenAiEmbeddingService(Endpoint, AzureCredentials.Default(), Model, meter, "eastus2", "DataZoneStandard");
        var graphs = new Dictionary<string, DocumentGraph>
        {
            ["native"] = Graphs.Native(),
            ["scanned"] = Graphs.Scanned(),
            ["bundle-lossrun"] = Graphs.BundleLossRun(),
            ["bundle-payroll"] = Graphs.BundlePayroll(),
        };

        var normalized = new Dictionary<string, EmbeddingResult>();
        var raw = new Dictionary<string, EmbeddingResult>();
        foreach (var (name, graph) in graphs)
        {
            normalized[name] = await service.EmbedAsync(FingerprintText.ForEmbedding(graph), graph.DocumentId, "azure-test", CancellationToken.None);
            raw[name] = await service.EmbedAsync(graph.FingerprintInputs.FirstPageMarkdown ?? "", graph.DocumentId, "azure-test", CancellationToken.None);
        }

        var centroid = Vectors.Centroid([normalized["native"].Vector]);
        var cosines = graphs.Keys.ToDictionary(k => k, k => Math.Round(Vectors.Cosine(normalized[k].Vector, centroid), 4));
        var rawCosines = graphs.Keys.ToDictionary(k => k, k => Math.Round(Vectors.Cosine(raw[k].Vector, raw["native"].Vector), 4));
        var measurement = new
        {
            model = Model,
            dimensions = normalized["native"].Vector.Length,
            cosineToNativeCentroid = cosines,
            rawMarkdownCosineToNative = rawCosines,
            latencyMs = normalized.ToDictionary(p => p.Key, p => Math.Round(p.Value.Latency.TotalMilliseconds)),
            tokens = normalized.ToDictionary(p => p.Key, p => p.Value.Usage[0].Quantity),
            rawTokens = raw.ToDictionary(p => p.Key, p => p.Value.Usage[0].Quantity),
            costUsd = CostCalculator.Compute("doc-native", "azure-test", "fingerprint", meter.Events.Select(e => e with { DocumentId = "doc-native" }), PriceTable.Embedded).CostUsd,
            calls = meter.Events.Count,
        };
        var line = JsonSerializer.Serialize(measurement);
        output.WriteLine(line);
        File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("LOSSRUN_MEASUREMENTS_DIR") ?? AppContext.BaseDirectory, "formats-measurements.jsonl"), line + Environment.NewLine);

        Assert.True(cosines["scanned"] >= 0.95, $"scanned {cosines["scanned"]}");
        Assert.True(cosines["bundle-lossrun"] >= 0.95, $"bundle loss run {cosines["bundle-lossrun"]}");
        Assert.True(cosines["bundle-payroll"] < 0.95, $"payroll {cosines["bundle-payroll"]}");
        Assert.All(meter.Events, e => Assert.Equal(("fingerprint", "AzureOpenAI"), (e.Stage, e.Service)));
    }

    [AzureFact]
    public async Task Blob_registry_is_publish_only()
    {
        var container = new BlobServiceClient(Blob, AzureCredentials.Default()).GetBlobContainerClient(TestContainer);
        var registry = new BlobFormatRegistry(container);
        var spec = Graphs.Spec("synthetic-mutual-pdf");
        spec.FormatId = $"azure-test-{DateTime.UtcNow:yyyyMMddHHmmss}"; // unique per run; versions are never overwritten or deleted

        await registry.PublishAsync(spec);
        await Assert.ThrowsAsync<FormatAlreadyPublishedException>(() => registry.PublishAsync(spec));

        var back = await registry.GetAsync(spec.FormatId);
        Assert.Equal(FormatJson.Write(spec), FormatJson.Write(back!));
        Assert.Contains(await registry.GetIndexAsync(), e => e.FormatId == spec.FormatId && e.Version == 1);
        output.WriteLine($"published and re-read {spec.Ref} in {TestContainer}/");
    }

    [AzureFact]
    public async Task Blob_stats_updates_are_etag_guarded()
    {
        var registry = new BlobFormatRegistry(new BlobServiceClient(Blob, AzureCredentials.Default()).GetBlobContainerClient(TestContainer));
        var formatId = $"azure-stats-{DateTime.UtcNow:yyyyMMddHHmmss}";

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => registry.UpdateStatsAsync(formatId, s =>
        {
            var stat = s.Fields.TryGetValue("claim.claimNumber", out var x) ? x : s.Fields["claim.claimNumber"] = new FieldStat();
            stat.SuccessCount++;
        })));

        Assert.Equal(6, (await registry.GetStatsAsync(formatId)).Fields["claim.claimNumber"].SuccessCount);
        output.WriteLine($"6 concurrent stats updates on {TestContainer}/{formatId}/stats.json: none lost");
    }
}
