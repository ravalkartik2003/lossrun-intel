using Azure.Messaging.ServiceBus;
using LossRun.Data;
using LossRun.Integration;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LossRun.Functions;

/// <summary>The integration pieces, built once per host (null members when Service Bus / the quote system are not configured).</summary>
public sealed record IntegrationRuntime(IOutboxRelay? Relay, CommandDispatcher? Dispatcher);

/// <summary>
/// Brief 12b: the outbox sweep (publishes anything the commands' fast path missed: a crash, a Service Bus or Event Grid
/// hiccup) and the dispatcher that delivers commands to the quote system, one session (quote) at a time, in order.
/// </summary>
public sealed class IntegrationFunctions(IntegrationRuntime integration, ILogger<IntegrationFunctions> logger)
{
    /// <summary>Rows older than this are the sweep's; younger ones are still the fast path's (a race only costs a duplicate publish, which is dropped).</summary>
    public static readonly TimeSpan SweepMinAge = TimeSpan.FromSeconds(20);

    [Function("OutboxSweep")]
    public async Task OutboxSweep([TimerTrigger("*/30 * * * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        if (integration.Relay is null)
        {
            return;
        }

        var published = await integration.Relay.SweepAsync(SweepMinAge, cancellationToken);
        if (published > 0)
        {
            logger.LogInformation("outbox sweep published {Count} message(s)", published);
        }
    }

    [Function("QuoteCommandDispatch")]
    public async Task QuoteCommandDispatch(
        [ServiceBusTrigger("%Integration:CommandQueue%", Connection = "ServiceBus", IsSessionsEnabled = true, AutoCompleteMessages = false)] ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions, CancellationToken cancellationToken)
    {
        if (integration.Dispatcher is null)
        {
            throw new InvalidOperationException("QuoteSystem:BaseUrl is not configured; the message stays on the queue.");
        }

        var kind = message.ApplicationProperties.TryGetValue(CommandHeaders.Kind, out var k) ? k as string : message.Subject;
        var quoteId = message.ApplicationProperties.TryGetValue(CommandHeaders.QuoteId, out var q) ? q as string : null;
        var (result, detail) = await integration.Dispatcher.DispatchAsync(message.MessageId, kind ?? "", quoteId, message.Body.ToString(), cancellationToken);
        if (result == DispatchResult.Rejected)
        {
            logger.LogWarning("quote command {Id} ({Kind}) rejected: {Detail}", message.MessageId, kind, detail);
            await actions.DeadLetterMessageAsync(message, deadLetterReason: "rejected-by-quote-system", deadLetterErrorDescription: detail, cancellationToken: cancellationToken);
            return;
        }

        logger.LogInformation("quote command {Id} ({Kind}) {Result}: {Detail}", message.MessageId, kind, result, detail);
        await actions.CompleteMessageAsync(message, cancellationToken);
    }
}
