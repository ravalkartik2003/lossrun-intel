using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using LossRun.Contracts;
using Xunit.Abstractions;

namespace LossRun.Analytics.Tests;

internal static class Files
{
    public static string Twin(string name) => Path.Combine(AppContext.BaseDirectory, "twin", name);

    /// <summary>A repo file (walks up from the test binaries), for files the tests regenerate on request.</summary>
    public static string Repo(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            {
                return Path.Combine([dir.FullName, .. parts]);
            }
        }

        throw new DirectoryNotFoundException("repo root");
    }

    public static bool Update => Environment.GetEnvironmentVariable("LOSSRUN_UPDATE_GOLDEN") == "1";
}

/// <summary>Brief 10 §1: the catalog validates and every metric is implemented; catalog.json for the twin is current.</summary>
public class CatalogTests
{
    [Fact]
    public void The_catalog_validates_against_its_schema()
    {
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schema", "catalog.schema.json")));

        var result = schema.Evaluate(JsonNode.Parse(Catalog.Embedded.ToJson()), new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, string.Join("; ", result.Details.Where(d => d.HasErrors).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation} {e.Value}"))));
        Assert.True(Catalog.Embedded.Metrics.Count >= 40);
        Assert.Equal(Catalog.Embedded.Metrics.Count, Catalog.Embedded.Metrics.Select(m => m.Name).Distinct().Count());
    }

    [Fact]
    public void Every_catalog_metric_has_an_implementation_and_a_hand_computed_expectation()
    {
        var hand = JsonNode.Parse(File.ReadAllText(Files.Twin("hand-account.json")))!;
        var snapshot = FeatureEngine.Compute(AnalyticsJson.ReadInput(hand["input"]!.ToJsonString()));

        Assert.Equal(Catalog.Embedded.Metrics.Select(m => m.Name), snapshot.Metrics.Select(m => m.Name));
        Assert.Equal(Catalog.Embedded.Metrics.Select(m => m.Name).Order(), hand["expected"]!.AsObject().Select(kv => kv.Key).Order());
    }

    [Fact]
    public void The_twin_catalog_json_is_generated_from_the_yaml()
    {
        var path = Files.Repo("src", "web", "packages", "metrics", "catalog.json");
        var expected = Catalog.Embedded.ToJson() + "\n";
        if (Files.Update)
        {
            File.WriteAllText(path, expected);
        }

        Assert.Equal(expected, File.ReadAllText(path).ReplaceLineEndings("\n"));
    }
}

/// <summary>Brief 10 acceptance: every metric against a hand-computed value (hand-account.json, shared with the twin).</summary>
public class HandComputedTests(ITestOutputHelper output)
{
    private static readonly JsonNode Hand = JsonNode.Parse(File.ReadAllText(Files.Twin("hand-account.json")))!;
    private static readonly AnalyticsInput Input = AnalyticsJson.ReadInput(Hand["input"]!.ToJsonString());

    public static TheoryData<string> Metrics() => [.. Hand["expected"]!.AsObject().Select(kv => kv.Key)];

    [Theory]
    [MemberData(nameof(Metrics))]
    public void Metric_matches_the_hand_computed_value(string name)
    {
        var expected = Hand["expected"]![name]!;
        var snapshot = JsonNode.Parse(FeatureEngine.Compute(Input).ToJson())!;
        var actual = snapshot["metrics"]!.AsArray().Single(m => m!["name"]!.GetValue<string>() == name)!;
        output.WriteLine(actual.ToJsonString());

        Assert.Equal(expected["value"]!.GetValue<string>(), Printed(actual["value"]));
        if (expected["breakdown"] is JsonArray breakdown)
        {
            Assert.Equal(breakdown.Select(b => $"{b![0]}={b[1]}"), actual["breakdown"]!.AsArray().Select(b => $"{b!["key"]}={Printed(b["value"])}"));
        }

        if (expected["contributors"] is { } contributors)
        {
            Assert.Equal(contributors.ToJsonString(), actual["contributors"]!.ToJsonString());
        }

        if (expected["overrides"] is { } overrides)
        {
            Assert.Equal(overrides.ToJsonString(), actual["overrides"]!.ToJsonString());
        }
    }

