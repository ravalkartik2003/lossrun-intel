using System.Text.Json;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Rules;

namespace LossRun.Api.Contracts;

// HTTP API v1 data transfer objects (docs/api/openapi.yaml is the source of truth; ApiContractTests keeps the two in
// sync). Shared by the Functions host, the submission-engine simulator and, via the OpenAPI document, the UI.
// JSON: camelCase, enums as camelCase strings, nulls omitted (CanonicalJson.Options).

public static class UploadStatuses
{
    public const string Received = "received";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string AwaitingReview = "awaiting-review";
    public const string Closed = "closed";
    public const string Failed = "failed";
}

/// <summary>Manifest item statuses (ADR 0005) plus the in-flight and review states.</summary>
public static class ItemStatuses
{
    public const string Processed = "processed";
    public const string KeptNotProcessed = "kept-not-processed";
    public const string Unsupported = "unsupported";
    public const string Processing = "processing";
    public const string AwaitingReview = "awaiting-review";
    public const string Rejected = "rejected";
}

public static class Roles
{
    public const string Applicant = "Applicant";
    public const string Underwriter = "Underwriter";
    public const string UWManager = "UWManager";
    public const string Admin = "Admin";

    /// <summary>Brief 26 Answers 3: works the investigation queue and reads everything a reviewer reads; never decides or overrides.</summary>
    public const string Engineer = "Engineer";

    /// <summary>Brief 25 Answers 2: the quote system's service identity — quote payload, upload manifests, integration status; writes nothing.</summary>
    public const string QuoteSystem = "QuoteSystem";

    /// <summary>Decides reviews, overrides values, acts on quotes.</summary>
    public static bool CanReview(string role) => role is Underwriter or UWManager or Admin;

    /// <summary>Reads what a reviewer reads (documents, reviews, analytics, audit, costs).</summary>
    public static bool CanRead(string role) => CanReview(role) || role is Engineer;

    /// <summary>Reads the quote payload: the readers plus the quote system.</summary>
    public static bool CanReadQuotePayload(string role) => CanRead(role) || role is QuoteSystem;
}

/// <summary><c>202</c> response of <c>POST /api/v1/uploads</c>.</summary>
public sealed record UploadAccepted(string UploadId, string StatusUrl, string EventsUrl, bool Existing);

/// <summary>What the submission says about the upload (stored with the manifest; feeds <see cref="SubmissionContext"/>).</summary>
/// <param name="ValuationDate">Valuation date the submission states (used only when the document prints none; 06b Answers 1).</param>
/// <param name="PolicyPeriods">Policy periods the submission states (fill unprinted policy dates, matched by policy number).</param>
/// <param name="PayrollByPeriod">Payroll per policy period (dollars), when the submission states it; merged into the quote context.</param>
/// <param name="FteByPeriod">FTE count per policy period, when the submission states it.</param>
/// <param name="LastDayWorked">Account-level last day worked (a single-claimant submission, or a plant closure date).</param>
/// <param name="WorkspaceId">Ad-hoc chat workspace the upload belongs to (brief 12); such uploads have no quote.</param>
public sealed record SubmissionInfo(string? QuoteId, string? CarrierHint, string UploaderRole, string UploaderId, DateOnly? ValuationDate = null,
    IReadOnlyList<ContextPeriod>? PolicyPeriods = null, IReadOnlyList<PeriodExposure>? PayrollByPeriod = null, IReadOnlyList<PeriodExposure>? FteByPeriod = null,
    DateOnly? LastDayWorked = null, string? WorkspaceId = null, DateOnly? TargetPolicyEffectiveDate = null, IReadOnlyList<int>? RequestedPolicyYears = null)
{
    public SubmissionContext ToContext() => new(CarrierHint, QuoteId, UploaderRole, ValuationDate, PolicyPeriods);
}

