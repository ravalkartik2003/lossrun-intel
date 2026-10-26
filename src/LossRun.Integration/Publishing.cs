using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.ServiceBus;
using LossRun.Data;

namespace LossRun.Integration;

/// <summary>Sends one outbox row to its channel. Throws on failure (the row stays pending).</summary>
public interface IOutboxPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Message properties every command carries, on the bus and on the HTTP call to the quote system.</summary>
public static class CommandHeaders
{
    public const string Kind = "kind";
    public const string QuoteId = "quoteId";

    /// <summary>Brief 24 step 3: the quote revision a command carries (also in its body), for receivers that read properties.</summary>
    public const string QuoteRevision = "quoteRevision";
    public const string IdempotencyKey = "Idempotency-Key";
    public const string Correlation = "X-LossRun-Outbox-Id";

    /// <summary>Session for commands without a quote (none today; kept so a session is always set).</summary>
    public const string NoQuoteSession = "no-quote";
}

/// <summary>
/// Commands → Service Bus queue: MessageId = outbox id (duplicate detection drops a re-publish), SessionId = quote id
/// (commands for one quote are delivered in order), Subject = kind.
/// </summary>
public sealed class ServiceBusOutboxPublisher(ServiceBusSender sender) : IOutboxPublisher
{
    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default) =>
        sender.SendMessageAsync(ToMessage(message), cancellationToken);

    public static ServiceBusMessage ToMessage(OutboxMessage message)
    {
        var m = new ServiceBusMessage(BinaryData.FromString(message.PayloadJson))
        {
            MessageId = message.Id,
            SessionId = message.QuoteId ?? CommandHeaders.NoQuoteSession,
            Subject = message.Kind,
            ContentType = "application/json",
            CorrelationId = message.Id,
        };
        m.ApplicationProperties[CommandHeaders.Kind] = message.Kind;
        if (message.QuoteId is { } q)
        {
            m.ApplicationProperties[CommandHeaders.QuoteId] = q;
        }

        if (System.Text.Json.Nodes.JsonNode.Parse(message.PayloadJson) is System.Text.Json.Nodes.JsonObject body && body["quoteRevision"] is System.Text.Json.Nodes.JsonValue v
            && v.TryGetValue<int>(out var revision))
        {
            m.ApplicationProperties[CommandHeaders.QuoteRevision] = revision;
        }

        return m;
    }
}

/// <summary>
/// Events → Event Grid topic as CloudEvents 1.0: id = outbox id, type = <c>lossrun.{kind}</c>, source = the API: configuration
/// (<c>Integration:EventSource</c>), default <see cref="DefaultSource"/> (ADR 0014 amendment).
/// </summary>
public sealed class EventGridOutboxPublisher(EventGridPublisherClient client, string source = EventGridOutboxPublisher.DefaultSource) : IOutboxPublisher
{
    public const string DefaultSource = "/lossrun/api";

    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default) =>
        client.SendEventAsync(ToCloudEvent(message, source), cancellationToken);

    public static CloudEvent ToCloudEvent(OutboxMessage message, string source) =>
        // JSON data format: subscribers get "data" as an object (the constructor's default, Binary, sends "data_base64").
        new(source, $"lossrun.{message.Kind}", BinaryData.FromString(message.PayloadJson), "application/json", CloudEventDataFormat.Json)
        {
            Id = message.Id,
            Subject = message.Subject,
            Time = message.CreatedAt,
        };
}

/// <summary>Routes by channel.</summary>
public sealed class RoutingOutboxPublisher(IOutboxPublisher commands, IOutboxPublisher events) : IOutboxPublisher
{
    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default) =>
        (message.Channel == OutboxChannels.Command ? commands : events).PublishAsync(message, cancellationToken);
}
