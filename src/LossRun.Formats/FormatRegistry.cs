using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using LossRun.Contracts;

namespace LossRun.Formats;

public sealed record FormatIndexEntry(string FormatId, int Version, string CarrierName, string FileKind, string IdentityKey, string HeaderKey, DateTimeOffset CreatedAt);

/// <summary>Embedding centroid of a format (formats/{formatId}/centroid.json).</summary>
public sealed class FormatCentroid
{
    public string FormatId { get; set; } = "";
    public string Model { get; set; } = "";
    public string TextNormalization { get; set; } = FingerprintText.Normalization;
    public int ExemplarCount { get; set; }
    public List<string> ExemplarDocumentIds { get; set; } = [];
    public float[] Vector { get; set; } = [];
}

public sealed class FormatAlreadyPublishedException(string formatRef)
    : InvalidOperationException($"Format {formatRef} is already published; publish a new version instead.");

/// <summary>Publish-only store of CarrierFormat versions plus their mutable sidecars (stats, centroid).</summary>
public interface IFormatRegistry
{
    /// <summary>Publishes a new version. Throws <see cref="FormatAlreadyPublishedException"/> if that version exists.</summary>
    Task PublishAsync(CarrierFormat format, CancellationToken cancellationToken = default);

    /// <summary>A specific version, or the latest when <paramref name="version"/> is null.</summary>
    Task<CarrierFormat?> GetAsync(string formatId, int? version = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FormatIndexEntry>> GetIndexAsync(CancellationToken cancellationToken = default);
    Task<FormatCentroid?> GetCentroidAsync(string formatId, CancellationToken cancellationToken = default);
    Task SaveCentroidAsync(FormatCentroid centroid, CancellationToken cancellationToken = default);
    Task<FieldStats> GetStatsAsync(string formatId, CancellationToken cancellationToken = default);

    /// <summary>Read-modify-write of the stats sidecar; the Blob implementation guards it with the blob ETag and retries.</summary>
    Task UpdateStatsAsync(string formatId, Action<FieldStats> update, CancellationToken cancellationToken = default);

    /// <summary>The learned glossary aliases (ADR 0019; <c>glossary-learned.json</c> or SQL); empty when none was written yet.</summary>
    Task<LearnedGlossary> GetLearnedGlossaryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-modify-write of the learned aliases (ADR 0019): <paramref name="update"/> applies one <see cref="AliasLifecycle"/>
    /// step and returns what it changed; the store persists it atomically (Blob: ETag retry; SQL: one transaction).
    /// </summary>
    Task<IReadOnlyList<AliasChange>> UpdateLearnedGlossaryAsync(Func<LearnedGlossary, IReadOnlyList<AliasChange>> update, CancellationToken cancellationToken = default);
}

public static class LearnedGlossaryRegistryExtensions
{
    /// <summary>A learn verified and published: its new mappings become candidates (ADR 0019 1), or add evidence to existing aliases.</summary>
    public static async Task<IReadOnlyList<AliasChange>> AppendLearnedMappingsAsync(this IFormatRegistry registry, IReadOnlyList<LearnedMapping> mappings,
        AliasPolicy? policy = null, CancellationToken cancellationToken = default)
    {
        var withHand = await (policy ?? AliasPolicy.Default).WithRegistryAsync(registry, cancellationToken).ConfigureAwait(false); // brief 29 Answers 4
        return await registry.UpdateLearnedGlossaryAsync(g => AliasLifecycle.RecordLearned(g, mappings, withHand), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The seed glossary with the active learned aliases attached (what ingestion, routing, replay and learn resolve headers with).</summary>
    public static async Task<Glossary> GlossaryAsync(this IFormatRegistry registry, Glossary? seed = null, CancellationToken cancellationToken = default) =>
        (seed ?? CanonicalData.Glossary).WithLearned((await registry.GetLearnedGlossaryAsync(cancellationToken).ConfigureAwait(false)).Mappings
            .Where(m => m.Status == AliasStatus.Active));
}

public sealed class InMemoryFormatRegistry : IFormatRegistry
{
    private readonly Dictionary<string, string> _specs = []; // ref → JSON (stored serialized: callers cannot mutate a published spec)
    private readonly Dictionary<string, FormatCentroid> _centroids = [];
    private readonly Dictionary<string, FieldStats> _stats = [];
    private readonly object _gate = new();

    public Task PublishAsync(CarrierFormat format, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_specs.TryAdd(format.Ref, FormatJson.Write(format)))
            {
                throw new FormatAlreadyPublishedException(format.Ref);
            }
        }

        return Task.CompletedTask;
    }