    public static TheoryData<int> Scenarios() => [.. Enumerable.Range(0, Hand["scenarios"]!.AsArray().Count)];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Scenario_matches_the_hand_computed_values(int index)
    {
        var s = Hand["scenarios"]![index]!;
        var scenario = AnalyticsJson.ReadScenario(JsonDocument.Parse(s["scenario"]!.ToJsonString()).RootElement);

        var snapshot = JsonNode.Parse(FeatureEngine.Compute(Input, scenario).ToJson())!;

        Assert.Equal(s["claims"]!["scenarioExcluded"]!.GetValue<int>(), snapshot["claims"]!["scenarioExcluded"]!.GetValue<int>());
        Assert.Equal(s["claims"]!["included"]!.GetValue<int>(), snapshot["claims"]!["included"]!.GetValue<int>());
        foreach (var (name, value) in s["expected"]!.AsObject())
        {
            Assert.Equal(value!.GetValue<string>(), Printed(snapshot["metrics"]!.AsArray().Single(m => m!["name"]!.GetValue<string>() == name)!["value"]));
        }

        Assert.NotNull(snapshot["scenario"]); // a non-baseline scenario is printed with the snapshot
    }

    [Fact]
    public void Per_claim_flags_name_ct_post_termination_large_litigated_non_ca_and_excluded_rows()
    {
        var snapshot = FeatureEngine.Compute(Input, new Scenario { ExcludeClaimIds = ["d/1/2"] });

        var flags = snapshot.PerClaimFlags.ToDictionary(f => f.ClaimId, f => string.Join(",", f.Flags));
        Assert.Equal("ct,large,litigated,open", flags["d/0/1"]);
        Assert.Equal("ct,large,nonCA,open", flags["d/0/3"]);
        Assert.Equal("ct,postTermination,litigated,open", flags["d/1/1"]);
        Assert.Equal("excluded", flags["d/1/2"]);
        Assert.Equal("nonWc", flags["d/1/3"]);
        Assert.False(flags.ContainsKey("d/0/0"));
    }

    [Fact]
    public void Per_period_aggregates_sum_the_claims_of_each_policy_period()
    {
        var p = FeatureEngine.Compute(Input).PerPeriod;

        Assert.Equal([("P22", 2022, 4, 2), ("P23", 2023, 3, 1)], p.Select(x => (x.PolicyNumber, x.PolicyYear!.Value, x.ClaimCount, x.OpenCount)));
        Assert.Equal([49_300_000L, 19_500_000L, 68_800_000L], p[0].Total);
    }

    [Fact]
    public void The_snapshot_is_deterministic_and_hashes_its_input()
    {
        var a = FeatureEngine.Compute(Input).ToJson();
        var b = FeatureEngine.Compute(AnalyticsJson.ReadInput(AnalyticsJson.WriteInput(Input))).ToJson();

        Assert.Equal(a, b);
        Assert.Matches("^[0-9a-f]{64}$", FeatureEngine.InputsHash(Input));
        Assert.Equal(AnalyticsJson.WriteInput(Input), AnalyticsJson.WriteInput(AnalyticsJson.ReadInput(AnalyticsJson.WriteInput(Input))));
    }