/// <summary>Every upload produces one (ADR 0005): each file and segment, its classification, status and reason.</summary>
public sealed class UploadManifest
{
    public string UploadId { get; set; } = "";
    public string Status { get; set; } = UploadStatuses.Received;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public SubmissionInfo Submission { get; set; } = new(null, null, Roles.Applicant, "");
    public List<ManifestFile> Files { get; set; } = [];
    public List<ManifestItem> Items { get; set; } = [];
    public UploadCost? Cost { get; set; }

    /// <summary>Times (ms after <see cref="CreatedAt"/>) the first loss-run claim became available and the upload completed.</summary>
    public long? FirstClaimMs { get; set; }
    public long? CompletedMs { get; set; }
}

public sealed record ManifestFile(string FileName, string Sha256, long SizeBytes, string BlobPath);

public sealed class ManifestItem
{
    /// <summary>Document id (graph and API id); unique per upload.</summary>
    public string DocumentId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? ParentDocumentId { get; set; }
    public string? PageRange { get; set; }

    /// <summary>LossRun, or the classifier category of other documents (Payroll, Letter, …), or the file kind when unsupported.</summary>
    public string Classification { get; set; } = "";
    public string Status { get; set; } = ItemStatuses.Processing;
    public string? Reason { get; set; }
    public string? BlobPath { get; set; }
    public string? RoutingTier { get; set; }
    public string? Badge { get; set; }
    public string? FormatRef { get; set; }
    public string? FormatStatus { get; set; }
    public int? ClaimCount { get; set; }
    public decimal? CostUsd { get; set; }
    public string? ReviewKind { get; set; }

    /// <summary>Brief 23 §2 (ADR 0020 amendment): rows of another line of business in this document, kept, not extracted.</summary>
    public List<KeptRowItem>? KeptRows { get; set; }

    /// <summary>Brief 30 item 4: printed values replay could not normalize (each one sends the document to Review); null when none.</summary>
    public int? UnnormalizedValues { get; set; }
}

/// <summary>One kept row of a loss-run document: the row is listed and counted, never in claims or totals.</summary>
public sealed record KeptRowItem(string? ClaimNumber, string? PolicyNumber, string Reason, int? Page, decimal? TotalIncurred);

public sealed record UploadCost(decimal TotalUsd, int LlmCalls, int EmbeddingCalls, int Pages, IReadOnlyDictionary<string, decimal> ByDocument);

/// <summary>Orchestration custom status, streamed by <c>GET /uploads/{id}/events</c> (one per upload and per loss-run document).</summary>
public sealed record UploadProgress(string Step, string? DocumentId, long ElapsedMs, decimal CostSoFar, string? Detail = null);

/// <summary><c>GET /uploads/{id}/status</c> and each SSE event: the upload's and its documents' latest progress.</summary>
public sealed record UploadStatusView(string UploadId, string RuntimeStatus, UploadProgress? Upload, IReadOnlyList<UploadProgress> Documents);

/// <summary><c>GET /documents/{id}</c>: canonical document with lineage, rule outcomes, routing and cost.</summary>
public sealed class DocumentResult
{
    public string DocumentId { get; set; } = "";
    public string UploadId { get; set; } = "";

    /// <summary>Increments on every review decision (1 = as extracted).</summary>
    public int Revision { get; set; } = 1;
    public LossRunDocument Document { get; set; } = new();
    public List<RuleOutcome> RuleOutcomes { get; set; } = [];
    public RoutingDecision? Routing { get; set; }
    public string? FormatRef { get; set; }
    public DocumentCost? Cost { get; set; }
    public string? ReviewState { get; set; }

    /// <summary>
    /// Brief 24 step 2: approved | unresolved | rejected per lineage path at this revision. Derived on read
    /// (<see cref="WithFieldStatus"/>), never stored: the same review state, outcomes and lineage always give the same map.
    /// </summary>
    public Dictionary<string, LossRun.Rules.FieldStatus>? FieldStatus { get; set; }

