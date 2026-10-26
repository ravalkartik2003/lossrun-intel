using System.Text.Json;
using System.Text.Json.Nodes;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Formats.Replay;
using LossRun.Ingestion.ContentUnderstanding;

namespace LossRun.Formats.Tests;

/// <summary>
/// The CU inline (sync) analyze returns the same result as the asynchronous one without any "span" (measured in
/// brief 07). Without the fallback the scan's fingerprint changed (embedding cosine 0.097) and it was re-learned.
/// </summary>
public class InlineResultTests
{
    private static JsonNode StripSpans(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                o.Remove("span");
                o.Remove("spans");
                foreach (var child in o.Select(p => p.Value).Where(v => v is not null).ToList())
                {
                    StripSpans(child!);
                }

                break;
            case JsonArray a:
                foreach (var child in a.Where(v => v is not null))
                {
                    StripSpans(child!);
                }

                break;
        }

        return node;
    }

    private static DocumentGraph Map(string recorded, bool inline)
    {
        var text = File.ReadAllText(Graphs.File("Recorded", recorded));
        var json = inline ? StripSpans(JsonNode.Parse(text)!).ToJsonString() : text;
        return CuResultMapper.Map(JsonDocument.Parse(json).RootElement, "doc", new SourceFile { FileName = recorded, Kind = FileKind.Pdf }, "prebuilt-layout",
            CanonicalData.Glossary);
    }

    [Theory]
    [InlineData("prebuilt-layout.lossrun-native.json", "synthetic-mutual-pdf")]
    [InlineData("prebuilt-layout.lossrun-scanned.json", "synthetic-mutual-pdf")]
    [InlineData("prebuilt-layout.lossrun2-native.json", null)]
    public void A_result_without_spans_fingerprints_and_replays_like_the_async_one(string recorded, string? formatId)
    {
        var async = Map(recorded, inline: false);
        var inline = Map(recorded, inline: true);

        Assert.Equal(FingerprintText.Compute(async), FingerprintText.Compute(inline));
        Assert.Equal(FingerprintText.ForEmbedding(async), FingerprintText.ForEmbedding(inline));
        Assert.Equal(async.Pages[0].Markdown?.Length > 0, inline.Pages[0].Markdown?.Length > 0);
        if (formatId is not null)
        {
            var spec = Graphs.Spec(formatId);
            Assert.Equal(ReplayEngine.Execute(async, spec).Document.PolicyPeriods.Sum(p => p.Claims.Count),
                ReplayEngine.Execute(inline, spec).Document.PolicyPeriods.Sum(p => p.Claims.Count));
            Assert.True(Rules.RuleEvaluator.PassesHardRules(ReplayEngine.Execute(inline, spec).RuleOutcomes));
        }
    }
}
