using System.Text.Json;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Ingestion.ContentUnderstanding;
using LossRun.Ingestion.Spreadsheets;

namespace LossRun.Formats.Tests;

/// <summary>Graphs built offline from recorded CU responses (synthetic fixtures) and from the spreadsheet fixtures.</summary>
internal static class Graphs
{
    public static string File(params string[] parts) => Path.Combine([AppContext.BaseDirectory, .. parts]);

    public static DocumentGraph Native() => FromRecorded("prebuilt-layout.lossrun-native.json", "doc-native");

    public static DocumentGraph Scanned() => FromRecorded("prebuilt-layout.lossrun-scanned.json", "doc-scanned");

    /// <summary>The loss-run segment (page 1) of the classified bundle.</summary>
    public static DocumentGraph BundleLossRun() => FromRecorded("lossrun_classifier_v1.bundle.json", "doc-bundle-p1-1", 1, 1, "lossrun_classifier_v1");

    /// <summary>The payroll page (page 2) of the bundle — must not match the loss-run format.</summary>
    public static DocumentGraph BundlePayroll() => FromRecorded("lossrun_classifier_v1.bundle.json", "doc-bundle-p2-2", 2, 2, "lossrun_classifier_v1");

    public static DocumentGraph Xlsx() => Sheet("lossrun-stacked.xlsx", FileKind.Xlsx, "doc-xlsx");

    public static DocumentGraph Csv() => Sheet("lossrun-export.csv", FileKind.Csv, "doc-csv");

    /// <summary>Brief 23: a run with a census-only (report-only) claim and a general-liability row, in the seen CSV format.</summary>
    public static DocumentGraph MixedLinesCsv() => Sheet("lossrun-mixed-lines.csv", FileKind.Csv, "doc-mixed");

    public static CarrierFormat Spec(string formatId) => FormatJson.Parse(System.IO.File.ReadAllText(File("formats", formatId, "1.json")));

    public static DocumentGraph Copy(DocumentGraph graph) =>
        JsonSerializer.Deserialize<DocumentGraph>(JsonSerializer.Serialize(graph, CanonicalJson.Options), CanonicalJson.Options)!;

    private static DocumentGraph FromRecorded(string name, string id, int? first = null, int? last = null, string engine = "prebuilt-layout")
    {
        var json = JsonDocument.Parse(System.IO.File.ReadAllText(File("Recorded", name))).RootElement;
        return CuResultMapper.Map(json, id, new SourceFile { FileName = name, Kind = FileKind.Pdf, FirstPage = first, LastPage = last }, engine,
            CanonicalData.Glossary, first, last);
    }

    private static DocumentGraph Sheet(string name, FileKind kind, string id) =>
        SpreadsheetReader.Read(System.IO.File.ReadAllBytes(File("fixtures", name)), new SourceFile { FileName = name, Kind = kind }, id, CanonicalData.Glossary, []);

    /// <summary>The hand specs at v1 (tests learn, drift and supersede from v1; brief 08c added synthetic-mutual-pdf v2).</summary>
    public static async Task<InMemoryFormatRegistry> RegistryAsync(int? maxVersion = 1)
    {
        var registry = new InMemoryFormatRegistry();
        await FormatDirectory.LoadIntoAsync(File("formats"), registry, maxVersion);
        return registry;
    }
}

/// <summary>
/// Deterministic offline stand-in for the embedding model: hashed bag-of-words (512 dims). Texts that share most words
/// (native vs scanned loss run) are close; a payroll page is not. Real cosines are measured in the Azure test.
/// </summary>
internal sealed class FakeEmbeddingService(string model = "text-embedding-3-large") : IEmbeddingService
{
    public int Calls { get; private set; }

    public Task<EmbeddingResult> EmbedAsync(string text, string documentId, string runId, CancellationToken cancellationToken)
    {
        Calls++;
        var vector = new float[512];
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var hash = 0;
            foreach (var ch in token)
            {
                hash = unchecked(hash * 31 + ch);
            }

            vector[(hash & int.MaxValue) % vector.Length] += 1;
        }

        var usage = new UsageEvent(documentId, runId, "fingerprint", "AzureOpenAI", "inputTokens", text.Length / 4, "token", model, "DataZoneStandard", "eastus2",
            DateTimeOffset.UnixEpoch, $"fake-{Calls}");
        return Task.FromResult(new EmbeddingResult(vector, model, TimeSpan.Zero, [usage]));
    }

    public static async Task SeedCentroidAsync(IFormatRegistry registry, string formatId, params DocumentGraph[] exemplars)
    {
        var fake = new FakeEmbeddingService();
        var vectors = new List<IReadOnlyList<float>>();
        foreach (var graph in exemplars)
        {
            vectors.Add((await fake.EmbedAsync(FingerprintText.ForEmbedding(graph), graph.DocumentId, "seed", CancellationToken.None)).Vector);
        }

        await registry.SaveCentroidAsync(new FormatCentroid { FormatId = formatId, Model = "text-embedding-3-large", ExemplarCount = vectors.Count, Vector = Vectors.Centroid(vectors) });
    }
}