    public Task<CarrierFormat?> GetAsync(string formatId, int? version = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var key = version is { } v
                ? $"{formatId}@{v}"
                : _specs.Keys.Where(k => k.StartsWith(formatId + "@", StringComparison.Ordinal)).MaxBy(k => int.Parse(k[(formatId.Length + 1)..]));
            return Task.FromResult(key is not null && _specs.TryGetValue(key, out var json) ? FormatJson.Parse(json) : null);
        }
    }

    public Task<IReadOnlyList<FormatIndexEntry>> GetIndexAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<FormatIndexEntry> index = _specs.Values.Select(FormatJson.Parse).Select(IndexEntry)
                .OrderBy(e => e.FormatId, StringComparer.Ordinal).ThenBy(e => e.Version).ToList();
            return Task.FromResult(index);
        }
    }

    public Task<FormatCentroid?> GetCentroidAsync(string formatId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_centroids.GetValueOrDefault(formatId));
        }
    }

    public Task SaveCentroidAsync(FormatCentroid centroid, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _centroids[centroid.FormatId] = centroid;
        }

        return Task.CompletedTask;
    }

    public Task<FieldStats> GetStatsAsync(string formatId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_stats.GetValueOrDefault(formatId) ?? new FieldStats { FormatId = formatId });
        }
    }

    public Task UpdateStatsAsync(string formatId, Action<FieldStats> update, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var stats = _stats.GetValueOrDefault(formatId) ?? new FieldStats { FormatId = formatId };
            update(stats);
            _stats[formatId] = stats;
        }

        return Task.CompletedTask;
    }

    private string _learned = JsonSerializer.Serialize(new LearnedGlossary(), CanonicalJson.Options); // serialized: callers cannot mutate it

    public Task<LearnedGlossary> GetLearnedGlossaryAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(JsonSerializer.Deserialize<LearnedGlossary>(_learned, CanonicalJson.Options)!);
        }
    }

    public Task<IReadOnlyList<AliasChange>> UpdateLearnedGlossaryAsync(Func<LearnedGlossary, IReadOnlyList<AliasChange>> update, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var glossary = JsonSerializer.Deserialize<LearnedGlossary>(_learned, CanonicalJson.Options)!.Upgrade();
            var changes = update(glossary);
            _learned = JsonSerializer.Serialize(glossary, CanonicalJson.Options);
            return Task.FromResult(changes);
        }
    }

    internal static FormatIndexEntry IndexEntry(CarrierFormat f) =>
        new(f.FormatId, f.Version, f.CarrierName, f.FileKind, f.Fingerprint.IdentityKey, f.Fingerprint.HeaderKey, f.CreatedAt);
}

/// <summary>
/// Blob layout in the <c>formats</c> container: <c>{formatId}/{version}.json</c> (written once, If-None-Match: *),
/// <c>index.json</c> (optimistic concurrency on ETag), <c>{formatId}/stats.json</c>, <c>{formatId}/centroid.json</c>.
/// </summary>
public sealed class BlobFormatRegistry(BlobContainerClient container) : IFormatRegistry
{
    private const string IndexBlob = "index.json";

