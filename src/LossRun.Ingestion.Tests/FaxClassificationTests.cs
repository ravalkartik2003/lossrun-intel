using System.Text.Json;
using LossRun.Ingestion.ContentUnderstanding;

namespace LossRun.Ingestion.Tests;

/// <summary>
/// Brief 08d: the c2-013 fax (corpus v2) that CU classified as a loss run but our derived check dropped (0.40 on exact header
/// matching against OCR-misread headers). Recorded classifier response: Recorded/lossrun_classifier.fax-c2-013.json.
/// </summary>
public class FaxClassificationTests
{
    private const string FaxRecording = "lossrun_classifier.fax-c2-013.json";

    private static IngestionOptions Classify => new() { ClassifyWhenPagesAtLeast = 1, FirstPageCheck = false };

    private static Harness Fax(string? category = null)
    {
        var harness = new Harness();
        harness.Cu.ClassifierRecording = FaxRecording;
        if (category is not null)
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Recorded");
            var name = $"lossrun_classifier.fax-c2-013.{category}.json";
            File.WriteAllText(Path.Combine(dir, name), Fixture.Recorded(FaxRecording).Replace("\"category\":\"LossRun\"", $"\"category\":\"{category}\"", StringComparison.Ordinal)
                .Replace("\"category\": \"LossRun\"", $"\"category\": \"{category}\"", StringComparison.Ordinal));
            harness.Cu.ClassifierRecording = name;
        }

        return harness;
    }

    [Fact]
    public async Task A_scanned_fax_cu_calls_a_loss_run_is_kept_with_ocr_tolerant_header_resolution()
    {
        var harness = Fax();

        var result = await harness.Pipeline(Classify).IngestAsync("c2-013.pdf", Fixture.Bytes("lossrun-scanned.pdf"));

        var segment = Assert.Single(result.Documents[0].Segments);
        Assert.Equal("LossRun", segment.Category);
        Assert.NotEqual("notLossRun", segment.DerivedDecision);
        Assert.True(segment.DerivedConfidence >= 0.5, $"derived {segment.DerivedConfidence}");
        Assert.Contains("OCR-tolerant", segment.DerivedConfidenceMethod);
        Assert.Equal(2, result.Documents.Count); // parent + the loss-run child
    }

    [Fact]
    public async Task On_exact_matching_a_single_document_cu_calls_a_loss_run_is_reviewed_not_dropped()
    {
        var harness = Fax();

        // Born-digital bytes: exact header resolution (0.40 on this recording) → notLossRun, floored to review for a single document.
        var result = await harness.Pipeline(Classify).IngestAsync("c2-013.pdf", Fixture.Bytes("lossrun-native.pdf"));

        var segment = Assert.Single(result.Documents[0].Segments);
        Assert.Equal(0.4, segment.DerivedConfidence);
        Assert.Equal("review", segment.DerivedDecision);
        Assert.Contains("single-document upload kept for review", segment.DerivedConfidenceMethod);
        Assert.Equal(2, result.Documents.Count);
    }

    [Fact]
    public async Task When_cu_says_other_a_dense_single_document_with_loss_run_headers_falls_back_to_review()
    {
        var harness = Fax("Other");

        var result = await harness.Pipeline(Classify).IngestAsync("c2-013.pdf", Fixture.Bytes("lossrun-scanned.pdf"));

        var segment = Assert.Single(result.Documents[0].Segments);
        Assert.Equal("Other", segment.Category);
        Assert.True(segment.DerivedDecision == "review", segment.DerivedConfidenceMethod);
        Assert.Contains("fallback", segment.DerivedConfidenceMethod);
        Assert.Equal(2, result.Documents.Count);
    }

    [Fact]
    public async Task A_specific_other_category_is_never_overridden()
    {
        var harness = Fax("Payroll");

        var result = await harness.Pipeline(Classify).IngestAsync("c2-013.pdf", Fixture.Bytes("lossrun-scanned.pdf"));

        Assert.Single(result.Documents); // payroll stays payroll: no loss-run child
        Assert.Null(Assert.Single(result.Documents[0].Segments).DerivedDecision);
    }

    [Fact]
    public void Classifier_v2_names_fax_and_scan_renderings_and_keeps_the_categories()
    {
        var v1 = JsonDocument.Parse(AnalyzerDefinitions.Get("lossrun_classifier_v1")).RootElement.GetProperty("config").GetProperty("contentCategories");
        var v2 = JsonDocument.Parse(AnalyzerDefinitions.Get("lossrun_classifier_v2")).RootElement.GetProperty("config").GetProperty("contentCategories");

        Assert.Equal(v1.EnumerateObject().Select(p => p.Name), v2.EnumerateObject().Select(p => p.Name));
        Assert.Contains("fax", v2.GetProperty("LossRun").GetProperty("description").GetString());
        Assert.Equal("lossrun_classifier_v2", new IngestionOptions().ClassifierAnalyzerId);
    }
}
