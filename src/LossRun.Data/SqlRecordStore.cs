using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using Microsoft.EntityFrameworkCore;

namespace LossRun.Data;

/// <summary>
/// Azure SQL as the system of record (brief 09 §5). Reads come from dbo tables; writes that touch Claim, Override or
/// AuditEvent go through <see cref="ClaimCommandService"/>. Manifests are read-modify-written under an update lock on
/// the Upload row, so concurrent activities of one upload serialize the way the Blob ETag loop did.
/// </summary>
public sealed class SqlRecordStore(Func<LossRunDbContext> contexts, ClaimCommandService commands) : IRecordStore
{
    private static string? Clip(string? s) => Manifests.Clip(s);

    public static SqlRecordStore Create(string connectionString, TimeProvider? clock = null)
    {
        var options = LossRunDbContext.Options(connectionString);
        LossRunDbContext Factory() => new(options);
        return new SqlRecordStore(Factory, new ClaimCommandService(Factory, clock));
    }

    public ClaimCommandService Commands => commands;

    public async Task<UploadManifest?> GetManifestAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var row = await db.Uploads.AsNoTracking().Include(u => u.Items).SingleOrDefaultAsync(u => u.Id == uploadId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : Manifests.From(row);
    }

    public async Task<UploadManifest> UpdateManifestAsync(string uploadId, Func<UploadManifest?, UploadManifest> change, Actor actor, CancellationToken cancellationToken = default)
    {
        UploadManifest? updated = null;
        await commands.InTransactionAsync(async db =>
        {
            var row = await db.Uploads.FromSqlInterpolated($"SELECT * FROM dbo.Upload WITH (UPDLOCK, HOLDLOCK) WHERE Id = {uploadId}")
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (row is not null)
            {
                await db.Entry(row).Collection(u => u.Items).LoadAsync(cancellationToken).ConfigureAwait(false);
            }

            updated = change(row is null ? null : Manifests.From(row));
            if (row is null)
            {
                row = new UploadRow { Id = uploadId };
                db.Uploads.Add(row);
                db.AuditEvents.Add(new AuditEventRow
                {
                    AtUtc = updated.CreatedAt.UtcDateTime, UserId = actor.UserId, Role = actor.Role, Action = "upload.create", UploadId = uploadId,
                    Rationale = Clip($"{updated.Files.Count} file(s): {string.Join(", ", updated.Files.Select(f => f.FileName))}"),
                    Source = actor.Source, CorrelationId = actor.CorrelationId,
                });
            }

            Manifests.Apply(updated, row);
        }, cancellationToken).ConfigureAwait(false);
        return updated!;
    }

    public Task IndexDocumentAsync(string documentId, string uploadId, CancellationToken cancellationToken = default) => Task.CompletedTask; // UploadItem is the index

