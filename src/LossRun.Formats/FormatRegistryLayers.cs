using System.Text.Json;
using LossRun.Contracts;

namespace LossRun.Formats;

/// <summary>
/// Read-through registry: a read-only base (the checked-in hand specs) under a writable top (Blob <c>formats</c>
/// container, or a local directory for offline demos). Reads see both; publications, centroids, stats and learned
/// glossary mappings go to the top. A version already present in the base can never be published again.
/// </summary>
public sealed class LayeredFormatRegistry(IFormatRegistry readOnlyBase, IFormatRegistry top) : IFormatRegistry
{
    public async Task PublishAsync(CarrierFormat format, CancellationToken cancellationToken = default)
    {
        if (await readOnlyBase.GetAsync(format.FormatId, format.Version, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new FormatAlreadyPublishedException(format.Ref);
        }

        await top.PublishAsync(format, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CarrierFormat?> GetAsync(string formatId, int? version = null, CancellationToken cancellationToken = default)
    {
        if (version is not null)
        {
            return await top.GetAsync(formatId, version, cancellationToken).ConfigureAwait(false)
                ?? await readOnlyBase.GetAsync(formatId, version, cancellationToken).ConfigureAwait(false);
        }

        var latest = (await GetIndexAsync(cancellationToken).ConfigureAwait(false)).Where(e => e.FormatId == formatId).MaxBy(e => e.Version);
        return latest is null ? null : await GetAsync(formatId, latest.Version, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Both layers, one entry per formatId@version. A ref both layers hold resolves to the top (learned) layer, as
    /// <see cref="GetAsync"/> does: documents already processed cite that spec (brief 08c: the live registry held learned
    /// synthetic-mutual-pdf v2–v4 when hand v2 shipped, and the index listed both v2s).
    /// </summary>
    public async Task<IReadOnlyList<FormatIndexEntry>> GetIndexAsync(CancellationToken cancellationToken = default)
    {
        var learned = await top.GetIndexAsync(cancellationToken).ConfigureAwait(false);
        var refs = learned.Select(e => (e.FormatId, e.Version)).ToHashSet();
        return [.. (await readOnlyBase.GetIndexAsync(cancellationToken).ConfigureAwait(false)).Where(e => !refs.Contains((e.FormatId, e.Version))), .. learned];
    }

    public async Task<FormatCentroid?> GetCentroidAsync(string formatId, CancellationToken cancellationToken = default) =>
        await top.GetCentroidAsync(formatId, cancellationToken).ConfigureAwait(false) ?? await readOnlyBase.GetCentroidAsync(formatId, cancellationToken).ConfigureAwait(false);

    public Task SaveCentroidAsync(FormatCentroid centroid, CancellationToken cancellationToken = default) => top.SaveCentroidAsync(centroid, cancellationToken);

    public Task<FieldStats> GetStatsAsync(string formatId, CancellationToken cancellationToken = default) => top.GetStatsAsync(formatId, cancellationToken);

    public Task UpdateStatsAsync(string formatId, Action<FieldStats> update, CancellationToken cancellationToken = default) =>
        top.UpdateStatsAsync(formatId, update, cancellationToken);

    public Task<LearnedGlossary> GetLearnedGlossaryAsync(CancellationToken cancellationToken = default) => top.GetLearnedGlossaryAsync(cancellationToken);

    public Task<IReadOnlyList<AliasChange>> UpdateLearnedGlossaryAsync(Func<LearnedGlossary, IReadOnlyList<AliasChange>> update, CancellationToken cancellationToken = default) =>
        top.UpdateLearnedGlossaryAsync(update, cancellationToken);
}

/// <summary>
/// File-backed registry with the Blob layout ({formatId}/{version}.json, centroid.json, stats.json,
/// glossary-learned.json) for offline CLI demos; publish-once via CreateNew. Single-process use only.
/// </summary>
public sealed class DirectoryFormatRegistry(string directory) : IFormatRegistry
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task PublishAsync(CarrierFormat format, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.Combine(directory, format.FormatId));
        try
        {
            await using var stream = new FileStream(Path.Combine(directory, format.FormatId, $"{format.Version}.json"), FileMode.CreateNew, FileAccess.Write);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(FormatJson.Write(format)).ConfigureAwait(false);
        }
        catch (IOException) when (File.Exists(Path.Combine(directory, format.FormatId, $"{format.Version}.json")))
        {
            throw new FormatAlreadyPublishedException(format.Ref);
        }
    }

    public async Task<CarrierFormat?> GetAsync(string formatId, int? version = null, CancellationToken cancellationToken = default)
    {
        var v = version ?? (await GetIndexAsync(cancellationToken).ConfigureAwait(false)).Where(e => e.FormatId == formatId).Select(e => (int?)e.Version).Max();
        var path = v is null ? null : Path.Combine(directory, formatId, $"{v}.json");
        return path is not null && File.Exists(path) ? FormatJson.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)) : null;
    }

    public Task<IReadOnlyList<FormatIndexEntry>> GetIndexAsync(CancellationToken cancellationToken = default)
    {
        var entries = !Directory.Exists(directory) ? [] : Directory.GetDirectories(directory)
            .SelectMany(d => Directory.GetFiles(d, "*.json").Where(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out _)))
            .Select(f => FormatJson.Parse(File.ReadAllText(f)))
            .Select(s => new FormatIndexEntry(s.FormatId, s.Version, s.CarrierName, s.FileKind, s.Fingerprint.IdentityKey, s.Fingerprint.HeaderKey, s.CreatedAt))
            .OrderBy(e => e.FormatId, StringComparer.Ordinal).ThenBy(e => e.Version)
            .ToList();
        return Task.FromResult<IReadOnlyList<FormatIndexEntry>>(entries);
    }

