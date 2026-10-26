using System.Text.Json;
using System.Text.Json.Nodes;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Formats.Replay;
using LossRun.Rules;
using Xunit.Abstractions;

namespace LossRun.Formats.Tests;

public class ReplayTests(ITestOutputHelper output)
{
    public static TheoryData<string, string> Families() => new()
    {
        { "native", "synthetic-mutual-pdf" },
        { "scanned", "synthetic-mutual-pdf" },
        { "bundle", "synthetic-mutual-pdf" },
        { "xlsx", "synthetic-mutual-xlsx" },
        { "csv", "synthetic-mutual-csv" },
    };

    private static DocumentGraph Graph(string family) => family switch
    {
        "native" => Graphs.Native(),
        "scanned" => Graphs.Scanned(),
        "bundle" => Graphs.BundleLossRun(),
        "xlsx" => Graphs.Xlsx(),
        "csv" => Graphs.Csv(),
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };

    [Theory]
    [MemberData(nameof(Families))]
    public void Every_fixture_family_replays_and_passes_every_hard_rule(string family, string formatId)
    {
        var result = ReplayEngine.Execute(Graph(family), Graphs.Spec(formatId));

        var hard = result.RuleOutcomes.Where(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard).ToList();
        foreach (var failure in result.RuleOutcomes.Where(o => o.Status == RuleStatus.Fail))
        {
            output.WriteLine($"{failure.RuleId} {failure.Severity}: {failure.Message} {failure.Detail}");
        }

        foreach (var issue in result.Issues)
        {
            output.WriteLine("issue: " + issue);
        }

        Assert.Empty(hard);
        Assert.Equal(3, result.Document.PolicyPeriods.Sum(p => p.Claims.Count));
        Assert.Equal(0, result.ModelCalls);
        Assert.Empty(result.UnmappedHeaders);
        Assert.Empty(result.Issues);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Replay_reads_the_same_values_as_the_source_document(string family, string formatId)
    {
        var expected = LossRun.Testing.SyntheticLossRuns.ThreeClaims();
        var document = ReplayEngine.Execute(Graph(family), Graphs.Spec(formatId)).Document;

        var expectedClaims = expected.PolicyPeriods.SelectMany(p => p.Claims).ToList();
        var actualClaims = document.PolicyPeriods.SelectMany(p => p.Claims).ToList();
        Assert.Equal(expectedClaims.Select(c => c.ClaimNumber), actualClaims.Select(c => c.ClaimNumber));
        Assert.Equal(expectedClaims.Select(c => c.DateOfInjury), actualClaims.Select(c => c.DateOfInjury));
        Assert.Equal(expectedClaims.Select(c => c.Status), actualClaims.Select(c => c.Status));
        Assert.Equal(expectedClaims.Select(c => c.ClaimType), actualClaims.Select(c => c.ClaimType));
        Assert.Equal(expectedClaims.Select(c => c.Indemnity!.Paid), actualClaims.Select(c => c.Indemnity!.Paid));
        Assert.Equal(expectedClaims.Select(c => c.Medical!.Reserve), actualClaims.Select(c => c.Medical!.Reserve));
        Assert.Equal(expectedClaims.Select(c => c.Total!.Incurred), actualClaims.Select(c => c.Total!.Incurred));
        Assert.Equal(expected.PolicyPeriods.Select(p => p.PolicyNumber), document.PolicyPeriods.Select(p => p.PolicyNumber));
        if (family != "csv")
        {
            Assert.Equal(new DateOnly(2024, 9, 30), document.ValuationDate);
            Assert.Equal(expected.PolicyPeriods.Select(p => p.EffectiveDate), document.PolicyPeriods.Select(p => p.EffectiveDate));
            Assert.Equal("Synthetic Mutual Insurance Co.", document.CarrierName);
        }
    }

    [Fact]
    public void Scanned_pdf_replays_through_the_line_fallback()
    {
        var result = ReplayEngine.Execute(Graphs.Scanned(), Graphs.Spec("synthetic-mutual-pdf"));

        Assert.Equal(1, result.Stats.LineFallbackBlocks);
        Assert.Equal(3, result.Stats.LineFallbackRows); // 2 claim lines + the subtotal line (header line excluded)
        Assert.Equal(1, result.Stats.TablesUsed);
        var first = result.Document.PolicyPeriods[0];
        Assert.Equal(["WC-22-0001", "WC-23-0002"], first.Claims.Select(c => c.ClaimNumber));
        Assert.Equal((2, 1, 1), (first.Subtotals!.ClaimCount, first.Subtotals.OpenCount, first.Subtotals.ClosedCount));
        var lineage = result.Document.Lineage!["policyPeriods[0].claims[0].claimNumber"];
        Assert.Equal(("replay", 1, 8), (lineage.Extractor, lineage.Page, lineage.Polygon!.Count));
        Assert.True(result.MinFieldConfidence < 1.0); // OCR confidence carried into field confidence
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Every_non_null_field_has_lineage(string family, string formatId)
    {
        var result = ReplayEngine.Execute(Graph(family), Graphs.Spec(formatId));
        var node = JsonNode.Parse(JsonSerializer.Serialize(result.Document, CanonicalJson.Options))!.AsObject();
        node.Remove("lineage");
        node.Remove("schemaVersion");
        node.Remove("documentId");

        var missing = Leaves(node, "").Where(path => !result.Document.Lineage!.ContainsKey(path)).ToList();

        Assert.Empty(missing);
        Assert.All(result.Document.Lineage!.Values, l =>
        {
            Assert.Equal(formatId, l.FormatId);
            Assert.Equal("1", l.FormatVersion);
            Assert.Contains(l.Extractor, new[] { "replay", "derived", "spec" });
            Assert.Null(l.Model); // no model involved in replay
        });
        Assert.Contains(result.Document.Lineage!.Values, l => l.Extractor == "derived");
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Replay_is_byte_identical_across_runs_and_graph_round_trips(string family, string formatId)
    {
        var graph = Graph(family);
        var spec = Graphs.Spec(formatId);

        var first = ReplayEngine.Execute(graph, spec).ToJson();
        var second = ReplayEngine.Execute(graph, spec).ToJson();
        var roundTripped = ReplayEngine.Execute(Graphs.Copy(graph), FormatJson.Parse(FormatJson.Write(spec))).ToJson();

        Assert.Equal(first, second);
        Assert.Equal(first, roundTripped);
    }

    [Fact]
    public void Spreadsheet_lineage_points_at_cells()
    {
        var lineage = ReplayEngine.Execute(Graphs.Xlsx(), Graphs.Spec("synthetic-mutual-xlsx")).Document.Lineage!;

        Assert.Equal(("Loss Run", "B7", (int?)null), (lineage["policyPeriods[0].claims[0].claimNumber"].Sheet, lineage["policyPeriods[0].claims[0].claimNumber"].Cell, lineage["policyPeriods[0].claims[0].claimNumber"].Page));
        Assert.Equal(("Loss Run", "B2"), (lineage["valuationDate"].Sheet, lineage["valuationDate"].Cell));
        Assert.Equal(("Summary", "B2"), (lineage["policyPeriods[0].effectiveDate"].Sheet, lineage["policyPeriods[0].effectiveDate"].Cell));
        Assert.Equal("derived", lineage["policyPeriods[0].claims[0].total.paid"].Extractor);
        Assert.Equal(1.0, lineage["policyPeriods[0].claims[0].indemnity.paid"].Confidence);
    }

    [Fact]
    public void A_period_lookup_reads_separate_effective_and_expiration_columns()
    {
        // Eval run (c4): the summary sheet prints Effective and Expiration as two columns, not one range cell.
        var expected = ReplayEngine.Execute(Graphs.Xlsx(), Graphs.Spec("synthetic-mutual-xlsx")).Document.PolicyPeriods
            .Select(p => (p.PolicyNumber, p.EffectiveDate, p.ExpirationDate)).ToList();
        var graph = Graphs.Xlsx();
        var table = graph.Tables.Single(t => t.SheetName == "Summary" && t.HeaderTexts.Contains("Policy Period"));
        var cells = graph.Cells.Where(c => c.TableId == table.Id && c.Sheet == "Summary").ToList();
        var column = cells.Min(c => c.Column) + table.HeaderTexts.IndexOf("Policy Period");
        table.HeaderTexts[table.HeaderTexts.IndexOf("Policy Period")] = "Effective";
        table.HeaderTexts.Add("Expiration");
        foreach (var cell in cells.Where(c => c.Column == column && c.RawValue.Contains('-', StringComparison.Ordinal)))
        {
            var parts = System.Text.RegularExpressions.Regex.Split(cell.RawValue, @"\s*-\s*");
            var expiration = JsonSerializer.Deserialize<GraphCell>(JsonSerializer.Serialize(cell, CanonicalJson.Options), CanonicalJson.Options)!;
            (cell.RawValue, expiration.RawValue, expiration.Column) = (parts[0], parts[1], cells.Min(c => c.Column) + table.HeaderTexts.Count - 1);
            graph.Cells.Add(expiration);
        }

        var spec = Graphs.Spec("synthetic-mutual-xlsx");
        (spec.Periods.Lookup!.RangeHeader, spec.Periods.Lookup.RangeRegex, spec.Periods.Lookup.EffectiveHeader, spec.Periods.Lookup.ExpirationHeader) =
            (null, null, "Effective", "Expiration");

        var replay = ReplayEngine.Execute(graph, spec);

        Assert.Empty(replay.Issues);
        Assert.Equal(expected, replay.Document.PolicyPeriods.Select(p => (p.PolicyNumber, p.EffectiveDate, p.ExpirationDate)));
        Assert.All(expected, e => Assert.NotNull(e.EffectiveDate));
    }

    [Fact]
    public void A_column_of_wrong_typed_values_becomes_issues_on_every_row_not_a_crash()
    {
        // 08b: a learned CSV spec mapped a text column to claim.claimType on every row; past 50 rows the replay threw.
        var spec = Graphs.Spec("synthetic-mutual-csv");
        var status = spec.ColumnMap.First(c => c.Target == "claim.status");
        spec.ColumnMap.Add(new ColumnMapping { Source = status.Source, Target = "claim.claimType", Normalizer = new NormalizerSpec { Type = "text" } });
        var graph = Graphs.Csv();
        var rows = ReplayEngine.Execute(graph, Graphs.Spec("synthetic-mutual-csv")).Stats.ClaimRows;

        var replay = ReplayEngine.Execute(graph, spec);

        Assert.Equal(rows, replay.Stats.ClaimRows);
        Assert.Equal(rows, replay.Issues.Count(i => i.Contains(".claimType: value", StringComparison.Ordinal)));
        Assert.All(replay.Document.PolicyPeriods.SelectMany(p => p.Claims), c => Assert.Null(c.ClaimType));
    }

    [Fact]
    public void Unmapped_headers_are_reported()
    {
        var spec = Graphs.Spec("synthetic-mutual-pdf");
        spec.ColumnMap.RemoveAll(c => c.Source == "Exp Paid");

        var result = ReplayEngine.Execute(Graphs.Native(), spec);

        Assert.Equal(["Exp Paid"], result.UnmappedHeaders);
    }

    [Fact]
    public void Ignored_headers_are_not_reported()
    {
        var spec = Graphs.Spec("synthetic-mutual-pdf");
        spec.ColumnMap.RemoveAll(c => c.Source == "Exp Paid");
        spec.IgnoredHeaders.Add("Exp Paid");

        Assert.Empty(ReplayEngine.Execute(Graphs.Native(), spec).UnmappedHeaders);
    }

    [Fact]
    public void Expiration_inclusive_formats_add_a_day()
    {
        var spec = Graphs.Spec("synthetic-mutual-pdf");
        spec.PeriodBoundaries.ExpirationInclusive = true;

        var document = ReplayEngine.Execute(Graphs.Native(), spec).Document;

        Assert.Equal(new DateOnly(2023, 7, 2), document.PolicyPeriods[0].ExpirationDate);
    }

    [Fact]
    public void A_value_of_the_wrong_type_is_an_issue_not_an_exception()
    {
        var spec = Graphs.Spec("synthetic-mutual-pdf");
        spec.DocumentFields.Single(f => f.Target == "document.reportRunDate").Normalizer = new NormalizerSpec { Type = "text" }; // "10/02/2024" is not a date value

        var replay = ReplayEngine.Execute(Graphs.Native(), spec);

        Assert.Null(replay.Document.ReportRunDate);
        Assert.Contains(replay.Issues, i => i.StartsWith("reportRunDate: value \"10/02/2024\" does not fit", StringComparison.Ordinal));
        Assert.Equal(3, replay.Stats.ClaimRows); // the rest of the document is intact
    }

    private static IEnumerable<string> Leaves(JsonNode? node, string path) => node switch
    {
        JsonObject o => o.SelectMany(p => Leaves(p.Value, path.Length == 0 ? p.Key : $"{path}.{p.Key}")),
        JsonArray a => a.SelectMany((n, i) => Leaves(n, $"{path}[{i}]")),
        null => [],
        _ => [path],
    };
}
