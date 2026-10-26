using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Rules;
using Microsoft.EntityFrameworkCore;

namespace LossRun.Data;

/// <summary>
/// The single command handler (brief 09 §3; underwriter-analytics.md KQ4): the only code that writes dbo.Claim,
/// dbo.Override and dbo.AuditEvent, each command in one transaction, every mutation an audit row. A decision's
/// correlation id ("{documentId}:r{revision}") ties its Override rows, its AuditEvents and the one Claim history
/// version it creates per claim — the invariant <see cref="AuditProof"/> checks.
/// </summary>
public sealed class ClaimCommandService(Func<LossRunDbContext> contexts, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ extraction persist

    /// <summary>Persists a freshly extracted document: Document, PolicyPeriod, Claim (as-adjusted = as-reported at revision 1),
    /// ExtractedValue (as-reported with lineage), RuleOutcome, DocumentCost and an <c>extraction.persist</c> audit row. Idempotent per document.</summary>
    public async Task PersistExtractionAsync(DocumentResult result, string? quoteId, Actor actor, CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async db =>
        {
            if (await db.Documents.AnyAsync(d => d.Id == result.DocumentId, cancellationToken).ConfigureAwait(false))
            {
                return; // a retried activity
            }

            var d = result.Document;
            var now = Now;
            db.Documents.Add(new DocumentRow
            {
                Id = result.DocumentId, UploadId = result.UploadId, QuoteId = quoteId, CarrierName = d.CarrierName, InsuredName = d.InsuredName, LineOfBusiness = d.LineOfBusiness,
                ValuationDate = d.ValuationDate, ReportRunDate = d.ReportRunDate, ExpenseTreatment = d.ExpenseTreatment.ToString(), FormatRef = result.FormatRef,
                RoutingTier = result.Routing?.Tier.ToString(), Badge = result.Routing?.Badge, ReviewState = result.ReviewState, Revision = result.Revision,
                CatalogVersion = RuleCatalog.Embedded.CatalogVersion, CostUsd = result.Cost?.CostUsd, ResultJson = Serialize(result), CreatedUtc = now, UpdatedUtc = now,
            });
            for (var p = 0; p < d.PolicyPeriods.Count; p++)
            {
                var period = d.PolicyPeriods[p];
                var periodId = Ids.Period(result.DocumentId, p);
                db.PolicyPeriods.Add(new PolicyPeriodRow
                {
                    Id = periodId, DocumentId = result.DocumentId, PeriodIndex = p, PolicyNumber = period.PolicyNumber, CarrierName = period.CarrierName,
                    EffectiveDate = period.EffectiveDate, ExpirationDate = period.ExpirationDate, DetailLevel = period.DetailLevel.ToString(),
                    SubtotalsJson = period.Subtotals is null ? null : JsonSerializer.Serialize(period.Subtotals, CanonicalJson.Options),
                });
                for (var c = 0; c < period.Claims.Count; c++)
                {
                    var row = new ClaimRow { Id = Ids.Claim(result.DocumentId, p, c), DocumentId = result.DocumentId, PolicyPeriodId = periodId, PeriodIndex = p, ClaimIndex = c };
                    ClaimColumns.Project(period.Claims[c], d, period, row);
                    row.ChangedBy = actor.UserId;
                    db.Claims.Add(row);
                }
            }

            db.ExtractedValues.AddRange(ExtractedValues(result, now));
            db.RuleOutcomes.AddRange(Outcomes(result));
            if (result.Cost is { } cost)
            {
                db.DocumentCosts.Add(new DocumentCostRow
                {
                    DocumentId = result.DocumentId, RunId = cost.RunId, Path = cost.Path, LlmCalls = cost.LlmCalls, EmbeddingCalls = cost.EmbeddingCalls, Pages = cost.Pages,
                    TokensIn = cost.TokensIn, TokensOut = cost.TokensOut, CostUsd = cost.CostUsd, BreakdownJson = JsonSerializer.Serialize(cost.Breakdown, CanonicalJson.Options),
                    PriceTableVersion = Ingestion.Metering.PriceTable.Embedded.Version, ComputedUtc = now,
                });
            }

            db.AuditEvents.Add(Audit(actor, "extraction.persist", now, result.UploadId, result.DocumentId, result.Revision, formatRef: result.FormatRef,
                rationale: $"{d.PolicyPeriods.Sum(x => x.Claims.Count)} claim(s); routing {result.Routing?.Tier}"));
            await InvalidateFeatureSnapshotsAsync(db, quoteId, now, actor.UserId, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ review decision (overrides)

    /// <summary>
    /// Writes a decided review: one Override row per overridden field (id from dbo.OverrideSeq, hi/lo), one AuditEvent per
    /// override and re-derived value, the claims' new values (one temporal version per claim and decision), the new
    /// revision's rule outcomes, the review decision and format approval events. Idempotent: a revision already applied is skipped.
    /// </summary>
    /// <summary>
    /// Brief 10 §2: a claim change (new document on the quote, or an override) invalidates the quote's cached feature
    /// snapshots in the same transaction — the cache can never serve numbers from before the change.
    /// </summary>
    private static async Task InvalidateFeatureSnapshotsAsync(LossRunDbContext db, string? quoteId, DateTime at, string by, CancellationToken cancellationToken)
    {
        if (quoteId is null)
        {
            return;
        }

        await db.FeatureSnapshots.Where(f => f.QuoteId == quoteId && f.InvalidatedUtc == null)
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.InvalidatedUtc, at).SetProperty(f => f.InvalidatedBy, by), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a decision (review decision or direct override) in one round trip (brief 13, 12b Answers 1): every row is computed
    /// here and <c>dbo.ApplyDecision</c> writes them in one transaction under the document's row lock: overrides (each
    /// superseding the field's previous one), audit events, the changed claims (one temporal version each), rule outcomes, the
    /// document, the review item, and the optional outbox rows (the decision's notification commits with it). False when another
    /// decision already took this revision. Was 7–8 statements plus a separate snapshot invalidation, each ~45 ms across regions.
    /// </summary>
    public async Task<DecisionApplyResult> ApplyDecisionAsync(DecisionRecord record, Actor actor, CancellationToken cancellationToken = default,
        IReadOnlyList<OutboxMessage>? outbox = null)
    {
        var json = DecisionPayload(record, actor, outbox ?? []);
        await using var db = contexts();
        var strategy = db.Database.CreateExecutionStrategy();
        var value = await strategy.ExecuteAsync(async () =>
            (await db.Database.SqlQuery<int>($"EXEC dbo.ApplyDecision @json = {json}").ToListAsync(cancellationToken).ConfigureAwait(false)).Single())
            .ConfigureAwait(false);
        return value switch { 1 => DecisionApplyResult.Applied, 2 => DecisionApplyResult.AlreadyApplied, _ => DecisionApplyResult.Conflict };
    }

    /// <summary>The JSON document <c>dbo.ApplyDecision</c> reads (property names are the column names).</summary>
    internal static string DecisionPayload(DecisionRecord record, Actor actor, IReadOnlyList<OutboxMessage> outbox)
    {
        var documentId = record.After.DocumentId;
        var decision = record.Decision;
        var at = decision.At.UtcDateTime;
        var correlation = $"{documentId}:r{record.After.Revision}";
        var who = actor with { CorrelationId = correlation };
        var catalog = RuleCatalog.Embedded.CatalogVersion;
        var overrides = new JsonArray();
        var audits = new JsonArray();
        var lastOverride = new Dictionary<Guid, int>();

        JsonObject Row(object row, params (string Name, JsonNode? Value)[] extra)
        {
            var node = JsonSerializer.SerializeToNode(row)!.AsObject();
            node.Remove("Id");
            foreach (var (name, value) in extra)
            {
                node[name] = value;
            }

            return node;
        }

        foreach (var change in record.Changes)
        {
            var position = JsonPath.ClaimPosition(change.FieldPath);
            var claimId = position is { } pos ? Ids.Claim(documentId, pos.Period, pos.Claim) : (Guid?)null;
            int? ord = null;
            if (change.Kind == "override")
            {
                ord = overrides.Count;
                overrides.Add(Row(new OverrideRow
                {
                    DocumentId = documentId, DocumentRevision = record.After.Revision, ClaimId = claimId, FieldPath = change.FieldPath, ClaimField = position?.Field,
                    OldValue = Clip(change.OldValue), NewValue = Clip(change.NewValue), Rationale = Clip(change.Rationale, 2000)!, UserId = decision.User, Role = decision.Role,
                    AtUtc = at, CatalogVersion = catalog, FormatRef = record.After.FormatRef, Source = who.Source, CorrelationId = correlation,
                }, ("Ord", ord.Value)));
                if (claimId is { } id)
                {
                    lastOverride[id] = ord.Value;
                }
            }

            audits.Add(Row(Audit(who, change.Kind, at, record.UploadId, documentId, record.After.Revision, claimId, change.FieldPath, change.OldValue, change.NewValue,
                change.Rationale, null, record.After.FormatRef), ("Ord", audits.Count), ("OverrideOrd", ord)));
        }

        audits.Add(Row(Audit(who, $"review.{decision.Action}", at, record.UploadId, documentId, record.After.Revision, rationale: decision.Rationale,
            formatRef: record.After.FormatRef), ("Ord", audits.Count), ("OverrideOrd", null)));
        if (record.FormatApproved)
        {
            audits.Add(Row(Audit(who, "format.approve", at, record.UploadId, documentId, record.After.Revision, rationale: record.After.FormatRef,
                formatRef: record.After.FormatRef), ("Ord", audits.Count), ("OverrideOrd", null)));
        }

        // Changed claims only (each gets exactly one history version): the after projection differs from the before one, or it was overridden.
        var claims = new JsonArray();
        var after = record.After.Document;
        var before = record.Before.Document;
        for (var p = 0; p < after.PolicyPeriods.Count; p++)
        {
            for (var c = 0; c < after.PolicyPeriods[p].Claims.Count; c++)
            {
                var id = Ids.Claim(documentId, p, c);
                var next = new ClaimRow { Id = id };
                ClaimColumns.Project(after.PolicyPeriods[p].Claims[c], after, after.PolicyPeriods[p], next);
                var previous = new ClaimRow { Id = id };
                if (p < before.PolicyPeriods.Count && c < before.PolicyPeriods[p].Claims.Count)
                {
                    ClaimColumns.Project(before.PolicyPeriods[p].Claims[c], before, before.PolicyPeriods[p], previous);
                }

                var overridden = lastOverride.TryGetValue(id, out var lastOrd);
                if (!overridden && JsonSerializer.Serialize(next) == JsonSerializer.Serialize(previous))
                {
                    continue;
                }

                next.ChangedBy = decision.User;
                var row = JsonSerializer.SerializeToNode(next)!.AsObject();
                row["LastOverrideOrd"] = overridden ? lastOrd : null;
                claims.Add(row);
            }
        }

        var payload = new JsonObject
        {
            ["DocumentId"] = documentId, ["NewRevision"] = record.After.Revision, ["ReviewState"] = record.After.ReviewState, ["At"] = at, ["User"] = decision.User,
            // Brief 30 item 6: the decision's review.decided row; when it is already stored the decision was applied (a retried activity).
            ["DecisionKey"] = outbox.FirstOrDefault(o => o.Kind == OutboxKinds.ReviewDecided)?.Id,
            ["KeepReviewOpen"] = record.KeepReviewOpen, // brief 30 item 15
            ["Result"] = JsonNode.Parse(Serialize(record.After)), ["Decision"] = JsonSerializer.SerializeToNode(decision, CanonicalJson.Options),
            ["Overrides"] = overrides, ["Audits"] = audits, ["Claims"] = claims,
            ["QuoteBump"] = record.QuoteBump is { } bump ? new JsonObject { ["QuoteId"] = bump.QuoteId, ["Cause"] = bump.Cause } : null,
            ["Outcomes"] = JsonSerializer.SerializeToNode(Outcomes(record.After).ToList()),
            ["Outbox"] = new JsonArray([.. outbox.Select(o => (JsonNode)new JsonObject
            {
                ["Id"] = o.Id, ["Channel"] = o.Channel, ["Kind"] = o.Kind, ["QuoteId"] = o.QuoteId, ["Subject"] = o.Subject, ["PayloadJson"] = o.PayloadJson,
                ["CreatedUtc"] = o.CreatedAt.UtcDateTime, ["CreatedBy"] = o.CreatedBy,
            })]),
        };
        return payload.ToJsonString();
    }

    public Task ExpireReviewAsync(string uploadId, string documentId, DateTimeOffset at, CancellationToken cancellationToken = default) =>
        InTransactionAsync(async db =>
        {
            var review = await db.ReviewItems.FindAsync([documentId], cancellationToken).ConfigureAwait(false);
            if (review is null || review.State != "pending")
            {
                return;
            }

            (review.State, review.DecidedUtc) = ("expired", at.UtcDateTime);
            db.AuditEvents.Add(Audit(Actor.System, "review.expired", at.UtcDateTime, uploadId, documentId));
        }, cancellationToken);

    /// <summary>Reopen for review: review row back to pending, document reviewState = reopened, <c>review.reopened</c> audit; one transaction.</summary>
    public Task ReopenReviewAsync(ReviewItem item, DocumentResult result, Actor actor, string reason, CancellationToken cancellationToken = default) =>
        InTransactionAsync(async db =>
        {
            var document = await db.Documents.FromSqlInterpolated($"SELECT * FROM dbo.Document WITH (UPDLOCK, HOLDLOCK) WHERE Id = {result.DocumentId}")
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var at = item.OpenedAt.UtcDateTime;
            if (document is not null)
            {
                (document.ReviewState, document.ResultJson, document.UpdatedUtc) = (result.ReviewState, Serialize(result), at);
            }

            var row = await db.ReviewItems.FindAsync([item.DocumentId], cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                db.ReviewItems.Add(Reviews.To(item));
            }
            else
            {
                var next = Reviews.To(item);
                (row.Kind, row.State, row.ReasonsJson, row.Badge, row.FormatRef, row.OpenedUtc, row.DecidedUtc, row.DecisionJson, row.Reopened) =
                    (next.Kind, next.State, next.ReasonsJson, next.Badge, next.FormatRef, next.OpenedUtc, null, null, next.Reopened);
            }

            db.AuditEvents.Add(Audit(actor, "review.reopened", at, item.UploadId, item.DocumentId, document?.Revision, rationale: reason, formatRef: item.FormatRef));
        }, cancellationToken);

    /// <summary>A mutation recorded elsewhere (format retire/approve, upload created, export, quote push) gets its audit row here.</summary>
    public Task AuditAsync(AuditEvent audit, Actor actor, CancellationToken cancellationToken = default) => AuditAsync(audit, actor, [], cancellationToken);

    /// <summary>
    /// Transactional outbox (brief 12b): the audit row and the outbox rows commit together or not at all, so a command
    /// is never audited without being queued for delivery, nor delivered without its audit row.
    /// </summary>
    /// <param name="bump">Brief 24 step 3: bump the quote's revision (dbo.BumpQuoteRevision) in this transaction and write it into the rows.</param>
    public Task AuditAsync(AuditEvent audit, Actor actor, IReadOnlyList<OutboxMessage> outbox, CancellationToken cancellationToken = default, QuoteRevisionBump? bump = null) =>
        InTransactionAsync(async db =>
        {
            if (bump is not null)
            {
                var revision = new Microsoft.Data.SqlClient.SqlParameter("@revision", System.Data.SqlDbType.Int) { Direction = System.Data.ParameterDirection.Output };
                await db.Database.ExecuteSqlRawAsync(
                    "EXEC dbo.BumpQuoteRevision @quoteId = @q, @at = @at, @cause = @cause, @documentId = @d, @documentRevision = @dr, @revision = @revision OUTPUT",
                    [new Microsoft.Data.SqlClient.SqlParameter("@q", bump.QuoteId), new Microsoft.Data.SqlClient.SqlParameter("@at", audit.At.UtcDateTime),
                        new Microsoft.Data.SqlClient.SqlParameter("@cause", bump.Cause), new Microsoft.Data.SqlClient.SqlParameter("@d", (object?)bump.DocumentId ?? DBNull.Value),
                        new Microsoft.Data.SqlClient.SqlParameter("@dr", (object?)bump.DocumentRevision ?? DBNull.Value), revision], cancellationToken).ConfigureAwait(false);
                outbox = QuoteRevisions.Apply(outbox, (int)revision.Value);
            }

            if (audit.Action.Length > 0)
            {
                db.AuditEvents.Add(Audit(actor with { UserId = audit.User, Role = audit.Role }, audit.Action, audit.At.UtcDateTime, audit.UploadId is { Length: > 0 } ? audit.UploadId : null,
                    audit.DocumentId, null, null, audit.FieldPath, Json(audit.OldValue), Json(audit.NewValue), audit.Rationale));
            }

            db.Outbox.AddRange(outbox.Select(o => new OutboxRow
            {
                Id = o.Id, Channel = o.Channel, Kind = o.Kind, QuoteId = o.QuoteId, Subject = o.Subject, PayloadJson = o.PayloadJson, CreatedUtc = o.CreatedAt.UtcDateTime,
                CreatedBy = o.CreatedBy,
            }));
        }, cancellationToken);

    /// <summary>
    /// Brief 26: inserts or updates dbo.Escalation when the stored version is <paramref name="expectedVersion"/>, with its
    /// audit row and outbox rows, in one transaction. False when another writer got there first.
    /// </summary>
    public async Task<bool> SaveEscalationAsync(EscalationRow next, int expectedVersion, AuditEvent audit, Actor actor, IReadOnlyList<OutboxMessage> outbox,
        CancellationToken cancellationToken = default)
    {
        var saved = false;
        try
        {
            await InTransactionAsync(async db =>
            {
                saved = false;
                var row = await db.Escalations.SingleOrDefaultAsync(e => e.Id == next.Id, cancellationToken).ConfigureAwait(false);
                if ((row?.Version ?? 0) != expectedVersion)
                {
                    return;
                }

                if (row is null)
                {
                    db.Escalations.Add(next);
                }
                else
                {
                    db.Entry(row).CurrentValues.SetValues(next);
                }

                db.AuditEvents.Add(Audit(actor with { UserId = audit.User, Role = audit.Role }, audit.Action, audit.At.UtcDateTime, audit.UploadId is { Length: > 0 } ? audit.UploadId : null,
                    audit.DocumentId, null, null, audit.FieldPath, Json(audit.OldValue), Json(audit.NewValue), audit.Rationale));
                db.Outbox.AddRange(outbox.Select(o => new OutboxRow
                {
                    Id = o.Id, Channel = o.Channel, Kind = o.Kind, QuoteId = o.QuoteId, Subject = o.Subject, PayloadJson = o.PayloadJson, CreatedUtc = o.CreatedAt.UtcDateTime,
                    CreatedBy = o.CreatedBy,
                }));
                saved = true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            return false; // a concurrent writer: the same id inserted (primary key) or the version moved (concurrency token); re-read and merge
        }

        return saved;
    }

    /// <summary>Upserts dbo.QuoteContext and writes the <c>quote.context</c> audit row (old → new JSON) in one transaction.</summary>
    public Task SaveQuoteContextAsync(string quoteId, string contextJson, Actor actor, DateTime at, CancellationToken cancellationToken = default) =>
        InTransactionAsync(async db =>
        {
            var row = await db.QuoteContexts.SingleOrDefaultAsync(q => q.QuoteId == quoteId, cancellationToken).ConfigureAwait(false);
            var old = row?.ContextJson;
            if (row is null)
            {
                row = new QuoteContextRow { QuoteId = quoteId };
                db.QuoteContexts.Add(row);
            }

            (row.ContextJson, row.UpdatedUtc, row.UpdatedBy) = (contextJson, at, actor.UserId);
            db.AuditEvents.Add(Audit(actor, "quote.context", at, null, null, fieldPath: QuoteAudit.FieldPath(quoteId), oldValue: old, newValue: contextJson,
                rationale: "submission context (exposures, last day worked)"));
        }, cancellationToken);

    /// <summary>Upserts dbo.QuoteLabel and writes one audit row per change (quote.rename, quote.retire, quote.restore) in one transaction.</summary>
    public Task SaveQuoteLabelAsync(QuoteLabel label, string rationale, Actor actor, CancellationToken cancellationToken = default) =>
        InTransactionAsync(async db =>
        {
            var row = await db.QuoteLabels.SingleOrDefaultAsync(q => q.QuoteId == label.QuoteId, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                row = new QuoteLabelRow { QuoteId = label.QuoteId };
                db.QuoteLabels.Add(row);
            }

            var at = label.UpdatedAt.UtcDateTime;
            var path = QuoteAudit.FieldPath(label.QuoteId);
            if (row.DisplayName != label.DisplayName)
            {
                db.AuditEvents.Add(Audit(actor, "quote.rename", at, null, null, fieldPath: path, oldValue: row.DisplayName, newValue: label.DisplayName, rationale: rationale));
            }

            if ((row.RetiredUtc is null) != (label.RetiredAt is null))
            {
                db.AuditEvents.Add(Audit(actor, label.RetiredAt is null ? "quote.restore" : "quote.retire", at, null, null, fieldPath: path,
                    oldValue: row.RetiredUtc is null ? "active" : "retired", newValue: label.RetiredAt is null ? "active" : "retired", rationale: rationale));
            }

            (row.DisplayName, row.RetiredUtc, row.RetiredBy, row.UpdatedUtc, row.UpdatedBy) =
                (label.DisplayName, label.RetiredAt?.UtcDateTime, label.RetiredBy, at, label.UpdatedBy);
        }, cancellationToken);

    /// <summary>
    /// Brief 32 item 4: upserts dbo.QuoteOwner (system-versioned, so every owner it had stays answerable) and writes the <c>quote.owner</c> audit row,
    /// in one transaction. Null <see cref="QuoteOwner.ApplicantUserId"/>: underwriter roles only.
    /// </summary>
    public Task SetQuoteOwnerAsync(QuoteOwner owner, string rationale, Actor actor, CancellationToken cancellationToken = default) =>
        InTransactionAsync(async db =>
        {
            var row = await db.QuoteOwners.SingleOrDefaultAsync(q => q.QuoteId == owner.QuoteId, cancellationToken).ConfigureAwait(false);
            var old = row?.ApplicantUserId;
            if (row is null)
            {
                row = new QuoteOwnerRow { QuoteId = owner.QuoteId, CreatedUtc = owner.CreatedAt.UtcDateTime, CreatedBy = owner.CreatedBy };
                db.QuoteOwners.Add(row);
            }

            row.ApplicantUserId = owner.ApplicantUserId;
            db.AuditEvents.Add(Audit(actor, "quote.owner", owner.CreatedAt.UtcDateTime, null, null, fieldPath: QuoteAudit.FieldPath(owner.QuoteId),
                oldValue: old ?? QuoteAudit.UnderwritersOnly, newValue: owner.ApplicantUserId ?? QuoteAudit.UnderwritersOnly, rationale: rationale));
        }, cancellationToken);

    /// <summary>Upserts dbo.Workspace and writes its audit row (workspace.create / workspace.delete) in one transaction.</summary>
    public Task SaveWorkspaceAsync(ChatWorkspace workspace, Actor actor, string action, CancellationToken cancellationToken = default, string? detail = null) =>
        InTransactionAsync(async db =>
        {
            var row = await db.Workspaces.SingleOrDefaultAsync(w => w.Id == workspace.WorkspaceId, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                row = new WorkspaceRow { Id = workspace.WorkspaceId };
                db.Workspaces.Add(row);
            }

            (row.OwnerUserId, row.Name, row.CreatedUtc, row.ExpiresUtc, row.DeletedUtc) =
                (workspace.OwnerUserId, workspace.Name, workspace.CreatedAt.UtcDateTime, workspace.ExpiresAt.UtcDateTime, workspace.DeletedAt?.UtcDateTime);
            db.AuditEvents.Add(Audit(actor, action, (workspace.DeletedAt ?? workspace.CreatedAt).UtcDateTime, null, null,
                fieldPath: $"workspaces/{workspace.WorkspaceId}", newValue: detail, rationale: workspace.Name));
        }, cancellationToken);

    /// <summary>Appends the chat turn (ledger) and its chat.turn audit row in one transaction.</summary>
    public Task SaveChatTurnAsync(ChatTurnRow turn, Actor actor, CancellationToken cancellationToken = default) =>
        InTransactionAsync(db =>
        {
            db.ChatTurns.Add(turn);
            var where = turn.QuoteId is { } q ? $"quotes/{q}" : turn.WorkspaceId is { } w ? $"workspaces/{w}" : "org";
            db.AuditEvents.Add(Audit(actor, "chat.turn", turn.AtUtc, null, null, fieldPath: where,
                newValue: $"{turn.Id}: {turn.CitationsJson.Length} chars of citations, cost {turn.CostUsd:0.000000}",
                rationale: turn.Question.Length > 1900 ? turn.Question[..1900] : turn.Question));
            return Task.CompletedTask;
        }, cancellationToken);

    // ------------------------------------------------------------------ helpers

    internal async Task InTransactionAsync(Func<LossRunDbContext, Task> work, CancellationToken cancellationToken)
    {
        await using var db = contexts();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            await work(db).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static AuditEventRow Audit(Actor actor, string action, DateTime at, string? uploadId, string? documentId, int? revision = null, Guid? claimId = null,
        string? fieldPath = null, string? oldValue = null, string? newValue = null, string? rationale = null, long? overrideId = null, string? formatRef = null) => new()
        {
            AtUtc = at, UserId = actor.UserId, Role = actor.Role, Action = action, UploadId = uploadId, DocumentId = documentId, DocumentRevision = revision, ClaimId = claimId,
            FieldPath = fieldPath, OldValue = Clip(oldValue), NewValue = Clip(newValue), Rationale = Clip(rationale, 2000), OverrideId = overrideId,
            CatalogVersion = RuleCatalog.Embedded.CatalogVersion, FormatRef = formatRef, Source = actor.Source, CorrelationId = actor.CorrelationId,
        };

    internal static IEnumerable<ExtractedValueRow> ExtractedValues(DocumentResult result, DateTime now)
    {
        var node = JsonSerializer.SerializeToNode(result.Document, CanonicalJson.Options)!;
        foreach (var (path, lineage) in (result.Document.Lineage ?? []).OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            var position = JsonPath.ClaimPosition(path);
            yield return new ExtractedValueRow
            {
                DocumentId = result.DocumentId, DocumentRevision = result.Revision,
                ClaimId = position is { } p ? Ids.Claim(result.DocumentId, p.Period, p.Claim) : null, FieldPath = path, ClaimField = position?.Field,
                Value = Clip(DocumentEdits.Text(JsonPath.Get(node, path))), RawText = Clip(lineage.SourceText), Confidence = lineage.Confidence, Extractor = lineage.Extractor,
                FormatRef = lineage.FormatId is null ? result.FormatRef : $"{lineage.FormatId}@{lineage.FormatVersion}", Page = lineage.Page,
                PolygonJson = lineage.Polygon is { Count: > 0 } poly ? JsonSerializer.Serialize(poly) : null, Sheet = lineage.Sheet, CellA1 = lineage.Cell,
                Model = lineage.Model, PromptVersion = lineage.PromptVersion, RecordedUtc = now,
            };
        }
    }

    internal static IEnumerable<RuleOutcomeRow> Outcomes(DocumentResult result) =>
        result.RuleOutcomes.Select((o, i) => new RuleOutcomeRow
        {
            DocumentId = result.DocumentId, DocumentRevision = result.Revision, Ordinal = i, RuleId = o.RuleId, Severity = o.Severity.ToString(), Status = o.Status.ToString(),
            Message = Clip(o.Message, 2000)!, AffectedFieldsJson = JsonSerializer.Serialize(o.AffectedFields), ClaimRef = o.ClaimId, PeriodIndex = o.PeriodIndex,
            Detail = Clip(o.Detail, 2000),
        });

    internal static string Serialize(DocumentResult result) => JsonSerializer.Serialize(result, CanonicalJson.Options);

    private static string? Json(JsonElement? value) => value is { } v ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;

    private static string? Clip(string? text, int max = 4000) => text is null || text.Length <= max ? text : text[..max];
}

/// <summary>Deterministic ids, so persisting and backfill are idempotent: a claim is its (document, period, claim) position.</summary>
public static class Ids
{
    public static Guid Period(string documentId, int period) => From($"{documentId}/p{period}");

    public static Guid Claim(string documentId, int period, int claim) => From($"{documentId}/p{period}/c{claim}");

    private static Guid From(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
}

/// <summary>Canonical claim ↔ dbo.Claim columns.</summary>
public static class ClaimColumns
{
    public static void Project(Claim c, LossRunDocument d, PolicyPeriod p, ClaimRow row)
    {
        Set(row, nameof(row.ClaimNumber), c.ClaimNumber);
        Set(row, nameof(row.ClaimantName), c.ClaimantName);
        Set(row, nameof(row.DateOfInjury), c.DateOfInjury);
        Set(row, nameof(row.DateReported), c.DateReported);
        Set(row, nameof(row.DateClosed), c.DateClosed);
        Set(row, nameof(row.Status), c.Status?.ToString());
        Set(row, nameof(row.RawStatus), c.RawStatus);
        Set(row, nameof(row.ClaimType), c.ClaimType?.ToString());
        Set(row, nameof(row.InjuryTypeCode), c.InjuryTypeCode);
        Set(row, nameof(row.NatureOfInjuryCode), c.NatureOfInjury?.Code);
        Set(row, nameof(row.CauseOfInjuryCode), c.CauseOfInjury?.Code);
        Set(row, nameof(row.PartOfBodyCode), c.PartOfBody?.Code);
        Set(row, nameof(row.AccidentDescription), c.AccidentDescription is { Length: > 2000 } a ? a[..2000] : c.AccidentDescription);
        Set(row, nameof(row.LitigationFlag), c.LitigationFlag);
        Set(row, nameof(row.CumulativeTrauma), c.CumulativeTrauma);
        Set(row, nameof(row.JurisdictionState), c.JurisdictionState);
        Set(row, nameof(row.ClassCode), c.ClassCode);
        Set(row, nameof(row.IndemnityPaid), c.Indemnity?.Paid);
        Set(row, nameof(row.IndemnityReserve), c.Indemnity?.Reserve);
        Set(row, nameof(row.IndemnityIncurred), c.Indemnity?.Incurred);
        Set(row, nameof(row.MedicalPaid), c.Medical?.Paid);
        Set(row, nameof(row.MedicalReserve), c.Medical?.Reserve);
        Set(row, nameof(row.MedicalIncurred), c.Medical?.Incurred);
        Set(row, nameof(row.ExpensePaid), c.Expense?.Paid);
        Set(row, nameof(row.ExpenseReserve), c.Expense?.Reserve);
        Set(row, nameof(row.ExpenseIncurred), c.Expense?.Incurred);
        Set(row, nameof(row.TotalPaid), c.Total?.Paid);
        Set(row, nameof(row.TotalReserve), c.Total?.Reserve);
        Set(row, nameof(row.TotalIncurred), c.Total?.Incurred);
        Set(row, nameof(row.RecoverySubrogation), c.RecoverySubrogation);
        Set(row, nameof(row.NetIncurred), c.NetIncurred);
        Set(row, nameof(row.ExpenseTreatment), d.ExpenseTreatment.ToString());
        Set(row, nameof(row.DetailLevel), p.DetailLevel.ToString());
    }

    /// <summary>Claim-relative field path → column ("medical.paid" → MedicalPaid), for the audit proof.</summary>
    public static string? Column(string claimField) => claimField switch
    {
        "claimNumber" => nameof(ClaimRow.ClaimNumber),
        "claimantName" => nameof(ClaimRow.ClaimantName),
        "dateOfInjury" => nameof(ClaimRow.DateOfInjury),
        "dateReported" => nameof(ClaimRow.DateReported),
        "dateClosed" => nameof(ClaimRow.DateClosed),
        "status" => nameof(ClaimRow.Status),
        "rawStatus" => nameof(ClaimRow.RawStatus),
        "claimType" => nameof(ClaimRow.ClaimType),
        "jurisdictionState" => nameof(ClaimRow.JurisdictionState),
        "classCode" => nameof(ClaimRow.ClassCode),
        "litigationFlag" => nameof(ClaimRow.LitigationFlag),
        "accidentDescription" => nameof(ClaimRow.AccidentDescription),
        _ when claimField.Split('.') is [var component, var measure] && component is "indemnity" or "medical" or "expense" or "total"
            && measure is "paid" or "reserve" or "incurred" => char.ToUpperInvariant(component[0]) + component[1..] + char.ToUpperInvariant(measure[0]) + measure[1..],
        _ => null,
    };

    // Assign only when different, so EF marks only real changes modified (one temporal version per changed claim).
    private static void Set<T>(ClaimRow row, string property, T value)
    {
        var info = typeof(ClaimRow).GetProperty(property)!;
        if (!Equals(info.GetValue(row), value))
        {
            info.SetValue(row, value);
        }
    }
}
