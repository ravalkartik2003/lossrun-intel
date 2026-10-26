using LossRun.Contracts;
using LossRun.Learning;

namespace LossRun.Evaluation.Tests;

/// <summary>Brief 34 A2: the evaluation runner's exemplar store (the production repair gate, on files under the run folder).</summary>
public class Brief34Tests
{
    private static DocumentGraph Graph(string id) => new() { DocumentId = id, SourceFile = new SourceFile { FileName = id + ".pdf", Kind = FileKind.Pdf } };

    [Fact]
    public async Task A2_the_file_exemplar_store_keeps_the_last_five_and_hands_the_repair_the_configured_count()
    {
        var folder = Directory.CreateTempSubdirectory("exemplars-").FullName;
        try
        {
            var store = new FileExemplarStore(folder);
            foreach (var id in new[] { "d1", "d2", "d3", "d4", "d5", "d6", "d2" })
            {
                await store.RecordAsync("fmt/a", Graph(id)); // d2 accepted again: it moves to the front
            }

            var again = new FileExemplarStore(folder); // persisted: a later document in the same run reads them back
            Assert.Equal(["d2", "d6"], (await again.GetAsync("fmt/a")).Select(g => g.DocumentId));
            Assert.Equal(LearnGate.Default.Exemplars, (await again.GetAsync("fmt/a")).Count);
            Assert.Equal(FileExemplarStore.Kept, Directory.GetFiles(Path.Combine(folder, "fmt_a")).Length); // d1 dropped (6 accepted, 5 kept), its graph deleted
            Assert.False(File.Exists(Path.Combine(folder, "fmt_a", "d1.json")));
            Assert.Empty(await again.GetAsync("another-format"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "config", "azure-prices.json")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("config/azure-prices.json");
    }

    /// <summary>The billed side of every table row (AI lines: name, billed qty, billed $; platform rows whole), by day.</summary>
    private static List<string> BilledSide(string markdown)
    {
        // Only the tables Reconcile writes: the AI lines and the platform lines of each day (a hand-added hourly table is not its output).
        var rows = new List<string>();
        string? table = null;
        foreach (var line in markdown.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (!line.StartsWith("| ", StringComparison.Ordinal))
            {
                table = null;
                continue;
            }

            var c = line.Split('|').Select(x => x.Trim()).ToArray();
            if (table is null)
            {
                table = c[1];
                continue;
            }

            if (line.StartsWith("|---", StringComparison.Ordinal))
            {
                continue;
            }

            if (table == "AI line" && c.Length >= 9)
            {
                rows.Add($"{c[1]} | {c[3]} | {c[6]}");
            }
            else if (table.StartsWith("Platform", StringComparison.Ordinal))
            {
                rows.Add(string.Join(" | ", c.Skip(1).Take(c.Length - 2)));
            }
        }

        return rows;
    }

    [Fact]
    public async Task A4_the_2026_10_23_reconciliation_reproduces_its_billed_side_from_config()
    {
        // The committed reconciliation's metered side came from the live dbo.UsageEvent, which is not offline. Its billed side and its line set
        // come from the bill and from config alone (models.json, the price table's billMeterPrefixes): they must reproduce exactly.
        var folder = Path.Combine(Root(), "docs", "evaluation", "results", "2026-10-23-cost-reconciliation");
        var work = Directory.CreateTempSubdirectory("reconcile-").FullName;
        try
        {
            var output = Path.Combine(work, "reconciliation.md");
            Assert.Equal(0, await Reconcile.RunAsync(Path.Combine(folder, "billing-cost-query.json"), Directory.CreateDirectory(Path.Combine(work, "runs")).FullName, null,
                new DateOnly(2026, 10, 21), new DateOnly(2026, 10, 22), output, publish: false));

            var committed = BilledSide(await File.ReadAllTextAsync(Path.Combine(folder, "reconciliation.md")));
            var rerun = BilledSide(await File.ReadAllTextAsync(output));
            foreach (var row in committed.Where(r => !r.StartsWith("**AI services**", StringComparison.Ordinal) && !r.StartsWith("AI line", StringComparison.Ordinal)))
            {
                Assert.Contains(row, rerun); // every billed line, from config; no model literal in Reconcile.cs (tools/generic-guard.sh rule 3)
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}

/// <summary>Brief 35 Answers 1: the blind-run alignment key compares the carrier like the replay carrier check (one function, CarrierNames.Normalize).</summary>
public class Brief35AlignmentTests
{
    private static LossRunDocument Printed(string carrier) => new()
    {
        CarrierName = carrier,
        ValuationDate = new DateOnly(2025, 1, 1),
        PolicyPeriods = [new PolicyPeriod { PolicyNumber = "WC-100", Claims = [new Claim { ClaimNumber = "WC-23-0001", DateOfInjury = new DateOnly(2023, 2, 1) }] }],
    };

    private static KeyedClaim Archive(string carrier) => new(carrier, "WC-100", "WC 23 0001", new DateOnly(2023, 2, 1), new DateOnly(2025, 1, 1), null, null, null, null, null, null);

    [Theory]
    // The names of the brief 35 five-upload test: the archive keys the carrier one way, the page prints another legal name.
    [InlineData("Synthetic Mutual Insurance Co.", "Synthetic Mutual Insurance Company", "matched")]
    [InlineData("Synthetic Mutual Insurance Co.", "Synthetic Mutual Indemnity Co", "matched")]
    [InlineData("SYNTHETIC MUTUAL", "The Synthetic Mutual Group, Inc.", "matched")]
    // A distinguishing word is not a corporate word: a reviewer's alias settles it on replay, never the alignment key.
    [InlineData("Synthetic Mutual Insurance Co.", "Synthetic Property Casualty Co of America", "archive-only")]
    // Another company's name (the c8 case) never aligns.
    [InlineData("Synthetic Mutual Insurance Co.", "Kappa Foods (synthetic)", "archive-only")]
    public void The_carrier_part_of_the_key_drops_corporate_words(string keyed, string printed, string expected)
    {
        var aligned = Alignment.Align([Archive(keyed)], [("doc", Printed(printed))]);
        Assert.Contains(aligned, a => a.Status == expected);
        Assert.Equal(CarrierNames.Normalize(keyed) == CarrierNames.Normalize(printed), expected == "matched"); // the key and the replay check share one function
    }

    [Fact]
    public void Two_carriers_with_one_core_stay_apart_on_policy_claim_and_date()
    {
        // "X Indemnity" and "X Casualty" share a core; the rest of the key still separates their claims.
        var other = Printed("Synthetic Casualty Co");
        other.PolicyPeriods[0].PolicyNumber = "WC-200";
        var aligned = Alignment.Align([Archive("Synthetic Indemnity Co")], [("doc", other)]);
        Assert.DoesNotContain(aligned, a => a.Status == "matched");
    }
}
