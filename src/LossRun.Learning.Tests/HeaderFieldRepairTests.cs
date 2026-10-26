using System.Text.Json;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Formats.Replay;

namespace LossRun.Learning.Tests;

/// <summary>Brief 23 §1: header-block fields repaired into the spec, and the document facts review states when they are not printed.</summary>
public class HeaderFieldRepairTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "constructs", name);

    private static DocumentGraph C10Graph() =>
        JsonSerializer.Deserialize<DocumentGraph>(File.ReadAllText(Fixture("c10-measures-rows.graph.json")), CanonicalJson.Options)!;

    private static CarrierFormat C10Spec() => FormatJson.Parse(File.ReadAllText(Fixture("c10-measures-rows.spec.json")));

    [Fact]
    public void A_document_field_the_spec_does_not_read_is_located_by_its_header_label()
    {
        var graph = C10Graph();
        var expected = ReplayEngine.Execute(graph, C10Spec()).Document.ValuationDate;
        var spec = C10Spec();
        spec.DocumentFields.RemoveAll(f => f.Target == "document.valuationDate");
        Assert.Null(ReplayEngine.Execute(graph, spec).Document.ValuationDate);

        var notes = SpecLint.LocateHeaderFields(spec, graph, CanonicalData.Glossary);

        Assert.Contains(notes, n => n.StartsWith("documentFields document.valuationDate: located in the header block", StringComparison.Ordinal));
        Assert.Equal(expected, ReplayEngine.Execute(graph, spec).Document.ValuationDate);
        Assert.Equal("headerLabel", spec.DocumentFields.Single(f => f.Target == "document.valuationDate").Locator.Type);
        Assert.Empty(SpecLint.LocateHeaderFields(spec, graph, CanonicalData.Glossary)); // a field the spec reads is never touched
    }

    [Fact]
    public void Review_states_a_document_fact_when_the_header_prints_a_losses_window_and_no_valuation_date()
    {
        var graph = C10Graph();
        graph.HeaderFields = [new HeaderField(HeaderFields.LossWindow, "Accident Date Range", "11/01/1999 to 12/23/1999", 1, null, 1.0)];

        var facts = SpecVerifier.DocumentFacts(graph, ["document.valuationDate", "policyPeriods[0].effectiveDate", "policyPeriods[0].expirationDate"]);

        Assert.Equal(
        [
            "document fact: the run prints no valuation (as-of) date",
            "document fact: the run prints a losses window (\"Accident Date Range\"), not a policy period",
        ], facts);
        Assert.Empty(SpecVerifier.DocumentFacts(C10Graph(), ["document.valuationDate"])); // printed: the gap is the spec's, not a fact
    }
}
