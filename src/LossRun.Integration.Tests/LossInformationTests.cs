using System.Text.Json;
using System.Text.Json.Nodes;

namespace LossRun.Integration.Tests;

/// <summary>Brief 25: the quote system's loss-information grid — payload mapping, cell states, revision ordering, the live fetch.</summary>
public class LossInformationTests
{
    private const string Quote = "Q-LI-1";

    /// <summary>
    /// A synthetic payload v2. Revision 1: doc-a (2023, approved, one R-10 field left) and doc-b (2022) waiting for review.
    /// Revision 2: doc-b approved, doc-c rejected.
    /// </summary>
    public static JsonObject Payload(int revision)
    {
        JsonObject Claim(string id, string number, string type, decimal incurred, decimal expense, JsonObject? status = null, bool reportOnly = false) => new()
        {
            ["id"] = id, ["claimNumber"] = number, ["claimType"] = type, ["reportOnly"] = reportOnly,
            ["total"] = new JsonObject { ["incurred"] = status?["total.incurred"] is null ? incurred : null },
            ["expense"] = new JsonObject { ["incurred"] = expense }, ["fieldStatus"] = status ?? [],
        };
        JsonObject Period(string doc, string upload, string? eff, string? exp, string? policy, string? excluded, JsonObject? status = null) => new()
        {
            ["documentId"] = doc, ["uploadId"] = upload, ["periodIndex"] = 0, ["revision"] = 1, ["policyNumber"] = policy, ["effectiveDate"] = eff, ["expirationDate"] = exp,
            ["carrierName"] = excluded is null ? "Synthetic Mutual Insurance Co." : null, ["valuationDate"] = excluded is null ? "2024-09-30" : null,
            ["valuationDateSource"] = doc == "doc-b" ? "submission" : null, ["detailLevel"] = "Claims", ["noLossesStatement"] = false, ["excludedReason"] = excluded,
            ["fieldStatus"] = status ?? [],
        };

        var claims = new JsonArray
        {
            Claim("doc-a/0/0", "WC-23-0001", "indemnity", 21_500m, 1_500m),
            Claim("doc-a/0/1", "WC-23-0002", "medicalOnly", 2_000m, 0m, new JsonObject { ["total.incurred"] = new JsonObject { ["status"] = "unresolved", ["reason"] = "R-10" } }),
            Claim("doc-a/0/2", "WC-23-0003", "medicalOnly", 0m, 0m, reportOnly: true),
        };
        var periods = new JsonArray { Period("doc-a", "up-1", "2023-07-01", "2024-07-01", "SYN-WC-1002", null) };
        if (revision == 1)
        {
            periods.Add(Period("doc-b", "up-2", null, null, null, "pending-review",
                new JsonObject { ["effectiveDate"] = new JsonObject { ["status"] = "unresolved", ["reason"] = "review-pending" } }));
        }
        else
        {
            periods.Add(Period("doc-b", "up-2", "2022-07-01", "2023-07-01", "SYN-WC-1001", null));
            claims.Add(Claim("doc-b/0/0", "WC-22-0001", "indemnity", 650m, 50m));
            periods.Add(Period("doc-c", "up-3", null, null, null, "rejected"));
        }

        return new JsonObject
        {
            ["payloadVersion"] = "2", ["quoteId"] = Quote, ["quoteRevision"] = revision, ["featuresView"] = "verified-only",
            ["readiness"] = new JsonObject { ["ready"] = false, ["blockingFieldCount"] = revision == 1 ? 1 : 1 }, ["policyPeriods"] = periods, ["claims"] = claims,
            ["periodSummaries"] = new JsonObject { ["rows"] = new JsonArray() },
        };
    }

    private static JsonObject Manifest(string upload, string file) => new()
    {
        ["uploadId"] = upload,
        ["items"] = new JsonArray { new JsonObject { ["fileName"] = file, ["classification"] = "LossRun", ["status"] = "processed" },
            new JsonObject { ["fileName"] = "broker-letter.pdf", ["classification"] = "Correspondence", ["status"] = "kept-not-processed", ["reason"] = "not a loss run" } },
    };