    public async Task PublishAsync(CarrierFormat format, CancellationToken cancellationToken = default)
    {
        var json = FormatJson.Write(format);
        try
        {
            await container.GetBlobClient($"{format.FormatId}/{format.Version}.json").UploadAsync(BinaryData.FromString(json),
                new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }, HttpHeaders = Json }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException e) when (e.Status is 409 or 412)
        {
            // Brief 30 item 36: the version blob is written before the index. A publish whose index update failed left it unindexed (invisible
            // to matching); the same spec published again resumes the index write. A different spec under that version is still refused.
            var indexed = (await ReadIndexAsync(cancellationToken).ConfigureAwait(false)).Index.Any(x => x.FormatId == format.FormatId && x.Version == format.Version);
            if (indexed || await ReadTextAsync($"{format.FormatId}/{format.Version}.json", cancellationToken).ConfigureAwait(false) != json)
            {
                throw new FormatAlreadyPublishedException(format.Ref);
            }
        }

        for (var attempt = 0; ; attempt++)
        {
            var (index, etag) = await ReadIndexAsync(cancellationToken).ConfigureAwait(false);
            if (index.Any(x => x.FormatId == format.FormatId && x.Version == format.Version))
            {
                return; // a concurrent resume indexed it
            }

            index.Add(InMemoryFormatRegistry.IndexEntry(format));
            try
            {
                var conditions = etag is { } e ? new BlobRequestConditions { IfMatch = e } : new BlobRequestConditions { IfNoneMatch = ETag.All };
                await container.GetBlobClient(IndexBlob).UploadAsync(BinaryData.FromObjectAsJson(index, CanonicalJson.Options),
                    new BlobUploadOptions { Conditions = conditions, HttpHeaders = Json }, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412 && attempt < 5)
            {
                // another publisher updated the index; re-read and retry
            }
        }
    }

    public async Task<CarrierFormat?> GetAsync(string formatId, int? version = null, CancellationToken cancellationToken = default)
    {
        var v = version ?? (await GetIndexAsync(cancellationToken).ConfigureAwait(false)).Where(e => e.FormatId == formatId).Select(e => (int?)e.Version).Max();
        if (v is null)
        {
            return null;
        }

        var text = await ReadTextAsync($"{formatId}/{v}.json", cancellationToken).ConfigureAwait(false);
        return text is null ? null : FormatJson.Parse(text);
    }

    public async Task<IReadOnlyList<FormatIndexEntry>> GetIndexAsync(CancellationToken cancellationToken = default) =>
        (await ReadIndexAsync(cancellationToken).ConfigureAwait(false)).Index;

    public async Task<FormatCentroid?> GetCentroidAsync(string formatId, CancellationToken cancellationToken = default)
    {
        var text = await ReadTextAsync($"{formatId}/centroid.json", cancellationToken).ConfigureAwait(false);
        return text is null ? null : JsonSerializer.Deserialize<FormatCentroid>(text, CanonicalJson.Options);
    }

    public Task SaveCentroidAsync(FormatCentroid centroid, CancellationToken cancellationToken = default) =>
        container.GetBlobClient($"{centroid.FormatId}/centroid.json").UploadAsync(BinaryData.FromObjectAsJson(centroid, CanonicalJson.Options),
            new BlobUploadOptions { HttpHeaders = Json }, cancellationToken);

    public async Task<FieldStats> GetStatsAsync(string formatId, CancellationToken cancellationToken = default)
    {
        var text = await ReadTextAsync($"{formatId}/stats.json", cancellationToken).ConfigureAwait(false);
        return text is null ? new FieldStats { FormatId = formatId } : JsonSerializer.Deserialize<FieldStats>(text, CanonicalJson.Options)!;
    }

    public Task UpdateStatsAsync(string formatId, Action<FieldStats> update, CancellationToken cancellationToken = default) =>
        GuardedUpdateAsync($"{formatId}/stats.json", () => new FieldStats { FormatId = formatId }, update, cancellationToken);