    [Fact]
    public void A_metric_without_its_inputs_says_why_and_a_printed_last_day_worked_is_enough_for_post_termination()
    {
        var bare = Input with { Context = Input.Context with { PayrollCentsByYear = null, FteByYear = null, LastDayWorked = null } };
        var s = FeatureEngine.Compute(bare);

        Assert.Equal("no payroll in the submission context", s["frequency_per_100_payroll"].NotMeasurable);
        Assert.Equal("no FTE count in the submission context", s["frequency_per_100_fte"].NotMeasurable);
        Assert.Equal("no last day worked in the submission context or on the claims", s["post_termination_ct_count"].NotMeasurable);
        Assert.Null(s["claim_count"].NotMeasurable);
        Assert.Contains("\"value\":null,\"notMeasurable\":\"no payroll in the submission context\"", s.ToJson());

        // Report 10 answer 1: the carrier prints the claimant's last day worked on one CT claim (d/1/1, injured 2024-02-15).
        var printed = bare with { Claims = [.. bare.Claims.Select(c => c.Id == "d/1/1" ? c with { LastDayWorked = "2024-01-31" } : c)] };
        var t = FeatureEngine.Compute(printed);
        Assert.Equal(1, t["post_termination_ct_count"].Value);
        Assert.Contains("postTermination", t.PerClaimFlags.Single(f => f.ClaimId == "d/1/1").Flags);
        Assert.Equal(t.ToJson(), FeatureEngine.Compute(AnalyticsJson.ReadInput(AnalyticsJson.WriteInput(printed))).ToJson()); // round-trips
    }

    /// <summary>A canonical number as its literal text ("0.5", "107500").</summary>
    private static string Printed(JsonNode? node) => node is null ? "null" : node.ToJsonString();
}

/// <summary>Brief 10 §2: building the engine input from canonical documents; as-reported reverses the overrides.</summary>
public class AccountBuilderTests
{
    private static LossRunDocument Doc() => new()
    {
        DocumentId = "doc-1",
        ValuationDate = new DateOnly(2025, 6, 30),
        PolicyPeriods =
        [
            new PolicyPeriod
            {
                PolicyNumber = "P1", EffectiveDate = new DateOnly(2023, 7, 1),
                Claims =
                [
                    new Claim
                    {
                        ClaimNumber = "WC-1", Status = ClaimStatus.Open, ClaimType = ClaimType.Indemnity, DateOfInjury = new DateOnly(2023, 9, 1), JurisdictionState = "CA",
                        Indemnity = new MoneyTriplet(100.10m, 200.00m, 300.10m), Medical = new MoneyTriplet(50m, null, 80m), LineOfBusiness = "WC",
                    },
                    new Claim { ClaimNumber = "WC-2", Status = ClaimStatus.Closed, Total = new MoneyTriplet(1000m, 0m, 1000m) },
                ],
            },
        ],
    };

    [Fact]
    public void Claims_become_cent_rows_with_lineage_ids()
    {
        var input = AccountBuilder.Build("Q-1", [new AccountDocument("doc-1", Doc(), [])]);

        var c = input.Claims[0];
        Assert.Equal(("doc-1/0/0", 2023, "open", "indemnity"), (c.Id, c.PolicyYear!.Value, c.Status, c.ClaimType));
        Assert.Equal((10_010L, 20_000L, 5_000L, 3_000L), (c.IndemnityPaidCents, c.IndemnityReserveCents, c.MedicalPaidCents, c.MedicalReserveCents)); // reserve = incurred − paid
        Assert.Equal(100_000L, input.Claims[1].IndemnityPaidCents); // totals only: carried under indemnity
        Assert.Equal(("Q-1", "2025-06-30"), (input.Context.QuoteId, input.Context.ValuationDate));
    }