    [Fact]
    public void Columns_are_policy_periods_newest_left_with_values_markers_and_excluded_columns()
    {
        var grid = LossInformation.Build(Quote, Payload(2), null, [Manifest("up-1", "lossrun-2023.pdf")]);

        Assert.Equal(["SYN-WC-1002", "SYN-WC-1001", "doc-c · period 1"], grid.Columns.Select(c => c.Title));
        var y2023 = grid.Columns[0];
        Assert.Equal(new LossInfoCell("2023-07-01", "value"), y2023.Cells["effectiveDate"]);
        Assert.Equal("$1,500.00", y2023.Cells["incurredExpense"].Value);
        Assert.Equal(("needs-input", "1 claim value(s) need input: validation rule R-10 still fails"), (y2023.Cells["incurredLoss"].State, y2023.Cells["incurredLoss"].Reason));
        Assert.Equal(("2", "1", "1"), (y2023.Cells["claims"].Value, y2023.Cells["indemnityClaims"].Value, y2023.Cells["medicalOnlyClaims"].Value)); // report-only apart
        Assert.Equal(1, y2023.ReportOnlyClaims);
        Assert.All(new[] { "totalPremium", "totalPayroll", "exMod" }, k => Assert.Equal("not-from-loss-run", y2023.Cells[k].State));
        Assert.Equal(("2024-09-30", "submission"), (grid.Columns[1].Cells["evaluationDate"].Value, grid.Columns[1].Cells["evaluationDate"].Source));
        Assert.Equal("$650.00", grid.Columns[1].Cells["incurredLoss"].Value);

        var rejected = grid.Columns[2];
        Assert.Equal("rejected", rejected.ExcludedReason);
        Assert.All(rejected.Cells.Where(c => c.Key is not ("totalPremium" or "totalPayroll" or "exMod")), c => Assert.Equal("excluded", c.Value.State));
        Assert.Equal(["lossrun-2023.pdf", "broker-letter.pdf"], grid.Attachments.Select(a => a.FileName));
        Assert.Equal("not a loss run", grid.Attachments[1].Reason);
        Assert.Equal(13, grid.Rows.Count);
    }

    [Fact]
    public void Without_every_effective_date_the_columns_keep_the_documents_order_and_say_which_date_needs_input()
    {
        var sorted = LossInformation.Build(Quote, Payload(2), null, []);
        Assert.True(sorted.OrderedByEffectiveDate);
        Assert.DoesNotContain(sorted.Columns, c => c.EffectiveDateNeedsInput);

        // Brief 25 Answers 1: an unresolved effective date (R-33) — source order, never a guessed one.
        var payload = Payload(2);
        var periods = payload["policyPeriods"]!.AsArray();
        var source = periods.Select(p => $"{p!["documentId"]}/{p["periodIndex"]}").ToList();
        periods.First(p => p!["excludedReason"] is null)!["effectiveDate"] = null;
        var unsorted = LossInformation.Build(Quote, payload, null, []);
        Assert.False(unsorted.OrderedByEffectiveDate);
        Assert.Equal(source, unsorted.Columns.Select(c => c.Key));
        var undated = periods.First(p => p!["excludedReason"] is null)!;
        Assert.Equal([$"{undated["documentId"]}/{undated["periodIndex"]}"], unsorted.Columns.Where(c => c.EffectiveDateNeedsInput).Select(c => c.Key));
    }

    [Fact]
    public void A_document_waiting_for_review_needs_input_in_every_loss_cell()
    {
        var pending = LossInformation.Build(Quote, Payload(1), null, []).Columns.Single(c => c.ExcludedReason == "pending-review");

        Assert.Equal(("needs-input", "the document is waiting for review"), (pending.Cells["incurredLoss"].State, pending.Cells["incurredLoss"].Reason));
        Assert.Equal("needs-input", pending.Cells["effectiveDate"].State);
    }

    [Fact]
    public async Task A_lower_revision_never_replaces_a_higher_one()
    {
        var service = new QuoteSystemService(new InMemoryQuoteSystemStorage(), TimeProvider.System);

        Assert.True(await service.AcceptPayloadAsync(Quote, Payload(2), []));
        Assert.False(await service.AcceptPayloadAsync(Quote, Payload(1), []));
        Assert.Equal(2, (await service.GridAsync(Quote))!.QuoteRevision);
        Assert.True(await service.AcceptPayloadAsync(Quote, Payload(2), [])); // the same revision again is harmless
    }

    [Fact]
    public async Task Quote_revised_fetches_the_payload_at_its_revision_and_the_manifests()
    {
        var api = new FakeApi { Payloads = { [1] = Payload(1), [2] = Payload(2) }, Manifests = { ["up-1"] = Manifest("up-1", "lossrun-2023.pdf") } };
        var service = new QuoteSystemService(new InMemoryQuoteSystemStorage(), TimeProvider.System, api);

        await service.ReceiveEventAsync(Event("ev-1", 1));
        Assert.Equal(1, (await service.GridAsync(Quote))!.QuoteRevision);
        api.Current = 2;
        await service.ReceiveEventAsync(Event("ev-2", 2));
        var grid = (await service.GridAsync(Quote))!;

        Assert.Equal(2, grid.QuoteRevision);
        Assert.Equal(("lossrun.quote.revised", 2), (grid.Integration.LastEventType, grid.Integration.LastEventRevision));
        Assert.Contains("quotes/Q-LI-1/quote-payload?revision=2", api.Calls);
        Assert.Contains(grid.Attachments, a => a.FileName == "lossrun-2023.pdf");

        // A late revision-1 event: the fetch at revision 1 is superseded (409), the latest is fetched, nothing goes backwards.
        await service.ReceiveEventAsync(Event("ev-1b", 1));
        Assert.Equal(2, (await service.GridAsync(Quote))!.QuoteRevision);
    }

