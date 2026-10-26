using System.Text.Json.Nodes;
using LossRun.Api.Contracts;
using LossRun.Data;
using LossRun.Orchestration;

namespace LossRun.Integration.Tests;

/// <summary>
/// Brief 12b acceptance: a crash anywhere between the command's commit and the quote system's write never loses a
/// command and never applies it twice. The bus below behaves like the Service Bus queue (duplicate detection on
/// MessageId within the window, sessions by quote id, dead-letter on rejection); the quote system is the same
/// <see cref="QuoteSystemService"/> the deployed mock runs.
/// </summary>
public class OutboxTests
{
    private const string Quote = "Q-OUTBOX-1";

    [Fact]
    public async Task A_crash_after_commit_and_before_publish_is_published_by_the_sweep()
    {
        var rig = new Rig();
        var id = await rig.CommandAsync(OutboxKinds.Flags, new { flags = new[] { new { claimId = "d/0/0", flags = new[] { "large-loss" } } } });
        // The process "crashed" before the fast path ran: nothing published.
        Assert.Empty(rig.Bus.Sent);

        Assert.Equal(1, await rig.Relay.SweepAsync(TimeSpan.Zero));
        await rig.DrainAsync();

        Assert.Equal(1, rig.QuoteSystemCount("flags", id));
        Assert.Equal("delivered", (await rig.Records.GetOutboxAsync(id))!.DeliveryStatus);
    }

    [Fact]
    public async Task A_crash_after_publish_and_before_marking_republishes_the_same_id_and_nothing_is_applied_twice()
    {
        var rig = new Rig();
        rig.Bus.FailAfterSend = 1; // the send succeeds, then the relay "dies" before it marks the row
        var id = await rig.CommandAsync(OutboxKinds.Annotation, new { text = "Large loss in 2023 needs a reserve check." });
        await rig.Relay.RelayAsync([id]);
        Assert.Null((await rig.Records.GetOutboxAsync(id))!.PublishedAt); // still pending

        Assert.Equal(1, await rig.Relay.SweepAsync(TimeSpan.Zero)); // re-published with the same MessageId
        await rig.DrainAsync();

        Assert.Equal(2, rig.Bus.SendAttempts);
        Assert.Equal(1, rig.Bus.DroppedAsDuplicate); // Service Bus duplicate detection
        Assert.Equal(1, rig.QuoteSystemCount("annotations", id));
        Assert.Equal(0, await rig.Relay.SweepAsync(TimeSpan.Zero)); // published once marked
    }

    [Fact]
    public async Task Without_duplicate_detection_the_receivers_idempotency_key_still_applies_it_once()
    {
        var rig = new Rig(duplicateDetection: false);
        rig.Bus.FailAfterSend = 1;
        var id = await rig.CommandAsync(OutboxKinds.FollowUp, new { assignee = "uw-2", action = "Request the 2022 loss run" });
        await rig.Relay.RelayAsync([id]);
        await rig.Relay.SweepAsync(TimeSpan.Zero);
        await rig.DrainAsync();

        Assert.Equal(2, rig.Dispatched.Count); // delivered twice to the dispatcher …
        Assert.Equal([DispatchResult.Delivered, DispatchResult.Replayed], rig.Dispatched.Select(d => d.Result));
        Assert.Equal(1, rig.QuoteSystemCount("followups", id)); // … applied once
    }

    [Fact]
    public async Task Publish_failures_keep_the_row_pending_until_a_sweep_succeeds()
    {
        var rig = new Rig();
        rig.Bus.FailBeforeSend = 3;
        var id = await rig.CommandAsync(OutboxKinds.Flags, new { flags = Array.Empty<object>() });
        await rig.Relay.RelayAsync([id]); // fast path fails: the command still succeeded for the caller
        await rig.Relay.SweepAsync(TimeSpan.Zero);
        await rig.Relay.SweepAsync(TimeSpan.Zero);
        Assert.Null((await rig.Records.GetOutboxAsync(id))!.PublishedAt);

        await rig.Relay.SweepAsync(TimeSpan.Zero);
        await rig.DrainAsync();

        var row = (await rig.Records.GetOutboxAsync(id))!;
        Assert.NotNull(row.PublishedAt);
        Assert.Equal(4, row.PublishAttempts);
        Assert.Equal(1, rig.QuoteSystemCount("flags", id));
    }

