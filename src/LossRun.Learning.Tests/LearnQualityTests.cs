using LossRun.Contracts;
using LossRun.Formats;

namespace LossRun.Learning.Tests;

/// <summary>
/// Brief 19 (learn quality, ADR 0018): the deterministic fixes found on the starr-cameron-county learn, on a hand-built graph
/// of the same shape (labels and values printed as separate paragraphs; inception / expiration columns beside a policy-year
/// column; a close date but no status), and the lint for the new constructs.
/// </summary>
public class LearnQualityTests
{
    private static DocumentGraph StarrShape()
    {
        var graph = new DocumentGraph { DocumentId = "doc-starr-shape", SourceFile = new SourceFile { FileName = "x.pdf", Kind = FileKind.Pdf } };
        graph.Pages.Add(new GraphPage { Number = 1 });
        string[] paragraphs = ["Example Carrier", "Valuation Date", "09/30/2024", "Insured", "Example County"];
        for (var i = 0; i < paragraphs.Length; i++)
        {
            graph.Paragraphs.Add(new GraphParagraph { Id = $"p{i}", Page = 1, Text = paragraphs[i], Polygon = [1, 1 + i * 0.2, 3, 1 + i * 0.2, 3, 1.15 + i * 0.2, 1, 1.15 + i * 0.2] });
        }

        graph.Tables.Add(new GraphTable
        {
            Id = "t0", Page = 1, ColumnCount = 6, HeaderRows = [0],
            HeaderTexts = ["Policy Year", "Inception Date", "Expiration Date", "Claim #", "Close Date", "Total Incurred"],
            HeaderFields = ["period.effectiveDate", "period.effectiveDate", "period.expirationDate", "claim.claimNumber", "claim.dateClosed", "claim.total.incurred"],
        });
        return graph;
    }

    private static CarrierFormat LearnedShape() => new()
    {
        FormatId = "candidate", Version = 1, FileKind = "pdf", CarrierName = "Example Carrier",
        Periods = new PeriodSpec { Source = "column", Column = "Policy Year" },
        ColumnMap =
        [
            new ColumnMapping { Source = "Policy Year", Target = "period.policyNumber" },
            new ColumnMapping { Source = "Claim #", Target = "claim.claimNumber", Normalizer = new NormalizerSpec { Type = "claimNumber" } },
            new ColumnMapping { Source = "Close Date", Target = "claim.dateClosed", Normalizer = new NormalizerSpec { Type = "date" } },
            new ColumnMapping { Source = "Total Incurred", Target = "claim.total.incurred", Normalizer = new NormalizerSpec { Type = "currency" } },
        ],
        IgnoredHeaders = ["Inception Date", "Expiration Date"],
        DocumentFields =
        [
            new DocumentFieldSpec { Target = "document.valuationDate", Locator = new FieldLocator { Type = "regex", Pattern = "Valuation Date:\\s*(?<value>\\S+)" } },
            new DocumentFieldSpec { Target = "document.insuredName", Locator = new FieldLocator { Type = "regex", Pattern = "Insured:\\s*(?<value>.+)$" } },
        ],
    };

    [Fact]
    public void A_label_and_its_value_printed_as_two_paragraphs_are_located_by_label_next()
    {
        var spec = LearnedShape();

        var notes = SpecLint.LocateLabelNextDates(spec, StarrShape(), CanonicalData.Glossary);

        Assert.Equal(2, notes.Count);
        var valuation = spec.DocumentFields.Single(f => f.Target == "document.valuationDate");
        Assert.Equal(("labelNext", "Valuation Date", "date"), (valuation.Locator.Type, valuation.Locator.Label, valuation.Normalizer.Type));
        Assert.Equal(("labelNext", "Insured"), (spec.DocumentFields.Single(f => f.Target == "document.insuredName").Locator.Type, spec.DocumentFields.Single(f => f.Target == "document.insuredName").Locator.Label));
    }

    [Fact]
    public void Printed_inception_and_expiration_columns_become_the_period_dates()
    {
        var spec = LearnedShape();

        SpecLint.MapPeriodDateColumns(spec, StarrShape(), CanonicalData.Glossary);

        Assert.Equal("Inception Date", spec.ColumnMap.Single(c => c.Target == "period.effectiveDate").Source); // not the 0.5-confidence "Policy Year"
        Assert.Equal("Expiration Date", spec.ColumnMap.Single(c => c.Target == "period.expirationDate").Source);
        Assert.Empty(spec.IgnoredHeaders);
    }

