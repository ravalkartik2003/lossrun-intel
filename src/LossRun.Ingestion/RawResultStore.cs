using System.Collections.Concurrent;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace LossRun.Ingestion;

/// <summary>Stores raw analyzer results next to the document (<c>extractions/{documentId}/{name}</c>).</summary>
public interface IRawResultStore
{
    /// <returns>The stored path, e.g. <c>extractions/doc-1234/layout.json</c>.</returns>
    Task<string> SaveAsync(string documentId, string name, string json, CancellationToken cancellationToken);

    /// <summary>Brief 33: the stored result and its path, or null (a re-delivered parse reuses it instead of calling Content Understanding again).</summary>
    Task<RawResult?> TryGetAsync(string documentId, string name, CancellationToken cancellationToken);
}

public sealed record RawResult(string Json, string Path);

public sealed class BlobRawResultStore(BlobContainerClient container) : IRawResultStore
{
    public async Task<string> SaveAsync(string documentId, string name, string json, CancellationToken cancellationToken)
    {
        var blob = container.GetBlobClient($"{documentId}/{name}");
        await blob.UploadAsync(BinaryData.FromString(json), new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" } },
            cancellationToken).ConfigureAwait(false);
        return $"{container.Name}/{documentId}/{name}";
    }

    public async Task<RawResult?> TryGetAsync(string documentId, string name, CancellationToken cancellationToken)
    {
        var blob = container.GetBlobClient($"{documentId}/{name}");
        try
        {
            var content = await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            return new RawResult(content.Value.Content.ToString(), $"{container.Name}/{documentId}/{name}");
        }
        catch (Azure.RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
    }
}

/// <summary>In-memory store (tests); optionally mirrors files to a local folder (CLI).</summary>
public sealed class InMemoryRawResultStore(string? mirrorDirectory = null) : IRawResultStore
{
    public ConcurrentDictionary<string, string> Items { get; } = new();

    public async Task<string> SaveAsync(string documentId, string name, string json, CancellationToken cancellationToken)
    {
        var path = $"extractions/{documentId}/{name}";
        Items[path] = json;
        if (mirrorDirectory is not null)
        {
            var file = Path.Combine(mirrorDirectory, documentId, name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
        }

        return path;
    }

    public Task<RawResult?> TryGetAsync(string documentId, string name, CancellationToken cancellationToken)
    {
        var path = $"extractions/{documentId}/{name}";
        return Task.FromResult(Items.TryGetValue(path, out var json) ? new RawResult(json, path) : null);
    }
}
