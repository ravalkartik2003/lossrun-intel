using System.Text.Json;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Formats.Replay;

namespace LossRun.Learning.Tests;

/// <summary>
/// Brief 20: the learn-loop repairs found on the corpus v4 c7/c8 learns and the real-sample run 5, on the recorded c7
/// graph (synthetic carrier, one table per claim). Each repair is deterministic and layout-generic.
/// </summary>
public class LearnLoopQualityTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "constructs", name);

    private static DocumentGraph C7Graph() =>
        JsonSerializer.Deserialize<DocumentGraph>(File.ReadAllText(Fixture("c7-claim-per-table.graph.json")), CanonicalJson.Options)!;

    private static CarrierFormat C7Spec() => FormatJson.Parse(File.ReadAllText(Fixture("c7-claim-per-table.spec.json")));

    private static DocumentFieldSpec Field(CarrierFormat spec, string target) => spec.DocumentFields.Single(f => f.Target == target);

    [Fact]
    public void A_label_cu_merged_into_the_paragraph_before_it_is_read_from_its_printed_line()
    {
        // CU returns "Named Insured: … Valuation Date: …" as one paragraph; the two labels are printed on two lines.
        var spec = C7Spec();
        Field(spec, "document.valuationDate").Locator.Pattern = @"^Valuation Date:\s*(?<value>\d{2}/\d{2}/\d{4})$";

        var replay = ReplayEngine.Execute(C7Graph(), spec);

        Assert.NotNull(replay.Document.ValuationDate);
        Assert.DoesNotContain(replay.Issues, i => i.Contains("valuationDate", StringComparison.Ordinal));
    }

    [Fact]
    public void Prompt_markup_and_a_literal_capture_are_repaired_and_an_unprinted_optional_field_is_removed()
    {
        var spec = C7Spec();
        Field(spec, "document.carrierName").Locator.Pattern = @"(?m)^#\s*(?<value>Specimen Indemnity Co\.)\s*$";
        Field(spec, "document.valuationDate").Locator.Pattern = @"^PageFooter:\s*Valuation Date:\s*(?<value>\d{2}/\d{2}/\d{4})$";
        var graph = C7Graph();
        var insured = ReplayEngine.Execute(graph, spec).Document.InsuredName!;
        Field(spec, "document.insuredName").Locator.Pattern = $@"^Named Insured:\s*(?<value>{System.Text.RegularExpressions.Regex.Escape(insured)})";
        spec.DocumentFields.Add(new DocumentFieldSpec { Target = "document.reportRunDate", Locator = new() { Type = "regex", Pattern = @"^Run Date:\s*(?<value>\S+)$" }, Normalizer = new() { Type = "date" } });

        var notes = SpecLint.RepairDocumentFields(spec, graph, CanonicalData.Glossary);

        Assert.Equal(@"(?m)^(?<value>Specimen Indemnity Co\.)\s*$", Field(spec, "document.carrierName").Locator.Pattern);
        Assert.Equal(@"^Valuation Date:\s*(?<value>\d{2}/\d{2}/\d{4})$", Field(spec, "document.valuationDate").Locator.Pattern);
        Assert.Equal(@"^Named Insured:\s*(?<value>.+)", Field(spec, "document.insuredName").Locator.Pattern);
        Assert.DoesNotContain(spec.DocumentFields, f => f.Target == "document.reportRunDate");
        Assert.Contains(notes, n => n.Contains("reportRunDate: removed", StringComparison.Ordinal));
        var replay = ReplayEngine.Execute(graph, spec);
        Assert.DoesNotContain(replay.Issues, i => i.StartsWith("document field", StringComparison.Ordinal));
    }

    [Fact]
    public void A_required_field_that_cannot_be_located_is_kept_so_the_gate_still_reports_it()
    {
        var spec = C7Spec();
        Field(spec, "document.valuationDate").Locator.Pattern = @"^Valued as of:\s*(?<value>\S+)$";

        SpecLint.RepairDocumentFields(spec, C7Graph(), CanonicalData.Glossary);

        Assert.Contains(spec.DocumentFields, f => f.Target == "document.valuationDate");
    }

    [Theory]
    [InlineData(@"^Workers' Compensation Loss Run Insured:\s*(?<value>.+?)\s*$", @"^Insured:\s*(?<value>.+?)\s*$")]
    [InlineData(@"(?m)^Named Insured:\s*.+?\s+Valuation Date:\s*(?<value>\d{2}/\d{2}/\d{4})$", @"(?m)^Date:\s*(?<value>\d{2}/\d{2}/\d{4})$")]
    public void A_regex_anchored_on_merged_text_gets_candidates_anchored_on_the_value_label(string pattern, string first)
    {
        var candidates = SpecLint.NearestLabelAnchors(pattern).ToList();

        Assert.Equal(first, candidates[0]);
        Assert.Contains(candidates, c => c.Contains("Valuation Date:", StringComparison.Ordinal) || c.Contains("^Insured:", StringComparison.Ordinal));
    }

    [Fact]
    public void Normalizers_are_fitted_to_the_canonical_field_type()
    {
        var spec = C7Spec();
        spec.TableLocator.Records!.LineFields.Single(l => l.Target == "claim.claimType").Normalizer = new() { Type = "text" };
        spec.ColumnMap.First(c => c.Target == "claim.dateOfInjury").Normalizer = new() { Type = "text" };
        spec.ColumnMap.First(c => c.Target == "claim.indemnity.paid").Normalizer = new() { Type = "text" };

        var notes = SpecLint.FitNormalizers(spec, CanonicalData.Glossary);

        Assert.Equal(3, notes.Count);
        Assert.Equal("enum", spec.TableLocator.Records.LineFields.Single(l => l.Target == "claim.claimType").Normalizer.Type);
        Assert.Equal("date", spec.ColumnMap.First(c => c.Target == "claim.dateOfInjury").Normalizer.Type);
        Assert.Equal("currency", spec.ColumnMap.First(c => c.Target == "claim.indemnity.paid").Normalizer.Type);
    }

    [Fact]
    public void A_word_boundary_after_punctuation_is_removed_so_total_rows_are_not_read_as_claims()
    {
        var spec = C7Spec();
        spec.TableLocator.RowKinds.Single(k => k.Kind == "subtotal").Regex = @"^Policy Total:\b";
        var graph = C7Graph();
        var before = ReplayEngine.Execute(graph, spec);

        Assert.Single(SpecLint.FixPunctuationBoundaries(spec));

        Assert.Equal("^Policy Total:", spec.TableLocator.RowKinds.Single(k => k.Kind == "subtotal").Regex);
        Assert.True(ReplayEngine.Execute(graph, spec).Stats.ClaimRows < before.Stats.ClaimRows);
    }

    [Fact]
    public void A_literal_claim_number_pattern_is_named_and_unmatched_records_show_their_printed_lines()
    {
        var spec = C7Spec();
        var graph = C7Graph();
        var first = ReplayEngine.Execute(graph, spec).Document.PolicyPeriods[0].Claims[0].ClaimNumber!;
        spec.TableLocator.Records!.LineFields.Single(l => l.Target == "claim.claimNumber").Pattern = $"^(?<value>{System.Text.RegularExpressions.Regex.Escape(first)})$";

        var replay = ReplayEngine.Execute(graph, spec);
        var issues = SpecLint.ClaimNumberLineFieldIssues(spec, replay).ToList();

        Assert.Contains(issues, i => i.Contains("is one printed claim number, not a pattern", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Contains("as replay reads them", StringComparison.Ordinal) && i.Contains("«Claimant:", StringComparison.Ordinal));
        Assert.Empty(SpecLint.ClaimNumberLineFieldIssues(C7Spec(), ReplayEngine.Execute(graph, C7Spec())));
    }

    [Fact]
    public void The_records_lines_instruction_quotes_the_printed_lines_below_the_table()
    {
        var hints = PromptBuilder.ConstructHints(C7Graph());

        var lines = Assert.Single(hints, h => h.Contains("exactly as replay reads them", StringComparison.Ordinal));
        Assert.Contains("Claimant:", lines, StringComparison.Ordinal);
        Assert.Contains("never one printed value", lines, StringComparison.Ordinal);
    }

    [Fact]
    public void Line_one_entries_are_set_under_the_column_their_composed_header_names()
    {
        // Corpus v4 c6: "Loss State: Ind Paid:" prints Ind Paid under Loss State; the model put it under the column to its left.
        var graph = new DocumentGraph { Tables = [new GraphTable { Id = "t0", HeaderTexts = ["Claim #:", "Loss State: Ind Paid:", "Date of Injury: Med Paid:"] }] };
        var spec = new CarrierFormat
        {
            TableLocator = new TableLocator { Records = new RecordSpec { Mode = "rows", KeySource = "Claim #:" } },
            ColumnMap =
            [
                new ColumnMapping { Source = "Claim #:", Target = "claim.claimNumber" },
                new ColumnMapping { Source = "Loss State:", Target = "claim.jurisdictionState" },
                new ColumnMapping { Source = "Date of Injury:", Target = "claim.dateOfInjury" },
                new ColumnMapping { Source = "Ind Paid:", Target = "claim.indemnity.paid", Line = 1, Under = "Claim #:" },
                new ColumnMapping { Source = "Med Paid:", Target = "claim.medical.paid", Line = 1, Under = "Loss State:" },
            ],
        };

        Assert.Equal(2, SpecLint.RepairRecordUnder(spec, graph).Count);

        Assert.Equal("Loss State:", spec.ColumnMap.Single(c => c.Target == "claim.indemnity.paid").Under);
        Assert.Equal("Date of Injury:", spec.ColumnMap.Single(c => c.Target == "claim.medical.paid").Under);
    }

    [Fact]
    public void A_spreadsheet_label_and_value_in_two_cells_are_located_by_label_right_and_one_label_locates_one_field()
    {
        // Corpus v4 c4: "Loss experience valued" in A2, the date in B2; the model wrote a regex over A2 for two fields.
        var graph = new DocumentGraph
        {
            SourceFile = new SourceFile { FileName = "x.xlsx", Kind = FileKind.Xlsx },
            Cells =
            [
                new GraphCell { Sheet = "Claims", Row = 1, Column = 0, A1 = "A2", RawValue = "Loss experience valued" },
                new GraphCell { Sheet = "Claims", Row = 1, Column = 1, A1 = "B2", RawValue = "2024-11-10", TypedValue = "2024-11-10" },
            ],
        };
        DocumentFieldSpec Regex(string target) => new() { Target = target, Locator = new() { Type = "regex", Pattern = @"^Loss experience valued\s+(?<value>\d{4}-\d{2}-\d{2})$" }, Normalizer = new() { Type = "date" } };
        var spec = new CarrierFormat { FileKind = "spreadsheet", DocumentFields = [Regex("document.reportRunDate"), Regex("document.valuationDate")] };

        var notes = SpecLint.LocateLabelRightValues(spec, graph, CanonicalData.Glossary);

        Assert.Single(notes);
        Assert.Equal("labelRight", spec.DocumentFields.Single(f => f.Target == "document.valuationDate").Locator.Type); // the required field first
        Assert.Equal("regex", spec.DocumentFields.Single(f => f.Target == "document.reportRunDate").Locator.Type);
        Assert.Equal(new DateOnly(2024, 11, 10), ReplayEngine.Execute(graph, spec).Document.ValuationDate);
    }

    [Fact]
    public void Claims_with_period_totals_become_an_instruction_and_a_period_summary_reading_is_linted()
    {
        // Brief 20 Answers 2: claim rows are the records, period rows are totals to tie against.
        var graph = C7Graph();
        graph.LayoutEvidence = [new LayoutEvidence("claims-with-period-totals", "t1", 1, "claim rows in 2 table(s) with a claim-number column, and a period table")];

        Assert.Single(PromptBuilder.ConstructHints(graph), h => h.StartsWith("- INSTRUCTION: claim rows are the records", StringComparison.Ordinal));
        var spec = C7Spec();
        spec.TableLocator.RowShape = "periodSummary";
        var replay = ReplayEngine.Execute(graph, spec);
        Assert.Contains(SpecLint.Check(spec, replay, graph), i => i.StartsWith("lint: rowShape periodSummary, but this document prints claim rows", StringComparison.Ordinal));
        Assert.DoesNotContain(SpecLint.Check(C7Spec(), ReplayEngine.Execute(graph, C7Spec()), graph), i => i.Contains("rowShape periodSummary", StringComparison.Ordinal));
    }

    [Fact]
    public void A_printed_zero_listed_as_null_on_an_amount_column_is_kept_as_zero()
    {
        // Brief 20 Answers 4 (c7-000 five-trial study): nullWhen "0.00" on amounts made zeros null and the totals no longer tied.
        var spec = C7Spec();
        foreach (var column in spec.ColumnMap.Where(c => c.Target.EndsWith(".paid", StringComparison.Ordinal) || c.Target.EndsWith(".reserve", StringComparison.Ordinal)))
        {
            column.NullWhen = ["0.00", "-"];
        }

        var graph = C7Graph();
        Assert.Contains(ReplayEngine.Execute(graph, spec).RuleOutcomes, o => o.RuleId is "R-10" or "R-12" && o.Status == Rules.RuleStatus.Fail);

        Assert.NotEmpty(SpecLint.KeepPrintedZeros(spec));

        Assert.All(spec.ColumnMap.Where(c => c.NullWhen.Count > 0), c => Assert.Equal(["-"], c.NullWhen)); // dashes still mean absent
        Assert.DoesNotContain(ReplayEngine.Execute(graph, spec).RuleOutcomes, o => o.RuleId is "R-10" or "R-12" && o.Status == Rules.RuleStatus.Fail);
    }

    [Fact]
    public void Components_declared_on_a_plain_total_are_dropped_and_named_ones_kept()
    {
        // Brief 20 Answers 2 (c7-000 study, mistake 2): "Total Incurred" names no components; "(Ind+Med)" does.
        var spec = new CarrierFormat
        {
            ColumnMap =
            [
                new ColumnMapping { Source = "Total Incurred", Target = "claim.total.incurred", Components = ["indemnity", "medical"] },
                new ColumnMapping { Source = "Paid (Ind+Med)", Target = "claim.total.paid", Components = ["indemnity", "medical"] },
            ],
        };

        Assert.Single(SpecLint.DropUnnamedComponents(spec, CanonicalData.Glossary));

        Assert.Null(spec.ColumnMap[0].Components);
        Assert.Equal(["indemnity", "medical"], spec.ColumnMap[1].Components);
    }

    [Fact]
    public void Raw_status_is_put_back_on_the_status_part_and_a_mapped_component_is_not_absent()
    {
        var spec = new CarrierFormat
        {
            AbsentComponents = ["expense"],
            ColumnMap =
            [
                new ColumnMapping { Source = "Claimant Status", Target = "claim.claimantName", Part = 0 },
                new ColumnMapping { Source = "Claimant Status", Target = "claim.status", Part = 1, Normalizer = new() { Type = "enum", GlossaryField = "claim.status" } },
                new ColumnMapping { Source = "Claimant Status", Target = "claim.rawStatus", Part = 2, PartPattern = "^(?<p0>.+?)\\s+(?<p1>Open|Closed)$" },
                new ColumnMapping { Source = "Exp Paid", Target = "claim.expense.paid", Normalizer = new() { Type = "currency" } },
            ],
        };

        Assert.Equal(2, SpecLint.RepairStackedParts(spec).Count);

        Assert.Equal(1, spec.ColumnMap.Single(c => c.Target == "claim.rawStatus").Part);
        Assert.Empty(spec.AbsentComponents);
    }

    [Fact]
    public void A_header_both_mapped_and_ignored_is_a_lint_issue()
    {
        // Brief 22: a summary spec published a column it also listed as ignored.
        var spec = C7Spec();
        var graph = C7Graph();
        Assert.DoesNotContain(SpecLint.Check(spec, ReplayEngine.Execute(graph, spec), graph), i => i.Contains("also in ignoredHeaders", StringComparison.Ordinal));

        spec.IgnoredHeaders.Add(spec.ColumnMap[0].Source.ToUpperInvariant());

        Assert.Single(SpecLint.Check(spec, ReplayEngine.Execute(graph, spec), graph), i => i.Contains("also in ignoredHeaders", StringComparison.Ordinal));
    }
}