    /// <summary>A copy carrying <see cref="FieldStatus"/>; the stored result is left as it is.</summary>
    public DocumentResult WithFieldStatus() => new()
    {
        DocumentId = DocumentId, UploadId = UploadId, Revision = Revision, Document = Document, RuleOutcomes = RuleOutcomes, Routing = Routing, FormatRef = FormatRef,
        Cost = Cost, ReviewState = ReviewState,
        FieldStatus = new(LossRun.Rules.FieldStatusEvaluator.Evaluate(Document, ReviewState, RuleOutcomes), StringComparer.Ordinal),
    };
}

public static class ReviewKinds
{
    /// <summary>Hard-rule failure, carrier mismatch, exhausted learn: processing waits for the decision.</summary>
    public const string DocumentReview = "document-review";

    /// <summary>A shadow format's document (06 Answers 4): processed, the reviewer confirms the new format.</summary>
    public const string ConfirmFormat = "confirm-format";
}

public sealed class ReviewItem
{
    public string DocumentId { get; set; } = "";
    public string UploadId { get; set; } = "";
    public string Kind { get; set; } = ReviewKinds.DocumentReview;

    /// <summary>pending | decided | expired.</summary>
    public string State { get; set; } = "pending";

    /// <summary>Set when an underwriter sent a processed document back to review (11 Answers 2); its decision applies at once.</summary>
    public bool Reopened { get; set; }
    public List<string> Reasons { get; set; } = [];
    public string? Badge { get; set; }
    public string? FormatRef { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public ReviewDecision? Decision { get; set; }
}

/// <summary>Brief 32 item 7: <see cref="DocumentResult.ReviewState"/> of a document an underwriter reopened (its values are under review again).</summary>
public static class ReviewStates
{
    public const string Reopened = "reopened";
}

public static class ReviewActions
{
    public const string Accept = "accept";
    public const string Override = "override";
    public const string Reject = "reject";
}

/// <summary>Body of <c>POST /reviews/{documentId}/decision</c>.</summary>
/// <param name="Aliases">ADR 0019, confirm-format reviews only: accept, edit or reject each learned alias the format introduced; the ones not listed are accepted with the format.</param>
/// <param name="PromoteToFormat">Brief 24 step 1: activate the document's format. Absent: true on a confirm-format item, false
/// otherwise; an explicit false on a confirm-format item approves the document and leaves the format in shadow. Never with reject.</param>
/// <param name="FormatChanges">Brief 34 A3: changes to the document's format the reviewer confirms (allow-list: <c>periods.fillDown</c> = true).</param>
public sealed record ReviewDecisionRequest(string Action, IReadOnlyList<FieldOverride>? Overrides = null, bool? PromoteToFormat = null, string? Rationale = null,
    IReadOnlyList<LossRun.Formats.AliasDecision>? Aliases = null, IReadOnlyDictionary<string, System.Text.Json.JsonElement>? FormatChanges = null);

/// <summary><c>POST /reviews/{documentId}/reopen</c> (11 Answers 2).</summary>
public sealed record ReopenRequest(string Reason);

/// <summary><c>POST /documents/{documentId}/overrides</c>: an underwriter's override on a processed document, outside a review.</summary>
public sealed record OverrideRequest(IReadOnlyList<FieldOverride> Overrides, string Rationale);

/// <summary>Result of a direct override: the document's new revision, the fields changed and the hard failures after re-validation.</summary>
public sealed record OverrideResult(string DocumentId, int Revision, int Changed, int HardFailures, int Attempts, DateTimeOffset At);

/// <param name="FieldPath">Canonical lineage path, e.g. <c>policyPeriods[0].claims[1].medical.paid</c>.</param>
public sealed record FieldOverride(string FieldPath, JsonElement NewValue, string Rationale);

/// <summary>The decision as recorded and raised to the orchestration (who and when added by the API).</summary>
/// <param name="DecisionId">Brief 30 item 6: generated by the API; the key that makes applying the decision idempotent (its outbox rows are keyed on it).</param>
public sealed record ReviewDecision(string DocumentId, string Action, IReadOnlyList<FieldOverride> Overrides, bool PromoteToFormat, string? Rationale, string User,
    string Role, DateTimeOffset At, IReadOnlyList<LossRun.Formats.AliasDecision>? Aliases = null, string? DecisionId = null,
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? FormatChanges = null)
{
    /// <summary>The idempotency key: <see cref="DecisionId"/>, or for a decision raised before brief 30 one derived from what identifies it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => DecisionId ?? "d-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
        $"{DocumentId}|{Action}|{User}|{At.UtcTicks}")))[..24];

    public static string NewId() => $"dec-{Guid.NewGuid():N}";
}

