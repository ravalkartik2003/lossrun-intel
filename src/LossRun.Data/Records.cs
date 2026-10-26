using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Rules;

namespace LossRun.Data;

/// <summary>Who caused a mutation, through which channel (api | ui | system | backfill), for the audit row.</summary>
public sealed record Actor(string UserId, string Role, string Source = "system", string? CorrelationId = null)
{
    public static readonly Actor System = new("system", "system");
}

/// <summary>One field changed by a review decision: an underwriter <c>override</c> or a <c>rederive</c> of a value the format derived from it.</summary>
public sealed record FieldChange(string Kind, string FieldPath, string? OldValue, string? NewValue, string Rationale);

/// <summary>A decided review: the document before and after, and every field change between them.</summary>
/// <param name="QuoteBump">Brief 24 step 3: the quote revision this decision bumps (null when the document has no quote).</param>
/// <param name="KeepReviewOpen">Brief 30 item 15: a direct override on a document whose confirm-format review is pending applies the values and leaves
/// that review (the format decision) open; only a decision on the review closes it.</param>
public sealed record DecisionRecord(string UploadId, DocumentResult Before, DocumentResult After, ReviewDecision Decision, IReadOnlyList<FieldChange> Changes,
    bool FormatApproved, QuoteRevisionBump? QuoteBump = null, bool KeepReviewOpen = false);

/// <summary>
/// The system of record behind the pipeline and the API (brief 09): manifests, document results, reviews, usage and
/// audit. Two implementations: Blob (brief 07 layout; local runs and tests) and SQL (<see cref="SqlRecordStore"/>,
/// where <see cref="ClaimCommandService"/> is the only writer of Claim, Override and AuditEvent).
/// </summary>
public interface IRecordStore
{
    Task<UploadManifest?> GetManifestAsync(string uploadId, CancellationToken cancellationToken = default);

    /// <summary>Read-modify-write of one manifest (created when <paramref name="change"/> gets null), safe under concurrent activities.</summary>
    Task<UploadManifest> UpdateManifestAsync(string uploadId, Func<UploadManifest?, UploadManifest> change, Actor actor, CancellationToken cancellationToken = default);

    Task IndexDocumentAsync(string documentId, string uploadId, CancellationToken cancellationToken = default);

    Task<string?> UploadOfDocumentAsync(string documentId, CancellationToken cancellationToken = default);

    /// <summary>A processed document's upload, quote and open review in one query (brief 12b: the override path's look-ups).</summary>
    Task<DocumentHead?> DocumentHeadAsync(string documentId, CancellationToken cancellationToken = default);

    Task<DocumentResult?> GetResultAsync(string documentId, CancellationToken cancellationToken = default);

    /// <summary>Persists a freshly extracted document (revision 1): claims, as-reported values with lineage, rule outcomes, cost.</summary>
    Task SaveResultAsync(DocumentResult result, Actor actor, CancellationToken cancellationToken = default);

