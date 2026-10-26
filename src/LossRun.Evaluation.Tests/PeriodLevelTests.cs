using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Formats.Replay;
using LossRun.Ingestion.Spreadsheets;
using LossRun.Learning;
using LossRun.Rules;

namespace LossRun.Evaluation.Tests;

/// <summary>
/// Brief 18 on synthetic renderings of carrier c5 (kept out of the corpus v3 plan, so the deck table is unchanged):
/// ADR 0016 (a policy year printed as "no claims") and ADR 0017 (a summary-only run by fiscal year), replayed and scored.
/// </summary>
public class PeriodLevelTests
{
    private const string Header = """
        "fingerprint": { "identityKey": "", "headerKey": "", "identityText": "", "headerText": "", "embedding": null },
        """;

    private static readonly CarrierFormat CsvWide = FormatJson.Parse($$"""
        {
          "specSchema": "carrier-format-0.3.0", "formatId": "test-c5-csv", "version": 1, "carrierName": "{{Carriers.PlaceholderAssurance.Name}}",
          "fileKind": "spreadsheet", "createdBy": "hand", "createdAt": "2026-10-24T00:00:00+00:00",
          {{Header}}
          "tableLocator": { "headerMatchMin": 0.8, "rowKinds": [] },
          "columnMap": [
            { "source": "Policy Number", "target": "period.policyNumber", "normalizer": { "type": "text" } },
            { "source": "Pol Eff", "target": "period.effectiveDate", "normalizer": { "type": "date" } },
            { "source": "Pol Exp", "target": "period.expirationDate", "normalizer": { "type": "date" } },
            { "source": "Claim Number", "target": "claim.claimNumber", "normalizer": { "type": "claimNumber" } },
            { "source": "Claimant", "target": "claim.claimantName", "normalizer": { "type": "text" } },
            { "source": "Loss Date", "target": "claim.dateOfInjury", "normalizer": { "type": "date" } },
            { "source": "Claim Status", "target": "claim.status", "normalizer": { "type": "enum", "glossaryField": "claim.status" } },
            { "source": "Ind Paid", "target": "claim.indemnity.paid", "normalizer": { "type": "currency" } },
            { "source": "Ind Res", "target": "claim.indemnity.reserve", "normalizer": { "type": "currency" } },
            { "source": "Med Paid", "target": "claim.medical.paid", "normalizer": { "type": "currency" } },
            { "source": "Med Res", "target": "claim.medical.reserve", "normalizer": { "type": "currency" } },
            { "source": "Exp Paid", "target": "claim.expense.paid", "normalizer": { "type": "currency" } },
            { "source": "Exp Res", "target": "claim.expense.reserve", "normalizer": { "type": "currency" } },
            { "source": "Total Incurred", "target": "claim.total.incurred", "normalizer": { "type": "currency" } }
          ],
          "ignoredHeaders": ["Carrier", "Valuation Date", "Report Date", "Close Date", "LOB", "State", "Cause of Loss", "Nature of Injury", "Loss Description",
            "Litigation", "Recoveries", "LDW"],
          "periods": { "source": "column", "column": "Policy Number" },
          "documentFields": [ { "target": "document.carrierName", "locator": { "type": "const", "value": "{{Carriers.PlaceholderAssurance.Name}}" }, "reason": "test" } ]
        }
        """);