/// <summary>Append-only audit event (Blob JSONL now; SQL ledger in brief 09).</summary>
public sealed record AuditEvent(DateTimeOffset At, string User, string Role, string Action, string UploadId, string? DocumentId, string? FieldPath = null,
    JsonElement? OldValue = null, JsonElement? NewValue = null, string? Rationale = null);

/// <summary><c>GET /formats</c> entry.</summary>
/// <summary>Result of <c>POST /formats/reset</c>: the learned versions retired (or that would be, on a dry run) and how many were kept.</summary>
public sealed record FormatResetResult(bool DryRun, IReadOnlyList<string> Retired, int Kept, DateTimeOffset At);

public sealed record FormatSummary(string FormatId, int Version, string CarrierName, string FileKind, string CreatedBy, DateTimeOffset CreatedAt, string Status,
    string? Supersedes, string? VerificationGate);

/// <summary><c>GET /documents/{id}/quote-payload</c> (shape in docs/api/quote-payload.md; feature math is brief 10).</summary>
/// <param name="ValuationDateSource">Brief 23 Answers 2: printed, or submission when the run prints none (R-32).</param>
public sealed record QuotePayload(string DocumentId, string? CarrierName, string? InsuredName, DateOnly? ValuationDate, IReadOnlyList<QuotePeriod> Periods,
    IReadOnlyList<Claim> Claims, string? ValuationDateSource = null);

public sealed record QuotePeriod(string? PolicyNumber, DateOnly? EffectiveDate, DateOnly? ExpirationDate, int ClaimCount, int OpenCount, MoneySums Indemnity,
    MoneySums Medical, MoneySums Expense, MoneySums Total);

public sealed record MoneySums(decimal Paid, decimal Reserve, decimal Incurred);

/// <summary>Outbound webhook body (<c>X-LossRun-Signature: sha256=…</c> HMAC over the raw body).</summary>
/// <param name="SentAt">Brief 30 item 30: when this delivery was signed (inside the signed body); a receiver refuses one older than 5 minutes.</param>
/// <param name="Nonce">Brief 30 item 30: unique per delivery (inside the signed body); a receiver refuses one it has seen.</param>
public sealed record WebhookEnvelope(string Event, string UploadId, DateTimeOffset OccurredAt, string Status, int Documents, int AwaitingReview, decimal CostUsd,
    DateTimeOffset? SentAt = null, string? Nonce = null);

public sealed record ApiError(string Code, string Message);

// ------------------------------------------------------------------ integration (brief 12b)

/// <summary>An outbox row as the API shows it (<c>GET /quotes/{quoteId}/integration</c>): what was sent, when it was published, what the quote system answered.</summary>
public sealed record OutboxEntry(string Id, string Channel, string Kind, string? QuoteId, string? Subject, JsonElement Payload, DateTimeOffset CreatedAt, string CreatedBy,
    DateTimeOffset? PublishedAt, int PublishAttempts, string? LastError, DateTimeOffset? DeliveredAt, string? DeliveryStatus, JsonElement? Delivery);

/// <summary><c>POST /quotes/{quoteId}/annotations</c>: a note pushed to the quote record.</summary>
public sealed record AnnotationRequest(string Text, IReadOnlyList<string>? ClaimIds = null, string? Category = null);

public sealed record Annotation(string Id, string QuoteId, string Text, IReadOnlyList<string> ClaimIds, string? Category, string CreatedBy, DateTimeOffset CreatedAt,
    string OutboxId);
