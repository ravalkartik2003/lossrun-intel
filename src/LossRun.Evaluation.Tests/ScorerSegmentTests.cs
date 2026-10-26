using LossRun.Contracts;

namespace LossRun.Evaluation.Tests;

/// <summary>Brief 22 follow-up: a run the classifier split into several loss-run segments is scored on all of them.</summary>
public class ScorerSegmentTests
{
    private static ExtractedDocument Part(int first, int last, string tier) => new()
    {
        GraphDocumentId = $"doc-p{first}-{last}", FirstPage = first, LastPage = last, FinalTier = tier,
        Document = tier == "Review" ? null : new LossRunDocument { DocumentId = $"doc-p{first}-{last}", PolicyPeriods = [new PolicyPeriod()] },
    };

    private static readonly ManifestEntry Entry = new("c10-005-native-pdf", "c10-005-native-pdf.pdf", "c10", "measures-rows", InputTypes.NativePdf, "c10-005", [], "", 0, 6);

    [Fact]
    public void A_run_split_into_segments_is_delivered_only_when_every_overlapping_segment_is()
    {
        var truth = new TruthRecord { LossRunSegment = new TruthSegment("LossRun", 1, 6) };

        var split = Scorer.Predicted(Entry, truth, [Part(1, 2, "ReplayAccepted"), Part(3, 3, "Review"), Part(4, 6, "Review")])!;
        Assert.Equal("Review", split.FinalTier);
        Assert.Null(split.Document);

        var delivered = Scorer.Predicted(Entry, truth, [Part(1, 2, "ReplayAccepted"), Part(3, 6, "Learned")])!;
        Assert.Equal(2, delivered.Document!.PolicyPeriods.Count);
    }

    [Fact]
    public void A_bundle_segment_outside_the_truth_pages_is_not_scored()
    {
        var truth = new TruthRecord { LossRunSegment = new TruthSegment("LossRun", 3, 4) };

        var predicted = Scorer.Predicted(Entry, truth, [Part(1, 2, "Review"), Part(3, 4, "ReplayAccepted")])!;

        Assert.Equal(("doc-p3-4", "ReplayAccepted"), (predicted.GraphDocumentId, predicted.FinalTier));
    }
}