    [Fact]
    public async Task Commands_for_one_quote_arrive_in_order_and_a_rejected_one_is_dead_lettered()
    {
        var rig = new Rig();
        var a = await rig.CommandAsync(OutboxKinds.Annotation, new { text = "first" });
        var b = await rig.CommandAsync(OutboxKinds.Annotation, new { text = "second" });
        var bad = OutboxMessage.Command(OutboxKinds.Annotation, Quote, "not an object", "uw-1", rig.Clock.GetUtcNow()); // 400 at the receiver
        await rig.Records.AuditWithOutboxAsync(Audit(rig, "quote.annotation"), Actor.System, [bad]);
        await rig.Relay.SweepAsync(TimeSpan.Zero);
        await rig.DrainAsync();

        var texts = rig.QuoteRecord()!["annotations"]!.AsArray().Select(n => n!["attributes"]!["text"]!.GetValue<string>()).ToList();
        Assert.Equal(["first", "second"], texts);
        Assert.Equal([bad.Id], rig.Bus.DeadLetters);
        Assert.Equal("rejected", (await rig.Records.GetOutboxAsync(bad.Id))!.DeliveryStatus);
        Assert.All(new[] { a, b }, id => Assert.Equal(1, rig.QuoteSystemCount("annotations", id)));
    }

    [Fact]
    public async Task An_older_quote_revision_delivered_after_a_newer_one_is_superseded_and_a_duplicate_is_replayed()
    {
        // Brief 24 step 3 (ADR 0024): revision 6's fast path failed, so the sweep publishes it after 7 (session order = publish
        // order). The receiver keeps 7; 6 is recorded as superseded (completed, not dead-lettered); a re-publish of 7 is a replay.
        var rig = new Rig(duplicateDetection: false);
        var six = await rig.CommandAsync(OutboxKinds.QuotePayload, new { quoteId = Quote, quoteRevision = 6, status = "completed" });
        var seven = await rig.CommandAsync(OutboxKinds.QuotePayload, new { quoteId = Quote, quoteRevision = 7, status = "completed" });
        await rig.Relay.RelayAsync([seven]);
        await rig.Relay.SweepAsync(TimeSpan.Zero); // publishes 6, now behind 7 in the quote's session
        rig.Bus.FailAfterSend = 0;
        await rig.Bus.PublishAsync((await rig.Records.GetOutboxAsync(seven))!); // a re-publish of 7 (no duplicate detection)
        await rig.DrainAsync();

        Assert.Equal([seven, six, seven], rig.Dispatched.Select(d => d.Id));
        Assert.Equal([DispatchResult.Delivered, DispatchResult.Superseded, DispatchResult.Replayed], rig.Dispatched.Select(d => d.Result));
        Assert.Equal(7, rig.QuoteRecord()!["lossRunAnalysis"]!["attributes"]!["quoteRevision"]!.GetValue<int>());
        Assert.Equal("superseded", (await rig.Records.GetOutboxAsync(six))!.DeliveryStatus);
        Assert.Empty(rig.Bus.DeadLetters);
    }

    [Fact]
    public async Task The_receiver_refuses_a_lower_revision_and_accepts_the_same_or_a_higher_one()
    {
        var service = new QuoteSystemService(new InMemoryQuoteSystemStorage(), TimeProvider.System);
        async Task<int> Put(string key, int revision) =>
            (await service.WriteAsync(Quote, "payload", key, key, new JsonObject { ["quoteRevision"] = revision })).Status;

        Assert.Equal(200, await Put("k-3", 3));
        Assert.Equal(409, await Put("k-2", 2));
        Assert.Equal(200, await Put("k-3b", 3));
        Assert.Equal(200, await Put("k-4", 4));
        Assert.Equal(4, (await service.GetQuoteAsync(Quote))!["data"]!["attributes"]!["lossRunAnalysis"]!["attributes"]!["quoteRevision"]!.GetValue<int>());
    }

    [Fact]
    public void A_command_carries_its_quote_revision_as_a_message_property()
    {
        var message = OutboxMessage.Command(OutboxKinds.QuotePayload, Quote, new { quoteId = Quote, quoteRevision = 7 }, "system", DateTimeOffset.UtcNow);

        Assert.Equal(7, ServiceBusOutboxPublisher.ToMessage(message).ApplicationProperties[CommandHeaders.QuoteRevision]);
    }

    [Fact]
    public async Task A_cloud_event_delivered_twice_is_recorded_once()
    {
        var service = new QuoteSystemService(new InMemoryQuoteSystemStorage(), TimeProvider.System);
        var e = new JsonObject { ["id"] = "ob-1", ["type"] = "lossrun.upload.completed", ["source"] = EventGridOutboxPublisher.DefaultSource, ["data"] = new JsonObject { ["quoteId"] = Quote } };

        Assert.True(await service.ReceiveEventAsync((JsonObject)e.DeepClone()));
        Assert.False(await service.ReceiveEventAsync((JsonObject)e.DeepClone()));
        var recorded = Assert.Single((await service.GetQuoteAsync(Quote))!["data"]!["attributes"]!["events"]!.AsArray());
        Assert.Equal(EventGridOutboxPublisher.DefaultSource, recorded!["source"]!.GetValue<string>()); // the mock keeps who sent it
    }

