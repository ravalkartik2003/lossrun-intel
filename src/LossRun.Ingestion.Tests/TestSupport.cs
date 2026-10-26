using LossRun.Contracts;
using LossRun.Ingestion;
using LossRun.Ingestion.ContentUnderstanding;
using LossRun.Ingestion.Metering;

namespace LossRun.Ingestion.Tests;

internal static class Fixture
{
    public const string Password = "synthetic-pass-04";

    public static byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    public static string Recorded(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Recorded", name));
}

/// <summary>Content Understanding fake: replays recorded responses (captured from the POC AI resource on synthetic fixtures).</summary>
internal sealed class FakeContentUnderstanding : IContentUnderstandingService
{
    public List<(string AnalyzerId, string ContentType, int Bytes)> Calls { get; } = [];
    public List<string> Ensured { get; } = [];

    /// <summary>Recorded classifier response to replay (default: the brief-04 bundle).</summary>
    public string ClassifierRecording { get; set; } = "lossrun_classifier_v1.bundle.json";

    public Task<CuCallResult> AnalyzeAsync(string analyzerId, BinaryData content, string contentType, CancellationToken cancellationToken)
    {
        Calls.Add((analyzerId, contentType, content.ToArray().Length));
        var json = analyzerId == "prebuilt-layout"
            ? Fixture.Recorded("prebuilt-layout.lossrun-native.json")
            : Fixture.Recorded(ClassifierRecording);
        return Task.FromResult(new CuCallResult(analyzerId, $"op-{Calls.Count}", json, TimeSpan.FromMilliseconds(5)));
    }

    public Task EnsureAnalyzerAsync(string analyzerId, CancellationToken cancellationToken)
    {
        Ensured.Add(analyzerId);
        return Task.CompletedTask;
    }
}

internal sealed class Harness
{
    public FakeContentUnderstanding Cu { get; } = new();
    public InMemoryCostMeter Meter { get; } = new();
    public InMemoryRawResultStore Store { get; } = new();

    public IngestionPipeline Pipeline(IngestionOptions? options = null) =>
        new(Cu, Meter, Store, PriceTable.Embedded, CanonicalData.Glossary, options ?? new IngestionOptions());
}

/// <summary>Runs only when LOSSRUN_AZURE_TESTS=1 (calls the AI resource in LOSSRUN_CU_ENDPOINT with DefaultAzureCredential; costs a few cents).</summary>
public sealed class AzureFactAttribute : FactAttribute
{
    public AzureFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LOSSRUN_AZURE_TESTS") != "1")
        {
            Skip = "Azure integration test: set LOSSRUN_AZURE_TESTS=1 (uses DefaultAzureCredential against LOSSRUN_CU_ENDPOINT).";
        }
    }
}
