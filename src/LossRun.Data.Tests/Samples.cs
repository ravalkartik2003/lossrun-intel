using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Rules;

namespace LossRun.Data.Tests;

/// <summary>Synthetic manifests and documents (no client data).</summary>
internal static class Samples
{
    public static UploadManifest Manifest(string uploadId, params string[] documentIds) => new()
    {
        UploadId = uploadId, Status = UploadStatuses.Processing, CreatedAt = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero),
        Submission = new SubmissionInfo("Q-100", "Synthetic Mutual Insurance Co.", Roles.Underwriter, "uw-1"),
        Files = [new ManifestFile("lossrun.pdf", new string('a', 64), 1234, $"uploads/{uploadId}/lossrun.pdf")],
        Items = [.. documentIds.Select(d => new ManifestItem { DocumentId = d, FileName = "lossrun.pdf", Classification = "LossRun", Status = ItemStatuses.Processing })],
    };

    /// <summary>One period, <paramref name="claims"/> claims; incurred values are derived (lineage "derived", "a + b") like a replayed spec.</summary>
    public static DocumentResult Result(string uploadId, string documentId, int claims = 3)
    {
        var lineage = new Dictionary<string, LineageRecord>(StringComparer.Ordinal);
        var period = new PolicyPeriod
        {
            PolicyNumber = "WC-100", EffectiveDate = new DateOnly(2024, 1, 1), ExpirationDate = new DateOnly(2025, 1, 1),
        };
        for (var i = 0; i < claims; i++)
        {
            var path = $"policyPeriods[0].claims[{i}]";
            var claim = new Claim
            {
                ClaimNumber = $"WC-24-{i + 1:0000}", ClaimantName = $"Synthetic Claimant {i + 1}", DateOfInjury = new DateOnly(2024, 2, 1).AddDays(i), Status = ClaimStatus.Open,
                Indemnity = new MoneyTriplet(1000m * (i + 1), 500m, 1000m * (i + 1) + 500m), Medical = new MoneyTriplet(200m, 100m, 300m),
                Total = new MoneyTriplet(1000m * (i + 1) + 200m, 600m, 1000m * (i + 1) + 800m),
            };
            period.Claims.Add(claim);
            foreach (var field in new[] { "claimNumber", "claimantName", "dateOfInjury", "status", "indemnity.paid", "indemnity.reserve", "medical.paid", "medical.reserve" })
            {
                lineage[$"{path}.{field}"] = new LineageRecord
                {
                    DocumentId = documentId, Page = 1, Polygon = [1, 2 + i, 2, 2 + i, 2, 2.2 + i, 1, 2.2 + i], SourceText = field, Extractor = "replay",
                    FormatId = "synthetic-mutual-pdf", FormatVersion = "1", Confidence = 0.98,
                };
            }

            foreach (var component in new[] { "indemnity", "medical" })
            {
                lineage[$"{path}.{component}.incurred"] = new LineageRecord
                {
                    DocumentId = documentId, Extractor = "derived", SourceText = $"{path}.{component}.paid + {path}.{component}.reserve",
                };
            }
        }

        var document = new LossRunDocument
        {
            DocumentId = documentId, CarrierName = "Synthetic Mutual Insurance Co.", InsuredName = "Synthetic Insured LLC", ValuationDate = new DateOnly(2025, 6, 30),
            ExpenseTreatment = ExpenseTreatment.Separate, PolicyPeriods = [period], Lineage = lineage,
        };
        return new DocumentResult
        {
            DocumentId = documentId, UploadId = uploadId, Document = document, RuleOutcomes = [.. RuleEvaluator.Default.Evaluate(document).Outcomes],
            FormatRef = "synthetic-mutual-pdf@1", ReviewState = "pending",
            Cost = new DocumentCost(documentId, uploadId, "ReplayAccepted", 0, 2, 0, 0, 0.01m, [new CostLine("ContentUnderstanding", "pages", 2, "page", 0.005m, 0.01m, "test")]),
        };
    }

    public static ReviewDecision Override(string documentId, string user, params (string Path, object Value, string Rationale)[] changes) =>
        new(documentId, ReviewActions.Override, [.. changes.Select(c => new FieldOverride(c.Path, JsonSerializer.SerializeToElement(c.Value), c.Rationale))], false,
            "corrected from the carrier's letter", user, Roles.Underwriter, DateTimeOffset.UtcNow);
}