    public async Task<LearnedGlossary> GetLearnedGlossaryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var content = await container.GetBlobClient(LearnedGlossaryBlob).DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            return (content.Value.Content.ToObjectFromJson<LearnedGlossary>(CanonicalJson.Options) ?? new LearnedGlossary()).Upgrade();
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return new LearnedGlossary();
        }
    }

    public async Task<IReadOnlyList<AliasChange>> UpdateLearnedGlossaryAsync(Func<LearnedGlossary, IReadOnlyList<AliasChange>> update, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AliasChange> changes = [];
        await GuardedUpdateAsync(LearnedGlossaryBlob, () => new LearnedGlossary(), g =>
        {
            var upgraded = g.Upgrade();
            (g.SidecarVersion, g.Mappings) = (upgraded.SidecarVersion, upgraded.Mappings);
            changes = update(g); // a retry after an ETag conflict re-applies the step to the newer glossary
        }, cancellationToken).ConfigureAwait(false);
        return changes;
    }

    private const string LearnedGlossaryBlob = "glossary-learned.json";

    /// <summary>Read-modify-write of a small JSON sidecar guarded by the blob ETag (If-Match / If-None-Match), retried on conflict.</summary>
    private async Task GuardedUpdateAsync<T>(string path, Func<T> create, Action<T> update, CancellationToken cancellationToken)
    {
        var blob = container.GetBlobClient(path);
        for (var attempt = 0; ; attempt++)
        {
            T value;
            BlobRequestConditions conditions;
            try
            {
                var current = await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
                value = current.Value.Content.ToObjectFromJson<T>(CanonicalJson.Options) ?? create();
                conditions = new BlobRequestConditions { IfMatch = current.Value.Details.ETag };
            }
            catch (RequestFailedException e) when (e.Status == 404)
            {
                value = create();
                conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };
            }

            update(value);
            try
            {
                await blob.UploadAsync(BinaryData.FromObjectAsJson(value, CanonicalJson.Options), new BlobUploadOptions { Conditions = conditions, HttpHeaders = Json },
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (RequestFailedException e) when (e.Status is 409 or 412 && attempt < 8)
            {
                // a concurrent writer got there first; re-read and re-apply
            }
        }
    }

    private async Task<(List<FormatIndexEntry> Index, ETag? ETag)> ReadIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            var download = await container.GetBlobClient(IndexBlob).DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            return (download.Value.Content.ToObjectFromJson<List<FormatIndexEntry>>(CanonicalJson.Options) ?? [], download.Value.Details.ETag);
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return ([], null);
        }
    }

    private async Task<string?> ReadTextAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            return (await container.GetBlobClient(name).DownloadContentAsync(cancellationToken).ConfigureAwait(false)).Value.Content.ToString();
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
    }

    private static BlobHttpHeaders Json => new() { ContentType = "application/json" };
}

/// <summary>Loads checked-in specs ({dir}/{formatId}/{version}.json and centroid.json) into a registry.</summary>
public static class FormatDirectory
{
    /// <param name="maxVersion">Load versions up to this one only (tests that exercise learning a hand spec's next version start from v1).</param>
    public static async Task<int> LoadIntoAsync(string directory, IFormatRegistry registry, int? maxVersion = null, CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var formatDir in Directory.GetDirectories(directory).Order(StringComparer.Ordinal))
        {
            foreach (var file in Directory.GetFiles(formatDir, "*.json")
                         .Where(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out var v) && (maxVersion is null || v <= maxVersion))
                         .OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f), System.Globalization.CultureInfo.InvariantCulture)))
            {
                await registry.PublishAsync(FormatJson.Parse(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
                count++;
            }

            var centroid = Path.Combine(formatDir, "centroid.json");
            if (File.Exists(centroid))
            {
                await registry.SaveCentroidAsync(JsonSerializer.Deserialize<FormatCentroid>(await File.ReadAllTextAsync(centroid, cancellationToken).ConfigureAwait(false),
                    CanonicalJson.Options)!, cancellationToken).ConfigureAwait(false);
            }
        }

        return count;
    }
}