    private static JsonObject Event(string id, int revision) => new()
    {
        ["id"] = id, ["type"] = "lossrun.quote.revised", ["source"] = "/test",
        ["data"] = new JsonObject { ["quoteId"] = Quote, ["quoteRevision"] = revision, ["cause"] = "review-decided" },
    };

    private sealed class FakeApi : ILossRunApi
    {
        public Dictionary<int, JsonObject> Payloads { get; } = [];
        public Dictionary<string, JsonObject> Manifests { get; } = new(StringComparer.Ordinal);
        public int Current { get; set; } = 1;
        public List<string> Calls { get; } = [];

        public Task<(int Status, JsonObject? Body)> GetAsync(string path, CancellationToken cancellationToken = default)
        {
            Calls.Add(path);
            if (path.StartsWith("uploads/", StringComparison.Ordinal))
            {
                return Task.FromResult(Manifests.TryGetValue(path["uploads/".Length..], out var m) ? (200, (JsonObject?)m.DeepClone().AsObject()) : (404, null));
            }

            var asked = path.Contains("?revision=", StringComparison.Ordinal) ? int.Parse(path[(path.IndexOf('=') + 1)..], System.Globalization.CultureInfo.InvariantCulture) : Current;
            return Task.FromResult(asked < Current ? (409, (JsonObject?)null) : (200, (JsonObject?)Payloads[asked].DeepClone().AsObject()));
        }
    }

    // ------------------------------------------------------------------ the recorded replay (the demo's offline fallback)

    private static string ReplayPath([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "LossRun.QuoteSystemMock", "wwwroot", "replay.json");

    private static string Recording()
    {
        var at = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var record1 = new JsonObject { ["payloadReceivedAt"] = at.ToString("O"),
            ["lossRunAnalysis"] = new JsonObject { ["idempotencyKey"] = "ob-replay-1", ["lossrunOutboxId"] = "ob-replay-1", ["receivedAt"] = at.ToString("O"), ["attributes"] = new JsonObject { ["quoteRevision"] = 1 } },
            ["events"] = new JsonArray { new JsonObject { ["type"] = "lossrun.quote.revised", ["receivedAt"] = at.ToString("O"), ["data"] = new JsonObject { ["quoteRevision"] = 1 } } } };
        var record2 = JsonNode.Parse(record1.ToJsonString())!.AsObject();
        record2["payloadReceivedAt"] = at.AddSeconds(40).ToString("O");
        record2["lossRunAnalysis"]!["idempotencyKey"] = "ob-replay-2";
        record2["lossRunAnalysis"]!["lossrunOutboxId"] = "ob-replay-2";
        record2["lossRunAnalysis"]!["attributes"]!["quoteRevision"] = 2;
        record2["events"]!.AsArray().Add(new JsonObject { ["type"] = "lossrun.quote.revised", ["receivedAt"] = at.AddSeconds(40).ToString("O"), ["data"] = new JsonObject { ["quoteRevision"] = 2 } });
        var manifests = new List<JsonObject> { Manifest("up-1", "lossrun-2023.pdf") };
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var steps = new[]
        {
            new { delayMs = 0, grid = LossInformation.Build("Q-DEMO-REPLAY", Payload(1), record1, manifests) with { QuoteId = "Q-DEMO-REPLAY" } },
            new { delayMs = 6000, grid = LossInformation.Build("Q-DEMO-REPLAY", Payload(2), record2, manifests) with { QuoteId = "Q-DEMO-REPLAY" } },
        };
        return JsonSerializer.Serialize(steps, web).ReplaceLineEndings("\n") + "\n";
    }

    [Fact]
    public void The_committed_replay_is_the_recording_of_the_two_revisions()
    {
        // Regenerate after a grid change: set REGENERATE_REPLAY=1 and run this test once.
        if (Environment.GetEnvironmentVariable("REGENERATE_REPLAY") == "1")
        {
            File.WriteAllText(ReplayPath(), Recording());
        }

        Assert.Equal(Recording(), File.ReadAllText(ReplayPath()).ReplaceLineEndings("\n"));
    }
}