    [Fact]
    public void Events_are_published_with_json_data_so_subscribers_can_read_the_quote_id()
    {
        var message = OutboxMessage.Event(OutboxKinds.UploadCompleted, Quote, "uploads/up-1", new { quoteId = Quote, uploadId = "up-1" }, "system", DateTimeOffset.UtcNow);
        var json = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(EventGridOutboxPublisher.ToCloudEvent(message, "/org/lossrun/api")))!; // what Event Grid delivers (structured mode)

        Assert.Equal(Quote, json["data"]!["quoteId"]!.GetValue<string>());
        Assert.Null(json["data_base64"]);
        Assert.Equal((message.Id, "lossrun.upload.completed", "/org/lossrun/api"), (json["id"]!.GetValue<string>(), json["type"]!.GetValue<string>(), json["source"]!.GetValue<string>())); // the configured source
    }

    private static AuditEvent Audit(Rig rig, string action) => new(rig.Clock.GetUtcNow(), "uw-1", Roles.Underwriter, action, "", null, $"quotes/{Quote}");

    /// <summary>Record store + relay + a Service-Bus-like bus + the dispatcher + the quote system, in process.</summary>
    private sealed class Rig
    {
        public Rig(bool duplicateDetection = true)
        {
            Records = new BlobRecordStore(new InMemoryObjectStore());
            Bus = new FakeBus(duplicateDetection);
            Relay = new OutboxRelay(Records, Bus, Clock);
            QuoteSystem = new QuoteSystemService(new InMemoryQuoteSystemStorage(), Clock);
            Dispatcher = new CommandDispatcher(new InProcessQuoteSystemEndpoint(QuoteSystem), Records, Clock);
        }

        public TimeProvider Clock { get; } = TimeProvider.System;
        public BlobRecordStore Records { get; }
        public FakeBus Bus { get; }
        public OutboxRelay Relay { get; }
        public QuoteSystemService QuoteSystem { get; }
        public CommandDispatcher Dispatcher { get; }
        public List<(string Id, DispatchResult Result)> Dispatched { get; } = [];

        /// <summary>A command as the API writes it: audit row + outbox row in one store call; no publish.</summary>
        public async Task<string> CommandAsync(string kind, object payload)
        {
            var message = OutboxMessage.Command(kind, Quote, payload, "uw-1", Clock.GetUtcNow());
            await Records.AuditWithOutboxAsync(Audit(this, kind), Actor.System, [message]);
            return message.Id;
        }

        /// <summary>The session receiver: each session's messages in send order, one session after another; 4xx dead-lettered.</summary>
        public async Task DrainAsync()
        {
            while (Bus.Sessions.Values.FirstOrDefault(q => q.Count > 0) is { } session && session.TryDequeue(out var m))
            {
                var (result, _) = await Dispatcher.DispatchAsync(m.Id, m.Kind, m.QuoteId, m.PayloadJson);
                Dispatched.Add((m.Id, result));
                if (result == DispatchResult.Rejected)
                {
                    Bus.DeadLetters.Add(m.Id);
                }
            }
        }

        public JsonObject? QuoteRecord() => QuoteSystem.GetQuoteAsync(Quote).GetAwaiter().GetResult()?["data"]?["attributes"]?.AsObject();

        public int QuoteSystemCount(string collection, string idempotencyKey) =>
            QuoteRecord()?[collection]?.AsArray().Count(n => n!["idempotencyKey"]!.GetValue<string>() == idempotencyKey) ?? 0;
    }

    private sealed class FakeBus(bool duplicateDetection) : IOutboxPublisher
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        /// <summary>Brief 24 step 3: one FIFO per session id (the quote), as Service Bus sessions deliver.</summary>
        public Dictionary<string, Queue<OutboxMessage>> Sessions { get; } = new(StringComparer.Ordinal);
        public List<OutboxMessage> Sent { get; } = [];
        public List<string> DeadLetters { get; } = [];
        public int SendAttempts { get; private set; }
        public int DroppedAsDuplicate { get; private set; }
        public int FailBeforeSend { get; set; }
        public int FailAfterSend { get; set; }

        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            SendAttempts++;
            if (FailBeforeSend-- > 0)
            {
                throw new TimeoutException("Service Bus unreachable (injected)");
            }

            if (duplicateDetection && !_seen.Add(message.Id))
            {
                DroppedAsDuplicate++; // accepted by the broker, silently dropped
            }
            else
            {
                Sent.Add(message);
                var session = message.QuoteId ?? CommandHeaders.NoQuoteSession;
                if (!Sessions.TryGetValue(session, out var queue))
                {
                    Sessions[session] = queue = new Queue<OutboxMessage>();
                }

                queue.Enqueue(message);
            }

            if (FailAfterSend-- > 0)
            {
                throw new OperationFailedException("process died after the send (injected)");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class OperationFailedException(string message) : Exception(message);
}
