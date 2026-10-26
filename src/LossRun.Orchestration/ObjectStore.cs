using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using LossRun.Contracts;

namespace LossRun.Orchestration;

/// <summary>
/// Blob persistence for brief 07 (SQL arrives in brief 09, which backfills from this layout). Two containers:
/// <c>uploads</c> (original files, version-level immutability) and <c>extractions</c> (everything derived). Layout:
/// <code>
/// uploads/{uploadId}/{fileName}                                  original bytes
/// extractions/uploads/{uploadId}/manifest.json                   UploadManifest (ETag-guarded updates)
/// extractions/uploads/{uploadId}/usage.jsonl                     every UsageEvent of the upload (append blob)
/// extractions/uploads/{uploadId}/documents/{documentId}/graph.json | replay.json | result.json
/// extractions/reviews/{documentId}.json                          ReviewItem
/// extractions/index/documents/{documentId}.json                  { uploadId }
/// extractions/index/keys/{idempotencyKeyHash}.json               { uploadId }
/// extractions/audit/{yyyy-MM-dd}.jsonl                           AuditEvent (append blob)
/// extractions/webhooks/received/{timestamp}-{n}.json             test sink
/// </code>
/// </summary>
public interface IObjectStore
{
    Task PutAsync(string container, string path, BinaryData data, bool overwrite = true, CancellationToken cancellationToken = default);

    Task<BinaryData?> GetAsync(string container, string path, CancellationToken cancellationToken = default);

    /// <summary>Read-modify-write guarded by an ETag; <paramref name="update"/> may run more than once.</summary>
    Task<T> UpdateAsync<T>(string container, string path, Func<T?, T> update, CancellationToken cancellationToken = default) where T : class;

