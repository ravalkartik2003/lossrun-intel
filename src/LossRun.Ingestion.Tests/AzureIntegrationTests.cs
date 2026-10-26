using System.Text.Json;
using Azure.Storage.Blobs;
using LossRun.Contracts;
using LossRun.Ingestion;
using LossRun.Ingestion.ContentUnderstanding;
using LossRun.Ingestion.Metering;
using Xunit.Abstractions;

namespace LossRun.Ingestion.Tests;

/// <summary>
/// Real calls to the AI resource (keyless, DefaultAzureCredential; LOSSRUN_CU_ENDPOINT, LOSSRUN_STORAGE_BLOB_ENDPOINT). Opt-in: LOSSRUN_AZURE_TESTS=1.
/// Each test appends a measurement line (latency, usage, cost) to azure-measurements.jsonl for the brief 04 report.
/// </summary>
[Trait("Azure", "true")]
public class AzureIntegrationTests(ITestOutputHelper output)
{
    private static IngestionOptions Options => new()
    {
        ContentUnderstandingEndpoint = Settings.RequiredUri("LOSSRUN_CU_ENDPOINT", "terraform output content_understanding_endpoint"),
        Passwords = [Fixture.Password],
    };

    private static Uri BlobEndpoint => Settings.RequiredUri("LOSSRUN_STORAGE_BLOB_ENDPOINT", "terraform output storage_blob_endpoint");

    private async Task<IngestionResult> IngestAsync(string fixture, IRawResultStore? store = null, ICostMeter? meter = null)
    {
        var pipeline = new IngestionPipeline(new ContentUnderstandingService(Options.ContentUnderstandingEndpoint!, AzureCredentials.Default()),
            meter ?? new InMemoryCostMeter(), store ?? new InMemoryRawResultStore(), PriceTable.Embedded, CanonicalData.Glossary, Options);
        var result = await pipeline.IngestAsync(fixture, Fixture.Bytes(fixture));
        Record(fixture, result);
        return result;
    }

    [AzureFact]
    public async Task Layout_on_the_native_pdf()
    {
        var result = await IngestAsync("lossrun-native.pdf");

        var graph = Assert.Single(result.Documents);
        Assert.Equal(("prebuilt-layout", 1, 2), (graph.Engine, graph.Pages.Count, graph.Tables.Count));
        Assert.Contains(graph.Cells, c => c.RawValue == "WC-22-0001" && c.Polygon is { Count: 8 });
        Assert.Equal(0.005m, Assert.Single(result.Costs).CostUsd);
    }

    [AzureFact]
    public async Task Layout_on_the_scanned_pdf()
    {
        var result = await IngestAsync("lossrun-scanned.pdf");

        var graph = Assert.Single(result.Documents);
        Assert.Single(graph.Pages);
        // Measured on the 2°-rotated, speckled scan: CU reports the skew, keeps every claim number intact in the
        // markdown/paragraphs, but recovers the table structure of only one of the two policy-period tables and splits
        // some claim numbers into several OCR words ("WC" + "22-0001"). See report 04.
        Assert.InRange(graph.Pages[0].Angle!.Value, 0.5, 3.0);
        Assert.NotEmpty(graph.Tables);
        foreach (var claim in new[] { "WC-22-0001", "WC-23-0002", "WC-24-0003" })
        {
            Assert.Contains(claim, graph.Pages[0].Markdown);
            Assert.Contains(graph.Paragraphs, p => p.Text == claim);
        }
        output.WriteLine($"scan: angle {graph.Pages[0].Angle}, min word confidence {graph.Words.Min(w => w.Confidence):0.000}, mean {graph.Words.Average(w => w.Confidence):0.000}");
    }

    [AzureFact]
    public async Task Layout_on_the_locked_pdf_after_unlock()
    {
        var result = await IngestAsync("lossrun-locked.pdf");

        var graph = Assert.Single(result.Documents);
        Assert.True(graph.SourceFile.WasEncrypted);
        Assert.Equal(2, graph.Tables.Count);
    }

    [AzureFact]
    public async Task Classifier_splits_the_bundle_with_the_loss_run_first()
    {
        var result = await IngestAsync("bundle.pdf");

        var parent = result.Documents[0];
        Assert.Equal(3, parent.Segments.Count);
        Assert.Equal("LossRun", parent.Segments[0].Category);
        Assert.Equal((1, 1), (parent.Segments[0].StartPage, parent.Segments[0].EndPage));
        Assert.Equal(["Payroll", "Correspondence"], parent.Segments.Skip(1).Select(s => s.Category));
        var child = Assert.Single(result.Documents.Skip(1));
        Assert.Equal(parent.Segments[0].ChildDocumentId, child.DocumentId);
        Assert.Single(result.Calls); // one classifier call; no second layout call for the segment
    }

    [AzureFact]
    public async Task Raw_results_and_usage_go_to_blob_keylessly()
    {
        var container = new BlobServiceClient(BlobEndpoint, AzureCredentials.Default()).GetBlobContainerClient("extractions");
        var result = await IngestAsync("lossrun-native.pdf", new BlobRawResultStore(container), new BlobJsonlCostMeter(container));

        var graph = Assert.Single(result.Documents);
        Assert.Equal($"extractions/{graph.DocumentId}/layout.json", graph.SourceFile.RawResultBlobPath);
        var layout = await container.GetBlobClient($"{graph.DocumentId}/layout.json").DownloadContentAsync();
        Assert.Equal("Succeeded", JsonDocument.Parse(layout.Value.Content.ToString()).RootElement.GetProperty("status").GetString());
        var usage = (await container.GetBlobClient($"{graph.DocumentId}/usage.jsonl").DownloadContentAsync()).Value.Content.ToString();
        Assert.Contains("\"meter\":\"documentPagesStandard\"", usage);
    }

    private void Record(string fixture, IngestionResult result)
    {
        foreach (var call in result.Calls)
        {
            var cost = result.Costs.Single(c => c.DocumentId == call.DocumentId);
            var line = JsonSerializer.Serialize(new
            {
                fixture,
                call.AnalyzerId,
                call.Stage,
                latencySeconds = Math.Round(call.Latency.TotalSeconds, 2),
                usage = call.Usage.ToDictionary(u => u.Service == "AzureOpenAI" ? $"{u.ModelOrAnalyzer}:{u.Meter}" : u.Meter, u => u.Quantity),
                cost.CostUsd,
                cost.LlmCalls,
                documents = result.Documents.Select(d => new { d.DocumentId, pages = d.Pages.Count, tables = d.Tables.Count, cells = d.Cells.Count, words = d.Words.Count, angle = d.Pages.FirstOrDefault()?.Angle }),
                segments = result.Documents[0].Segments.Select(s => new { s.Category, s.StartPage, s.EndPage, s.Confidence }),
                recordedUtc = DateTimeOffset.UtcNow,
            }, CanonicalJson.Options);
            output.WriteLine(line);
            var dir = Environment.GetEnvironmentVariable("LOSSRUN_MEASUREMENTS_DIR") ?? AppContext.BaseDirectory;
            File.AppendAllText(Path.Combine(dir, "azure-measurements.jsonl"), line + Environment.NewLine);
        }
    }
}
