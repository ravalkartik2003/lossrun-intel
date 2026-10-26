using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;

namespace LossRun.Orchestration;

/// <summary>
/// The brief-07 Blob layout behind <see cref="IRecordStore"/>: manifests, results, reviews and the audit JSONL in the
/// <c>extractions</c> container. Used for local runs and tests; with <c>Sql:ConnectionString</c> the host uses
/// <see cref="SqlRecordStore"/> instead (brief 09) and Blob keeps originals, graphs and raw CU JSON.
/// </summary>
public sealed class BlobRecordStore(IObjectStore store) : IRecordStore
{
    public Task<UploadManifest?> GetManifestAsync(string uploadId, CancellationToken cancellationToken = default) =>
        store.GetJsonAsync<UploadManifest>(Containers.Extractions, StorePaths.Manifest(uploadId), cancellationToken);

    public Task<UploadManifest> UpdateManifestAsync(string uploadId, Func<UploadManifest?, UploadManifest> change, Actor actor, CancellationToken cancellationToken = default) =>
        store.UpdateAsync<UploadManifest>(Containers.Extractions, StorePaths.Manifest(uploadId), change, cancellationToken);

    public Task IndexDocumentAsync(string documentId, string uploadId, CancellationToken cancellationToken = default) =>
        store.PutJsonAsync(Containers.Extractions, StorePaths.DocumentIndex(documentId), new DocumentIndexEntry(uploadId), cancellationToken);

    public async Task<string?> UploadOfDocumentAsync(string documentId, CancellationToken cancellationToken = default) =>
        (await store.GetJsonAsync<DocumentIndexEntry>(Containers.Extractions, StorePaths.DocumentIndex(documentId), cancellationToken).ConfigureAwait(false))?.UploadId;