    public async Task<FormatCentroid?> GetCentroidAsync(string formatId, CancellationToken cancellationToken = default) =>
        await ReadAsync<FormatCentroid>(Path.Combine(formatId, "centroid.json"), cancellationToken).ConfigureAwait(false);

    public Task SaveCentroidAsync(FormatCentroid centroid, CancellationToken cancellationToken = default) =>
        WriteAsync(Path.Combine(centroid.FormatId, "centroid.json"), centroid, cancellationToken);

    public async Task<FieldStats> GetStatsAsync(string formatId, CancellationToken cancellationToken = default) =>
        await ReadAsync<FieldStats>(Path.Combine(formatId, "stats.json"), cancellationToken).ConfigureAwait(false) ?? new FieldStats { FormatId = formatId };

    public Task UpdateStatsAsync(string formatId, Action<FieldStats> update, CancellationToken cancellationToken = default) =>
        LockedAsync(async () =>
        {
            var stats = await GetStatsAsync(formatId, cancellationToken).ConfigureAwait(false);
            update(stats);
            await WriteAsync(Path.Combine(formatId, "stats.json"), stats, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public async Task<LearnedGlossary> GetLearnedGlossaryAsync(CancellationToken cancellationToken = default) =>
        (await ReadAsync<LearnedGlossary>("glossary-learned.json", cancellationToken).ConfigureAwait(false) ?? new LearnedGlossary()).Upgrade();

    public async Task<IReadOnlyList<AliasChange>> UpdateLearnedGlossaryAsync(Func<LearnedGlossary, IReadOnlyList<AliasChange>> update, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AliasChange> changes = [];
        await LockedAsync(async () =>
        {
            var glossary = await GetLearnedGlossaryAsync(cancellationToken).ConfigureAwait(false);
            changes = update(glossary);
            await WriteAsync("glossary-learned.json", glossary, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return changes;
    }

    private async Task LockedAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T?> ReadAsync<T>(string relative, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, relative);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), CanonicalJson.Options) : default;
    }

    private async Task WriteAsync<T>(string relative, T value, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, CanonicalJson.Options), cancellationToken).ConfigureAwait(false);
    }
}