    Task<ReviewItem?> GetReviewAsync(string documentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReviewItem>> ListReviewsAsync(string state, CancellationToken cancellationToken = default);

    Task OpenReviewAsync(ReviewItem item, CancellationToken cancellationToken = default);

    /// <summary>Applies a decided review in one unit: the new revision, overrides, re-derived values, audit events, the closed review item.</summary>
    /// <param name="outbox">Rows that commit with the decision (brief 24 step 3: review.decided, quote.revised, quote.payload). Keyed on the decision
    /// (brief 30 item 6): when its <c>review.decided</c> row is already stored the decision was applied and nothing is written again.</param>
    /// <returns>Applied; AlreadyApplied (a retry of this decision); Conflict (another decision took the revision: nothing written, brief 30 item 7).</returns>
    Task<DecisionApplyResult> ApplyDecisionAsync(DecisionRecord record, CancellationToken cancellationToken = default, IReadOnlyList<OutboxMessage>? outbox = null);

    Task ExpireReviewAsync(string uploadId, string documentId, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reopen for review (11 Answers 2): the review item becomes pending again, the document's reviewState becomes
    /// <c>reopened</c>, and a <c>review.reopened</c> audit event records who and why, in one unit. Nothing else changes until a decision.
    /// </summary>
    Task ReopenReviewAsync(ReviewItem item, DocumentResult result, Actor actor, string reason, CancellationToken cancellationToken = default);

    /// <summary>Applies a decision outside the upload orchestration (reopened review, direct override); false when another
    /// decision got the revision first (the caller re-reads and retries).</summary>
    /// <param name="outbox">Rows that commit with the decision (its <c>review.decided</c> notification).</param>
    Task<bool> TryApplyDecisionAsync(DecisionRecord record, CancellationToken cancellationToken = default, IReadOnlyList<OutboxMessage>? outbox = null);

    /// <summary>An audit event for a mutation that has no other record here (format retire/approve, export, quote push).</summary>
    Task AuditAsync(AuditEvent audit, Actor actor, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------------ transactional outbox (brief 12b)

    /// <summary>The audit row and the outbox rows in one transaction (SQL). An empty <see cref="AuditEvent.Action"/> writes the outbox rows alone.</summary>
    /// <param name="bump">Brief 24 step 3: bump this quote's revision in the same unit and put it into the rows (<see cref="QuoteRevisions.Token"/>).</param>
    Task AuditWithOutboxAsync(AuditEvent audit, Actor actor, IReadOnlyList<OutboxMessage> outbox, CancellationToken cancellationToken = default, QuoteRevisionBump? bump = null);

    /// <summary>Brief 24 step 3: the quote's current revision (0 before its first upload completes).</summary>
    Task<int> QuoteRevisionAsync(string quoteId, CancellationToken cancellationToken = default);

    Task<OutboxMessage?> GetOutboxAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Unpublished rows created before <paramref name="createdBefore"/>, oldest first.</summary>
    Task<IReadOnlyList<OutboxMessage>> PendingOutboxAsync(DateTimeOffset createdBefore, int max, CancellationToken cancellationToken = default);

    Task MarkOutboxPublishedAsync(string id, DateTimeOffset at, CancellationToken cancellationToken = default);

    Task MarkOutboxFailedAsync(string id, string error, CancellationToken cancellationToken = default);

    /// <summary>What the receiver answered (dispatcher): delivered | replayed | rejected, with the response body.</summary>
    Task RecordOutboxDeliveryAsync(string id, string status, string? deliveryJson, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>The quote's outbox rows (or all, when <paramref name="quoteId"/> is null), newest first.</summary>
    Task<IReadOnlyList<OutboxMessage>> OutboxAsync(string? quoteId, int max, CancellationToken cancellationToken = default);

    Task RecordUsageAsync(string uploadId, UsageEvent usage, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UsageEvent>> ReadUsageAsync(string uploadId, CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------- analytics (brief 10)

    /// <summary>Loss-run documents submitted under a quote with their current revision, ordered by id (the snapshot cache key, without loading results).</summary>
    Task<IReadOnlyList<QuoteDocument>> DocumentsOfQuoteAsync(string quoteId, CancellationToken cancellationToken = default);

    /// <summary>Overrides applied to a document, oldest first (as-reported = before each field's first override).</summary>
    Task<IReadOnlyList<StoredOverride>> OverridesOfDocumentAsync(string documentId, CancellationToken cancellationToken = default);

    /// <summary>Every document of the quote with its overrides, in id order: the analytics account in two queries (brief 12b), not two per document.</summary>
    /// <param name="only">When given, just these documents of the quote (the analytics cache reloads only what changed).</param>
    /// <summary>The given documents with their overrides (two queries, whatever the count).</summary>
    Task<IReadOnlyList<QuoteResult>> ResultsAsync(IReadOnlyCollection<string> documentIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QuoteResult>> QuoteResultsAsync(string quoteId, CancellationToken cancellationToken = default, IReadOnlyCollection<string>? only = null);

    /// <summary>A cached feature snapshot for the key, unless a claim change invalidated it.</summary>
    Task<StoredFeatureSnapshot?> GetFeatureSnapshotAsync(FeatureSnapshotKey key, CancellationToken cancellationToken = default);

    Task SaveFeatureSnapshotAsync(StoredFeatureSnapshot snapshot, CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------- workbench (brief 11)

    /// <summary>The quote's submission context (exposures, last day worked); null when none was supplied.</summary>
    Task<QuoteContext?> GetQuoteContextAsync(string quoteId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the quote context, with a <c>quote.context</c> audit event (old → new) in the same unit.</summary>
    Task SaveQuoteContextAsync(QuoteContext context, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Quotes that have loss-run documents, most recently updated first.</summary>
    /// <param name="includeRetired">Also list retired quotes (brief 13); each summary carries its display name and retired time.</param>
    Task<IReadOnlyList<QuoteSummary>> ListQuotesAsync(CancellationToken cancellationToken = default, bool includeRetired = false);

    Task<QuoteLabel?> GetQuoteLabelAsync(string quoteId, CancellationToken cancellationToken = default);

    /// <summary>Brief 30 item 1: the quote's owner record (dbo.QuoteOwner), null when none was written.</summary>
    Task<QuoteOwner?> GetQuoteOwnerAsync(string quoteId, CancellationToken cancellationToken = default);

    /// <summary>Writes the owner record when the quote has none and returns the record that holds (the existing one when another upload got there first). Never updates.</summary>
    Task<QuoteOwner> ClaimQuoteOwnerAsync(QuoteOwner owner, CancellationToken cancellationToken = default);

    /// <summary>Brief 32 item 4: transfers or clears the owner (UWManager/Admin), with a <c>quote.owner</c> audit event (old → new) in the same unit.</summary>
    Task SetQuoteOwnerAsync(QuoteOwner owner, string rationale, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Saves the label and audits each change (quote.rename / quote.retire / quote.restore) with the rationale.</summary>
    Task SaveQuoteLabelAsync(QuoteLabel label, string rationale, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Audit events, newest first: those of one document, or of a quote (its documents' events plus quote-level events,
    /// which carry <c>FieldPath = quotes/{quoteId}</c> because the ledger table has no quote column and cannot be altered).
    /// </summary>
    Task<AuditTrailView> AuditTrailAsync(string? documentId, string? quoteId, int max, CancellationToken cancellationToken = default);

    /// <summary>Every document's metered cost (dbo.DocumentCost; the results' cost lines in Blob mode).</summary>
    Task<IReadOnlyList<DocumentCostLine>> DocumentCostsAsync(CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------- chat (brief 12)

    /// <summary>
    /// Loss-run documents (with a stored result) a chat scope may read: <c>org</c> = every document not in a workspace,
    /// <c>quote</c> = the quote's documents, <c>workspace</c> = the documents uploaded into that workspace. The chat
    /// tools resolve their scope only through this method.
    /// </summary>
    Task<IReadOnlyList<ScopedDocument>> DocumentsInScopeAsync(string scope, string? quoteId, string? workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a workspace (delete = set DeletedAt), with a workspace.create / workspace.delete audit event.</summary>
    Task SaveWorkspaceAsync(ChatWorkspace workspace, Actor actor, string action, CancellationToken cancellationToken = default, string? detail = null);

    /// <summary>Every upload made into the workspace (12 Answers: expiry purges their files and extraction JSON).</summary>
    Task<IReadOnlyList<string>> UploadsOfWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Brief 23 follow-up: the uploads that name a quote, documents or not (a failed upload has none).</summary>
    Task<IReadOnlyList<string>> UploadsOfQuoteAsync(string quoteId, CancellationToken cancellationToken = default);

    Task<ChatWorkspace?> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Workspaces past their expiry that are not deleted yet (the cleanup timer).</summary>
    Task<IReadOnlyList<ChatWorkspace>> ExpiredWorkspacesAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Appends a chat turn (dbo.ChatTurn ledger) and its chat.turn audit event in one unit.</summary>
    Task SaveChatTurnAsync(ChatTurnResult turn, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>A conversation's turns, oldest first.</summary>
    Task<IReadOnlyList<ChatTurnResult>> ConversationAsync(string conversationId, CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------- engineering escalation (brief 26, ADR 0025)

    Task<Escalation?> GetEscalationAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Every escalation, most recently seen first (a POC-sized queue; the API filters).</summary>
    Task<IReadOnlyList<Escalation>> ListEscalationsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the record if the stored version is <paramref name="expectedVersion"/> (0 = it must not exist yet), with its
    /// audit event and outbox rows in the same unit; the saved version is <c>expectedVersion + 1</c>. False on a conflict
    /// (the caller re-reads and merges again).
    /// </summary>
    Task<bool> SaveEscalationAsync(Escalation escalation, int expectedVersion, AuditEvent audit, Actor actor, IReadOnlyList<OutboxMessage> outbox,
        CancellationToken cancellationToken = default);

    /// <summary>Documents processed since <paramref name="since"/>, as the hourly pattern detector reads them.</summary>
    Task<IReadOnlyList<EscalationObservation>> EscalationObservationsAsync(DateTimeOffset since, CancellationToken cancellationToken = default);
}

/// <summary>A document a chat scope can see, with the ids its index chunks carry.</summary>
/// <param name="Revision">The document's revision (the result cache key); 0 when unknown (Blob mode).</param>
/// <param name="FileName">The uploaded file the document came from (citation labels).</param>
public sealed record ScopedDocument(string DocumentId, string UploadId, string? QuoteId, string? WorkspaceId, int Revision = 0, string? FileName = null);

/// <summary>
/// Parsed document results by (document, revision), shared by the analytics endpoints and the chat tools (brief 13): a
/// 1,046-claim account's result JSON took seconds to load and parse per request. A revision is immutable, so an entry never
/// goes stale; an override bumps one revision, so only that document reloads.
/// </summary>
public static class ResultCache
{
    private const int Capacity = 4000;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Id, int Revision), QuoteResult> Entries = new();

    public static async Task<IReadOnlyList<QuoteResult>> GetAsync(IRecordStore records, IReadOnlyList<(string Id, int Revision)> documents, CancellationToken cancellationToken = default)
    {
        // Revision 0 = unknown (Blob mode): always loaded fresh, never cached.
        var fresh = new Dictionary<string, QuoteResult>(StringComparer.Ordinal);
        var missing = documents.Where(d => d.Revision <= 0 || !Entries.ContainsKey(d)).Select(d => d.Id).Distinct().ToList();
        if (missing.Count > 0)
        {
            if (Entries.Count + missing.Count > Capacity)
            {
                Entries.Clear();
            }

            foreach (var loaded in await records.ResultsAsync(missing, cancellationToken).ConfigureAwait(false))
            {
                fresh[loaded.Result.DocumentId] = loaded;
                Entries[(loaded.Result.DocumentId, loaded.Result.Revision)] = loaded;
            }
        }

        var list = new List<QuoteResult>(documents.Count);
        foreach (var d in documents)
        {
            if (d.Revision > 0 && Entries.TryGetValue(d, out var hit) || fresh.TryGetValue(d.Id, out hit))
            {
                list.Add(hit);
            }
        }

        return list;
    }
}

/// <summary>Quote-level audit rows carry this field path (see <see cref="IRecordStore.AuditTrailAsync"/>).</summary>
public static class QuoteAudit
{
    public static string FieldPath(string quoteId) => $"quotes/{quoteId}";

    /// <summary>Brief 32 item 4: how a <c>quote.owner</c> audit row writes "no Applicant owner" (underwriter roles only).</summary>
    public const string UnderwritersOnly = "underwriter roles only";
}

/// <param name="ReviewState">Brief 30 item 16: part of the document's identity for the quote's caches — a reopen changes it without a new revision.</param>
public sealed record QuoteDocument(string DocumentId, int Revision, string? ReviewState = null);

/// <summary>An override as stored (dbo.Override, or the Blob overrides file).</summary>
public sealed record DocumentHead(string DocumentId, string UploadId, string? QuoteId, string? ReviewState, string? ReviewKind);

public sealed record QuoteResult(DocumentResult Result, IReadOnlyList<StoredOverride> Overrides);

/// <param name="Role">Brief 24 step 5: the reviewer's role (dbo.Override.Role).</param>
/// <param name="SupersedesOverrideId">The field's previous override this one replaced (the corrections chain).</param>
/// <param name="Revision">The document revision the override produced.</param>
public sealed record StoredOverride(string Id, string DocumentId, string FieldPath, string? OldValue, string? NewValue, string? Rationale, string? UserId, DateTimeOffset AtUtc,
    string? Role = null, string? SupersedesOverrideId = null, int? Revision = null);

/// <summary>Identity of a cached feature snapshot (dbo.FeatureSnapshot): quote, document revisions, catalog, view, scenario.</summary>
public sealed record FeatureSnapshotKey(string QuoteId, string RevisionKey, string CatalogVersion, string View, string ScenarioHash);

public sealed record StoredFeatureSnapshot(FeatureSnapshotKey Key, string InputsHash, int ClaimCount, string Json, DateTimeOffset CreatedUtc);

/// <summary>
/// Applies a review decision to a document result without touching storage (brief 07 semantics): overrides carry
/// lineage extractor <c>manual</c>; values the format derived from an overridden field ("a + b") are recomputed; rules
/// re-run; the revision increments.
/// </summary>
/// <summary>Brief 30 items 6–7: what applying a decision did.</summary>
public enum DecisionApplyResult
{
    Applied,
    AlreadyApplied,
    Conflict,
}

public static class DocumentEdits
{
    /// <param name="context">Brief 29 §3: the quote's rule context (R-15, R-25); null keeps them not-applicable.</param>
    public static (DocumentResult After, IReadOnlyList<FieldChange> Changes) Apply(DocumentResult before, ReviewDecision decision, RuleContext? context = null)
    {
        var node = JsonSerializer.SerializeToNode(before.Document, CanonicalJson.Options)!.AsObject();
        node.Remove("lineage");
        var lineage = new Dictionary<string, LineageRecord>(before.Document.Lineage ?? [], StringComparer.Ordinal);
        var changes = new List<FieldChange>();
        if (decision.Action != ReviewActions.Override && decision.Overrides.Count > 0)
        {
            // Brief 24 step 1: never drop corrections silently (the API answers 400 overrides-not-allowed first).
            throw new ArgumentException($"A {decision.Action} decision carries {decision.Overrides.Count} override(s); overrides belong to an override decision.", nameof(decision));
        }

        var overrides = decision.Overrides;
        foreach (var change in overrides)
        {
            var old = JsonPath.Get(node, change.FieldPath);
            JsonPath.Set(node, change.FieldPath, JsonNode.Parse(change.NewValue.GetRawText()));
            var prior = lineage.GetValueOrDefault(change.FieldPath);
            lineage[change.FieldPath] = new LineageRecord
            {
                DocumentId = decision.DocumentId, Extractor = "manual", SourceText = change.Rationale, Confidence = 1.0,
                Page = prior?.Page, Polygon = prior?.Polygon, Sheet = prior?.Sheet, Cell = prior?.Cell,
            };
            changes.Add(new FieldChange("override", change.FieldPath, Text(old), change.NewValue.ValueKind == JsonValueKind.Null ? null : Text(JsonNode.Parse(change.NewValue.GetRawText())),
                change.Rationale));
        }

        var changed = overrides.Select(o => o.FieldPath).ToHashSet(StringComparer.Ordinal);
        foreach (var (derivedPath, record) in lineage.Where(l => l.Value.Extractor == "derived").ToList())
        {
            var sources = (record.SourceText ?? "").Split(" + ", StringSplitOptions.RemoveEmptyEntries);
            if (!sources.Any(changed.Contains) || sources.Select(s => JsonPath.Get(node, s)).ToList() is not { } values || values.Any(v => v is null))
            {
                continue;
            }

            var sum = values.Sum(v => v!.GetValue<decimal>());
            changes.Add(new FieldChange("rederive", derivedPath, Text(JsonPath.Get(node, derivedPath)), sum.ToString(CultureInfo.InvariantCulture),
                $"recomputed from {record.SourceText} after override"));
            JsonPath.Set(node, derivedPath, JsonValue.Create(sum));
        }

        var document = CanonicalJson.Deserialize(node.ToJsonString(CanonicalJson.Options));
        document.Lineage = lineage;
        var after = new DocumentResult
        {
            DocumentId = before.DocumentId, UploadId = before.UploadId, Revision = before.Revision + 1, Document = document,
            RuleOutcomes = [.. RuleEvaluator.Default.Evaluate(document, context).Outcomes], Routing = before.Routing, FormatRef = before.FormatRef, Cost = before.Cost,
            ReviewState = decision.Action,
        };
        return (after, changes);
    }

    /// <summary>A JSON value as stored in the audit/override columns: strings unquoted, everything else as JSON.</summary>
    public static string? Text(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(CanonicalJson.Options),
    };
}

/// <summary>Get/set by lineage path ("policyPeriods[0].claims[1].medical.paid") on a canonical document node.</summary>
public static class JsonPath
{
    private static IEnumerable<object> Segments(string path) =>
        path.Split('.').SelectMany(part =>
        {
            var name = part.Split('[')[0];
            var indexes = part.Split('[').Skip(1).Select(i => (object)int.Parse(i.TrimEnd(']'), CultureInfo.InvariantCulture));
            return new object[] { name }.Concat(indexes);
        });

    public static JsonNode? Get(JsonNode root, string path)
    {
        JsonNode? node = root;
        foreach (var segment in Segments(path))
        {
            node = segment switch { string name => (node as JsonObject)?[name], int index => node is JsonArray a && index < a.Count ? a[index] : null, _ => null };
        }

        return node;
    }

    public static void Set(JsonNode root, string path, JsonNode? value)
    {
        var segments = Segments(path).ToList();
        var node = root;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            JsonNode? next;
            if (segments[i] is string name)
            {
                var obj = (JsonObject)node;
                next = obj[name];
                if (next is null)
                {
                    next = segments[i + 1] is int ? new JsonArray() : new JsonObject();
                    obj[name] = next;
                }
            }
            else
            {
                next = ((JsonArray)node)[(int)segments[i]];
            }

            node = next ?? throw new ArgumentException($"Path {path} does not exist.", nameof(path));
        }

        switch (segments[^1])
        {
            case string last:
                ((JsonObject)node)[last] = value;
                break;
            case int index:
                ((JsonArray)node)[index] = value;
                break;
        }
    }

    /// <summary>(period index, claim index, claim-relative field) of a claim path, e.g. (0, 2, "medical.paid"); null for document/period paths.</summary>
    public static (int Period, int Claim, string Field)? ClaimPosition(string path)
    {
        const string periods = "policyPeriods[";
        if (!path.StartsWith(periods, StringComparison.Ordinal))
        {
            return null;
        }

        var close = path.IndexOf(']', StringComparison.Ordinal);
        var rest = path[(close + 1)..];
        if (!rest.StartsWith(".claims[", StringComparison.Ordinal))
        {
            return null;
        }

        var claimClose = rest.IndexOf(']', StringComparison.Ordinal);
        return (int.Parse(path[periods.Length..close], CultureInfo.InvariantCulture), int.Parse(rest[".claims[".Length..claimClose], CultureInfo.InvariantCulture),
            rest[(claimClose + 2)..]);
    }
}

/// <summary>What expiry removed from storage for one upload: blob names deleted, and any an immutability policy still retains.</summary>
public sealed record UploadPurge(string UploadId, IReadOnlyList<string> Deleted, IReadOnlyList<string> Retained);
