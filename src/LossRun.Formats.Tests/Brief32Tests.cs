using System.Text.RegularExpressions;
using LossRun.Contracts;
using LossRun.Formats.Replay;

namespace LossRun.Formats.Tests;

/// <summary>Brief 32 item 2: every replay site that leaves a printed value absent routes it (brief 30 item 4 covered the normalizers only).</summary>
public class Brief32Tests
{
    [Fact]
    public async Task A_printed_period_date_that_does_not_parse_routes_to_review_with_its_cell()
    {
        var graph = Graphs.Xlsx();
        var range = graph.Cells.First(c => Regex.IsMatch(c.RawValue.Trim(), @"^\d{2}/\d{2}/\d{4}\s*-\s*\d{2}/\d{2}/\d{4}$"));
        range.RawValue = "13/45/2022" + range.RawValue.Trim()[10..]; // two digits each: the range regex matches, the date does not parse

        var (_, replay, decision) = await new FormatMemory(await Graphs.RegistryAsync(), null).ProcessAsync(graph, "run", DateTimeOffset.UnixEpoch);

        var issue = Assert.Single(replay!.Unnormalized, u => u.FieldPath.EndsWith(".effectiveDate", StringComparison.Ordinal));
        Assert.Equal(("date", range.Sheet), (issue.Normalizer, issue.Sheet));
        Assert.Equal(RoutingTier.Review, decision.Tier); // before brief 32: ReplayAccepted with the period's effective date null
        Assert.Contains(decision.Reasons, r => r.Contains("effectiveDate '13/45/2022'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_period_lookup_table_routes_to_review()
    {
        var graph = Graphs.Xlsx();
        var spec = Graphs.Spec("synthetic-mutual-xlsx");
        var summary = graph.Tables.First(t => t.SheetName == spec.Periods.Lookup!.Sheet);
        summary.SheetName = "Renamed"; // the carrier renamed the summary sheet: every period date and subtotal is unread

        var replay = ReplayEngine.Execute(graph, spec);

        Assert.Contains(replay.Unnormalized, u => (u.FieldPath, u.Normalizer) == ("period lookup", "lookup"));
        Assert.NotEmpty(FormatRouter.UnnormalizedReasons(replay.Unnormalized));
    }

    [Fact]
    public void A_merged_cell_that_is_not_split_routes_to_review_with_its_page()
    {
        var graph = StructuralRepairTests.MergedHeaderGraph();
        var cell = graph.Cells.First(c => c.TableId == "t2" && c.RawValue.StartsWith("10/01/2022", StringComparison.Ordinal));
        cell.RawValue = "10/01/2022"; // the status line is missing

        var replay = ReplayEngine.Execute(graph, StructuralRepairTests.MergedHeaderSpec());

        var issue = Assert.Single(replay.Unnormalized, u => u.Normalizer == "merged-split"); // before brief 32: an issue only, the row's two values absent
        Assert.Equal(cell.Page, issue.Page);
        Assert.Contains("10/01/2022", issue.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_dropped_on_the_type_check_routes_to_review_with_its_cell()
    {
        var graph = Graphs.Csv();
        var spec = Graphs.Spec("synthetic-mutual-csv");
        spec.ColumnMap.First(c => c.Target == "claim.dateOfInjury").Normalizer = new NormalizerSpec { Type = "text" }; // a learned spec with the wrong normalizer: the text passes, the date type does not

        var replay = ReplayEngine.Execute(graph, spec);

        var dropped = replay.Unnormalized.Where(u => u.Normalizer == "type").ToList();
        Assert.NotEmpty(dropped); // before brief 32: issues only, ReplayAccepted with every date of injury null
        Assert.All(dropped, u => Assert.EndsWith(".dateOfInjury", u.FieldPath, StringComparison.Ordinal));
        Assert.All(dropped, u => Assert.NotNull(u.Cell));
    }
}