    public async Task<string?> UploadOfDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        return await db.UploadItems.AsNoTracking().Where(i => i.DocumentId == documentId).Select(i => i.UploadId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? await db.Documents.AsNoTracking().Where(d => d.Id == documentId).Select(d => d.UploadId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DocumentHead?> DocumentHeadAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        return await (from d in db.Documents.AsNoTracking()
                      where d.Id == documentId
                      from r in db.ReviewItems.AsNoTracking().Where(r => r.DocumentId == d.Id).DefaultIfEmpty()
                      select new DocumentHead(d.Id, d.UploadId, d.QuoteId, r == null ? null : r.State, r == null ? null : r.Kind))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DocumentResult?> GetResultAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var json = await db.Documents.AsNoTracking().Where(d => d.Id == documentId).Select(d => d.ResultJson).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return json is null ? null : JsonSerializer.Deserialize<DocumentResult>(json, CanonicalJson.Options);
    }

    public async Task SaveResultAsync(DocumentResult result, Actor actor, CancellationToken cancellationToken = default)
    {
        string? quoteId;
        await using (var db = contexts())
        {
            quoteId = await db.Uploads.AsNoTracking().Where(u => u.Id == result.UploadId).Select(u => u.QuoteId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        await commands.PersistExtractionAsync(result, quoteId, actor, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReviewItem?> GetReviewAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var row = await db.ReviewItems.AsNoTracking().SingleOrDefaultAsync(r => r.DocumentId == documentId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : Reviews.From(row);
    }

    public async Task<IReadOnlyList<ReviewItem>> ListReviewsAsync(string state, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var rows = await db.ReviewItems.AsNoTracking().Where(r => state == "all" || r.State == state).OrderBy(r => r.OpenedUtc).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(Reviews.From)];
    }

    public Task OpenReviewAsync(ReviewItem item, CancellationToken cancellationToken = default) =>
        commands.InTransactionAsync(async db =>
        {
            if (await db.ReviewItems.FindAsync([item.DocumentId], cancellationToken).ConfigureAwait(false) is null)
            {
                db.ReviewItems.Add(Reviews.To(item));
            }
        }, cancellationToken);

    public Task<DecisionApplyResult> ApplyDecisionAsync(DecisionRecord record, CancellationToken cancellationToken = default, IReadOnlyList<OutboxMessage>? outbox = null) =>
        commands.ApplyDecisionAsync(record, new Actor(record.Decision.User, record.Decision.Role, "api"), cancellationToken, outbox); // brief 30 item 7: the proc's answer

    public Task ExpireReviewAsync(string uploadId, string documentId, DateTimeOffset at, CancellationToken cancellationToken = default) =>
        commands.ExpireReviewAsync(uploadId, documentId, at, cancellationToken);

    public Task ReopenReviewAsync(ReviewItem item, DocumentResult result, Actor actor, string reason, CancellationToken cancellationToken = default) =>
        commands.ReopenReviewAsync(item, result, actor, reason, cancellationToken);

    public async Task<bool> TryApplyDecisionAsync(DecisionRecord record, CancellationToken cancellationToken = default, IReadOnlyList<OutboxMessage>? outbox = null) =>
        await ApplyDecisionAsync(record, cancellationToken, outbox).ConfigureAwait(false) != DecisionApplyResult.Conflict;

    public Task AuditAsync(AuditEvent audit, Actor actor, CancellationToken cancellationToken = default) => commands.AuditAsync(audit, actor, cancellationToken);

    public Task AuditWithOutboxAsync(AuditEvent audit, Actor actor, IReadOnlyList<OutboxMessage> outbox, CancellationToken cancellationToken = default,
        QuoteRevisionBump? bump = null) =>
        commands.AuditAsync(audit, actor, outbox, cancellationToken, bump);

    public async Task<int> QuoteRevisionAsync(string quoteId, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        return (await db.Database.SqlQuery<int>($"SELECT Revision AS Value FROM dbo.QuoteRevision WHERE QuoteId = {quoteId}").ToListAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault();
    }

    public async Task<OutboxMessage?> GetOutboxAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var row = await db.Outbox.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToOutbox(row);
    }

    public async Task<IReadOnlyList<OutboxMessage>> PendingOutboxAsync(DateTimeOffset createdBefore, int max, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var before = createdBefore.UtcDateTime;
        var rows = await db.Outbox.AsNoTracking().Where(o => o.PublishedUtc == null && o.CreatedUtc <= before).OrderBy(o => o.CreatedUtc).Take(max)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(ToOutbox)];
    }

    public async Task MarkOutboxPublishedAsync(string id, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var when = at.UtcDateTime;
        await db.Outbox.Where(o => o.Id == id && o.PublishedUtc == null)
            .ExecuteUpdateAsync(u => u.SetProperty(o => o.PublishedUtc, when).SetProperty(o => o.PublishAttempts, o => o.PublishAttempts + 1), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task MarkOutboxFailedAsync(string id, string error, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var clipped = error.Length > 2000 ? error[..2000] : error;
        await db.Outbox.Where(o => o.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(o => o.LastError, clipped).SetProperty(o => o.PublishAttempts, o => o.PublishAttempts + 1), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RecordOutboxDeliveryAsync(string id, string status, string? deliveryJson, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var when = at.UtcDateTime;
        await db.Outbox.Where(o => o.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(o => o.DeliveredUtc, when).SetProperty(o => o.DeliveryStatus, status).SetProperty(o => o.DeliveryJson, deliveryJson),
                cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OutboxMessage>> OutboxAsync(string? quoteId, int max, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var rows = await db.Outbox.AsNoTracking().Where(o => quoteId == null || o.QuoteId == quoteId).OrderByDescending(o => o.CreatedUtc).Take(max)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(ToOutbox)];
    }

    private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));

    private static OutboxMessage ToOutbox(OutboxRow r) => new(r.Id, r.Channel, r.Kind, r.QuoteId, r.Subject, r.PayloadJson, Utc(r.CreatedUtc), r.CreatedBy)
    {
        PublishedAt = r.PublishedUtc is { } p ? Utc(p) : null, PublishAttempts = r.PublishAttempts, LastError = r.LastError,
        DeliveredAt = r.DeliveredUtc is { } d ? Utc(d) : null, DeliveryStatus = r.DeliveryStatus, DeliveryJson = r.DeliveryJson,
    };

    public async Task RecordUsageAsync(string uploadId, UsageEvent usage, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        db.UsageEvents.Add(new UsageEventRow
        {
            DocumentId = usage.DocumentId, RunId = uploadId, Stage = usage.Stage, Service = usage.Service, Meter = usage.Meter, Quantity = usage.Quantity, Unit = usage.Unit,
            ModelOrAnalyzer = usage.ModelOrAnalyzer, ModelVersion = usage.ModelVersion, DeploymentType = usage.DeploymentType, Region = usage.Region,
            TimestampUtc = usage.TimestampUtc.UtcDateTime, CorrelationId = usage.CorrelationId,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<QuoteDocument>> DocumentsOfQuoteAsync(string quoteId, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        return await db.Documents.AsNoTracking().Where(d => d.QuoteId == quoteId).OrderBy(d => d.Id).Select(d => new QuoteDocument(d.Id, d.Revision, d.ReviewState))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredOverride>> OverridesOfDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var rows = await db.Overrides.AsNoTracking().Where(o => o.DocumentId == documentId).OrderBy(o => o.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(ToStored)];
    }

    public async Task<IReadOnlyList<QuoteResult>> ResultsAsync(IReadOnlyCollection<string> documentIds, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var ids = documentIds.ToList();
        var documents = await db.Documents.AsNoTracking().Where(d => ids.Contains(d.Id) && d.ResultJson != null).OrderBy(d => d.Id)
            .Select(d => new { d.Id, d.ResultJson }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var overrides = (await db.Overrides.AsNoTracking().Where(o => ids.Contains(o.DocumentId)).OrderBy(o => o.Id).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(o => o.DocumentId, StringComparer.Ordinal);
        return [.. documents.Select(d => new QuoteResult(JsonSerializer.Deserialize<DocumentResult>(d.ResultJson!, CanonicalJson.Options)!, [.. overrides[d.Id].Select(ToStored)]))];
    }

    public async Task<IReadOnlyList<QuoteResult>> QuoteResultsAsync(string quoteId, CancellationToken cancellationToken = default, IReadOnlyCollection<string>? only = null)
    {
        await using var db = contexts();
        var ids = only?.ToList();
        var documents = await db.Documents.AsNoTracking().Where(d => d.QuoteId == quoteId && d.ResultJson != null && (ids == null || ids.Contains(d.Id))).OrderBy(d => d.Id)
            .Select(d => new { d.Id, d.ResultJson }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var overrides = (await db.Overrides.AsNoTracking()
            .Where(o => db.Documents.Any(d => d.Id == o.DocumentId && d.QuoteId == quoteId) && (ids == null || ids.Contains(o.DocumentId))).OrderBy(o => o.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToLookup(o => o.DocumentId, StringComparer.Ordinal);
        return [.. documents.Select(d => new QuoteResult(JsonSerializer.Deserialize<DocumentResult>(d.ResultJson!, CanonicalJson.Options)!, [.. overrides[d.Id].Select(ToStored)]))];
    }

    private static StoredOverride ToStored(OverrideRow o) => new(o.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), o.DocumentId, o.FieldPath, o.OldValue,
        o.NewValue, o.Rationale, o.UserId, new DateTimeOffset(o.AtUtc, TimeSpan.Zero), o.Role,
        o.SupersedesOverrideId?.ToString(System.Globalization.CultureInfo.InvariantCulture), o.DocumentRevision);

    public async Task<StoredFeatureSnapshot?> GetFeatureSnapshotAsync(FeatureSnapshotKey key, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var row = await db.FeatureSnapshots.AsNoTracking()
            .Where(f => f.QuoteId == key.QuoteId && f.RevisionKey == key.RevisionKey && f.CatalogVersion == key.CatalogVersion && f.View == key.View
                && f.ScenarioHash == key.ScenarioHash && f.InvalidatedUtc == null)
            .OrderByDescending(f => f.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : new StoredFeatureSnapshot(key, row.InputsHash, row.ClaimCount, row.SnapshotJson, new DateTimeOffset(row.CreatedUtc, TimeSpan.Zero));
    }

    public async Task SaveFeatureSnapshotAsync(StoredFeatureSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var k = snapshot.Key;
        db.FeatureSnapshots.Add(new FeatureSnapshotRow
        {
            QuoteId = k.QuoteId, RevisionKey = k.RevisionKey, CatalogVersion = k.CatalogVersion, View = k.View, ScenarioHash = k.ScenarioHash,
            InputsHash = snapshot.InputsHash, ClaimCount = snapshot.ClaimCount, SnapshotJson = snapshot.Json, CreatedUtc = snapshot.CreatedUtc.UtcDateTime,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ workbench (brief 11)

    public async Task<QuoteContext?> GetQuoteContextAsync(string quoteId, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var row = await db.QuoteContexts.AsNoTracking().SingleOrDefaultAsync(q => q.QuoteId == quoteId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : JsonSerializer.Deserialize<QuoteContext>(row.ContextJson, CanonicalJson.Options)! with
        {
            QuoteId = quoteId, UpdatedAt = new DateTimeOffset(row.UpdatedUtc, TimeSpan.Zero), UpdatedBy = row.UpdatedBy,
        };
    }

    public Task SaveQuoteContextAsync(QuoteContext context, Actor actor, CancellationToken cancellationToken = default) =>
        commands.SaveQuoteContextAsync(context.QuoteId, JsonSerializer.Serialize(context with { UpdatedAt = null, UpdatedBy = null }, CanonicalJson.Options), actor,
            (context.UpdatedAt ?? DateTimeOffset.UtcNow).UtcDateTime, cancellationToken);

    // ------------------------------------------------------------------ engineering escalation (brief 26)

    public async Task<Escalation?> GetEscalationAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var row = await db.Escalations.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToEscalation(row);
    }

    public async Task<IReadOnlyList<Escalation>> ListEscalationsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var rows = await db.Escalations.AsNoTracking().OrderByDescending(e => e.LastSeenUtc).ThenBy(e => e.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(ToEscalation)];
    }

    public Task<bool> SaveEscalationAsync(Escalation escalation, int expectedVersion, AuditEvent audit, Actor actor, IReadOnlyList<OutboxMessage> outbox,
        CancellationToken cancellationToken = default) =>
        commands.SaveEscalationAsync(new EscalationRow
        {
            Id = escalation.Id, ReasonCode = escalation.ReasonCode, FormatFingerprint = escalation.FormatFingerprint, FormatRef = escalation.FormatRef, Carrier = escalation.Carrier,
            DocumentIdsJson = JsonSerializer.Serialize(escalation.DocumentIds), UploadIdsJson = JsonSerializer.Serialize(escalation.UploadIds),
            FirstSeenUtc = escalation.FirstSeen.UtcDateTime, LastSeenUtc = escalation.LastSeen.UtcDateTime, Count = escalation.Count,
            TraceSummaryJson = EscalationLedger.TraceJson(escalation.TraceSummary), Status = escalation.Status, ResolutionNote = escalation.ResolutionNote,
            TuningItemRef = escalation.TuningItemRef, Version = expectedVersion + 1, UpdatedUtc = escalation.UpdatedAt.UtcDateTime, UpdatedBy = escalation.UpdatedBy,
        }, expectedVersion, audit, actor, outbox, cancellationToken);

    private static Escalation ToEscalation(EscalationRow r) => new()
    {
        Id = r.Id, ReasonCode = r.ReasonCode, FormatFingerprint = r.FormatFingerprint, FormatRef = r.FormatRef, Carrier = r.Carrier,
        DocumentIds = JsonSerializer.Deserialize<List<string>>(r.DocumentIdsJson) ?? [], UploadIds = JsonSerializer.Deserialize<List<string>>(r.UploadIdsJson) ?? [],
        FirstSeen = new DateTimeOffset(r.FirstSeenUtc, TimeSpan.Zero), LastSeen = new DateTimeOffset(r.LastSeenUtc, TimeSpan.Zero), Count = r.Count,
        TraceSummary = JsonSerializer.Deserialize<EscalationTrace>(r.TraceSummaryJson, CanonicalJson.Options) ?? new(), Status = r.Status, ResolutionNote = r.ResolutionNote,
        TuningItemRef = r.TuningItemRef, Version = r.Version, UpdatedAt = new DateTimeOffset(r.UpdatedUtc, TimeSpan.Zero), UpdatedBy = r.UpdatedBy,
    };

    private sealed class ObservationRow
    {
        public string DocumentId { get; set; } = "";
        public string UploadId { get; set; } = "";
        public string? Carrier { get; set; }
        public string? Fingerprint { get; set; }
        public string? FormatRef { get; set; }
        public DateTime ProcessedUtc { get; set; }
        public bool Reviewed { get; set; }
        public string? FailedRuleIds { get; set; }
        public int Reopens { get; set; }
    }

    public async Task<IReadOnlyList<EscalationObservation>> EscalationObservationsAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var from = since.UtcDateTime;
        var rows = await db.Database.SqlQuery<ObservationRow>($"""
            SELECT d.Id AS DocumentId, d.UploadId, d.CarrierName AS Carrier, JSON_VALUE(d.ResultJson, '$.routing.fingerprintKey') AS Fingerprint, d.FormatRef,
                   d.CreatedUtc AS ProcessedUtc, CAST(CASE WHEN r.Kind = N'document-review' THEN 1 ELSE 0 END AS bit) AS Reviewed,
                   (SELECT STRING_AGG(x.RuleId, N',') FROM (SELECT DISTINCT o.RuleId FROM dbo.RuleOutcome o
                        WHERE o.DocumentId = d.Id AND o.DocumentRevision = d.Revision AND o.Status = N'Fail') x) AS FailedRuleIds,
                   (SELECT COUNT(*) FROM dbo.AuditEvent a WHERE a.DocumentId = d.Id AND a.Action = N'review.reopened') AS Reopens
            FROM dbo.Document d LEFT JOIN dbo.ReviewItem r ON r.DocumentId = d.Id
            WHERE d.CreatedUtc >= {from}
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new EscalationObservation(r.DocumentId, r.UploadId, r.Carrier, r.Fingerprint, r.FormatRef, new DateTimeOffset(r.ProcessedUtc, TimeSpan.Zero),
            r.Reviewed, r.FailedRuleIds is { Length: > 0 } ids ? ids.Split(',') : [], r.Reopens))];
    }

    public async Task<QuoteOwner?> GetQuoteOwnerAsync(string quoteId, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var row = await db.QuoteOwners.AsNoTracking().SingleOrDefaultAsync(q => q.QuoteId == quoteId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : new QuoteOwner(row.QuoteId, row.ApplicantUserId, new DateTimeOffset(DateTime.SpecifyKind(row.CreatedUtc, DateTimeKind.Utc)), row.CreatedBy);
    }

    public async Task<QuoteOwner> ClaimQuoteOwnerAsync(QuoteOwner owner, CancellationToken cancellationToken = default)
    {
        await using (var db = contexts())
        {
            // Insert-if-absent under a key-range lock: two first uploads of one quote cannot both win.
            await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO dbo.QuoteOwner (QuoteId, ApplicantUserId, CreatedUtc, CreatedBy)
SELECT {owner.QuoteId}, {owner.ApplicantUserId}, {owner.CreatedAt.UtcDateTime}, {owner.CreatedBy}
WHERE NOT EXISTS (SELECT 1 FROM dbo.QuoteOwner WITH (UPDLOCK, HOLDLOCK) WHERE QuoteId = {owner.QuoteId});", cancellationToken).ConfigureAwait(false);
        }

        return await GetQuoteOwnerAsync(owner.QuoteId, cancellationToken).ConfigureAwait(false) ?? owner;
    }

    public async Task<QuoteLabel?> GetQuoteLabelAsync(string quoteId, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var row = await db.QuoteLabels.AsNoTracking().SingleOrDefaultAsync(q => q.QuoteId == quoteId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToLabel(row);
    }

    private static QuoteLabel ToLabel(QuoteLabelRow row) => new(row.QuoteId, row.DisplayName,
        row.RetiredUtc is { } retired ? new DateTimeOffset(retired, TimeSpan.Zero) : null, row.RetiredBy, new DateTimeOffset(row.UpdatedUtc, TimeSpan.Zero), row.UpdatedBy);

    public Task SaveQuoteLabelAsync(QuoteLabel label, string rationale, Actor actor, CancellationToken cancellationToken = default) =>
        commands.SaveQuoteLabelAsync(label, rationale, actor, cancellationToken);

    public Task SetQuoteOwnerAsync(QuoteOwner owner, string rationale, Actor actor, CancellationToken cancellationToken = default) =>
        commands.SetQuoteOwnerAsync(owner, rationale, actor, cancellationToken);

    public async Task<IReadOnlyList<QuoteSummary>> ListQuotesAsync(CancellationToken cancellationToken = default, bool includeRetired = false)
    {
        await using var db = contexts();
        var labels = (await db.QuoteLabels.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(l => l.QuoteId, ToLabel, StringComparer.Ordinal);
        var quotes = await db.Documents.AsNoTracking().Where(d => d.QuoteId != null)
            .GroupBy(d => d.QuoteId!)
            .Select(g => new
            {
                QuoteId = g.Key, Documents = g.Count(), Insured = g.Max(d => d.InsuredName), Valuation = g.Max(d => d.ValuationDate),
                Pending = g.Count(d => d.ReviewState == "pending"), Updated = g.Max(d => d.UpdatedUtc),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var claims = await db.Claims.AsNoTracking()
            .Join(db.Documents.Where(d => d.QuoteId != null), c => c.DocumentId, d => d.Id, (c, d) => d.QuoteId!)
            .GroupBy(q => q).Select(g => new { QuoteId = g.Key, Claims = g.Count() })
            .ToDictionaryAsync(x => x.QuoteId, x => x.Claims, cancellationToken).ConfigureAwait(false);
        return [.. quotes.OrderByDescending(q => q.Updated)
            .Select(q => (Quote: q, Label: labels.GetValueOrDefault(q.QuoteId)))
            .Where(x => includeRetired || x.Label?.RetiredAt is null)
            .Select(x => new QuoteSummary(x.Quote.QuoteId, x.Quote.Documents, claims.GetValueOrDefault(x.Quote.QuoteId), x.Quote.Insured,
                x.Quote.Valuation, x.Quote.Pending, new DateTimeOffset(x.Quote.Updated, TimeSpan.Zero), x.Label?.DisplayName, x.Label?.RetiredAt))];
    }

    public async Task<AuditTrailView> AuditTrailAsync(string? documentId, string? quoteId, int max, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var query = db.AuditTrail.AsNoTracking();
        if (documentId is not null)
        {
            query = query.Where(a => a.DocumentId == documentId);
        }
        else if (quoteId is not null)
        {
            var path = QuoteAudit.FieldPath(quoteId);
            var documents = db.Documents.Where(d => d.QuoteId == quoteId).Select(d => d.Id);
            query = query.Where(a => a.FieldPath == path || documents.Contains(a.DocumentId!));
        }

        var rows = await query.OrderByDescending(a => a.Id).Take(max).ToListAsync(cancellationToken).ConfigureAwait(false);
        var digest = await db.LedgerDigests.AsNoTracking().OrderByDescending(d => d.GeneratedUtc).Select(d => (DateTime?)d.GeneratedUtc)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return new AuditTrailView([.. rows.Select(a => new AuditTrailEntry(a.Id, new DateTimeOffset(a.AtUtc, TimeSpan.Zero), a.UserId, a.Role, a.Action, a.UploadId,
                a.DocumentId, a.DocumentRevision, a.FieldPath, a.OldValue, a.NewValue, a.Rationale, a.LedgerTransactionId,
                a.CommittedUtc is { } c ? new DateTimeOffset(c, TimeSpan.Zero) : null, Principal(a.CommittedBy)))],
            "ledger", digest is { } g ? new DateTimeOffset(g, TimeSpan.Zero) : null);
    }

    /// <summary>
    /// The ledger's committing principal as shown to users: an Entra workload identity is printed by the engine as
    /// <c>objectId@tenantId</c>, which is not shown (tenant ids stay out of the API); a named login is kept.
    /// </summary>
    private static string? Principal(string? name) =>
        name is { } n && n.Split('@') is [var oid, var tid] && Guid.TryParse(oid, out _) && Guid.TryParse(tid, out _) ? "app managed identity" : name;

    public async Task<IReadOnlyList<DocumentCostLine>> DocumentCostsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var rows = await db.DocumentCosts.AsNoTracking()
            .GroupJoin(db.Documents, c => c.DocumentId, d => d.Id, (c, d) => new { Cost = c, Documents = d })
            .SelectMany(x => x.Documents.DefaultIfEmpty(), (x, d) => new { x.Cost, Carrier = d == null ? null : d.CarrierName })
            .OrderBy(x => x.Cost.ComputedUtc).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(x => new DocumentCostLine(x.Cost.DocumentId, x.Carrier, x.Cost.Path, x.Cost.CostUsd, x.Cost.Pages, x.Cost.LlmCalls,
            new DateTimeOffset(x.Cost.ComputedUtc, TimeSpan.Zero), JsonSerializer.Deserialize<List<CostLine>>(x.Cost.BreakdownJson, CanonicalJson.Options) ?? []))];
    }

    // ------------------------------------------------------------------ chat (brief 12)

    public async Task<IReadOnlyList<ScopedDocument>> DocumentsInScopeAsync(string scope, string? quoteId, string? workspaceId,
        CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        // Parameterized (FromSqlInterpolated); the workspace lives in the upload's submission JSON. Brief 24 step 2: a rejected
        // document is out of every chat scope (its audit trail stays on the document).
        var rows = scope switch
        {
            ChatScopes.Quote => db.ScopedDocuments.FromSqlInterpolated($@"SELECT d.Id, d.UploadId, d.QuoteId, JSON_VALUE(u.SubmissionJson, '$.workspaceId') AS WorkspaceId, d.Revision, ui.FileName
                FROM dbo.Document AS d JOIN dbo.Upload AS u ON u.Id = d.UploadId LEFT JOIN dbo.UploadItem AS ui ON ui.DocumentId = d.Id
                WHERE d.QuoteId = {quoteId} AND JSON_VALUE(u.SubmissionJson, '$.workspaceId') IS NULL AND ISNULL(d.ReviewState, '') <> 'reject'"),
            ChatScopes.Workspace => db.ScopedDocuments.FromSqlInterpolated($@"SELECT d.Id, d.UploadId, d.QuoteId, JSON_VALUE(u.SubmissionJson, '$.workspaceId') AS WorkspaceId, d.Revision, ui.FileName
                FROM dbo.Document AS d JOIN dbo.Upload AS u ON u.Id = d.UploadId LEFT JOIN dbo.UploadItem AS ui ON ui.DocumentId = d.Id
                WHERE JSON_VALUE(u.SubmissionJson, '$.workspaceId') = {workspaceId} AND ISNULL(d.ReviewState, '') <> 'reject'"),
            ChatScopes.Org => db.ScopedDocuments.FromSqlRaw(@"SELECT d.Id, d.UploadId, d.QuoteId, CAST(NULL AS nvarchar(64)) AS WorkspaceId, d.Revision, ui.FileName
                FROM dbo.Document AS d JOIN dbo.Upload AS u ON u.Id = d.UploadId LEFT JOIN dbo.UploadItem AS ui ON ui.DocumentId = d.Id
                WHERE JSON_VALUE(u.SubmissionJson, '$.workspaceId') IS NULL AND ISNULL(d.ReviewState, '') <> 'reject'"),
            _ => throw new ArgumentException($"Unknown chat scope '{scope}'.", nameof(scope)),
        };
        var list = await rows.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. list.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => new ScopedDocument(r.Id, r.UploadId, r.QuoteId, r.WorkspaceId, r.Revision, r.FileName))];
    }

    public Task SaveWorkspaceAsync(ChatWorkspace workspace, Actor actor, string action, CancellationToken cancellationToken = default, string? detail = null) =>
        commands.SaveWorkspaceAsync(workspace, actor, action, cancellationToken, detail);

    public async Task<IReadOnlyList<string>> UploadsOfWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        return await db.Database.SqlQuery<string>($"SELECT Id AS Value FROM dbo.Upload WHERE JSON_VALUE(SubmissionJson, '$.workspaceId') = {workspaceId}")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> UploadsOfQuoteAsync(string quoteId, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        return await db.Uploads.Where(u => u.QuoteId == quoteId).Select(u => u.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChatWorkspace?> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var row = await db.Workspaces.AsNoTracking().SingleOrDefaultAsync(w => w.Id == workspaceId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToWorkspace(row);
    }

    public async Task<IReadOnlyList<ChatWorkspace>> ExpiredWorkspacesAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var at = now.UtcDateTime;
        var rows = await db.Workspaces.AsNoTracking().Where(w => w.DeletedUtc == null && w.ExpiresUtc <= at).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(ToWorkspace)];
    }

    private static ChatWorkspace ToWorkspace(WorkspaceRow r) => new(r.Id, r.OwnerUserId, r.Name, new DateTimeOffset(r.CreatedUtc, TimeSpan.Zero),
        new DateTimeOffset(r.ExpiresUtc, TimeSpan.Zero), r.DeletedUtc is { } d ? new DateTimeOffset(d, TimeSpan.Zero) : null);

    public Task SaveChatTurnAsync(ChatTurnResult turn, Actor actor, CancellationToken cancellationToken = default) =>
        commands.SaveChatTurnAsync(new ChatTurnRow
        {
            Id = turn.TurnId, ConversationId = turn.ConversationId, AtUtc = turn.At.UtcDateTime, UserId = turn.UserId, Role = turn.Role, Scope = turn.Scope,
            QuoteId = turn.QuoteId, WorkspaceId = turn.WorkspaceId, Question = turn.Question.Length > 4000 ? turn.Question[..4000] : turn.Question, Answer = turn.Text,
            NotFound = turn.NotFound, CitationsJson = JsonSerializer.Serialize(turn.Citations, CanonicalJson.Options),
            ToolTraceJson = JsonSerializer.Serialize(turn.ToolTrace, CanonicalJson.Options), InputTokens = turn.Usage.InputTokens,
            CachedInputTokens = turn.Usage.CachedInputTokens, OutputTokens = turn.Usage.OutputTokens, ModelCalls = turn.Usage.ModelCalls,
            Embeddings = turn.Usage.Embeddings, SearchQueries = turn.Usage.SearchQueries, CostUsd = turn.Usage.CostUsd, FirstTokenMs = turn.Usage.FirstTokenMs,
            TotalMs = turn.Usage.TotalMs, UsageJson = JsonSerializer.Serialize(turn.Usage, CanonicalJson.Options),
        }, actor, cancellationToken);

    public async Task<IReadOnlyList<ChatTurnResult>> ConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var rows = await db.ChatTurns.AsNoTracking().Where(t => t.ConversationId == conversationId).OrderBy(t => t.AtUtc).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(r => new ChatTurnResult(r.Id, r.ConversationId, r.UserId, r.Role, r.Scope, r.QuoteId, r.WorkspaceId, r.Question, r.Answer, r.NotFound,
            JsonSerializer.Deserialize<List<ChatCitation>>(r.CitationsJson, CanonicalJson.Options) ?? [],
            JsonSerializer.Deserialize<List<ChatToolCall>>(r.ToolTraceJson, CanonicalJson.Options) ?? [],
            JsonSerializer.Deserialize<ChatTurnUsage>(r.UsageJson, CanonicalJson.Options)!, new DateTimeOffset(r.AtUtc, TimeSpan.Zero)))];
    }

    public async Task<IReadOnlyList<UsageEvent>> ReadUsageAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        await using var db = contexts();
        var rows = await db.UsageEvents.AsNoTracking().Where(u => u.RunId == uploadId).OrderBy(u => u.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(u => new UsageEvent(u.DocumentId, u.RunId, u.Stage, u.Service, u.Meter, u.Quantity, u.Unit, u.ModelOrAnalyzer, u.DeploymentType, u.Region,
            new DateTimeOffset(u.TimestampUtc, TimeSpan.Zero), u.CorrelationId, u.ModelVersion))];
    }
}

/// <summary>UploadManifest ↔ dbo.Upload / dbo.UploadItem.</summary>
public static class Manifests
{
    public static UploadManifest From(UploadRow row) => new()
    {
        UploadId = row.Id, Status = row.Status, CreatedAt = Utc(row.CreatedUtc), CompletedAt = row.CompletedUtc is { } c ? Utc(c) : null,
        Submission = JsonSerializer.Deserialize<SubmissionInfo>(row.SubmissionJson, CanonicalJson.Options)!,
        Files = JsonSerializer.Deserialize<List<ManifestFile>>(row.FilesJson, CanonicalJson.Options)!,
        Items = [.. row.Items.OrderBy(i => i.Ordinal).Select(i => new ManifestItem
        {
            DocumentId = i.DocumentId, FileName = i.FileName, ParentDocumentId = i.ParentDocumentId, PageRange = i.PageRange, Classification = i.Classification,
            Status = i.Status, Reason = i.Reason, BlobPath = i.BlobPath, RoutingTier = i.RoutingTier, Badge = i.Badge, FormatRef = i.FormatRef, FormatStatus = i.FormatStatus,
            ClaimCount = i.ClaimCount, CostUsd = i.CostUsd, ReviewKind = i.ReviewKind,
            KeptRows = i.KeptRowsJson is null ? null : JsonSerializer.Deserialize<List<KeptRowItem>>(i.KeptRowsJson, CanonicalJson.Options),
            UnnormalizedValues = i.UnnormalizedValues,
        })],
        Cost = row.CostJson is null ? null : JsonSerializer.Deserialize<UploadCost>(row.CostJson, CanonicalJson.Options), FirstClaimMs = row.FirstClaimMs,
        CompletedMs = row.CompletedMs,
    };

    public static void Apply(UploadManifest m, UploadRow row)
    {
        row.Status = m.Status;
        row.CreatedUtc = m.CreatedAt.UtcDateTime;
        row.CompletedUtc = m.CompletedAt?.UtcDateTime;
        row.QuoteId = m.Submission.QuoteId;
        row.CarrierHint = m.Submission.CarrierHint;
        row.UploaderId = m.Submission.UploaderId;
        row.UploaderRole = m.Submission.UploaderRole;
        row.ValuationDate = m.Submission.ValuationDate;
        row.SubmissionJson = JsonSerializer.Serialize(m.Submission, CanonicalJson.Options);
        row.FilesJson = JsonSerializer.Serialize(m.Files, CanonicalJson.Options);
        row.CostUsd = m.Cost?.TotalUsd;
        row.CostJson = m.Cost is null ? null : JsonSerializer.Serialize(m.Cost, CanonicalJson.Options);
        row.FirstClaimMs = m.FirstClaimMs;
        row.CompletedMs = m.CompletedMs;

        var wanted = m.Items.Select((item, ordinal) => (item, ordinal)).ToDictionary(x => x.item.DocumentId, StringComparer.Ordinal);
        row.Items.RemoveAll(i => !wanted.ContainsKey(i.DocumentId));
        foreach (var (item, ordinal) in wanted.Values)
        {
            var target = row.Items.FirstOrDefault(i => i.DocumentId == item.DocumentId);
            if (target is null)
            {
                target = new UploadItemRow { DocumentId = item.DocumentId, UploadId = row.Id };
                row.Items.Add(target);
            }

            (target.Ordinal, target.FileName, target.ParentDocumentId, target.PageRange, target.Classification, target.Status, target.Reason, target.BlobPath) =
                (ordinal, item.FileName, item.ParentDocumentId, item.PageRange, item.Classification, item.Status, Clip(item.Reason), item.BlobPath);
            (target.RoutingTier, target.Badge, target.FormatRef, target.FormatStatus, target.ClaimCount, target.CostUsd, target.ReviewKind) =
                (item.RoutingTier, item.Badge, item.FormatRef, item.FormatStatus, item.ClaimCount, item.CostUsd, item.ReviewKind);
            target.KeptRowsJson = item.KeptRows is { Count: > 0 } kept ? JsonSerializer.Serialize(kept, CanonicalJson.Options) : null;
            target.UnnormalizedValues = item.UnnormalizedValues;
        }
    }

    internal static string? Clip(string? s) => s is { Length: > 2000 } ? s[..2000] : s;

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

/// <summary>ReviewItem ↔ dbo.ReviewItem.</summary>
public static class Reviews
{
    public static ReviewItem From(ReviewItemRow r) => new()
    {
        DocumentId = r.DocumentId, UploadId = r.UploadId, Kind = r.Kind, State = r.State, Reasons = JsonSerializer.Deserialize<List<string>>(r.ReasonsJson) ?? [],
        Badge = r.Badge, FormatRef = r.FormatRef, OpenedAt = new DateTimeOffset(DateTime.SpecifyKind(r.OpenedUtc, DateTimeKind.Utc)),
        DecidedAt = r.DecidedUtc is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)) : null,
        Decision = r.DecisionJson is null ? null : JsonSerializer.Deserialize<ReviewDecision>(r.DecisionJson, CanonicalJson.Options), Reopened = r.Reopened,
    };

    public static ReviewItemRow To(ReviewItem i) => new()
    {
        DocumentId = i.DocumentId, UploadId = i.UploadId, Kind = i.Kind, State = i.State, ReasonsJson = JsonSerializer.Serialize(i.Reasons), Badge = i.Badge,
        FormatRef = i.FormatRef, OpenedUtc = i.OpenedAt.UtcDateTime, DecidedUtc = i.DecidedAt?.UtcDateTime,
        DecisionJson = i.Decision is null ? null : JsonSerializer.Serialize(i.Decision, CanonicalJson.Options), Reopened = i.Reopened,
    };
}
