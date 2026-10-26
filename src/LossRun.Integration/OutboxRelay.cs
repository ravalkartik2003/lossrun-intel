using LossRun.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LossRun.Integration;

/// <summary>
/// The outbox relay (brief 12b). Publish first, mark second: a crash between the two re-publishes the same id on the
/// next sweep, which Service Bus duplicate detection (MessageId) and the quote system (Idempotency-Key) both drop. A
/// crash before the publish leaves the row pending for the sweep. So a committed command is never lost and never
/// applied twice. The fast path runs right after the command commits; the timer sweep runs every 30 s.
/// </summary>
public sealed class OutboxRelay(IRecordStore records, IOutboxPublisher publisher, TimeProvider clock, ILogger? logger = null) : IOutboxRelay
{
    private readonly ILogger _log = logger ?? NullLogger.Instance;

    public async Task RelayAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        foreach (var id in ids)
        {
            try
            {
                if (await records.GetOutboxAsync(id, cancellationToken).ConfigureAwait(false) is { PublishedAt: null } message)
                {
                    await PublishOneAsync(message, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "outbox fast path: {Id} left for the sweep", id); // never fail the command that wrote it
            }
        }
    }

    public async Task<int> SweepAsync(TimeSpan minAge, CancellationToken cancellationToken = default)
    {
        var published = 0;
        foreach (var message in await records.PendingOutboxAsync(clock.GetUtcNow() - minAge, 200, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await PublishOneAsync(message, cancellationToken).ConfigureAwait(false);
                published++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "outbox sweep: {Id} ({Kind}) still pending", message.Id, message.Kind);
            }
        }

        return published;
    }

    private async Task PublishOneAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await publisher.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await records.MarkOutboxFailedAsync(message.Id, $"{e.GetType().Name}: {e.Message}", cancellationToken).ConfigureAwait(false);
            throw;
        }

        await records.MarkOutboxPublishedAsync(message.Id, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }
}