    Task AppendLineAsync(string container, string path, string line, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListAsync(string container, string prefix, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every blob under <paramref name="prefix"/>, previous versions included. Returns the names deleted and the
    /// names an immutability policy still retains (left in place, reported, never an error).
    /// </summary>
    Task<(IReadOnlyList<string> Deleted, IReadOnlyList<string> Retained)> DeletePrefixAsync(string container, string prefix, CancellationToken cancellationToken = default);
}

public static class Containers
{
    public const string Uploads = "uploads";
    public const string Extractions = "extractions";
}

public static class StoreJson
{
    public static BinaryData Serialize<T>(T value) => BinaryData.FromString(JsonSerializer.Serialize(value, CanonicalJson.Options));

    public static T? Deserialize<T>(BinaryData? data) => data is null ? default : JsonSerializer.Deserialize<T>(data.ToString(), CanonicalJson.Options);

    public static async Task<T?> GetJsonAsync<T>(this IObjectStore store, string container, string path, CancellationToken cancellationToken = default) =>
        Deserialize<T>(await store.GetAsync(container, path, cancellationToken).ConfigureAwait(false));

    public static Task PutJsonAsync<T>(this IObjectStore store, string container, string path, T value, CancellationToken cancellationToken = default) =>
        store.PutAsync(container, path, Serialize(value), true, cancellationToken);
}

public sealed class BlobObjectStore(BlobServiceClient service) : IObjectStore
{
    private static readonly BlobHttpHeaders Json = new() { ContentType = "application/json" };
    private readonly ConcurrentDictionary<string, BlobContainerClient> _containers = new();

    private BlobContainerClient Container(string name) => _containers.GetOrAdd(name, service.GetBlobContainerClient);

    public async Task PutAsync(string container, string path, BinaryData data, bool overwrite = true, CancellationToken cancellationToken = default)
    {
        var options = new BlobUploadOptions { HttpHeaders = path.EndsWith(".json", StringComparison.Ordinal) ? Json : null };
        if (!overwrite)
        {
            options.Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };
        }

        try
        {
            await Container(container).GetBlobClient(path).UploadAsync(data, options, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException e) when (!overwrite && e.Status is 409 or 412)
        {
            // already there: put-if-absent is idempotent
        }
    }

    public async Task<BinaryData?> GetAsync(string container, string path, CancellationToken cancellationToken = default)
    {
        try
        {
            return (await Container(container).GetBlobClient(path).DownloadContentAsync(cancellationToken).ConfigureAwait(false)).Value.Content;
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
    }

    public async Task<T> UpdateAsync<T>(string container, string path, Func<T?, T> update, CancellationToken cancellationToken = default) where T : class
    {
        var blob = Container(container).GetBlobClient(path);
        for (var attempt = 0; ; attempt++)
        {
            T? current = null;
            BlobRequestConditions conditions;
            try
            {
                var download = await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
                current = StoreJson.Deserialize<T>(download.Value.Content);
                conditions = new BlobRequestConditions { IfMatch = download.Value.Details.ETag };
            }
            catch (RequestFailedException e) when (e.Status == 404)
            {
                conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };
            }

            var next = update(current);
            try
            {
                await blob.UploadAsync(StoreJson.Serialize(next), new BlobUploadOptions { Conditions = conditions, HttpHeaders = Json }, cancellationToken).ConfigureAwait(false);
                return next;
            }
            catch (RequestFailedException e) when (e.Status is 409 or 412 && attempt < 20)
            {
                await Task.Delay(Random.Shared.Next(20, 120), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task AppendLineAsync(string container, string path, string line, CancellationToken cancellationToken = default)
    {
        var blob = Container(container).GetAppendBlobClient(path);
        await blob.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await blob.AppendBlockAsync(new MemoryStream(Encoding.UTF8.GetBytes(line + "\n")), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListAsync(string container, string prefix, CancellationToken cancellationToken = default)
    {
        var names = new List<string>();
        await foreach (var item in Container(container).GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken).ConfigureAwait(false))
        {
            names.Add(item.Name);
        }

        return names;
    }

    public async Task<(IReadOnlyList<string> Deleted, IReadOnlyList<string> Retained)> DeletePrefixAsync(string container, string prefix,
        CancellationToken cancellationToken = default)
    {
        var client = Container(container);
        var (deleted, retained) = (new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));
        // Versioning is on (uploads is version-level immutable): delete every version, then the current blob.
        await foreach (var item in client.GetBlobsAsync(BlobTraits.None, BlobStates.Version, prefix, cancellationToken).ConfigureAwait(false))
        {
            var blob = item.VersionId is { } v && item.IsLatestVersion != true ? client.GetBlobClient(item.Name).WithVersion(v) : client.GetBlobClient(item.Name);
            try
            {
                await blob.DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                deleted.Add(item.Name);
            }
            catch (RequestFailedException e) when (e.Status is 409 or 403)
            {
                retained.Add(item.Name); // still inside the immutability period
            }
        }

        deleted.ExceptWith(retained);
        return ([.. deleted], [.. retained]);
    }
}

/// <summary>In-process store for tests and local runs.</summary>
public sealed class InMemoryObjectStore : IObjectStore
{
    private readonly ConcurrentDictionary<string, BinaryData> _blobs = new();
    private readonly Lock _gate = new();

    private static string Key(string container, string path) => $"{container}/{path}";

    public Task PutAsync(string container, string path, BinaryData data, bool overwrite = true, CancellationToken cancellationToken = default)
    {
        if (overwrite)
        {
            _blobs[Key(container, path)] = data;
        }
        else
        {
            _blobs.TryAdd(Key(container, path), data);
        }

        return Task.CompletedTask;
    }

    public Task<BinaryData?> GetAsync(string container, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(_blobs.TryGetValue(Key(container, path), out var data) ? data : null);

    public Task<T> UpdateAsync<T>(string container, string path, Func<T?, T> update, CancellationToken cancellationToken = default) where T : class
    {
        lock (_gate)
        {
            var next = update(StoreJson.Deserialize<T>(_blobs.GetValueOrDefault(Key(container, path))));
            _blobs[Key(container, path)] = StoreJson.Serialize(next);
            return Task.FromResult(next);
        }
    }

    public Task AppendLineAsync(string container, string path, string line, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var existing = _blobs.GetValueOrDefault(Key(container, path))?.ToString() ?? "";
            _blobs[Key(container, path)] = BinaryData.FromString(existing + line + "\n");
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAsync(string container, string prefix, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(_blobs.Keys.Where(k => k.StartsWith($"{container}/{prefix}", StringComparison.Ordinal))
            .Select(k => k[(container.Length + 1)..]).Order(StringComparer.Ordinal).ToList());

    public async Task<(IReadOnlyList<string> Deleted, IReadOnlyList<string> Retained)> DeletePrefixAsync(string container, string prefix,
        CancellationToken cancellationToken = default)
    {
        var names = await ListAsync(container, prefix, cancellationToken).ConfigureAwait(false);
        foreach (var name in names)
        {
            _blobs.TryRemove(Key(container, name), out _);
        }

        return (names, []);
    }
}

public static class StorePaths
{
    public static string Source(string uploadId, string fileName) => $"{uploadId}/{fileName}";
    public static string Manifest(string uploadId) => $"uploads/{uploadId}/manifest.json";
    public static string Usage(string uploadId) => $"uploads/{uploadId}/usage.jsonl";
    public static string Document(string uploadId, string documentId, string name) => $"uploads/{uploadId}/documents/{documentId}/{name}";
    public static string Review(string documentId) => $"reviews/{documentId}.json";
    public static string DocumentIndex(string documentId) => $"index/documents/{documentId}.json";
    public static string KeyIndex(string keyHash) => $"index/keys/{keyHash}.json";
    public static string Audit(DateTimeOffset at) => $"audit/{at:yyyy-MM-dd}.jsonl";
    public static string QuoteDocument(string quoteId, string documentId) => $"index/quotes/{Safe(quoteId)}/{documentId}.json";
    public static string QuoteDocuments(string quoteId) => $"index/quotes/{Safe(quoteId)}/";
    public static string Overrides(string documentId) => $"overrides/{documentId}.jsonl";
    public static string QuoteContext(string quoteId) => $"quotes/{Safe(quoteId)}/context.json";
    public static string QuoteLabel(string quoteId) => $"quotes/{Safe(quoteId)}/label.json";

    public static string QuoteOwner(string quoteId) => $"quotes/{Safe(quoteId)}/owner.json";

    /// <summary>Brief 30 item 19: the format's last accepted documents (their graphs stay under their upload), newest first.</summary>
    public static string FormatExemplars(string formatId) => $"formats/{Safe(formatId)}/exemplars.json";
    public static string Workspace(string workspaceId) => $"workspaces/{Safe(workspaceId)}.json";
    public static string Outbox(string id) => $"outbox/{Safe(id)}.json";
    public static string ChatConversation(string conversationId) => $"chat/{Safe(conversationId)}.jsonl";
    public static string FeatureGeneration(string quoteId) => $"features/{Safe(quoteId)}/generation.json";
    public static string FeatureSnapshot(string quoteId, string keyHash) => $"features/{Safe(quoteId)}/{keyHash}.json";

    /// <summary>A quote id as one path segment.</summary>
    public static string Safe(string id) => Uri.EscapeDataString(id);
}

/// <summary>Blob-mode feature-snapshot invalidation: snapshots carry the quote's generation; any claim change bumps it.</summary>
public sealed record FeatureGeneration(long Generation);

public sealed record BlobFeatureSnapshot(long Generation, LossRun.Data.StoredFeatureSnapshot Snapshot);

public sealed record DocumentIndexEntry(string UploadId);

/// <summary>Deletes one upload's originals and everything derived from it (workspace expiry, 12 Answers).</summary>
public static class WorkspacePurge
{
    public static async Task<LossRun.Data.UploadPurge> UploadAsync(IObjectStore store, string uploadId, CancellationToken cancellationToken = default)
    {
        var (sources, sourcesKept) = await store.DeletePrefixAsync(Containers.Uploads, $"{uploadId}/", cancellationToken).ConfigureAwait(false);
        var (derived, derivedKept) = await store.DeletePrefixAsync(Containers.Extractions, $"uploads/{uploadId}/", cancellationToken).ConfigureAwait(false);
        return new LossRun.Data.UploadPurge(uploadId, [.. sources.Select(n => $"{Containers.Uploads}/{n}"), .. derived.Select(n => $"{Containers.Extractions}/{n}")],
            [.. sourcesKept.Select(n => $"{Containers.Uploads}/{n}"), .. derivedKept.Select(n => $"{Containers.Extractions}/{n}")]);
    }
}
