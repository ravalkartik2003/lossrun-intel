using System.Text.Json;
using System.Text.Json.Nodes;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Ingestion.ContentUnderstanding;
using LossRun.Learning;

namespace LossRun.Learning.Tests;

/// <summary>Graphs built offline from real Content Understanding responses recorded on the synthetic fixtures.</summary>
internal static class Graphs
{
    public static string File(params string[] parts) => Path.Combine([AppContext.BaseDirectory, .. parts]);

    public static DocumentGraph Native() => FromRecorded("prebuilt-layout.lossrun-native.json", "doc-native");

    public static DocumentGraph Scanned() => FromRecorded("prebuilt-layout.lossrun-scanned.json", "doc-scanned");

    /// <summary>Second carrier ("Fictional Casualty Group"), native text layer.</summary>
    public static DocumentGraph Second() => FromRecorded("prebuilt-layout.lossrun2-native.json", "doc-second");

    public static DocumentGraph SecondScanned() => FromRecorded("prebuilt-layout.lossrun2-scanned.json", "doc-second-scanned");

    /// <summary>Synthetic Mutual v2: "Reported" renamed to "Date Rptd", "Lit" column added.</summary>
    public static DocumentGraph Drifted() => FromRecorded("prebuilt-layout.lossrun-drifted.json", "doc-drifted");

    public static DocumentGraph Xlsx() => Sheet("lossrun-stacked.xlsx", FileKind.Xlsx, "doc-xlsx");

    public static DocumentGraph Csv() => Sheet("lossrun-export.csv", FileKind.Csv, "doc-csv");

    private static DocumentGraph Sheet(string name, FileKind kind, string id) =>
        LossRun.Ingestion.Spreadsheets.SpreadsheetReader.Read(System.IO.File.ReadAllBytes(File("fixtures", name)), new SourceFile { FileName = name, Kind = kind }, id,
            CanonicalData.Glossary, []);

    public static CarrierFormat Spec(string formatId) => FormatJson.Parse(System.IO.File.ReadAllText(File("formats", formatId, "1.json")));

    public static string SecondProposal => System.IO.File.ReadAllText(File("Specs", "fictional-casualty.proposal.json"));

    /// <summary>The reference proposal with one column mapping removed (a plausible first-attempt mistake).</summary>
    public static string SecondProposalWithout(string source)
    {
        var node = JsonNode.Parse(SecondProposal)!.AsObject();
        var map = node["columnMap"]!.AsArray();
        foreach (var entry in map.Where(e => e!["source"]!.GetValue<string>() == source).ToList())
        {
            map.Remove(entry);
        }

        return node.ToJsonString();
    }

    /// <summary>The hand specs at v1 (tests learn, drift and supersede from v1; brief 08c added synthetic-mutual-pdf v2).</summary>
    public static async Task<InMemoryFormatRegistry> RegistryAsync(int? maxVersion = 1)
    {
        var registry = new InMemoryFormatRegistry();
        await FormatDirectory.LoadIntoAsync(File("formats"), registry, maxVersion);
        return registry;
    }

    private static DocumentGraph FromRecorded(string name, string id)
    {
        var json = JsonDocument.Parse(System.IO.File.ReadAllText(File("Recorded", name))).RootElement;
        return CuResultMapper.Map(json, id, new SourceFile { FileName = name, Kind = FileKind.Pdf }, "prebuilt-layout", CanonicalData.Glossary, null, null);
    }

    public static LearnOptions Options(int maxIterations = 3, bool image = false) => new()
    {
        LearnDeployment = "learn-deployment",
        LearnModelVersion = "test-version",
        DeltaDeployment = "delta-deployment",
        DeltaModelVersion = "test-version",
        MaxIterations = maxIterations,
        IncludePageImage = image,
    };
}

/// <summary>
/// Offline stand-in for the Responses API: answers from a script (per stage), records every request, and reports token
/// usage the way the service does (the static prefix is cached from the second call on).
/// </summary>
internal sealed class FakeLearnModel(Func<ModelRequest, int, string> answer) : ILearnModel
{
    public List<ModelRequest> Requests { get; } = [];

    public int Calls => Requests.Count;

    public int CallsFor(string stage) => Requests.Count(r => r.Stage == stage);

    public static FakeLearnModel Sequence(params string[] answers) => new((_, i) => answers[Math.Min(i, answers.Length - 1)]);

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        var index = Requests.Count;
        Requests.Add(request);
        var json = answer(request, index);
        var prefix = request.Instructions.Length / 4;
        var input = prefix + request.Turns.Sum(t => t.Text.Length) / 4;
        var cached = index == 0 ? 0 : prefix / 128 * 128;
        var output = json.Length / 4;
        var correlation = $"fake-{index}";
        var usage = ModelUsage.Events(request, input, cached, output, correlation, "DataZoneStandard", "eastus2", DateTimeOffset.UnixEpoch);
        return Task.FromResult(new ModelResponse(json, input, cached, output, 0, TimeSpan.FromMilliseconds(10), correlation, usage));
    }
}
