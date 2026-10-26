using System.Text.RegularExpressions;
using LossRun.Formats;

namespace LossRun.Learning.Tests;

/// <summary>Brief 30 item 19: a drift repair is verified on the format's previously accepted documents too, not on the new one alone.</summary>
public class AuditFixTests
{
    private const string Delta = """{"additions":[{"source":"Date Rptd","target":"claim.dateReported","normalizer":{"type":"date"}},{"source":"Lit","target":"claim.litigationFlag","normalizer":{"type":"boolean"}}],"ignoredHeaders":[]}""";

    private static FormatRepairer Repairer(IFormatRegistry registry, FakeLearnModel model) =>
        new(model, new FormatLearner(model, registry, Graphs.Options(), clock: new FixedClock()), Graphs.Options());

    [Fact]
    public async Task Item19_a_repair_that_verifies_on_the_new_document_but_breaks_an_accepted_one_is_rejected()
    {
        // The format's previously accepted document, as the repaired spec would now read it: its totals no longer tie.
        var exemplar = Graphs.Native();
        var cell = exemplar.Cells.First(c => c.Row > 0 && Regex.IsMatch(c.RawValue.Trim(), @"^[1-9][\d,]*\.\d\d$"));
        cell.RawValue = "9" + cell.RawValue.Trim();
        var registry = await Graphs.RegistryAsync();
        var model = new FakeLearnModel((r, _) => r.Stage == "delta" ? Delta : "{}");

        var outcome = await Repairer(registry, model).RepairAsync(Graphs.Drifted(), Graphs.Spec("synthetic-mutual-pdf"), ["Date Rptd", "Lit"], "run",
            exemplars: [exemplar]);

        Assert.False(outcome.Published); // before brief 30 the exemplar was never consulted and the repair published v2
        Assert.Contains(outcome.Reasons, r => r.Contains("exemplar", StringComparison.Ordinal) && r.Contains(exemplar.DocumentId, StringComparison.Ordinal));
        Assert.Single(await registry.GetIndexAsync(), e => e.FormatId == "synthetic-mutual-pdf");
    }

    [Fact]
    public async Task Item19_a_repair_that_keeps_the_accepted_documents_verifying_publishes()
    {
        var registry = await Graphs.RegistryAsync();
        var outcome = await Repairer(registry, new FakeLearnModel((r, _) => r.Stage == "delta" ? Delta : "{}"))
            .RepairAsync(Graphs.Drifted(), Graphs.Spec("synthetic-mutual-pdf"), ["Date Rptd", "Lit"], "run", exemplars: [Graphs.Native()]);

        Assert.True(outcome.Published, string.Join(" | ", outcome.Reasons));
        Assert.Equal(2, outcome.Spec!.Version);
    }

    [Fact]
    public void Item19_the_exemplar_count_is_configuration() => Assert.Equal(2, LearnGate.Default.Exemplars);

