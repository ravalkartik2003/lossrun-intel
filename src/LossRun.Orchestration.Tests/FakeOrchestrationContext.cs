using LossRun.Api.Contracts;
using LossRun.Contracts;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LossRun.Orchestration.Tests;

/// <summary>
/// Durable test harness: a TaskOrchestrationContext whose activities are delegates, whose sub-orchestrations run the
/// real orchestrator code in a child context, and whose external events and timers the test controls.
/// </summary>
internal sealed class FakeOrchestrationContext(string instanceId, FakeOrchestrationContext.Harness harness) : TaskOrchestrationContext
{
    public sealed class Harness
    {
        public Dictionary<string, Func<object?, Task<object?>>> Activities { get; } = new(StringComparer.Ordinal);
        public List<(string Instance, string Activity, object? Input)> ActivityCalls { get; } = [];
        public Dictionary<string, TaskOptions?> ActivityOptions { get; } = new(StringComparer.Ordinal);

        /// <summary>Brief 33: the waits the retry loops asked for (the handler's or the policy's timer), in order.</summary>
        public List<TimeSpan> RetryTimers { get; } = [];
        public Dictionary<string, List<object>> Statuses { get; } = new(StringComparer.Ordinal);
        public List<string> SubOrchestrations { get; } = [];
        public Queue<object> Events { get; } = new();

        /// <summary>Orchestrations waiting for an event; <see cref="Raise"/> completes the oldest or queues the event.</summary>
        public Queue<Action<object>> Waiters { get; } = new();

        public void Raise(object payload)
        {
            lock (Events)
            {
                if (Waiters.TryDequeue(out var waiter))
                {
                    waiter(payload);
                }
                else
                {
                    Events.Enqueue(payload);
                }
            }
        }

        /// <summary>True: timers fire as soon as they are created (the review deadline passes).</summary>
        public bool FireTimers { get; set; }

        public DateTime Now { get; set; } = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>When set, timers fire after this real delay (the learn-slot retry loop needs time to pass).</summary>
        public TimeSpan? TimerDelay { get; set; }

        /// <summary>The <see cref="Orchestration.LearnGate"/> entity's state; operations run under its lock.</summary>
        public LearnGateState LearnGate { get; } = new();

        public int LearnSlots { get; set; } = Orchestration.LearnGate.DefaultSlots;

        public List<string> EntityOperations { get; } = [];

        public void Activity<TIn, TOut>(string name, Func<TIn, TOut> body) =>
            Activities[name] = input => Task.FromResult<object?>(body((TIn)input!));

        public void ActivityAsync<TIn, TOut>(string name, Func<TIn, Task<TOut>> body) =>
            Activities[name] = async input => await body((TIn)input!);

        public IEnumerable<string> Steps(string instance) => Statuses.GetValueOrDefault(instance, []).OfType<UploadProgress>().Select(p => p.Step);
    }

    public override TaskName Name => "fake";
    public override string InstanceId => instanceId;
    public override ParentOrchestrationInstance? Parent => null;
    public override DateTime CurrentUtcDateTime => harness.Now;
    public override bool IsReplaying => false;
    public override TaskOrchestrationEntityFeature Entities => new FakeEntities(harness);
    protected override ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;

    public override T GetInput<T>() => throw new NotSupportedException();

    /// <summary>
    /// One activity call, retried as the Durable worker does (brief 33): with a retry policy, up to <c>MaxNumberOfAttempts</c> while
    /// <c>HandleFailure</c> allows it; with a retry handler, while the handler returns true for a <see cref="RetryContext"/> of the last attempt.
    /// An exception the activity throws reaches the orchestrator as a <see cref="TaskFailedException"/> whose details are built the way the
    /// worker's conversion builds them (type full name, message, the app's exception properties, the inner chain; tools/capture-failures).
    /// </summary>
    public override async Task<T> CallActivityAsync<T>(TaskName name, object? input = null, TaskOptions? options = null)
    {
        var started = harness.Now;
        for (var attempt = 1; ; attempt++)
        {
            lock (harness.ActivityCalls)
            {
                harness.ActivityCalls.Add((instanceId, name.Name, input));
                harness.ActivityOptions[name.Name] = options;
                harness.Now = harness.Now.AddMilliseconds(100);
            }

            try
            {
                var result = await harness.Activities[name.Name](input);
                return (T)result!;
            }
            catch (Exception e) when (options?.Retry is not null)
            {
                var failed = e as TaskFailedException ?? new TaskFailedException(name.Name, attempt, Details(e));
                var retry = options.Retry!;
                if (retry.Policy is { } policy)
                {
                    if (attempt >= policy.MaxNumberOfAttempts || policy.Handle?.Invoke(failed) == false)
                    {
                        throw failed;
                    }

                    var wait = TimeSpan.FromTicks((long)(policy.FirstRetryInterval.Ticks * Math.Pow(policy.BackoffCoefficient, attempt - 1)));
                    harness.RetryTimers.Add(wait);
                    harness.Now += wait;
                    continue;
                }

                if (retry.Handler is { } handler)
                {
                    _inRetryHandler = true;
                    try
                    {
                        if (await handler(new RetryContext(this, attempt, failed.FailureDetails, harness.Now - started, CancellationToken.None)))
                        {
                            continue;
                        }
                    }
                    finally
                    {
                        _inRetryHandler = false;
                    }
                }

                throw failed;
            }
        }
    }