    private static readonly CarrierFormat Summary = FormatJson.Parse($$"""
        {
          "specSchema": "carrier-format-0.3.0", "formatId": "test-c5-summary", "version": 1, "carrierName": "{{Carriers.PlaceholderAssurance.Name}}",
          "fileKind": "spreadsheet", "createdBy": "hand", "createdAt": "2026-10-24T00:00:00+00:00",
          {{Header}}
          "tableLocator": { "headerMatchMin": 0.8, "rowShape": "periodSummary", "rowKinds": [ { "kind": "grandTotal", "regex": "^Grand Total" } ] },
          "columnMap": [
            { "source": "Fiscal Year", "target": "period.periodLabel", "normalizer": { "type": "text" } },
            { "source": "Open Claims", "target": "period.subtotals.openCount", "normalizer": { "type": "integer" } },
            { "source": "Closed Claims", "target": "period.subtotals.closedCount", "normalizer": { "type": "integer" } },
            { "source": "Total Claims", "target": "period.subtotals.claimCount", "normalizer": { "type": "integer" } },
            { "source": "Ind Paid", "target": "claim.indemnity.paid", "normalizer": { "type": "currency" } },
            { "source": "Ind Res", "target": "claim.indemnity.reserve", "normalizer": { "type": "currency" } },
            { "source": "Med Paid", "target": "claim.medical.paid", "normalizer": { "type": "currency" } },
            { "source": "Med Res", "target": "claim.medical.reserve", "normalizer": { "type": "currency" } },
            { "source": "Exp Paid", "target": "claim.expense.paid", "normalizer": { "type": "currency" } },
            { "source": "Exp Res", "target": "claim.expense.reserve", "normalizer": { "type": "currency" } },
            { "source": "Total Incurred", "target": "claim.total.incurred", "normalizer": { "type": "currency" } },
            { "source": "Deductible Recovery", "target": "period.subtotals.recoveries.deductible", "normalizer": { "type": "currency" } }
          ],
          "ignoredHeaders": [],
          "periods": { "source": "column", "kind": "fiscalYear" },
          "documentFields": [ { "target": "document.carrierName", "locator": { "type": "const", "value": "{{Carriers.PlaceholderAssurance.Name}}" }, "reason": "test" } ],
          "derivedRules": [
            { "target": "indemnity.incurred", "sum": ["indemnity.paid", "indemnity.reserve"], "when": "absent", "appliesTo": ["totals"] },
            { "target": "medical.incurred", "sum": ["medical.paid", "medical.reserve"], "when": "absent", "appliesTo": ["totals"] },
            { "target": "expense.incurred", "sum": ["expense.paid", "expense.reserve"], "when": "absent", "appliesTo": ["totals"] }
          ]
        }
        """);

    private static LossRunDocument Truth() => new TruthFactory(new Random(8)).Create(Carriers.PlaceholderAssurance, "c5-x", [], minClaims: 6, maxClaims: 12);

    private static DocumentGraph Graph(Rendered rendered) =>
        SpreadsheetReader.Read(rendered.Bytes, new SourceFile { FileName = "c5.csv", Kind = FileKind.Csv }, "doc-c5", CanonicalData.Glossary, []);

    private static List<FieldObservation> Score(Rendered rendered, LossRunDocument truth, LossRunDocument extracted)
    {
        var record = new TruthRecord { Id = "c5-x", Carrier = "c5", InputType = InputTypes.Csv, Printed = rendered.Printed, Document = truth };
        var entry = new ManifestEntry(record.Id, "c5.csv", "c5", "csv-wide", InputTypes.Csv, "c5-x", [], "", 0, 1);
        return Scorer.ScoreFields(entry, record, extracted, hardPassed: true, "seen").Fields;
    }

    [Fact]
    public void A_policy_year_printed_as_no_claims_states_no_losses_for_that_period_only()
    {
        var (rendered, truth) = Renderers.CsvWideWithNoLossesYear(Truth(), emptyPeriod: 1);
        var empty = truth.PolicyPeriods[1].PolicyNumber;

        var replay = ReplayEngine.Execute(Graph(rendered), CsvWide);

        var period = replay.Document.PolicyPeriods.Single(p => p.PolicyNumber == empty);
        Assert.True(period.NoLossesStatement);
        Assert.Empty(period.Claims);
        Assert.Equal(DetailLevel.Claims, period.DetailLevel);
        Assert.NotNull(period.EffectiveDate); // the statement row still carries the period's dates
        Assert.All(replay.Document.PolicyPeriods.Where(p => p != period), p => Assert.False(p.NoLossesStatement));
        Assert.False(replay.Document.NoLossesStatement); // not every period says so (ADR 0016)
        Assert.Equal(truth.PolicyPeriods.Sum(p => p.Claims.Count), replay.Stats.ClaimRows);
        Assert.Contains(replay.RuleOutcomes, o => o.RuleId == "R-14" && o.Status == RuleStatus.Pass);
        Assert.DoesNotContain(replay.RuleOutcomes, o => o.RuleId is "R-14" or "R-27" && o.Status == RuleStatus.Fail);
        Assert.Equal("replay", replay.Document.Lineage![$"policyPeriods[{replay.Document.PolicyPeriods.IndexOf(period)}].noLossesStatement"].Extractor);

        var fields = Score(rendered, truth, replay.Document);
        Assert.Contains(fields, f => f.Field == "period.noLossesStatement" && f.Match);
        Assert.All(fields.Where(f => f.Scope == "period"), f => Assert.True(f.Match, $"{f.Field}: {f.Expected} vs {f.Actual}"));
    }