    [Fact]
    public void As_reported_puts_each_overridden_field_back_to_the_value_before_its_first_override()
    {
        var current = Doc();
        current.PolicyPeriods[0].Claims[0].Indemnity!.Reserve = 50m; // after two overrides 200 → 120 → 50
        current.PolicyPeriods[0].Claims[0].Status = ClaimStatus.Closed;
        OverrideRecord[] overrides =
        [
            new("7", "doc-1", "policyPeriods[0].claims[0].indemnity.reserve", "200.00", "120.00", "review", "uw", DateTimeOffset.Parse("2026-10-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            new("9", "doc-1", "policyPeriods[0].claims[0].indemnity.reserve", "120.00", "50", "review", "uw", DateTimeOffset.Parse("2026-10-02T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            new("8", "doc-1", "policyPeriods[0].claims[0].status", "open", "closed", "closed by adjuster", "uw", DateTimeOffset.Parse("2026-10-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
        ];

        var reported = AccountBuilder.Build("Q-1", [new AccountDocument("doc-1", current, overrides)], AccountBuilder.AsReported);
        var adjusted = AccountBuilder.Build("Q-1", [new AccountDocument("doc-1", current, overrides)], AccountBuilder.AsAdjusted);

        Assert.Equal((20_000L, "open"), (reported.Claims[0].IndemnityReserveCents, reported.Claims[0].Status));
        Assert.Equal((5_000L, "closed"), (adjusted.Claims[0].IndemnityReserveCents, adjusted.Claims[0].Status));
        Assert.Equal(["7:indemnity.reserve", "9:indemnity.reserve", "8:status"], adjusted.Overrides.Select(o => $"{o.Id}:{o.Field}"));
        Assert.All(adjusted.Overrides, o => Assert.Equal("doc-1/0/0", o.ClaimId));

        var delta = FeatureEngine.Compute(adjusted)["reserve_total"];
        Assert.Equal(["7", "9"], delta.Overrides); // the overrides that explain reported → adjusted for this metric
    }
}

/// <summary>
/// Brief 10 §7: a 1,000-claim / 5-year account computes within 50 ms server-side. A latency budget, so it runs alone
/// (<c>--filter Category=Performance</c>): with other test assemblies in parallel, CPU contention breaks it (src/README.md).
/// </summary>
[Trait("Category", "Performance")]
public class PerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void A_thousand_claim_five_year_account_computes_within_budget()
    {
        var input = PerfAccount.Build();
        var scenario = new Scenario { ReserveFactorBps = 1000, CapPerClaimCents = 25_000_000, IncludeCT = false };
        for (var i = 0; i < 10; i++)
        {
            FeatureEngine.Compute(input, scenario);
        }

        var ms = new List<double>();
        for (var i = 0; i < 60; i++)
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            FeatureEngine.Compute(input, scenario).ToJson();
            ms.Add(t.Elapsed.TotalMilliseconds);
        }

        ms.Sort();
        var (p50, p95) = (ms[29], ms[56]);
        output.WriteLine($"compute + canonical JSON, {input.Claims.Count} claims: p50 {p50:0.00} ms · p95 {p95:0.00} ms · max {ms[^1]:0.00} ms");
        Assert.True(p50 <= 50, $"p50 {p50:0.0} ms over the 50 ms budget");

        var golden = JsonNode.Parse(File.ReadAllText(Files.Twin("golden.json")))!;
        Assert.Equal(golden["perfAccountSnapshotSha256"]!.GetValue<string>(),
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(FeatureEngine.Compute(input).ToJson()))));
    }
}

/// <summary>Brief 10 §3: golden parity cases (≥ 50) the TypeScript twin asserts byte-for-byte too.</summary>
public class GoldenTests
{
    [Fact]
    public void Every_golden_case_reproduces_byte_for_byte()
    {
        var path = Files.Twin("golden.json");
        var cases = JsonNode.Parse(File.ReadAllText(path))!["cases"]!.AsArray();

        Assert.True(cases.Count >= 50, $"{cases.Count} golden cases");
        foreach (var c in cases)
        {
            var input = AnalyticsJson.ReadInput(c!["input"]!.ToJsonString());
            var scenario = c["scenario"] is { } s ? AnalyticsJson.ReadScenario(JsonDocument.Parse(s.ToJsonString()).RootElement) : null;
            var actual = FeatureEngine.Compute(input, scenario, c["view"]!.GetValue<string>()).ToJson();
            Assert.True(actual == c["expected"]!.GetValue<string>(), $"golden case {c["name"]} differs");
        }
    }
}
