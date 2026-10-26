namespace LossRun.Orchestration.Tests;

/// <summary>Brief 08c §4: at most 3 learn/delta activities at once across documents (08b Answers 4).</summary>
public class LearnGateTests
{
    private static FakeOrchestrationContext.Harness Harness(string tier, Func<LearnInput, Task<LearnResult>> learn)
    {
        var harness = new FakeOrchestrationContext.Harness { TimerDelay = TimeSpan.FromMilliseconds(5) };
        harness.Activity<DocumentWork, RouteResult>(ActivityNames.Route, w => new RouteResult(w.DocumentId, tier, null, false, [], ["fingerprint miss"], "new format → learn path", false, 0.01m));
        harness.ActivityAsync(ActivityNames.Learn, learn);
        harness.ActivityAsync(ActivityNames.Delta, learn);
        harness.Activity<PersistInput, DocumentOutcome>(ActivityNames.ValidateAndPersist, p => new DocumentOutcome(p.Work.DocumentId, p.Learn?.Published == true ? "Learned" : "Review", null, 3, 0.05m));
        return harness;
    }

    private static Task<DocumentOutcome[]> RunAll(FakeOrchestrationContext.Harness harness, int documents) =>
        Task.WhenAll(Enumerable.Range(0, documents).Select(i => Task.Run(() =>
            LossRunOrchestration.RunAsync(new FakeOrchestrationContext($"up~doc-{i}", harness), new DocumentWork("up", $"doc-{i}", $"f{i}.pdf", true)))));

    [Theory]
    [InlineData("Learn")]
    [InlineData("ReplayWithDelta")]
    public async Task A_burst_of_new_formats_runs_at_most_three_model_activities_at_once(string tier)
    {
        int running = 0, peak = 0;
        var harness = Harness(tier, async input =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(40);
            Interlocked.Decrement(ref running);
            return new LearnResult(input.Work.DocumentId, true, "carrier@1", "shadow", [], 2, 0.04m);
        });

        var outcomes = await RunAll(harness, 8);

        Assert.All(outcomes, o => Assert.Equal("Learned", o.FinalTier));
        Assert.Equal(3, peak);
        Assert.Empty(harness.LearnGate.Holders); // every slot released
        Assert.Equal(8, harness.LearnGate.Granted);
        Assert.True(harness.LearnGate.Denied > 0);
        Assert.Contains(harness.Statuses.Values.SelectMany(s => s).OfType<Api.Contracts.UploadProgress>(), p => p.Step.EndsWith("-queued", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_learn_releases_its_slot()
    {
        var harness = Harness("Learn", _ => Task.FromException<LearnResult>(new InvalidOperationException("model 500")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAll(harness, 1));

        Assert.Empty(harness.LearnGate.Holders);
        Assert.Contains(nameof(LearnGate.Release), harness.EntityOperations);
    }

    [Fact]
    public async Task Replay_hits_never_touch_the_gate()
    {
        var harness = Harness("ReplayAccepted", _ => throw new InvalidOperationException("no model call on replay"));

        await RunAll(harness, 4);

        Assert.Empty(harness.EntityOperations);
    }

    [Fact]
    public void A_slot_held_past_its_lease_is_reclaimed()
    {
        var state = new LearnGateState();
        var t0 = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(LearnGate.Acquire(state, new LearnSlotRequest($"crashed-{i}", t0), 3));
        }

        Assert.False(LearnGate.Acquire(state, new LearnSlotRequest("next", t0.AddMinutes(5)), 3));
        Assert.True(LearnGate.Acquire(state, new LearnSlotRequest("next", t0 + LearnGate.Lease + TimeSpan.FromSeconds(1)), 3));
        Assert.Equal(3, state.Expired);
        Assert.True(LearnGate.Acquire(state, new LearnSlotRequest("next", t0.AddMinutes(16)), 3)); // re-asking while holding is idempotent
        Assert.Single(state.Holders);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    [Fact]
    public void Item30_33_the_lease_outlasts_the_function_timeout_and_is_configuration()
    {
        var host = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(FindUp("src/LossRun.Functions/host.json"))))!;
        var timeout = TimeSpan.Parse(host["functionTimeout"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(LearnGate.Lease > timeout, $"lease {LearnGate.Lease} must outlast functionTimeout {timeout}"); // before brief 30: 15 min < 30 min

        Environment.SetEnvironmentVariable("Learn__LeaseMinutes", "50");
        try
        {
            Assert.Equal(TimeSpan.FromMinutes(50), LearnGate.Lease);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Learn__LeaseMinutes", null);
        }
    }

    private static string FindUp(string relative)
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, relative)))
            {
                return Path.Combine(dir, relative);
            }
        }

        throw new FileNotFoundException(relative);
    }
}
