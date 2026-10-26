using System.Text.Json.Nodes;

namespace LossRun.Integration.Tests;

/// <summary>Brief 30 item 13: the loss-information grid never shows 0 for a value the loss run did not print.</summary>
public class AuditFixTests
{
    private static JsonObject Payload(params JsonObject[] claims) => new()
    {
        ["payloadVersion"] = "2", ["quoteId"] = "Q-I13", ["quoteRevision"] = 1, ["readiness"] = new JsonObject { ["ready"] = true, ["blockingFieldCount"] = 0 },
        ["policyPeriods"] = new JsonArray
        {
            new JsonObject
            {
                ["documentId"] = "doc-a", ["uploadId"] = "up-1", ["periodIndex"] = 0, ["policyNumber"] = "SYN-WC-1", ["effectiveDate"] = "2023-07-01",
                ["detailLevel"] = "Claims", ["noLossesStatement"] = false, ["excludedReason"] = null, ["fieldStatus"] = new JsonObject(),
            },
        },
        ["claims"] = new JsonArray([.. claims]), ["periodSummaries"] = new JsonObject { ["rows"] = new JsonArray() },
    };

    private static JsonObject Claim(int i, decimal? incurred, string? type) => new()
    {
        ["id"] = $"doc-a/0/{i}", ["claimNumber"] = $"WC-{i}", ["claimType"] = type, ["total"] = new JsonObject { ["incurred"] = incurred },
        ["expense"] = new JsonObject { ["incurred"] = 0m }, ["fieldStatus"] = new JsonObject(),
    };

    [Fact]
    public void Item13_a_total_with_an_unprinted_incurred_is_incomplete_not_a_smaller_sum()
    {
        var cells = LossInformation.Build("Q-I13", Payload(Claim(0, 1_000m, "indemnity"), Claim(1, null, "indemnity")), null, []).Columns.Single().Cells;

        Assert.Null(cells["incurredLoss"].Value); // before brief 30: "$1,000.00", the unprinted claim counted as 0
        Assert.Equal("incomplete", cells["incurredLoss"].State);
        Assert.Contains("1 of 2", cells["incurredLoss"].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Item13_claim_type_counts_are_not_applicable_when_the_loss_run_prints_no_claim_type()
    {
        var cells = LossInformation.Build("Q-I13", Payload(Claim(0, 1_000m, null), Claim(1, 500m, null)), null, []).Columns.Single().Cells;

        Assert.Equal(("n/a", "none"), (cells["indemnityClaims"].Value, cells["indemnityClaims"].State)); // before: "0"
        Assert.Contains("claim type not on the loss run", cells["indemnityClaims"].Reason, StringComparison.Ordinal);
        Assert.Equal("2", cells["claims"].Value);
        Assert.Equal("$1,500.00", cells["incurredLoss"].Value);
    }
}

/// <summary>Brief 30 item 24: no data route of the mock quote system is anonymous (only its static page files are).</summary>
public class MockAuthTests
{
    [Fact]
    public void Item24_only_the_static_page_of_the_mock_is_anonymous()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "LossRun.sln")))
        {
            dir = Path.GetDirectoryName(dir)!;
        }

        var source = File.ReadAllText(Path.Combine(dir, "LossRun.QuoteSystemMock", "Program.cs"));
        var anonymous = System.Text.RegularExpressions.Regex.Matches(source, @"AuthorizationLevel\.Anonymous[^\]]*Route = ""([^""]+)""").Select(m => m.Groups[1].Value).ToList();
        Assert.All(anonymous, route => Assert.StartsWith("ui/", route, StringComparison.Ordinal)); // before brief 30: the grid and its stream too
        Assert.Single(anonymous);
    }
}

/// <summary>Brief 31 item 6 (ADR 0024 amendment): a reopen's payload may be smaller than the previous revision's; the receiver takes it.</summary>
public class ReopenPayloadTests
{
    [Fact]
    public async Task A_smaller_payload_at_a_newer_reopened_revision_replaces_the_bigger_one()
    {
        var service = new QuoteSystemService(new InMemoryQuoteSystemStorage(), TimeProvider.System);
        var full = LossInformationTests.Payload(2); // doc-a and doc-b verified: 4 claims
        Assert.True(await service.AcceptPayloadAsync("Q-LI-1", full, []));

        // Revision 3, cause "reopened": doc-b is back in review, so its claims leave the verified view.
        var reopened = LossInformationTests.Payload(2);
        reopened["quoteRevision"] = 3;
        reopened["cause"] = "reopened";
        var claims = reopened["claims"]!.AsArray();
        claims.Remove(claims.Single(c => c!["id"]!.GetValue<string>().StartsWith("doc-b/", StringComparison.Ordinal)));
        var docB = reopened["policyPeriods"]!.AsArray().Single(p => p!["documentId"]!.GetValue<string>() == "doc-b")!;
        docB["excludedReason"] = "pending-review";

        Assert.True(await service.AcceptPayloadAsync("Q-LI-1", reopened, [])); // not an error: the verified view shrank
        var grid = LossInformation.Build("Q-LI-1", reopened, null, []);
        Assert.Equal(3, grid.QuoteRevision);
        Assert.Equal("pending-review", grid.Columns.Single(c => c.Key == "doc-b/0").ExcludedReason);
        Assert.False(await service.AcceptPayloadAsync("Q-LI-1", full, [])); // the older, bigger revision is refused
    }
}
