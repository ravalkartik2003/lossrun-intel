using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Formats.Replay;
using LossRun.Ingestion.Spreadsheets;
using LossRun.Rules;

namespace LossRun.Evaluation.Tests;

/// <summary>
/// Brief 19 (ADR 0018, component-sum headers) on the corpus-gen 0.4.0 c9 rendering: totals headed "(Ind+Med)" beside separate
/// expense columns. Offline: the CSV comes from the generator, the graph from the spreadsheet reader.
/// </summary>
public class ConstructCsvTests
{
    private static readonly CarrierFormat Composite = FormatJson.Parse($$"""
        {
          "specSchema": "carrier-format-0.4.0", "formatId": "construct-c9-composite", "version": 1, "carrierName": "{{Carriers.SampleComposite.Name}}",
          "fileKind": "spreadsheet", "createdBy": "hand", "createdAt": "2026-10-24T00:00:00+00:00",
          "fingerprint": { "identityKey": "", "headerKey": "", "identityText": "", "headerText": "", "embedding": null },
          "tableLocator": { "headerMatchMin": 0.8, "rowKinds": [] },
          "columnMap": [
            { "source": "Policy Number", "target": "period.policyNumber", "normalizer": { "type": "text" } },
            { "source": "Pol Eff", "target": "period.effectiveDate", "normalizer": { "type": "date" } },
            { "source": "Pol Exp", "target": "period.expirationDate", "normalizer": { "type": "date" } },
            { "source": "Valuation Date", "target": "document.valuationDate", "normalizer": { "type": "date" } },
            { "source": "Claim Number", "target": "claim.claimNumber", "normalizer": { "type": "claimNumber" } },
            { "source": "Claimant", "target": "claim.claimantName", "normalizer": { "type": "text" } },
            { "source": "Loss Date", "target": "claim.dateOfInjury", "normalizer": { "type": "date" } },
            { "source": "Claim Status", "target": "claim.status", "normalizer": { "type": "enum", "glossaryField": "claim.status" } },
            { "source": "Ind Paid", "target": "claim.indemnity.paid", "normalizer": { "type": "currency" } },
            { "source": "Med Paid", "target": "claim.medical.paid", "normalizer": { "type": "currency" } },
            { "source": "Paid (Ind+Med)", "target": "claim.total.paid", "components": ["indemnity", "medical"], "normalizer": { "type": "currency" } },
            { "source": "Ind Res", "target": "claim.indemnity.reserve", "normalizer": { "type": "currency" } },
            { "source": "Med Res", "target": "claim.medical.reserve", "normalizer": { "type": "currency" } },
            { "source": "Reserve (Ind+Med)", "target": "claim.total.reserve", "components": ["indemnity", "medical"], "normalizer": { "type": "currency" } },
            { "source": "Total Incurred (Ind+Med)", "target": "claim.total.incurred", "components": ["indemnity", "medical"], "normalizer": { "type": "currency" } },
            { "source": "Exp Paid", "target": "claim.expense.paid", "normalizer": { "type": "currency" } },
            { "source": "Exp Res", "target": "claim.expense.reserve", "normalizer": { "type": "currency" } }
          ],
          "ignoredHeaders": [],
          "periods": { "source": "column", "column": "Policy Number" },
          "documentFields": [
            { "target": "document.carrierName", "locator": { "type": "const", "value": "{{Carriers.SampleComposite.Name}}" }, "reason": "test" },
            { "target": "document.expenseTreatment", "locator": { "type": "const", "value": "separate" }, "reason": "expense columns" }
          ],
          "derivedRules": [
            { "target": "indemnity.incurred", "sum": ["indemnity.paid", "indemnity.reserve"], "when": "absent", "appliesTo": ["claim"] },
            { "target": "medical.incurred", "sum": ["medical.paid", "medical.reserve"], "when": "absent", "appliesTo": ["claim"] },
            { "target": "expense.incurred", "sum": ["expense.paid", "expense.reserve"], "when": "absent", "appliesTo": ["claim"] }
          ]
        }
        """);

    private static (Rendered Rendered, LossRunDocument Truth) Render()
    {
        var truth = Renderers.IndemnityMedicalTotals(new TruthFactory(new Random(19)).Create(Carriers.SampleComposite, "c9-x", [], minClaims: 6, maxClaims: 12));
        return (Renderers.Render(Carriers.SampleComposite, truth), truth);
    }

    private static DocumentGraph Graph(byte[] bytes) =>
        SpreadsheetReader.Read(bytes, new SourceFile { FileName = "c9.csv", Kind = FileKind.Csv }, "doc-c9", CanonicalData.Glossary, []);

    [Fact]
    public void Totals_naming_indemnity_and_medical_are_checked_against_those_components_not_all_three()
    {
        var (rendered, truth) = Render();

        var replay = ReplayEngine.Execute(Graph(rendered.Bytes), Composite);

        Assert.Equal(TotalComposition.IndemnityMedical, replay.Document.TotalComposition);
        Assert.Contains(replay.RuleOutcomes, o => o.RuleId == "R-29" && o.Status == RuleStatus.Pass && o.Severity == RuleSeverity.Hard);
        Assert.DoesNotContain(replay.RuleOutcomes, o => o.RuleId == "R-20" && o.Status is RuleStatus.Pass or RuleStatus.Fail);
        Assert.DoesNotContain(replay.RuleOutcomes, o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard);
        Assert.Equal(truth.PolicyPeriods.Sum(p => p.Claims.Count), replay.Stats.ClaimRows);
        Assert.Equal("spec", replay.Document.Lineage!["totalComposition"].Extractor);
    }

    [Fact]
    public void Without_the_declared_components_r20_fails_on_every_claim_with_expense()
    {
        var spec = FormatJson.Parse(FormatJson.Write(Composite));
        spec.ColumnMap.ForEach(c => c.Components = null);

        var replay = ReplayEngine.Execute(Graph(Render().Rendered.Bytes), spec);

        Assert.Null(replay.Document.TotalComposition);
        Assert.Contains(replay.RuleOutcomes, o => o.RuleId == "R-20" && o.Status == RuleStatus.Fail);
    }

    [Fact]
    public void A_total_that_does_not_add_up_to_its_named_components_fails_r29()
    {
        var csv = System.Text.Encoding.UTF8.GetString(Render().Rendered.Bytes).Split("\r\n");
        var cells = csv[1].Split(',');
        cells[14] = "999999.99"; // Total Incurred (Ind+Med) of the first claim
        csv[1] = string.Join(',', cells);

        var replay = ReplayEngine.Execute(Graph(System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", csv))), Composite);

        Assert.Contains(replay.RuleOutcomes, o => o.RuleId == "R-29" && o.Status == RuleStatus.Fail);
    }

    [Fact]
    public void The_glossary_reads_the_rendered_headers_as_component_sums()
    {
        var headers = System.Text.Encoding.UTF8.GetString(Render().Rendered.Bytes).Split("\r\n")[0].Split(',');

        Assert.All(headers.Where(h => h.Contains('+', StringComparison.Ordinal)), h =>
            Assert.Equal(["indemnity", "medical"], CanonicalData.Glossary.ResolveHeader(h)!.Components));
    }
}
