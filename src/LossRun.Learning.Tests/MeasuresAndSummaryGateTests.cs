using System.Text.Json;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Formats.Replay;
using LossRun.Rules;

namespace LossRun.Learning.Tests;

/// <summary>
/// Brief 22 Answers 1 and 6: the measures-as-rows construct in the learn loop (instruction, lint, repair) on the recorded
/// corpus-gen 0.7.0 c10 graph, and the summary-only gate rule that every amount read is checked by a printed total.
/// </summary>
public class MeasuresAndSummaryGateTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "constructs", name);

    private static DocumentGraph C10Graph() =>
        JsonSerializer.Deserialize<DocumentGraph>(File.ReadAllText(Fixture("c10-measures-rows.graph.json")), CanonicalJson.Options)!;

    private static CarrierFormat C10Spec() => FormatJson.Parse(File.ReadAllText(Fixture("c10-measures-rows.spec.json")));

    [Fact]
    public void The_measures_evidence_is_an_instruction_and_replaces_the_records_rows_instruction()
    {
        var graph = C10Graph();
        graph.LayoutEvidence = LossRun.Ingestion.LayoutEvidenceDetector.Detect(graph);

        var hints = PromptBuilder.ConstructHints(graph);

        Assert.Contains(hints, h => h.Contains("\"mode\": \"measures\"", StringComparison.Ordinal) && h.StartsWith("- INSTRUCTION", StringComparison.Ordinal));
        Assert.DoesNotContain(hints, h => h.Contains("\"mode\": \"rows\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Per_measure_entries_of_one_column_are_repaired_to_the_component_and_then_verify()
    {
        // The c10-000 learn: one entry per measure from the same column, each reading the key row; R-02 failed on every claim.
        var spec = C10Spec();
        foreach (var component in new[] { "claim.total", "claim.indemnity", "claim.medical", "claim.expense" })
        {
            var column = spec.ColumnMap.Single(c => c.Target == component);
            spec.ColumnMap.Remove(column);
            spec.ColumnMap.AddRange(new[] { "paid", "reserve", "incurred" }.Select(m => new ColumnMapping { Source = column.Source, Target = $"{component}.{m}", Normalizer = column.Normalizer }));
        }

        var graph = C10Graph();
        Assert.Contains(SpecVerifier.Verify(graph, spec).HardFailures, f => f.RuleId == "R-02");

        Assert.Equal(4, SpecLint.RepairMeasureColumns(spec).Count);

        var verification = SpecVerifier.Verify(graph, spec);
        Assert.Equal(Verification.PdfTotalsGate, verification.Gate());
        Assert.Empty(SpecLint.RepairMeasureColumns(spec)); // idempotent
    }

    [Fact]
    public void Component_targets_without_a_measure_belong_to_measures_mode_only()
    {
        var spec = C10Spec();
        var graph = C10Graph();
        Assert.DoesNotContain(SpecLint.Check(spec, ReplayEngine.Execute(graph, spec), graph), i => i.Contains("component", StringComparison.Ordinal));

        spec.TableLocator.Records = null;
        Assert.Contains(SpecLint.Check(spec, ReplayEngine.Execute(graph, spec), graph), i => i.Contains("names a component without a measure", StringComparison.Ordinal));

        var measures = C10Spec();
        measures.ColumnMap.RemoveAll(c => ReplayEngine.MeasureComponents.Contains(c.Target));
        Assert.Contains(SpecLint.Check(measures, ReplayEngine.Execute(graph, measures), graph), i => i.Contains("records.mode measures reads the component columns", StringComparison.Ordinal));
    }

    private static LossRunDocument Summary(decimal? recovery)
    {
        static Totals Period(decimal incurred) => new() { ClaimCount = 2, Total = new MoneyTriplet(null, null, incurred) };
        var document = new LossRunDocument
        {
            SchemaVersion = CanonicalData.SchemaVersion, DocumentId = "doc-summary", CarrierName = "Synthetic Mutual", NoLossesStatement = false,
            PolicyPeriods =
            [
                new PolicyPeriod { PeriodLabel = "2022", DetailLevel = DetailLevel.SummaryOnly, Subtotals = Period(100m) },
                new PolicyPeriod { PeriodLabel = "2023", DetailLevel = DetailLevel.SummaryOnly, Subtotals = Period(50m) },
            ],
            ReportTotals = new Totals { ClaimCount = 4, Total = new MoneyTriplet(null, null, 150m) },
        };
        if (recovery is { } r)
        {
            document.PolicyPeriods[0].Subtotals!.Recoveries = new RecoveryAmounts { Other = r };
        }

        return document;
    }

    [Fact]
    public void Every_amount_a_summary_only_replay_reads_must_be_checked_by_a_printed_total()
    {
        // Brief 22 Answers 6: the wrong publish tied on incurred and the claim count while two analysis columns were read as
        // recoveries. Each amount no passing R-26 covers is an issue, so the spec is not published.
        var gate = LearnGate.Default;
        Assert.True(gate.SummaryOnly.EveryAmountChecked);

        var clean = Summary(recovery: null);
        Assert.Empty(SpecVerifier.UncheckedAmounts(clean, RuleEvaluator.Default.Evaluate(clean).Outcomes, gate.SummaryOnly.TotalsRules));

        var recovered = Summary(recovery: 25m);
        var issues = SpecVerifier.UncheckedAmounts(recovered, RuleEvaluator.Default.Evaluate(recovered).Outcomes, gate.SummaryOnly.TotalsRules);
        Assert.Equal("recoveries.other", Assert.Single(issues).Split(' ')[3]);
    }
}