    /// <summary>Starr's shape with two claim rows (one open, one closed) and a total row printing open / closed counts.</summary>
    private static DocumentGraph WithRows(int printedOpen, int printedClosed)
    {
        var graph = StarrShape();
        string[][] rows =
        [
            ["Policy Year", "Inception Date", "Expiration Date", "Claim #", "Close Date", "Total Incurred"],
            ["2023", "01/01/2023", "01/01/2024", "C-1", "", "100.00"],
            ["2023", "01/01/2023", "01/01/2024", "C-2", "05/05/2023", "200.00"],
            [$"Total (2 claims: {printedOpen} open / {printedClosed} closed)", "", "", "", "", "300.00"],
        ];
        for (var r = 0; r < rows.Length; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                graph.Cells.Add(new GraphCell { TableId = "t0", Page = 1, Row = r, Column = c, RawValue = rows[r][c], Kind = r == 0 ? "columnHeader" : "content" });
            }
        }

        graph.Tables[0].RowCount = rows.Length;
        return graph;
    }

    private static CarrierFormat WithCounts()
    {
        var spec = LearnedShape();
        spec.TableLocator.RowKinds.Add(new RowKindRule
        {
            Kind = "grandTotal", Regex = "^Total",
            CountsRegex = @"(?<claimCount>\d+) claims: (?<openCount>\d+) open / (?<closedCount>\d+) closed",
        });
        return spec;
    }

    [Fact]
    public void Status_from_the_close_date_is_kept_only_when_printed_open_and_closed_counts_confirm_it()
    {
        // Brief 19 Answers 2: a carrier convention, verified per format, never assumed.
        var confirmed = WithCounts();
        Assert.Single(SpecLint.DeclareStatusFrom(confirmed, WithRows(1, 1), CanonicalData.Glossary));
        Assert.Equal("closeDate", confirmed.StatusFrom);

        var contradicted = WithCounts();
        SpecLint.DeclareStatusFrom(contradicted, WithRows(2, 0), CanonicalData.Glossary); // the run counts both as open
        Assert.Equal("unknown", contradicted.StatusFrom);

        var noCounts = LearnedShape();
        SpecLint.DeclareStatusFrom(noCounts, StarrShape(), CanonicalData.Glossary);
        Assert.Equal("unknown", noCounts.StatusFrom);
    }

    [Fact]
    public void An_unknown_status_is_recorded_and_flagged_not_guessed()
    {
        var spec = WithCounts();
        spec.StatusFrom = "unknown";

        var replay = Formats.Replay.ReplayEngine.Execute(WithRows(1, 1), spec);

        Assert.All(replay.Document.PolicyPeriods.SelectMany(p => p.Claims), c => Assert.Equal(ClaimStatus.Unknown, c.Status));
        Assert.Contains(replay.RuleOutcomes, o => o.RuleId == "R-30" && o.Status == Rules.RuleStatus.Fail && o.Severity == Rules.RuleSeverity.Flag);
    }

    [Fact]
    public void A_run_without_a_medical_column_records_medical_absent_and_the_gate_does_not_require_it()
    {
        // Brief 19 Answers 1 (Starr): indemnity and expense printed, no medical column.
        var spec = WithCounts();
        Assert.Single(SpecLint.DeclareAbsentComponents(spec, WithRows(1, 1), CanonicalData.Glossary));
        Assert.Equal(["medical"], spec.AbsentComponents);

        var replay = Formats.Replay.ReplayEngine.Execute(WithRows(1, 1), spec);
        Assert.Equal(false, replay.Document.ComponentPresence?.Medical?.Present);
        Assert.DoesNotContain(LearnGate.Default.Missing(replay.Document), m => m.Contains("medical", StringComparison.Ordinal));

        var printed = WithRows(1, 1);
        printed.Tables[0].HeaderTexts[5] = "Medical Paid";
        var other = WithCounts();
        Assert.Empty(SpecLint.DeclareAbsentComponents(other, printed, CanonicalData.Glossary));
    }

    [Fact]
    public void Construct_hints_reach_the_model_only_for_documents_that_show_the_construct()
    {
        // Brief 19 regression: in the shared prefix the constructs were applied to ordinary layouts.
        var hints = PromptBuilder.ConstructHints(StarrShape());
        Assert.Contains(hints, h => h.Contains("statusFrom", StringComparison.Ordinal));
        Assert.Contains(hints, h => h.Contains("labelNext", StringComparison.Ordinal));
        Assert.DoesNotContain(hints, h => h.Contains("tableLocator.records", StringComparison.Ordinal));

        // An ordinary layout gets no construct hint; only the brief 20 Answers 2 fact (claim rows with per-period totals).
        Assert.All(PromptBuilder.ConstructHints(Graphs.Native()), h => Assert.StartsWith("- INSTRUCTION: claim rows are the records", h, StringComparison.Ordinal));
        Assert.DoesNotContain("\"part\": 0", PromptBuilder.StaticPrefix, StringComparison.Ordinal);
    }

    [Fact]
    public void Ingestion_evidence_becomes_an_instruction_naming_the_construct_and_the_evidence()
    {
        // Brief 19 Answers 3: an instruction from evidence, not a hint; no evidence, no instruction.
        var graph = StarrShape();
        graph.LayoutEvidence = [new LayoutEvidence("records-rows", "t0", 1, "2 of 2 claim-number rows are each followed by an amounts row without a claim number")];

        var hints = PromptBuilder.ConstructHints(graph);

        var instruction = Assert.Single(hints, h => h.StartsWith("- INSTRUCTION", StringComparison.Ordinal));
        Assert.Contains("\"mode\": \"rows\"", instruction, StringComparison.Ordinal);
        Assert.Contains("followed by an amounts row", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain(PromptBuilder.ConstructHints(StarrShape()), h => h.Contains("INSTRUCTION", StringComparison.Ordinal));
    }

    [Fact]
    public void A_status_rule_beside_a_status_column_and_an_inverted_lookup_are_repaired()
    {
        var spec = LearnedShape();
        spec.ColumnMap.Add(new ColumnMapping { Source = "Status", Target = "claim.status" });
        spec.StatusFrom = "closeDate";
        Assert.Single(SpecLint.DeclareStatusFrom(spec, StarrShape(), CanonicalData.Glossary));
        Assert.Null(spec.StatusFrom);

        spec.Periods = new PeriodSpec { Source = "column", Column = "Policy", Lookup = new PeriodLookup { Sheet = "Summary", KeyHeader = "Policy", Subtotals = new() { ["claimCount"] = "Claims", ["Total Incurred"] = "total.incurred" } } };
        Assert.Single(SpecLint.FixInvertedLookupSubtotals(spec));
        Assert.Equal(new Dictionary<string, string> { ["Claims"] = "claimCount", ["Total Incurred"] = "total.incurred" }, spec.Periods.Lookup!.Subtotals);
    }

    [Fact]
    public void A_total_row_regex_anchored_before_the_amounts_is_unanchored_only_when_that_makes_it_match()
    {
        var graph = StarrShape();
        graph.Cells.AddRange([
            new GraphCell { TableId = "t0", Page = 1, Row = 5, Column = 0, RawValue = "Policy Total - 2 Claims" },
            new GraphCell { TableId = "t0", Page = 1, Row = 5, Column = 5, RawValue = "1,234.00" },
        ]);
        var spec = LearnedShape();
        spec.TableLocator.RowKinds.Add(new RowKindRule { Kind = "subtotal", Regex = @"^Policy Total\s*-?\s*\d+\s+Claims?$" });
        spec.TableLocator.RowKinds.Add(new RowKindRule { Kind = "grandTotal", Regex = "^Account Total$" });

        Assert.Single(SpecLint.UnanchorTotalRowKinds(spec, graph));
        Assert.False(spec.TableLocator.RowKinds[0].Regex.EndsWith('$'));
        Assert.Equal("^Account Total$", spec.TableLocator.RowKinds[1].Regex); // matches nothing either way: left to the model
    }

    [Fact]
    public void Parts_of_a_spec_that_cannot_take_effect_are_removed()
    {
        var spec = LearnedShape();
        spec.TableLocator.Records = new RecordSpec { Mode = "rows", LineFields = [new LineFieldSpec { Pattern = "^x$", Target = "claim.claimType" }] };
        spec.DocumentFields.Add(new DocumentFieldSpec { Target = "document.reportRunDate", Locator = new FieldLocator { Type = "const", Value = System.Text.Json.JsonDocument.Parse("\"2024-01-01\"").RootElement } });
        spec.DocumentFields.Add(new DocumentFieldSpec { Target = "document.expenseTreatment", Locator = new FieldLocator { Type = "const", Value = System.Text.Json.JsonDocument.Parse("\"separate\"").RootElement } });

        Assert.Equal(2, SpecLint.DropInertParts(spec).Count);
        Assert.Empty(spec.TableLocator.Records.LineFields);
        Assert.DoesNotContain(spec.DocumentFields, f => f.Target == "document.reportRunDate");
        Assert.Contains(spec.DocumentFields, f => f.Target == "document.expenseTreatment"); // an allowed layout constant stays
    }

    [Fact]
    public void The_construct_lint_names_inconsistent_uses()
    {
        var spec = LearnedShape();
        spec.ColumnMap.Add(new ColumnMapping { Source = "Ind Paid:", Target = "claim.indemnity.paid", Line = 1, Under = "Claim #" }); // no records
        spec.ColumnMap.Add(new ColumnMapping { Source = "Total Incurred (Ind+Med)", Target = "claim.total.incurred", Components = ["indemnity", "expense"] });
        spec.ColumnMap.Add(new ColumnMapping { Source = "Claim Number Claim Type", Target = "claim.claimType", Part = 1, PartPattern = "^(?<p0>\\S+)$" });
        spec.StatusFrom = "always";

        var issues = SpecLint.Check(spec, Formats.Replay.ReplayEngine.Execute(StarrShape(), spec));

        Assert.Contains(issues, i => i.Contains("needs tableLocator.records.mode = rows", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Contains("the header names indemnity + medical", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Contains("capture group p1", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Contains("statusFrom \"always\"", StringComparison.Ordinal));
    }
}
