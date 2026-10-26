using LossRun.Contracts;
using LossRun.Formats;
using Microsoft.EntityFrameworkCore;

namespace LossRun.Data;

/// <summary>
/// Format memory with Azure SQL as the source (brief 09 §1 0006): specs in dbo.FormatVersion (immutable per version),
/// lifecycle columns on the same row, per-field counters in dbo.FormatFieldStats. Every publish is also written to
/// <paramref name="cache"/> (Blob), which keeps the centroids. Learned glossary aliases live in dbo.GlossaryAlias (ADR 0019). Formats served from a read-only
/// layer (<paramref name="readOnlySpecs"/>, the checked-in hand specs) are registered here on their first stats update,
/// so their counters and lifecycle (approve, retire) live in SQL too; the index leaves them to that layer.
/// </summary>
public sealed class SqlFormatRegistry(Func<LossRunDbContext> contexts, IFormatRegistry cache, IFormatRegistry? readOnlySpecs = null) : IFormatRegistry
{
    private const string ReadOnlyMarker = "read-only:";

    public async Task PublishAsync(CarrierFormat format, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        if (await db.FormatVersions.AnyAsync(v => v.FormatId == format.FormatId && v.Version == format.Version, cancellationToken).ConfigureAwait(false))
        {
            throw new FormatAlreadyPublishedException(format.Ref);
        }

        if (await db.Formats.FindAsync([format.FormatId], cancellationToken).ConfigureAwait(false) is null)
        {
            db.Formats.Add(new FormatRow { Id = format.FormatId, CarrierName = format.CarrierName, FileKind = format.FileKind, CreatedUtc = format.CreatedAt.UtcDateTime });
        }

        db.FormatVersions.Add(Row(format));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException e) when (e.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2627 or 2601 })
        {
            throw new FormatAlreadyPublishedException(format.Ref);
        }

        try
        {
            await cache.PublishAsync(format, cancellationToken).ConfigureAwait(false);
        }
        catch (FormatAlreadyPublishedException)
        {
            // the cache already holds it (backfill, or a publish retried after the SQL commit)
        }
    }

    public async Task<CarrierFormat?> GetAsync(string formatId, int? version = null, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var rows = db.FormatVersions.AsNoTracking().Where(v => v.FormatId == formatId);
        var json = await (version is { } n ? rows.Where(v => v.Version == n) : rows.OrderByDescending(v => v.Version)).Select(v => v.SpecJson)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return json is null ? null : FormatJson.Parse(json);
    }

    public async Task<IReadOnlyList<FormatIndexEntry>> GetIndexAsync(CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var rows = await db.FormatVersions.AsNoTracking().Where(v => !v.CreatedBy.StartsWith(ReadOnlyMarker)).Join(db.Formats, v => v.FormatId, f => f.Id, (v, f) => new { v, f })
            .OrderBy(x => x.v.FormatId).ThenBy(x => x.v.Version).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(x => new FormatIndexEntry(x.v.FormatId, x.v.Version, x.f.CarrierName, x.f.FileKind, x.v.IdentityKey ?? "", x.v.HeaderKey ?? "",
            new DateTimeOffset(DateTime.SpecifyKind(x.v.CreatedUtc, DateTimeKind.Utc))))];
    }

    public Task<FormatCentroid?> GetCentroidAsync(string formatId, CancellationToken cancellationToken = default) => cache.GetCentroidAsync(formatId, cancellationToken);

    public Task SaveCentroidAsync(FormatCentroid centroid, CancellationToken cancellationToken = default) => cache.SaveCentroidAsync(centroid, cancellationToken);

    public async Task<FieldStats> GetStatsAsync(string formatId, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        return await LoadStatsAsync(db, formatId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Read-modify-write under an update lock on the Format row (the Blob ETag loop's equivalent).</summary>
    public async Task UpdateStatsAsync(string formatId, Action<FieldStats> update, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var locked = await db.Formats.FromSqlInterpolated($"SELECT * FROM dbo.Format WITH (UPDLOCK, HOLDLOCK) WHERE Id = {formatId}").ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (locked.Count == 0)
            {
                await RegisterReadOnlyAsync(db, formatId, cancellationToken).ConfigureAwait(false);
            }

            var stats = await LoadStatsAsync(db, formatId, cancellationToken).ConfigureAwait(false);
            update(stats);
            if (stats.Embedding is { } hits && await db.Formats.FindAsync([formatId], cancellationToken).ConfigureAwait(false) is { } format)
            {
                (format.EmbeddingHits, format.EmbeddingMean, format.EmbeddingM2) = (hits.Count, hits.Mean, hits.M2);
            }

            var versions = await db.FormatVersions.Where(v => v.FormatId == formatId).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in versions)
            {
                if (stats.Lifecycle.TryGetValue(row.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), out var l))
                {
                    (row.Status, row.CleanReplays, row.ActivatedBy, row.ActivatedUtc, row.RetiredBy, row.RetiredUtc, row.RetiredReason) =
                        (l.Status, l.CleanReplays, l.ActivatedBy, l.ActivatedAt?.UtcDateTime, l.RetiredBy, l.RetiredAt?.UtcDateTime, l.RetiredReason);
                }
            }

            var fields = await db.FormatFieldStats.Where(f => f.FormatId == formatId).ToDictionaryAsync(f => f.FieldPath, cancellationToken).ConfigureAwait(false);
            foreach (var (path, stat) in stats.Fields)
            {
                if (!fields.TryGetValue(path, out var row))
                {
                    row = new FormatFieldStatsRow { FormatId = formatId, FieldPath = path };
                    db.FormatFieldStats.Add(row);
                }

                (row.SuccessCount, row.LastSeenUtc, row.LastFormatVersion) = (stat.SuccessCount, stat.LastSeen?.UtcDateTime, stat.LastFormatVersion);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>ADR 0019: learned aliases from dbo.GlossaryAlias (system-versioned) with their evidence (dbo.GlossaryAliasEvidence, append-only).</summary>
    public async Task<LearnedGlossary> GetLearnedGlossaryAsync(CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        return await LoadAliasesAsync(db, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One lifecycle step in one transaction: the alias table is locked (low volume: learns, accepted replays and reviews),
    /// the step runs on the loaded aliases, changed rows are updated (history keeps the previous version) and new evidence
    /// rows are inserted. Evidence is never updated or deleted (ledger).
    /// </summary>
    public async Task<IReadOnlyList<AliasChange>> UpdateLearnedGlossaryAsync(Func<LearnedGlossary, IReadOnlyList<AliasChange>> update, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync("SELECT TOP (0) Id FROM dbo.GlossaryAlias WITH (TABLOCKX, HOLDLOCK)", cancellationToken).ConfigureAwait(false);
            var glossary = await LoadAliasesAsync(db, cancellationToken).ConfigureAwait(false);
            var evidenceBefore = glossary.Mappings.SelectMany(m => m.Evidence.Select(e => (m.Id!, e.DocumentId))).ToHashSet();
            var changes = update(glossary);

            var rows = await db.GlossaryAliases.ToDictionaryAsync(r => r.Id, cancellationToken).ConfigureAwait(false);
            foreach (var alias in glossary.Mappings)
            {
                alias.Id ??= alias.Key;
                if (!rows.TryGetValue(alias.Id, out var row))
                {
                    db.GlossaryAliases.Add(row = new GlossaryAliasRow { Id = alias.Id });
                }

                Copy(alias, row);
                foreach (var e in alias.Evidence.Where(e => !evidenceBefore.Contains((alias.Id, e.DocumentId))))
                {
                    db.GlossaryAliasEvidence.Add(new GlossaryAliasEvidenceRow
                    {
                        AliasId = alias.Id, DocumentId = e.DocumentId, FormatRef = e.FormatRef, Route = e.Route, TotalsTie = e.TotalsTie, AtUtc = e.AtUtc.UtcDateTime,
                    });
                }
            }

            foreach (var removed in rows.Values.Where(r => glossary.Mappings.All(m => m.Id != r.Id)))
            {
                db.GlossaryAliases.Remove(removed); // merged into another alias by a reviewer's edit; its history stays
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return changes;
        }).ConfigureAwait(false);
    }

    private static async Task<LearnedGlossary> LoadAliasesAsync(LossRunDbContext db, CancellationToken cancellationToken)
    {
        var evidence = (await db.GlossaryAliasEvidence.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)).ToLookup(e => e.AliasId);
        var aliases = await db.GlossaryAliases.AsNoTracking().OrderBy(a => a.FirstSeenUtc).ThenBy(a => a.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new LearnedGlossary
        {
            Mappings = aliases.Select(r => new LearnedMapping
            {
                Id = r.Id, Header = r.Header, CanonicalField = r.CanonicalField, Status = r.Status, CarrierName = r.CarrierName, FormatId = r.FormatId,
                FormatVersion = r.FormatVersion, FirstSeenDocumentId = r.FirstSeenDocumentId, FirstSeenUtc = Utc(r.FirstSeenUtc),
                LearnedBy = new LearnedBy { Model = r.LearnedModel, ModelVersion = r.LearnedModelVersion, PromptVersion = r.PromptVersion },
                Confidence = r.Confidence, TotalsTie = r.TotalsTie, VerificationGate = r.VerificationGate, ReviewedBy = r.ReviewedBy,
                ActivatedBy = r.ActivatedBy, ActivatedUtc = r.ActivatedUtc is { } a ? Utc(a) : null, ActivationReason = r.ActivationReason,
                RetiredBy = r.RetiredBy, RetiredUtc = r.RetiredUtc is { } t ? Utc(t) : null, RetiredReason = r.RetiredReason,
                RetiredAuditEventId = r.RetiredAuditEventId, EditedFrom = r.EditedFrom,
                Evidence = [.. evidence[r.Id].OrderBy(e => e.AtUtc).ThenBy(e => e.DocumentId, StringComparer.Ordinal).Select(e => new AliasEvidence
                {
                    DocumentId = e.DocumentId, FormatRef = e.FormatRef, Route = e.Route, TotalsTie = e.TotalsTie, AtUtc = Utc(e.AtUtc),
                })],
            }).ToList(),
        };
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static void Copy(LearnedMapping alias, GlossaryAliasRow row)
    {
        (row.Header, row.CanonicalField, row.Status, row.CarrierName, row.FormatId, row.FormatVersion) =
            (alias.Header, alias.CanonicalField, alias.Status, alias.CarrierName, alias.FormatId, alias.FormatVersion);
        (row.FirstSeenDocumentId, row.FirstSeenUtc, row.LearnedModel, row.LearnedModelVersion, row.PromptVersion, row.Confidence) =
            (alias.FirstSeenDocumentId, alias.FirstSeenUtc.UtcDateTime, alias.LearnedBy.Model, alias.LearnedBy.ModelVersion, alias.LearnedBy.PromptVersion, alias.Confidence);
        (row.TotalsTie, row.VerificationGate, row.ReviewedBy, row.ActivatedBy, row.ActivatedUtc, row.ActivationReason) =
            (alias.TotalsTie, alias.VerificationGate, alias.ReviewedBy, alias.ActivatedBy, alias.ActivatedUtc?.UtcDateTime, alias.ActivationReason);
        (row.RetiredBy, row.RetiredUtc, row.RetiredReason, row.RetiredAuditEventId, row.EditedFrom) =
            (alias.RetiredBy, alias.RetiredUtc?.UtcDateTime, alias.RetiredReason, alias.RetiredAuditEventId, alias.EditedFrom);
    }

    /// <summary>Copies a spec and its stats from another registry (backfill), keeping the cache untouched.</summary>
    public async Task ImportAsync(CarrierFormat format, FieldStats stats, CancellationToken cancellationToken = default)
    {
        await using (var db = contexts())
        {
            if (await db.FormatVersions.AnyAsync(v => v.FormatId == format.FormatId && v.Version == format.Version, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (await db.Formats.FindAsync([format.FormatId], cancellationToken).ConfigureAwait(false) is null)
            {
                db.Formats.Add(new FormatRow { Id = format.FormatId, CarrierName = format.CarrierName, FileKind = format.FileKind, CreatedUtc = format.CreatedAt.UtcDateTime });
            }

            db.FormatVersions.Add(Row(format));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await UpdateStatsAsync(format.FormatId, s =>
        {
            foreach (var (version, lifecycle) in stats.Lifecycle)
            {
                s.Lifecycle[version] = lifecycle;
            }

            foreach (var (field, stat) in stats.Fields)
            {
                s.Fields[field] = stat;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A format only the read-only layer knows (a hand spec): its rows, marked so the index leaves it to that layer.</summary>
    private async Task RegisterReadOnlyAsync(LossRunDbContext db, string formatId, CancellationToken cancellationToken)
    {
        var versions = readOnlySpecs is null ? [] : (await readOnlySpecs.GetIndexAsync(cancellationToken).ConfigureAwait(false)).Where(e => e.FormatId == formatId).ToList();
        var first = versions.Count == 0 ? null : await readOnlySpecs!.GetAsync(formatId, versions.Min(v => v.Version), cancellationToken).ConfigureAwait(false);
        db.Formats.Add(new FormatRow
        {
            Id = formatId, CarrierName = first?.CarrierName ?? "", FileKind = first?.FileKind ?? "", CreatedUtc = (first?.CreatedAt ?? DateTimeOffset.UtcNow).UtcDateTime,
        });
        foreach (var entry in versions)
        {
            if (await readOnlySpecs!.GetAsync(formatId, entry.Version, cancellationToken).ConfigureAwait(false) is { } spec)
            {
                var row = Row(spec);
                row.CreatedBy = ReadOnlyMarker + spec.CreatedBy;
                db.FormatVersions.Add(row);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static FormatVersionRow Row(CarrierFormat format) => new()
    {
        FormatId = format.FormatId, Version = format.Version, SpecJson = FormatJson.Write(format), CreatedBy = format.CreatedBy, CreatedUtc = format.CreatedAt.UtcDateTime,
        Supersedes = format.Supersedes, VerificationGate = format.Learning?.VerificationGate, IdentityKey = format.Fingerprint.IdentityKey, HeaderKey = format.Fingerprint.HeaderKey,
        Status = format.Status ?? FormatLifecycle.Active,
    };

    private static async Task<FieldStats> LoadStatsAsync(LossRunDbContext db, string formatId, CancellationToken cancellationToken)
    {
        var stats = new FieldStats { FormatId = formatId };
        if (await db.Formats.AsNoTracking().Where(f => f.Id == formatId).Select(f => new { f.EmbeddingHits, f.EmbeddingMean, f.EmbeddingM2 }).SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false) is { EmbeddingHits: { } count } embedding)
        {
            stats.Embedding = new EmbeddingHits { Count = count, Mean = embedding.EmbeddingMean ?? 0, M2 = embedding.EmbeddingM2 ?? 0 };
        }

        foreach (var f in await db.FormatFieldStats.AsNoTracking().Where(f => f.FormatId == formatId).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            stats.Fields[f.FieldPath] = new FieldStat
            {
                SuccessCount = f.SuccessCount, LastSeen = f.LastSeenUtc is { } s ? new DateTimeOffset(DateTime.SpecifyKind(s, DateTimeKind.Utc)) : null,
                LastFormatVersion = f.LastFormatVersion,
            };
        }

        // Lifecycle entries exist once FormatLifecycle has initialized them (publish → InitializeAsync); a version without
        // one reads as active, exactly like a stats.json without the key.
        foreach (var v in await db.FormatVersions.AsNoTracking().Where(v => v.FormatId == formatId && (v.ActivatedBy != null || v.RetiredBy != null || v.Status != FormatLifecycle.Active || v.CleanReplays > 0))
                     .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            stats.Lifecycle[v.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new VersionLifecycle
            {
                Status = v.Status, CleanReplays = v.CleanReplays, ActivatedBy = v.ActivatedBy, ActivatedAt = Utc(v.ActivatedUtc), RetiredBy = v.RetiredBy,
                RetiredAt = Utc(v.RetiredUtc), RetiredReason = v.RetiredReason,
            };
        }

        return stats;
    }

    private static DateTimeOffset? Utc(DateTime? value) => value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;
}