    /// <summary>Failure details as the worker's conversion produces them (captured: type, message, provider properties, inner chain).</summary>
    public static TaskFailureDetails Details(Exception e) => new(e.GetType().FullName!, e.Message, e.StackTrace, e.InnerException is { } inner ? Details(inner) : null,
        ExceptionProperties.Of(e) is { } p ? new Dictionary<string, object?>(p) : null);

    private bool _inRetryHandler;

    /// <summary>A retry handler's wait is recorded and passed at once (the clock moves), so a test reads the delays without waiting for them.</summary>
    public override Task CreateTimer(DateTime fireAt, CancellationToken cancellationToken)
    {
        if (_inRetryHandler)
        {
            harness.RetryTimers.Add(fireAt - harness.Now);
            harness.Now = fireAt;
            return Task.CompletedTask;
        }

        return OtherTimer(cancellationToken);
    }

    private Task OtherTimer(CancellationToken cancellationToken) =>
        harness.TimerDelay is { } delay ? Task.Delay(delay, cancellationToken)
        : harness.FireTimers ? Task.CompletedTask : Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);

    /// <summary>The <see cref="Orchestration.LearnGate"/> entity, in process.</summary>
    private sealed class FakeEntities(Harness harness) : TaskOrchestrationEntityFeature
    {
        public override Task<TResult> CallEntityAsync<TResult>(EntityInstanceId id, string operationName, object? input = null, CallEntityOptions? options = null) =>
            Task.FromResult((TResult)Run(id, operationName, input)!);

        public override Task CallEntityAsync(EntityInstanceId id, string operationName, object? input = null, CallEntityOptions? options = null)
        {
            Run(id, operationName, input);
            return Task.CompletedTask;
        }

        public override Task SignalEntityAsync(EntityInstanceId id, string operationName, object? input = null, SignalEntityOptions? options = null)
        {
            Run(id, operationName, input);
            return Task.CompletedTask;
        }

        public override Task<IAsyncDisposable> LockEntitiesAsync(IEnumerable<EntityInstanceId> entityIds) => throw new NotSupportedException();

        public override bool InCriticalSection([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IReadOnlyList<EntityInstanceId>? entityIds)
        {
            entityIds = null;
            return false;
        }

        private object? Run(EntityInstanceId id, string operation, object? input)
        {
            Assert.Equal(Orchestration.LearnGate.Id, id);
            lock (harness.LearnGate)
            {
                harness.EntityOperations.Add(operation);
                return operation switch
                {
                    nameof(Orchestration.LearnGate.TryAcquire) => Orchestration.LearnGate.Acquire(harness.LearnGate, (LearnSlotRequest)input!, harness.LearnSlots),
                    nameof(Orchestration.LearnGate.Release) => harness.LearnGate.Holders.Remove((string)input!),
                    _ => throw new NotSupportedException(operation),
                };
            }
        }
    }

    public override Task<T> WaitForExternalEvent<T>(string eventName, CancellationToken cancellationToken = default)
    {
        lock (harness.Events)
        {
            if (harness.Events.TryDequeue(out var e))
            {
                return Task.FromResult((T)e);
            }

            var pending = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = true;
            harness.Waiters.Enqueue(payload =>
            {
                if (active)
                {
                    pending.TrySetResult((T)payload);
                }
                else
                {
                    harness.Events.Enqueue(payload); // the waiter was cancelled (timer won): keep the event for the next wait
                }
            });
            cancellationToken.Register(() =>
            {
                active = false;
                pending.TrySetCanceled();
            });
            return pending.Task;
        }
    }

    public override void SendEvent(string instanceId, string eventName, object payload) => throw new NotSupportedException();

    public override void SetCustomStatus(object? customStatus)
    {
        lock (harness.Statuses)
        {
            if (!harness.Statuses.TryGetValue(instanceId, out var list))
            {
                harness.Statuses[instanceId] = list = [];
            }

            list.Add(customStatus!);
        }
    }

    public override async Task<TResult> CallSubOrchestratorAsync<TResult>(TaskName orchestratorName, object? input = null, TaskOptions? options = null)
    {
        var child = (options as SubOrchestrationOptions)?.InstanceId ?? Guid.NewGuid().ToString("N");
        harness.SubOrchestrations.Add(child);
        var context = new FakeOrchestrationContext(child, harness);
        object result = orchestratorName.Name switch
        {
            OrchestratorNames.ProcessLossRun => await LossRunOrchestration.RunAsync(context, (DocumentWork)input!),
            _ => throw new NotSupportedException(orchestratorName.Name),
        };
        return (TResult)result;
    }

    public override void ContinueAsNew(object? newInput = null, bool preserveUnprocessedEvents = true) => throw new NotSupportedException();

    public override Guid NewGuid() => Guid.NewGuid();
}

internal static class Fixture
{
    public static string File(params string[] parts) => Path.Combine([AppContext.BaseDirectory, .. parts]);

    public static byte[] Bytes(string name) => System.IO.File.ReadAllBytes(File("fixtures", name));

    public static string Recorded(string name) => System.IO.File.ReadAllText(File("Recorded", name));
}
