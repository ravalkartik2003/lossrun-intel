using Microsoft.DurableTask;
using Microsoft.DurableTask.Entities;

namespace LossRun.Orchestration;

/// <param name="Holder">Orchestration instance id asking for a slot.</param>
/// <param name="At">The orchestration's replay-safe clock (entities have none of their own).</param>
public sealed record LearnSlotRequest(string Holder, DateTime At);

public sealed class LearnGateState
{
    /// <summary>Slot holders and when they took the slot.</summary>
    public Dictionary<string, DateTime> Holders { get; set; } = new(StringComparer.Ordinal);

    public long Granted { get; set; }
    public long Denied { get; set; }
    public long Expired { get; set; }
}

/// <summary>
/// Caps concurrent learn and delta activities across all uploads (08b Answers 4): at 100K TPM, 27 of 40 concurrent
/// learns were throttled. A Durable entity counts slots; an orchestration that gets none waits on a durable timer and
/// asks again, so a burst of new carriers queues instead of 429-storming (the model client's 429 retry stays as the
/// second line). A slot not released within <see cref="Lease"/> (a crashed worker) is reclaimed.
/// </summary>
public sealed class LearnGate : TaskEntity<LearnGateState>
{
    public const string EntityName = nameof(LearnGate);
    public const int DefaultSlots = 3;
    public static readonly EntityInstanceId Id = new(EntityName, "learn");
    /// <summary>
    /// Brief 30 item 33: a slot is reclaimed only after the longest a learn activity can run (host.json functionTimeout, 30 min) plus a
    /// margin, so a live learn never loses its slot to a second one. <c>Learn__LeaseMinutes</c> (app setting) overrides; read in the entity.
    /// </summary>
    public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(35);

    public static TimeSpan Lease => int.TryParse(Environment.GetEnvironmentVariable("Learn__LeaseMinutes"), out var m) && m > 0 ? TimeSpan.FromMinutes(m) : DefaultLease;
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(10);

    /// <summary>Slots from <c>Learn__MaxConcurrent</c> (app setting), default 3; read in the entity, never in an orchestrator.</summary>
    public static int Slots => int.TryParse(Environment.GetEnvironmentVariable("Learn__MaxConcurrent"), out var n) && n > 0 ? n : DefaultSlots;

    public bool TryAcquire(LearnSlotRequest request) => Acquire(State, request, Slots);

    public void Release(string holder) => State.Holders.Remove(holder);

    public LearnGateState Get() => State;

    public static bool Acquire(LearnGateState state, LearnSlotRequest request, int slots)
    {
        foreach (var stale in state.Holders.Where(h => request.At - h.Value > Lease).Select(h => h.Key).ToList())
        {
            state.Holders.Remove(stale);
            state.Expired++;
        }

        if (state.Holders.ContainsKey(request.Holder))
        {
            return true;
        }

        if (state.Holders.Count >= slots)
        {
            state.Denied++;
            return false;
        }

        state.Holders[request.Holder] = request.At;
        state.Granted++;
        return true;
    }

    protected override LearnGateState InitializeState(TaskEntityOperation entityOperation) => new();
}