    [Fact]
    public void Claim_rows_under_a_no_losses_statement_fail_r14_for_that_period()
    {
        var (rendered, truth) = Renderers.CsvWideWithNoLossesYear(Truth(), emptyPeriod: 1);
        var replay = ReplayEngine.Execute(Graph(rendered), CsvWide);
        var document = replay.Document;
        document.PolicyPeriods.Single(p => p.NoLossesStatement).Claims.Add(document.PolicyPeriods[0].Claims[0]);

        var outcomes = RuleEvaluator.Default.Evaluate(document, null).Outcomes;

        Assert.Single(outcomes, o => o.RuleId == "R-14" && o.Status == RuleStatus.Fail);
        Assert.NotNull(truth);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_document_level_statement_over_rows_fails_r14_only_when_there_is_one_period(bool onePeriod)
    {
        // The spec's documentFields locator reports "no losses" (here: any carrier cell), but every period lists claims.
        var spec = FormatJson.Parse(FormatJson.Write(CsvWide));
        spec.DocumentFields.Add(new DocumentFieldSpec { Target = "document.noLossesStatement", Locator = new FieldLocator { Type = "regexPresent", Pattern = "Placeholder" } });
        var truth = Truth();
        if (onePeriod)
        {
            truth.PolicyPeriods.RemoveRange(1, truth.PolicyPeriods.Count - 1);
        }

        var replay = ReplayEngine.Execute(Graph(Renderers.Render(Carriers.PlaceholderAssurance, truth)), spec);

        Assert.All(replay.Document.PolicyPeriods, p => Assert.NotEmpty(p.Claims));
        Assert.Equal(onePeriod, replay.RuleOutcomes.Any(o => o.RuleId == "R-14" && o.Status == RuleStatus.Fail));
        Assert.Equal(onePeriod, replay.Document.NoLossesStatement);

        // Brief 18 Answers 3: a statement no period could take is flagged (R-28), not silently dropped.
        Assert.Equal(!onePeriod, replay.Document.UnattributedNoLossesStatement);
        Assert.Equal(!onePeriod, replay.RuleOutcomes.Any(o => o.RuleId == "R-28" && o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Flag));
    }

    [Fact]
    public void A_no_losses_statement_read_with_a_text_locator_does_not_break_the_replay()
    {
        // Brief 19 constructs run: a learned spec read document.noLossesStatement with a regex (a string), and the
        // attribution read it as a boolean: "String cannot be converted to Boolean" on every attempt.
        var spec = FormatJson.Parse(FormatJson.Write(CsvWide));
        spec.DocumentFields.Add(new DocumentFieldSpec { Target = "document.noLossesStatement", Locator = new FieldLocator { Type = "regex", Pattern = "(?<value>No claims exist)" } });

        var replay = ReplayEngine.Execute(Graph(Renderers.CsvWideWithNoLossesYear(Truth(), emptyPeriod: 1).Rendered), spec);

        Assert.Contains(replay.Document.PolicyPeriods, p => p.NoLossesStatement);
        Assert.False(replay.Document.NoLossesStatement);
    }

    [Fact]
    public void An_empty_period_without_a_statement_is_flagged_not_failed()
    {
        var document = ReplayEngine.Execute(Graph(Renderers.CsvWideWithNoLossesYear(Truth(), emptyPeriod: 1).Rendered), CsvWide).Document;
        var period = document.PolicyPeriods.Single(p => p.NoLossesStatement);
        period.NoLossesStatement = false;

        var outcomes = RuleEvaluator.Default.Evaluate(document, null).Outcomes;

        var flag = Assert.Single(outcomes, o => o.RuleId == "R-27" && o.Status == RuleStatus.Fail);
        Assert.Equal(RuleSeverity.Flag, flag.Severity);
        Assert.DoesNotContain(outcomes, o => o.RuleId == "R-14" && o.Status == RuleStatus.Fail);
    }

    [Fact]
    public void A_summary_only_run_replays_as_period_rows_that_tie_to_the_grand_total()
    {
        var (rendered, truth) = Renderers.CsvPeriodSummary(Truth());

        var replay = ReplayEngine.Execute(Graph(rendered), Summary);

        var document = replay.Document;
        Assert.Equal(0, replay.Stats.ClaimRows);
        Assert.Equal(truth.PolicyPeriods.Count, replay.Stats.SummaryRows);
        Assert.True(LearnGate.IsSummaryOnly(document));
        Assert.All(document.PolicyPeriods, p =>
        {
            Assert.Equal(DetailLevel.SummaryOnly, p.DetailLevel);
            Assert.Equal(PeriodKind.FiscalYear, p.PeriodKind);
            Assert.StartsWith("FY ", p.PeriodLabel);
            Assert.NotNull(p.Subtotals!.Recoveries!.Deductible);
        });
        Assert.Equal(truth.ReportTotals!.Recoveries!.Deductible, document.ReportTotals!.Recoveries!.Deductible);
        Assert.Contains(replay.RuleOutcomes, o => o.RuleId == "R-26" && o.Status == RuleStatus.Pass);
        Assert.DoesNotContain(replay.RuleOutcomes, o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard);

        var fields = Score(rendered, truth, document);
        Assert.Contains(fields, f => f.Scope == "subtotal");
        Assert.All(fields, f => Assert.True(f.Match, $"{f.Field}: {f.Expected} vs {f.Actual}"));

        var verification = SpecVerifier.Verify(Graph(rendered), Summary);
        Assert.True(verification.TotalsTie);
        Assert.DoesNotContain(verification.MissingRequired, m => m.StartsWith("claim rows", StringComparison.Ordinal));
    }

    [Fact]
    public void Year_rows_a_learned_spec_classifies_as_subtotals_are_still_period_rows()
    {
        // stcharles (brief 18 run): the learned periodSummary spec gave every year row a "subtotal" row kind.
        var (rendered, truth) = Renderers.CsvPeriodSummary(Truth());
        var spec = FormatJson.Parse(FormatJson.Write(Summary));
        spec.TableLocator.RowKinds.Insert(0, new RowKindRule { Kind = "subtotal", Regex = "^FY " });

        var replay = ReplayEngine.Execute(Graph(rendered), spec);

        Assert.Equal(truth.PolicyPeriods.Count, replay.Stats.SummaryRows);
        Assert.Contains(replay.RuleOutcomes, o => o.RuleId == "R-26" && o.Status == RuleStatus.Pass);
    }

    [Fact]
    public void Two_columns_read_into_one_target_is_a_lint_issue()
    {
        var spec = FormatJson.Parse(FormatJson.Write(Summary));
        spec.ColumnMap.Single(c => c.Source == "Deductible Recovery").Target = "claim.total.incurred";

        var issues = SpecVerifier.Verify(Graph(Renderers.CsvPeriodSummary(Truth()).Rendered), spec).Issues;

        Assert.Contains(issues, i => i.Contains("all map to claim.total.incurred", StringComparison.Ordinal));
    }

    [Fact]
    public void A_summary_row_whose_parts_do_not_add_up_fails_r26()
    {
        var (rendered, _) = Renderers.CsvPeriodSummary(Truth());
        var csv = System.Text.Encoding.UTF8.GetString(rendered.Bytes).Split("\r\n");
        var cells = csv[1].Split(',');
        cells[4] = "999999.99"; // first fiscal year's indemnity paid
        csv[1] = string.Join(',', cells);

        var replay = ReplayEngine.Execute(Graph(rendered with { Bytes = System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", csv)) }), Summary);

        Assert.Contains(replay.RuleOutcomes, o => o.RuleId == "R-26" && o.Status == RuleStatus.Fail);
    }

    [Fact]
    public void Loss_paid_without_a_medical_column_is_an_ambiguous_composite()
    {
        // Brief 17 Answers 2, on the spec the learn path would write for "Loss Paid | Loss Reserve | Expense Paid".
        var spec = FormatJson.Parse(FormatJson.Write(CsvWide));
        spec.ColumnMap.RemoveAll(c => c.Target.StartsWith("claim.medical.", StringComparison.Ordinal));
        spec.ColumnMap.Single(c => c.Target == "claim.indemnity.paid").Source = "Loss Paid";
        var graph = Graph(Renderers.CsvWideWithNoLossesYear(Truth(), 1).Rendered);

        Assert.Contains("ambiguous-composite", Assert.Single(ReplayEngine.Execute(graph, spec).AmbiguousComposites));

        spec.ColumnMap.Add(new ColumnMapping { Source = "Med Paid", Target = "claim.medical.paid", Normalizer = new NormalizerSpec { Type = "currency" } });
        Assert.Empty(ReplayEngine.Execute(graph, spec).AmbiguousComposites);
    }
}
