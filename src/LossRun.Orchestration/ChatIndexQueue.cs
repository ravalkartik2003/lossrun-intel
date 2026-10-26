using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;

namespace LossRun.Orchestration;

/// <summary>
/// Brief 12 section 1: activities hand finished uploads to the chat indexer through a queue, so a slow or failed embedding
/// call never holds up or fails extraction. The message is the upload id; the indexer is idempotent by chunk id.
/// </summary>
public interface IChatIndexQueue
{
    Task EnqueueAsync(string uploadId, CancellationToken cancellationToken = default);
}

/// <summary>Azure Storage queue (identity-based); base64 messages, the Functions queue trigger's default encoding.</summary>
public sealed class StorageChatIndexQueue(QueueClient queue, ILogger? logger = null) : IChatIndexQueue
{
    public async Task EnqueueAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        try
        {
            await queue.SendMessageAsync(uploadId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Chat indexing is downstream of extraction; never fail the upload for it ("lossrun chat reindex" catches up).
            logger?.LogWarning(e, "chat-index enqueue failed for {UploadId}", uploadId);
        }
    }

    public static QueueClient Client(Uri queueServiceUri, string queueName, Azure.Core.TokenCredential credential) =>
        new(new Uri(queueServiceUri, queueName), credential, new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
}

/// <summary>Tests and local runs: remembers what was enqueued.</summary>
public sealed class InMemoryChatIndexQueue : IChatIndexQueue
{
    public List<string> Enqueued { get; } = [];

    public Task EnqueueAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        lock (Enqueued)
        {
            Enqueued.Add(uploadId);
        }

        return Task.CompletedTask;
    }
}