    public async Task<DocumentHead?> DocumentHeadAsync(string documentId, CancellationToken cancellationToken = default)
    {
        if (await UploadOfDocumentAsync(documentId, cancellationToken).ConfigureAwait(false) is not { } uploadId
            || await GetResultAsync(documentId, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        var review = await GetReviewAsync(documentId, cancellationToken).ConfigureAwait(false);
        return new DocumentHead(documentId, uploadId, await QuoteOfUploadAsync(uploadId, cancellationToken).ConfigureAwait(false), review?.State, review?.Kind);
    }

    public async Task<DocumentResult?> GetResultAsync(string documentId, CancellationToken cancellationToken = default) =>
        await UploadOfDocumentAsync(documentId, cancellationToken).ConfigureAwait(false) is { } uploadId
            ? await store.GetJsonAsync<DocumentResult>(Containers.Extractions, StorePaths.Document(uploadId, documentId, "result.json"), cancellationToken).ConfigureAwait(false)
            : null;

    public async Task SaveResultAsync(DocumentResult result, Actor actor, CancellationToken cancellationToken = default)
    {
        await store.PutJsonAsync(Containers.Extractions, StorePaths.Document(result.UploadId, result.DocumentId, "result.json"), result, cancellationToken).ConfigureAwait(false);
        if (await QuoteOfUploadAsync(result.UploadId, cancellationToken).ConfigureAwait(false) is { } quoteId)
        {
            await store.PutJsonAsync(Containers.Extractions, StorePaths.QuoteDocument(quoteId, result.DocumentId), new DocumentIndexEntry(result.UploadId), cancellationToken)
                .ConfigureAwait(false);
            await BumpFeatureGenerationAsync(quoteId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> QuoteOfUploadAsync(string uploadId, CancellationToken cancellationToken) =>
        (await GetManifestAsync(uploadId, cancellationToken).ConfigureAwait(false))?.Submission.QuoteId;

    private Task BumpFeatureGenerationAsync(string quoteId, CancellationToken cancellationToken) =>
        store.UpdateAsync<FeatureGeneration>(Containers.Extractions, StorePaths.FeatureGeneration(quoteId), g => new FeatureGeneration((g?.Generation ?? 0) + 1), cancellationToken);

    private async Task<long> FeatureGenerationAsync(string quoteId, CancellationToken cancellationToken) =>
        (await store.GetJsonAsync<FeatureGeneration>(Containers.Extractions, StorePaths.FeatureGeneration(quoteId), cancellationToken).ConfigureAwait(false))?.Generation ?? 0;

    public async Task<IReadOnlyList<QuoteDocument>> DocumentsOfQuoteAsync(string quoteId, CancellationToken cancellationToken = default)
    {
        var documents = new List<QuoteDocument>();
        foreach (var id in (await store.ListAsync(Containers.Extractions, StorePaths.QuoteDocuments(quoteId), cancellationToken).ConfigureAwait(false))
                     .Select(p => Path.GetFileNameWithoutExtension(p)).Order(StringComparer.Ordinal))
        {
            var result = await GetResultAsync(id, cancellationToken).ConfigureAwait(false);
            documents.Add(new QuoteDocument(id, result?.Revision ?? 0, result?.ReviewState));
        }

        return documents;
    }

    public async Task<IReadOnlyList<StoredOverride>> OverridesOfDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        var text = (await store.GetAsync(Containers.Extractions, StorePaths.Overrides(documentId), cancellationToken).ConfigureAwait(false))?.ToString() ?? "";
        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonSerializer.Deserialize<StoredOverride>(l, CanonicalJson.Options)!)];
    }

    public async Task<IReadOnlyList<QuoteResult>> ResultsAsync(IReadOnlyCollection<string> documentIds, CancellationToken cancellationToken = default)
    {
        var list = new List<QuoteResult>();
        foreach (var id in documentIds)
        {
            if (await GetResultAsync(id, cancellationToken).ConfigureAwait(false) is { } result)
            {
                list.Add(new QuoteResult(result, await OverridesOfDocumentAsync(id, cancellationToken).ConfigureAwait(false)));
            }
        }

        return list;
    }

    public async Task<IReadOnlyList<QuoteResult>> QuoteResultsAsync(string quoteId, CancellationToken cancellationToken = default, IReadOnlyCollection<string>? only = null)
    {
        var list = new List<QuoteResult>();
        foreach (var d in (await DocumentsOfQuoteAsync(quoteId, cancellationToken).ConfigureAwait(false)).Where(d => only is null || only.Contains(d.DocumentId)))
        {
            if (await GetResultAsync(d.DocumentId, cancellationToken).ConfigureAwait(false) is { } result)
            {
                list.Add(new QuoteResult(result, await OverridesOfDocumentAsync(d.DocumentId, cancellationToken).ConfigureAwait(false)));
            }
        }

        return list;
    }

    public async Task<StoredFeatureSnapshot?> GetFeatureSnapshotAsync(FeatureSnapshotKey key, CancellationToken cancellationToken = default)
    {
        var cached = await store.GetJsonAsync<BlobFeatureSnapshot>(Containers.Extractions, StorePaths.FeatureSnapshot(key.QuoteId, KeyHash(key)), cancellationToken)
            .ConfigureAwait(false);
        return cached is not null && cached.Generation == await FeatureGenerationAsync(key.QuoteId, cancellationToken).ConfigureAwait(false) ? cached.Snapshot : null;
    }

    public async Task SaveFeatureSnapshotAsync(StoredFeatureSnapshot snapshot, CancellationToken cancellationToken = default) =>
        await store.PutJsonAsync(Containers.Extractions, StorePaths.FeatureSnapshot(snapshot.Key.QuoteId, KeyHash(snapshot.Key)),
            new BlobFeatureSnapshot(await FeatureGenerationAsync(snapshot.Key.QuoteId, cancellationToken).ConfigureAwait(false), snapshot), cancellationToken).ConfigureAwait(false);

    private static string KeyHash(FeatureSnapshotKey key) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{key.RevisionKey}|{key.CatalogVersion}|{key.View}|{key.ScenarioHash}")))[..32];

    public Task<ReviewItem?> GetReviewAsync(string documentId, CancellationToken cancellationToken = default) =>
        store.GetJsonAsync<ReviewItem>(Containers.Extractions, StorePaths.Review(documentId), cancellationToken);

    public async Task<IReadOnlyList<ReviewItem>> ListReviewsAsync(string state, CancellationToken cancellationToken = default)
    {
        var items = new List<ReviewItem>();
        foreach (var path in await store.ListAsync(Containers.Extractions, "reviews/", cancellationToken).ConfigureAwait(false))
        {
            if (await store.GetJsonAsync<ReviewItem>(Containers.Extractions, path, cancellationToken).ConfigureAwait(false) is { } item && (state == "all" || item.State == state))
            {
                items.Add(item);
            }
        }

        return [.. items.OrderBy(i => i.OpenedAt)];
    }

    public async Task ReopenReviewAsync(ReviewItem item, DocumentResult result, Actor actor, string reason, CancellationToken cancellationToken = default)
    {
        await store.PutJsonAsync(Containers.Extractions, StorePaths.Review(item.DocumentId), item, cancellationToken).ConfigureAwait(false);
        await SaveResultAsync(result, actor, cancellationToken).ConfigureAwait(false);
        await AppendAsync(new AuditEvent(item.OpenedAt, actor.UserId, actor.Role, "review.reopened", item.UploadId, item.DocumentId, Rationale: reason), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Blob mode (dev and tests) has no transaction; the revision and the decision's keyed outbox row are checked first (not atomic).</summary>
    public async Task<bool> TryApplyDecisionAsync(DecisionRecord record, CancellationToken cancellationToken = default, IReadOnlyList<OutboxMessage>? outbox = null) =>
        await ApplyDecisionAsync(record, cancellationToken, outbox).ConfigureAwait(false) != DecisionApplyResult.Conflict;

    // ------------------------------------------------------------------ quote revision (brief 24 step 3; dev/tests: not transactional)

    private sealed record BlobQuoteRevision(string QuoteId, int Revision, DateTimeOffset ChangedAt, string Cause, string? DocumentId, int? DocumentRevision);

    public async Task<int> QuoteRevisionAsync(string quoteId, CancellationToken cancellationToken = default) =>
        (await store.GetJsonAsync<BlobQuoteRevision>(Containers.Extractions, QuoteRevisionPath(quoteId), cancellationToken).ConfigureAwait(false))?.Revision ?? 0;

    private async Task<int> BumpAsync(QuoteRevisionBump bump, DateTimeOffset at, CancellationToken cancellationToken) =>
        (await store.UpdateAsync<BlobQuoteRevision>(Containers.Extractions, QuoteRevisionPath(bump.QuoteId),
            current => new BlobQuoteRevision(bump.QuoteId, (current?.Revision ?? 0) + 1, at, bump.Cause, bump.DocumentId, bump.DocumentRevision), cancellationToken)
            .ConfigureAwait(false)).Revision;

    private static string QuoteRevisionPath(string quoteId) => $"quotes/{Uri.EscapeDataString(quoteId)}/revision.json";

    // ------------------------------------------------------------------ outbox (dev/tests: not transactional with the audit append)

    public async Task AuditWithOutboxAsync(AuditEvent audit, Actor actor, IReadOnlyList<OutboxMessage> outbox, CancellationToken cancellationToken = default,
        QuoteRevisionBump? bump = null)
    {
        if (bump is not null)
        {
            outbox = QuoteRevisions.Apply(outbox, await BumpAsync(bump, audit.At, cancellationToken).ConfigureAwait(false));
        }

        foreach (var o in outbox)
        {
            await store.PutJsonAsync(Containers.Extractions, StorePaths.Outbox(o.Id), o, cancellationToken).ConfigureAwait(false);
        }

        if (audit.Action.Length > 0)
        {
            await AppendAsync(audit, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<OutboxMessage?> GetOutboxAsync(string id, CancellationToken cancellationToken = default) =>
        store.GetJsonAsync<OutboxMessage>(Containers.Extractions, StorePaths.Outbox(id), cancellationToken);

    public async Task<IReadOnlyList<OutboxMessage>> PendingOutboxAsync(DateTimeOffset createdBefore, int max, CancellationToken cancellationToken = default) =>
        [.. (await AllOutboxAsync(cancellationToken).ConfigureAwait(false)).Where(o => o.PublishedAt is null && o.CreatedAt <= createdBefore).OrderBy(o => o.CreatedAt).Take(max)];

    public Task MarkOutboxPublishedAsync(string id, DateTimeOffset at, CancellationToken cancellationToken = default) =>
        store.UpdateAsync<OutboxMessage>(Containers.Extractions, StorePaths.Outbox(id),
            o => o!.PublishedAt is null ? o with { PublishedAt = at, PublishAttempts = o.PublishAttempts + 1 } : o, cancellationToken);

    public Task MarkOutboxFailedAsync(string id, string error, CancellationToken cancellationToken = default) =>
        store.UpdateAsync<OutboxMessage>(Containers.Extractions, StorePaths.Outbox(id), o => o! with { LastError = error, PublishAttempts = o.PublishAttempts + 1 },
            cancellationToken);

    public Task RecordOutboxDeliveryAsync(string id, string status, string? deliveryJson, DateTimeOffset at, CancellationToken cancellationToken = default) =>
        store.UpdateAsync<OutboxMessage>(Containers.Extractions, StorePaths.Outbox(id),
            o => o! with { DeliveredAt = at, DeliveryStatus = status, DeliveryJson = deliveryJson }, cancellationToken);

    public async Task<IReadOnlyList<OutboxMessage>> OutboxAsync(string? quoteId, int max, CancellationToken cancellationToken = default) =>
        [.. (await AllOutboxAsync(cancellationToken).ConfigureAwait(false)).Where(o => quoteId is null || o.QuoteId == quoteId).OrderByDescending(o => o.CreatedAt).Take(max)];

    private async Task<List<OutboxMessage>> AllOutboxAsync(CancellationToken cancellationToken)
    {
        var list = new List<OutboxMessage>();
        foreach (var path in await store.ListAsync(Containers.Extractions, "outbox/", cancellationToken).ConfigureAwait(false))
        {
            if (await store.GetJsonAsync<OutboxMessage>(Containers.Extractions, path, cancellationToken).ConfigureAwait(false) is { } o)
            {
                list.Add(o);
            }
        }

        return list;
    }

    public Task OpenReviewAsync(ReviewItem item, CancellationToken cancellationToken = default) =>
        store.PutJsonAsync(Containers.Extractions, StorePaths.Review(item.DocumentId), item, cancellationToken);

    public async Task<DecisionApplyResult> ApplyDecisionAsync(DecisionRecord record, CancellationToken cancellationToken = default, IReadOnlyList<OutboxMessage>? outbox = null)
    {
        var decision = record.Decision;
        // Brief 30 items 6–7, as dbo.ApplyDecision does: a decision whose review.decided row is stored was applied; a revision already taken is a conflict.
        if (outbox?.FirstOrDefault(o => o.Kind == OutboxKinds.ReviewDecided) is { } decided && await GetOutboxAsync(decided.Id, cancellationToken).ConfigureAwait(false) is not null)
        {
            return DecisionApplyResult.AlreadyApplied;
        }

        // Brief 32 item 6 (demo topology; SQL commits all of this in one transaction): the review.decided row is written last and marks the decision
        // applied. Every write before it is idempotent, so a retry after a crash completes a partial decision instead of duplicating it or refusing it:
        // override lines dedupe by id, audit lines by their text, and a stored result already at this revision with this document is this decision's.
        var stored = await GetResultAsync(decision.DocumentId, cancellationToken).ConfigureAwait(false);
        if (stored?.Revision is not { } current)
        {
            return DecisionApplyResult.Conflict;
        }

        // Retry rule (brief 32): a stored result counts as this decision's only while the review is pending or holds this decision's key — do not simplify this away.
        // This decision's own partial write only while no other decision has taken the review: two identical concurrent decisions produce the same
        // document at the same revision, and the loser must be a conflict, not a resume (brief 32 item 1).
        var reviewNow = await GetReviewAsync(decision.DocumentId, cancellationToken).ConfigureAwait(false);
        var resultSaved = current == record.After.Revision && SameDocument(stored, record.After)
            && (reviewNow is null || reviewNow.State == "pending" || reviewNow.Decision?.Key == decision.Key);
        if (current >= record.After.Revision && !resultSaved)
        {
            return DecisionApplyResult.Conflict;
        }

        var ordinal = 0;
        var thisDecision = $"{decision.DocumentId}:r{record.After.Revision}:";
        var existing = await OverridesOfDocumentAsync(decision.DocumentId, cancellationToken).ConfigureAwait(false);
        var written = existing.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        // Brief 24 step 5: each override names the field's previous one (the chain the chat's explain_value returns) — never one of this decision's own lines.
        var last = existing.Where(o => !o.Id.StartsWith(thisDecision, StringComparison.Ordinal))
            .GroupBy(o => o.FieldPath, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Id, StringComparer.Ordinal);
        foreach (var change in record.Changes)
        {
            await AppendOnceAsync(new AuditEvent(decision.At, decision.User, decision.Role, change.Kind, record.UploadId, decision.DocumentId, change.FieldPath,
                Element(change.OldValue), Element(change.NewValue), change.Rationale), cancellationToken).ConfigureAwait(false);
            if (change.Kind == "override")
            {
                var line = new StoredOverride($"{thisDecision}{++ordinal}", decision.DocumentId, change.FieldPath, change.OldValue, change.NewValue,
                    change.Rationale, decision.User, decision.At, decision.Role, last.GetValueOrDefault(change.FieldPath), record.After.Revision);
                last[change.FieldPath] = line.Id;
                if (written.Add(line.Id))
                {
                    await store.AppendLineAsync(Containers.Extractions, StorePaths.Overrides(decision.DocumentId), JsonSerializer.Serialize(line, CanonicalJson.Options), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        if (await QuoteOfUploadAsync(record.UploadId, cancellationToken).ConfigureAwait(false) is { } quoteId)
        {
            await BumpFeatureGenerationAsync(quoteId, cancellationToken).ConfigureAwait(false); // any claim change invalidates the quote's snapshots
        }

        if (!resultSaved)
        {
            await SaveResultAsync(record.After, new Actor(decision.User, decision.Role, "api"), cancellationToken).ConfigureAwait(false);
        }

        await AppendOnceAsync(new AuditEvent(decision.At, decision.User, decision.Role, $"review.{decision.Action}", record.UploadId, decision.DocumentId, Rationale: decision.Rationale),
            cancellationToken).ConfigureAwait(false);
        if (record.FormatApproved)
        {
            await AppendOnceAsync(new AuditEvent(decision.At, decision.User, decision.Role, "format.approve", record.UploadId, decision.DocumentId, Rationale: record.After.FormatRef),
                cancellationToken).ConfigureAwait(false);
        }

        var review = record.KeepReviewOpen ? null : await store.UpdateAsync<ReviewItem>(Containers.Extractions, StorePaths.Review(decision.DocumentId), r =>
        {
            r ??= new ReviewItem { DocumentId = decision.DocumentId, UploadId = record.UploadId };
            (r.State, r.DecidedAt, r.Decision) = ("decided", decision.At, decision);
            return r;
        }, cancellationToken).ConfigureAwait(false);
        if (outbox is { Count: > 0 } || record.QuoteBump is not null)
        {
            await AuditWithOutboxAsync(new AuditEvent(decision.At, decision.User, decision.Role, "", "", null), Actor.System, outbox ?? [], cancellationToken,
                record.QuoteBump is { } bump ? bump with { DocumentId = decision.DocumentId, DocumentRevision = record.After.Revision } : null).ConfigureAwait(false);
        }

        _ = review;
        return DecisionApplyResult.Applied;
    }

    public async Task ExpireReviewAsync(string uploadId, string documentId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await store.UpdateAsync<ReviewItem>(Containers.Extractions, StorePaths.Review(documentId), r =>
        {
            r ??= new ReviewItem { DocumentId = documentId, UploadId = uploadId };
            (r.State, r.DecidedAt) = ("expired", at);
            return r;
        }, cancellationToken).ConfigureAwait(false);
        await AppendAsync(new AuditEvent(at, "system", "system", "review.expired", uploadId, documentId), cancellationToken).ConfigureAwait(false);
    }

    public Task AuditAsync(AuditEvent audit, Actor actor, CancellationToken cancellationToken = default) => AppendAsync(audit, cancellationToken);

    public Task RecordUsageAsync(string uploadId, UsageEvent usage, CancellationToken cancellationToken = default) =>
        store.AppendLineAsync(Containers.Extractions, StorePaths.Usage(uploadId), JsonSerializer.Serialize(usage, CanonicalJson.Options), cancellationToken);

    public async Task<IReadOnlyList<UsageEvent>> ReadUsageAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        var text = (await store.GetAsync(Containers.Extractions, StorePaths.Usage(uploadId), cancellationToken).ConfigureAwait(false))?.ToString() ?? "";
        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonSerializer.Deserialize<UsageEvent>(l, CanonicalJson.Options)!)];
    }

    // ------------------------------------------------------------------ workbench (brief 11)

    public Task<QuoteContext?> GetQuoteContextAsync(string quoteId, CancellationToken cancellationToken = default) =>
        store.GetJsonAsync<QuoteContext>(Containers.Extractions, StorePaths.QuoteContext(quoteId), cancellationToken);

    public async Task SaveQuoteContextAsync(QuoteContext context, Actor actor, CancellationToken cancellationToken = default)
    {
        var old = await GetQuoteContextAsync(context.QuoteId, cancellationToken).ConfigureAwait(false);
        var at = context.UpdatedAt ?? DateTimeOffset.UtcNow;
        var stored = context with { UpdatedAt = at, UpdatedBy = actor.UserId };
        await store.PutJsonAsync(Containers.Extractions, StorePaths.QuoteContext(context.QuoteId), stored, cancellationToken).ConfigureAwait(false);
        await AppendAsync(new AuditEvent(at, actor.UserId, actor.Role, "quote.context", "", null, QuoteAudit.FieldPath(context.QuoteId),
            old is null ? null : JsonSerializer.SerializeToElement(old with { UpdatedAt = null, UpdatedBy = null }, CanonicalJson.Options),
            JsonSerializer.SerializeToElement(context with { UpdatedAt = null, UpdatedBy = null }, CanonicalJson.Options),
            "submission context (exposures, last day worked)"), cancellationToken).ConfigureAwait(false);
    }

    public Task<QuoteOwner?> GetQuoteOwnerAsync(string quoteId, CancellationToken cancellationToken = default) =>
        store.GetJsonAsync<QuoteOwner>(Containers.Extractions, StorePaths.QuoteOwner(quoteId), cancellationToken);

    public async Task SetQuoteOwnerAsync(QuoteOwner owner, string rationale, Actor actor, CancellationToken cancellationToken = default)
    {
        var old = await GetQuoteOwnerAsync(owner.QuoteId, cancellationToken).ConfigureAwait(false);
        await store.PutJsonAsync(Containers.Extractions, StorePaths.QuoteOwner(owner.QuoteId), old is null ? owner : old with { ApplicantUserId = owner.ApplicantUserId },
            cancellationToken).ConfigureAwait(false);
        await AppendAsync(new AuditEvent(owner.CreatedAt, actor.UserId, actor.Role, "quote.owner", "", null, QuoteAudit.FieldPath(owner.QuoteId),
            JsonSerializer.SerializeToElement(old?.ApplicantUserId ?? QuoteAudit.UnderwritersOnly), JsonSerializer.SerializeToElement(owner.ApplicantUserId ?? QuoteAudit.UnderwritersOnly),
            rationale), cancellationToken).ConfigureAwait(false);
    }

    public Task<QuoteOwner> ClaimQuoteOwnerAsync(QuoteOwner owner, CancellationToken cancellationToken = default) =>
        store.UpdateAsync<QuoteOwner>(Containers.Extractions, StorePaths.QuoteOwner(owner.QuoteId), current => current ?? owner, cancellationToken);

    public Task<QuoteLabel?> GetQuoteLabelAsync(string quoteId, CancellationToken cancellationToken = default) =>
        store.GetJsonAsync<QuoteLabel>(Containers.Extractions, StorePaths.QuoteLabel(quoteId), cancellationToken);

    public async Task SaveQuoteLabelAsync(QuoteLabel label, string rationale, Actor actor, CancellationToken cancellationToken = default)
    {
        var old = await GetQuoteLabelAsync(label.QuoteId, cancellationToken).ConfigureAwait(false);
        await store.PutJsonAsync(Containers.Extractions, StorePaths.QuoteLabel(label.QuoteId), label, cancellationToken).ConfigureAwait(false);
        var path = QuoteAudit.FieldPath(label.QuoteId);
        if (old?.DisplayName != label.DisplayName)
        {
            await AppendAsync(new AuditEvent(label.UpdatedAt, actor.UserId, actor.Role, "quote.rename", "", null, path,
                old?.DisplayName is { } before ? JsonSerializer.SerializeToElement(before) : null,
                label.DisplayName is { } after ? JsonSerializer.SerializeToElement(after) : null, rationale), cancellationToken).ConfigureAwait(false);
        }

        if ((old?.RetiredAt is null) != (label.RetiredAt is null))
        {
            await AppendAsync(new AuditEvent(label.UpdatedAt, actor.UserId, actor.Role, label.RetiredAt is null ? "quote.restore" : "quote.retire", "", null, path,
                JsonSerializer.SerializeToElement(old?.RetiredAt is null ? "active" : "retired"), JsonSerializer.SerializeToElement(label.RetiredAt is null ? "active" : "retired"),
                rationale), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<QuoteSummary>> ListQuotesAsync(CancellationToken cancellationToken = default, bool includeRetired = false)
    {
        var byQuote = (await store.ListAsync(Containers.Extractions, "index/quotes/", cancellationToken).ConfigureAwait(false))
            .Select(p => p.Split('/')).Where(p => p.Length == 4).GroupBy(p => Uri.UnescapeDataString(p[2]), p => Path.GetFileNameWithoutExtension(p[3]));
        var quotes = new List<QuoteSummary>();
        foreach (var g in byQuote)
        {
            var results = new List<DocumentResult>();
            foreach (var id in g)
            {
                if (await GetResultAsync(id, cancellationToken).ConfigureAwait(false) is { } r)
                {
                    results.Add(r);
                }
            }

            var label = await GetQuoteLabelAsync(g.Key, cancellationToken).ConfigureAwait(false);
            if (label?.RetiredAt is not null && !includeRetired)
            {
                continue;
            }

            quotes.Add(new QuoteSummary(g.Key, results.Count, results.Sum(r => r.Document.PolicyPeriods.Sum(p => p.Claims.Count)),
                results.Select(r => r.Document.InsuredName).OfType<string>().Order(StringComparer.Ordinal).LastOrDefault(),
                results.Select(r => r.Document.ValuationDate).Max(), results.Count(r => r.ReviewState == "pending"), null, label?.DisplayName, label?.RetiredAt));
        }

        return [.. quotes.OrderBy(q => q.QuoteId, StringComparer.Ordinal)];
    }

    public async Task<AuditTrailView> AuditTrailAsync(string? documentId, string? quoteId, int max, CancellationToken cancellationToken = default)
    {
        var documents = quoteId is null ? [] : (await DocumentsOfQuoteAsync(quoteId, cancellationToken).ConfigureAwait(false)).Select(d => d.DocumentId).ToHashSet(StringComparer.Ordinal);
        var path = quoteId is null ? null : QuoteAudit.FieldPath(quoteId);
        var entries = new List<AuditTrailEntry>();
        foreach (var blob in (await store.ListAsync(Containers.Extractions, "audit/", cancellationToken).ConfigureAwait(false)).OrderDescending(StringComparer.Ordinal))
        {
            var text = (await store.GetAsync(Containers.Extractions, blob, cancellationToken).ConfigureAwait(false))?.ToString() ?? "";
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var a = JsonSerializer.Deserialize<AuditEvent>(line, CanonicalJson.Options)!;
                if (documentId is not null ? a.DocumentId == documentId : quoteId is null || a.FieldPath == path || a.DocumentId is { } d && documents.Contains(d))
                {
                    entries.Add(new AuditTrailEntry(null, a.At, a.User, a.Role, a.Action, a.UploadId is { Length: > 0 } u ? u : null, a.DocumentId, null, a.FieldPath,
                        Text(a.OldValue), Text(a.NewValue), a.Rationale));
                }
            }
        }

        return new AuditTrailView([.. entries.OrderByDescending(e => e.At).Take(max)], "append-only-blob", null);
    }

    private static string? Text(JsonElement? value) => value is not { } v || v.ValueKind == JsonValueKind.Null ? null
        : v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();

    public async Task<IReadOnlyList<DocumentCostLine>> DocumentCostsAsync(CancellationToken cancellationToken = default)
    {
        var lines = new List<DocumentCostLine>();
        foreach (var id in (await store.ListAsync(Containers.Extractions, "index/documents/", cancellationToken).ConfigureAwait(false)).Select(Path.GetFileNameWithoutExtension))
        {
            if (await GetResultAsync(id!, cancellationToken).ConfigureAwait(false) is { Cost: { } c } r)
            {
                lines.Add(new DocumentCostLine(r.DocumentId, r.Document.CarrierName, c.Path, c.CostUsd, c.Pages, c.LlmCalls, DateTimeOffset.MinValue, c.Breakdown));
            }
        }

        return lines;
    }

    // ------------------------------------------------------------------ chat (brief 12)

    public async Task<IReadOnlyList<ScopedDocument>> DocumentsInScopeAsync(string scope, string? quoteId, string? workspaceId,
        CancellationToken cancellationToken = default)
    {
        if (scope is not (ChatScopes.Org or ChatScopes.Quote or ChatScopes.Workspace))
        {
            throw new ArgumentException($"Unknown chat scope '{scope}'.", nameof(scope));
        }

        var manifests = new Dictionary<string, UploadManifest?>(StringComparer.Ordinal);
        var documents = new List<ScopedDocument>();
        foreach (var id in (await store.ListAsync(Containers.Extractions, "index/documents/", cancellationToken).ConfigureAwait(false)).Select(Path.GetFileNameWithoutExtension))
        {
            if (await UploadOfDocumentAsync(id!, cancellationToken).ConfigureAwait(false) is not { } uploadId)
            {
                continue;
            }

            if (!manifests.TryGetValue(uploadId, out var manifest))
            {
                manifests[uploadId] = manifest = await GetManifestAsync(uploadId, cancellationToken).ConfigureAwait(false);
            }

            var submission = manifest?.Submission;
            var (q, w) = (submission?.QuoteId, submission?.WorkspaceId);
            var admitted = scope switch
            {
                ChatScopes.Quote => w is null && q is not null && q == quoteId,
                ChatScopes.Workspace => w is not null && w == workspaceId,
                _ => w is null,
            };
            // Brief 24 step 2: a rejected document is out of every chat scope.
            if (admitted && await GetResultAsync(id!, cancellationToken).ConfigureAwait(false) is { ReviewState: not ReviewActions.Reject })
            {
                documents.Add(new ScopedDocument(id!, uploadId, q, w));
            }
        }

        return [.. documents.OrderBy(d => d.DocumentId, StringComparer.Ordinal)];
    }

    public async Task SaveWorkspaceAsync(ChatWorkspace workspace, Actor actor, string action, CancellationToken cancellationToken = default, string? detail = null)
    {
        await store.PutJsonAsync(Containers.Extractions, StorePaths.Workspace(workspace.WorkspaceId), workspace, cancellationToken).ConfigureAwait(false);
        await AppendAsync(new AuditEvent(workspace.DeletedAt ?? workspace.CreatedAt, actor.UserId, actor.Role, action, "", null, $"workspaces/{workspace.WorkspaceId}",
            NewValue: detail is null ? null : JsonSerializer.SerializeToElement(detail), Rationale: workspace.Name), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> UploadsOfQuoteAsync(string quoteId, CancellationToken cancellationToken = default)
    {
        var uploads = new List<string>();
        foreach (var path in await store.ListAsync(Containers.Extractions, "uploads/", cancellationToken).ConfigureAwait(false))
        {
            if (path.EndsWith("/manifest.json", StringComparison.Ordinal)
                && await store.GetJsonAsync<UploadManifest>(Containers.Extractions, path, cancellationToken).ConfigureAwait(false) is { } m && m.Submission.QuoteId == quoteId)
            {
                uploads.Add(m.UploadId);
            }
        }

        return uploads;
    }

    public async Task<IReadOnlyList<string>> UploadsOfWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        var uploads = new List<string>();
        foreach (var path in await store.ListAsync(Containers.Extractions, "uploads/", cancellationToken).ConfigureAwait(false))
        {
            if (path.EndsWith("/manifest.json", StringComparison.Ordinal)
                && await store.GetJsonAsync<UploadManifest>(Containers.Extractions, path, cancellationToken).ConfigureAwait(false) is { } m && m.Submission.WorkspaceId == workspaceId)
            {
                uploads.Add(m.UploadId);
            }
        }

        return uploads;
    }

    public Task<ChatWorkspace?> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default) =>
        store.GetJsonAsync<ChatWorkspace>(Containers.Extractions, StorePaths.Workspace(workspaceId), cancellationToken);

    public async Task<IReadOnlyList<ChatWorkspace>> ExpiredWorkspacesAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var list = new List<ChatWorkspace>();
        foreach (var path in await store.ListAsync(Containers.Extractions, "workspaces/", cancellationToken).ConfigureAwait(false))
        {
            if (await store.GetJsonAsync<ChatWorkspace>(Containers.Extractions, path, cancellationToken).ConfigureAwait(false) is { DeletedAt: null } w && w.ExpiresAt <= now)
            {
                list.Add(w);
            }
        }

        return list;
    }

    public async Task SaveChatTurnAsync(ChatTurnResult turn, Actor actor, CancellationToken cancellationToken = default)
    {
        await store.AppendLineAsync(Containers.Extractions, StorePaths.ChatConversation(turn.ConversationId), JsonSerializer.Serialize(turn, CanonicalJson.Options),
            cancellationToken).ConfigureAwait(false);
        var where = turn.QuoteId is { } q ? $"quotes/{q}" : turn.WorkspaceId is { } w ? $"workspaces/{w}" : "org";
        await AppendAsync(new AuditEvent(turn.At, actor.UserId, actor.Role, "chat.turn", "", null, where, null,
            JsonSerializer.SerializeToElement(turn.TurnId), turn.Question), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChatTurnResult>> ConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var text = (await store.GetAsync(Containers.Extractions, StorePaths.ChatConversation(conversationId), cancellationToken).ConfigureAwait(false))?.ToString() ?? "";
        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonSerializer.Deserialize<ChatTurnResult>(l, CanonicalJson.Options)!)];
    }

    // ------------------------------------------------------------------ engineering escalation (brief 26; dev/tests: not transactional)

    private static string EscalationPath(string id) => $"escalations/{StorePaths.Safe(id)}.json";

    public Task<Escalation?> GetEscalationAsync(string id, CancellationToken cancellationToken = default) =>
        store.GetJsonAsync<Escalation>(Containers.Extractions, EscalationPath(id), cancellationToken);

    public async Task<IReadOnlyList<Escalation>> ListEscalationsAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<Escalation>();
        foreach (var path in await store.ListAsync(Containers.Extractions, "escalations/", cancellationToken).ConfigureAwait(false))
        {
            if (await store.GetJsonAsync<Escalation>(Containers.Extractions, path, cancellationToken).ConfigureAwait(false) is { } e)
            {
                list.Add(e);
            }
        }

        return [.. list.OrderByDescending(e => e.LastSeen).ThenBy(e => e.Id, StringComparer.Ordinal)];
    }

    private sealed class EscalationConflict : Exception;

    public async Task<bool> SaveEscalationAsync(Escalation escalation, int expectedVersion, AuditEvent audit, Actor actor, IReadOnlyList<OutboxMessage> outbox,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await store.UpdateAsync<Escalation>(Containers.Extractions, EscalationPath(escalation.Id),
                current => (current?.Version ?? 0) == expectedVersion ? escalation with { Version = expectedVersion + 1 } : throw new EscalationConflict(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EscalationConflict)
        {
            return false;
        }

        await AuditWithOutboxAsync(audit, actor, outbox, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<EscalationObservation>> EscalationObservationsAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        // Reopens from the audit files of the window (one pass), then one result, review and manifest read per document.
        var reopens = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var blob in await store.ListAsync(Containers.Extractions, "audit/", cancellationToken).ConfigureAwait(false))
        {
            if (string.CompareOrdinal(Path.GetFileNameWithoutExtension(blob), since.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)) < 0)
            {
                continue;
            }

            var text = (await store.GetAsync(Containers.Extractions, blob, cancellationToken).ConfigureAwait(false))?.ToString() ?? "";
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (JsonSerializer.Deserialize<AuditEvent>(line, CanonicalJson.Options) is { Action: "review.reopened", DocumentId: { } reopened })
                {
                    reopens[reopened] = reopens.GetValueOrDefault(reopened) + 1;
                }
            }
        }

        var manifests = new Dictionary<string, UploadManifest?>(StringComparer.Ordinal);
        var list = new List<EscalationObservation>();
        foreach (var id in (await store.ListAsync(Containers.Extractions, "index/documents/", cancellationToken).ConfigureAwait(false)).Select(Path.GetFileNameWithoutExtension))
        {
            if (id is null || await GetResultAsync(id, cancellationToken).ConfigureAwait(false) is not { } result)
            {
                continue;
            }

            if (!manifests.TryGetValue(result.UploadId, out var manifest))
            {
                manifests[result.UploadId] = manifest = await GetManifestAsync(result.UploadId, cancellationToken).ConfigureAwait(false);
            }

            var at = manifest?.CreatedAt ?? DateTimeOffset.MinValue;
            if (at < since)
            {
                continue;
            }

            var review = await GetReviewAsync(id, cancellationToken).ConfigureAwait(false);
            list.Add(new EscalationObservation(id, result.UploadId, result.Document.CarrierName, result.Routing?.FingerprintKey, result.FormatRef ?? result.Routing?.FormatRef, at,
                review?.Kind == ReviewKinds.DocumentReview, [.. result.RuleOutcomes.Where(o => o.Status == LossRun.Rules.RuleStatus.Fail).Select(o => o.RuleId).Distinct(StringComparer.Ordinal)],
                reopens.GetValueOrDefault(id)));
        }

        return list;
    }

    /// <summary>Brief 32 item 6: appends the audit line unless the day's file already holds it (a retried decision writes the same text).</summary>
    private async Task AppendOnceAsync(AuditEvent audit, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(audit, CanonicalJson.Options);
        if (await store.GetAsync(Containers.Extractions, StorePaths.Audit(audit.At), cancellationToken).ConfigureAwait(false) is { } day
            && day.ToString().Split('\n').Any(l => l.TrimEnd('\r') == line))
        {
            return;
        }

        await store.AppendLineAsync(Containers.Extractions, StorePaths.Audit(audit.At), line, cancellationToken).ConfigureAwait(false);
    }

    private static bool SameDocument(DocumentResult a, DocumentResult b) =>
        JsonSerializer.Serialize(a.Document, CanonicalJson.Options) == JsonSerializer.Serialize(b.Document, CanonicalJson.Options);

    private Task AppendAsync(AuditEvent audit, CancellationToken cancellationToken) =>
        store.AppendLineAsync(Containers.Extractions, StorePaths.Audit(audit.At), JsonSerializer.Serialize(audit, CanonicalJson.Options), cancellationToken);

    /// <summary>Audit values as JSON (numbers stay numbers, as brief 07 wrote them); text that is not JSON becomes a string.</summary>
    private static JsonElement? Element(string? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(value).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(value);
        }
    }
}

/// <summary>Records every UsageEvent of an upload in the system of record (usage.jsonl in Blob, dbo.UsageEvent in SQL).</summary>
public sealed class UploadCostMeter(IRecordStore records, string uploadId) : ICostMeter
{
    public async ValueTask RecordAsync(UsageEvent usage, CancellationToken cancellationToken = default) =>
        await records.RecordUsageAsync(uploadId, usage, cancellationToken).ConfigureAwait(false);
}