    /// <summary>Brief 34 A2: an exemplar store as the pipeline uses it (production: Blob; the eval runner: files).</summary>
    private sealed class ListExemplars : IExemplarStore
    {
        public Dictionary<string, List<LossRun.Contracts.DocumentGraph>> Formats { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<LossRun.Contracts.DocumentGraph>> GetAsync(string formatId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LossRun.Contracts.DocumentGraph>>([.. Formats.GetValueOrDefault(formatId, []).Take(LearnGate.Default.Exemplars)]);

        public Task RecordAsync(string formatId, LossRun.Contracts.DocumentGraph graph, CancellationToken cancellationToken = default)
        {
            var list = Formats.TryGetValue(formatId, out var l) ? l : Formats[formatId] = [];
            list.RemoveAll(g => g.DocumentId == graph.DocumentId);
            list.Insert(0, graph);
            return Task.CompletedTask;
        }
    }

    private static LearningPipeline Pipeline(IFormatRegistry registry, FakeLearnModel model, IExemplarStore? exemplars)
    {
        var learner = new FormatLearner(model, registry, Graphs.Options(), clock: new FixedClock());
        return new LearningPipeline(registry, null, learner, new FormatRepairer(model, learner, Graphs.Options()), exemplars: exemplars);
    }

    [Fact]
    public async Task Brief34_the_local_pipeline_rejects_a_repair_that_breaks_an_accepted_document()
    {
        // The Item19 breaking case, now through the pipeline the eval runner uses: before brief 34 it had no exemplar store and published v2.
        var exemplar = Graphs.Native();
        var cell = exemplar.Cells.First(c => c.Row > 0 && Regex.IsMatch(c.RawValue.Trim(), @"^[1-9][\d,]*\.\d\d$"));
        cell.RawValue = "9" + cell.RawValue.Trim();
        var store = new ListExemplars();
        await store.RecordAsync("synthetic-mutual-pdf", exemplar);
        var registry = await Graphs.RegistryAsync();
        var model = new FakeLearnModel((r, _) => r.Stage == "delta" ? Delta : "{}");

        var result = await Pipeline(registry, model, store).ProcessAsync(Graphs.Drifted(), "run", DateTimeOffset.UnixEpoch);

        Assert.Equal(("Review", false), (result.FinalTier, result.Learn!.Published));
        Assert.Contains(result.Learn.Reasons, r => r.Contains("exemplar", StringComparison.Ordinal) && r.Contains(exemplar.DocumentId, StringComparison.Ordinal));
        Assert.Single(await registry.GetIndexAsync(), e => e.FormatId == "synthetic-mutual-pdf");

        // Without a store (the runner before brief 34), the same repair publishes.
        var unguarded = await Pipeline(await Graphs.RegistryAsync(), new FakeLearnModel((r, _) => r.Stage == "delta" ? Delta : "{}"), null)
            .ProcessAsync(Graphs.Drifted(), "run", DateTimeOffset.UnixEpoch);
        Assert.Equal("Delta", unguarded.FinalTier);
    }

    [Fact]
    public async Task Brief34_an_accepted_document_becomes_an_exemplar_the_next_repair_verifies_on()
    {
        var store = new ListExemplars();
        var registry = await Graphs.RegistryAsync();
        var model = new FakeLearnModel((r, _) => r.Stage == "delta" ? Delta : "{}");

        var replayed = await Pipeline(registry, model, store).ProcessAsync(Graphs.Native(), "run", DateTimeOffset.UnixEpoch);
        Assert.Equal("ReplayAccepted", replayed.FinalTier);
        Assert.Equal(Graphs.Native().DocumentId, Assert.Single(store.Formats["synthetic-mutual-pdf"]).DocumentId);

        var repaired = await Pipeline(registry, model, store).ProcessAsync(Graphs.Drifted(), "run", DateTimeOffset.UnixEpoch);
        Assert.Equal("Delta", repaired.FinalTier); // the good exemplar keeps verifying, so the repair publishes
        Assert.Equal(2, store.Formats["synthetic-mutual-pdf"].Count);
    }

    [Fact]
    public void Item45_a_date_constant_is_located_by_a_glossary_label_not_by_the_first_equal_cell()
    {
        // The valuation date also happens to be a claim's date of injury, printed first, in a claims row ("WC-22-0001" to its left).
        var graph = new LossRun.Contracts.DocumentGraph { DocumentId = "doc-c" };
        graph.Pages.Add(new LossRun.Contracts.GraphPage { Number = 1 });
        void Cell(int row, int column, string text) => graph.Cells.Add(new LossRun.Contracts.GraphCell { Page = 1, Row = row, Column = column, RawValue = text });
        Cell(5, 0, "WC-22-0001");
        Cell(5, 1, "2024-09-30");
        Cell(1, 0, "Valuation Date");
        Cell(1, 1, "2024-09-30");
        var spec = Graphs.Spec("synthetic-mutual-xlsx");
        spec.DocumentFields.Clear();
        spec.DocumentFields.Add(new DocumentFieldSpec
        {
            Target = "document.valuationDate",
            Locator = new FieldLocator { Type = "const", Value = System.Text.Json.JsonDocument.Parse("\"2024-09-30\"").RootElement },
        });

        SpecLint.LocatePrintedConstants(spec, graph, LossRun.Contracts.CanonicalData.Glossary);

        Assert.Equal(("labelRight", "Valuation Date"), (spec.DocumentFields[0].Locator.Type, spec.DocumentFields[0].Locator.Label)); // before brief 30: "WC-22-0001"
    }

    private static (CarrierFormat Spec, LossRun.Contracts.DocumentGraph Graph) InjuryTypeColumn(string target, params string[] values)
    {
        var graph = new LossRun.Contracts.DocumentGraph { DocumentId = "doc-it" };
        graph.Tables.Add(new LossRun.Contracts.GraphTable { Id = "t0", Page = 1, HeaderRows = [0], HeaderTexts = ["Claim #", "Injury Type"] });
        graph.Cells.Add(new LossRun.Contracts.GraphCell { TableId = "t0", Page = 1, Row = 0, Column = 1, RawValue = "Injury Type" });
        for (var i = 0; i < values.Length; i++)
        {
            graph.Cells.Add(new LossRun.Contracts.GraphCell { TableId = "t0", Page = 1, Row = i + 1, Column = 1, RawValue = values[i] });
        }

        var spec = Graphs.Spec("synthetic-mutual-xlsx");
        spec.ColumnMap.Clear();
        spec.ColumnMap.Add(new ColumnMapping { Source = "Injury Type", Target = target, Normalizer = new NormalizerSpec { Type = "text" } });
        return (spec, graph);
    }

    [Fact]
    public void Brief31_injury_type_codes_map_to_the_code_field_and_words_to_the_claim_type()
    {
        var glossary = LossRun.Contracts.CanonicalData.Glossary;
        Assert.Equal("claim.injuryTypeCode", glossary.ResolveHeader("Injury Type", ["01", "05", "06", "01"])!.Field);
        Assert.Equal("claim.claimType", glossary.ResolveHeader("Injury Type", ["Indemnity", "Medical Only"])!.Field);
        Assert.Equal("claim.claimType", glossary.ResolveHeader("Injury Type")!.Field); // no values: the higher-confidence alias, as before

        var (codes, codeGraph) = InjuryTypeColumn("claim.claimType", "01", "05", "06");
        Assert.Single(SpecLint.RetargetByValueShape(codes, codeGraph, glossary)); // before brief 31: claim.claimType kept, every code unnormalizable
        Assert.Equal(("claim.injuryTypeCode", "code", "statPlan.injuryType"), (codes.ColumnMap[0].Target, codes.ColumnMap[0].Normalizer.Type, codes.ColumnMap[0].Normalizer.CodeSystem));

        var (words, wordGraph) = InjuryTypeColumn("claim.injuryTypeCode", "Indemnity", "Medical Only");
        SpecLint.RetargetByValueShape(words, wordGraph, glossary);
        Assert.Equal(("claim.claimType", "enum"), (words.ColumnMap[0].Target, words.ColumnMap[0].Normalizer.Type));

        var (right, rightGraph) = InjuryTypeColumn("claim.claimType", "Indemnity");
        Assert.Empty(SpecLint.RetargetByValueShape(right, rightGraph, glossary));
    }

    /// <summary>A synthetic CSV export (the seen fixture's columns); <paramref name="policies"/> is the policy cell of each claim row.</summary>
    private static LossRun.Contracts.DocumentGraph PolicyColumnExport(params string[] policies)
    {
        var lines = new List<string> { "Policy #,Claim #,Claimant,DOI,Report Date,Status,Type,Indemnity Paid,Indemnity Reserve,Medical Paid,Medical Reserve,Expense Paid,Total Incurred" };
        for (var i = 0; i < policies.Length; i++)
        {
            lines.Add($"{policies[i]},WC-24-{i + 1:0000},\"Synthetic Claimant {(char)('A' + i)}\",01/{i + 10}/2024,01/{i + 12}/2024,Closed,MO,0.00,0.00,{i + 1}00.00,0.00,0.00,{i + 1}00.00");
        }

        lines.Add(",Total,,,,,,0.00,0.00,2100.00,0.00,0.00,2100.00");
        return LossRun.Ingestion.Spreadsheets.SpreadsheetReader.Read(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines)),
            new LossRun.Contracts.SourceFile { FileName = "fill-down.csv", Kind = LossRun.Contracts.FileKind.Csv }, "doc-fill", LossRun.Contracts.CanonicalData.Glossary, []);
    }

    [Fact]
    public void Brief31_the_lint_declares_fill_down_when_the_learning_document_shows_the_pattern()
    {
        // The fill-down export: the policy printed on a group's first row only (the total row's blank policy cell does not count).
        var fillDown = PolicyColumnExport("SYN-WC-1001", "", "SYN-WC-1002", "", "", "SYN-WC-1003");
        var spec = Graphs.Spec("synthetic-mutual-csv");
        var note = Assert.Single(SpecLint.DeclareFillDown(spec, fillDown)); // before brief 31: fillDown stayed false, every blank row went to Review
        Assert.True(spec.Periods.FillDown);
        Assert.Contains("3 claim rows", note, StringComparison.Ordinal);
        Assert.DoesNotContain(LossRun.Formats.Replay.ReplayEngine.Execute(fillDown, spec).Unnormalized, u => u.FieldPath.EndsWith("periodAssignment", StringComparison.Ordinal));

        // Not the pattern: the policy repeated on every row, a period whose first row is blank, repeats with a gap, one blank only.
        foreach (var policies in new[] { new[] { "SYN-WC-1001", "SYN-WC-1001", "SYN-WC-1002" }, ["", "SYN-WC-1001", "", ""],
            ["SYN-WC-1001", "SYN-WC-1001", "", "", "SYN-WC-1002"], ["SYN-WC-1001", "", "SYN-WC-1002", "SYN-WC-1003"] })
        {
            var other = Graphs.Spec("synthetic-mutual-csv");
            Assert.Empty(SpecLint.DeclareFillDown(other, PolicyColumnExport(policies)));
            Assert.False(other.Periods.FillDown);
        }
    }
}
