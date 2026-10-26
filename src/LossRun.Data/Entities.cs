namespace LossRun.Data;

// Row types for the db/changelog tables (all in dbo). Columns mirror the changesets one to one; the changesets are the
// schema source (ADR 0006), these classes only map it.

public sealed class UploadRow
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? QuoteId { get; set; }
    public string? CarrierHint { get; set; }
    public string UploaderId { get; set; } = "";
    public string UploaderRole { get; set; } = "";
    public DateOnly? ValuationDate { get; set; }
    public string SubmissionJson { get; set; } = "{}";
    public string FilesJson { get; set; } = "[]";
    public decimal? CostUsd { get; set; }
    public string? CostJson { get; set; }
    public long? FirstClaimMs { get; set; }
    public long? CompletedMs { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public List<UploadItemRow> Items { get; set; } = [];
}

public sealed class UploadItemRow
{
    public string DocumentId { get; set; } = "";
    public string UploadId { get; set; } = "";
    public int Ordinal { get; set; }
    public string FileName { get; set; } = "";
    public string? ParentDocumentId { get; set; }
    public string? PageRange { get; set; }
    public string Classification { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Reason { get; set; }
    public string? BlobPath { get; set; }
    public string? RoutingTier { get; set; }
    public string? Badge { get; set; }
    public string? FormatRef { get; set; }
    public string? FormatStatus { get; set; }
    public int? ClaimCount { get; set; }
    public decimal? CostUsd { get; set; }
    public string? ReviewKind { get; set; }

    /// <summary>Brief 23 §2: the item's kept rows (other lines of business) as JSON; changeset 0019.</summary>
    public string? KeptRowsJson { get; set; }

    /// <summary>Brief 30 item 4: printed values replay could not normalize; changeset 0023.</summary>
    public int? UnnormalizedValues { get; set; }
}

public sealed class DocumentRow
{
    public string Id { get; set; } = "";
    public string UploadId { get; set; } = "";
    public string? QuoteId { get; set; }
    public string? CarrierName { get; set; }
    public string? InsuredName { get; set; }
    public string? LineOfBusiness { get; set; }
    public DateOnly? ValuationDate { get; set; }
    public DateOnly? ReportRunDate { get; set; }
    public string ExpenseTreatment { get; set; } = "Unknown";
    public string? FormatRef { get; set; }
    public string? RoutingTier { get; set; }
    public string? Badge { get; set; }
    public string? ReviewState { get; set; }
    public int Revision { get; set; } = 1;
    public string? CatalogVersion { get; set; }
    public decimal? CostUsd { get; set; }
    public string ResultJson { get; set; } = "{}";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class PolicyPeriodRow
{
    public Guid Id { get; set; }
    public string DocumentId { get; set; } = "";
    public int PeriodIndex { get; set; }
    public string? PolicyNumber { get; set; }
    public string? CarrierName { get; set; }
    public DateOnly? EffectiveDate { get; set; }
    public DateOnly? ExpirationDate { get; set; }
    public string DetailLevel { get; set; } = "Claims";
    public string? SubtotalsJson { get; set; }
}

/// <summary>Resolved (as-adjusted) claim values; temporal (dbo.ClaimHistory). Written only by <see cref="ClaimCommandService"/>.</summary>
public sealed class ClaimRow
{
    public Guid Id { get; set; }
    public string DocumentId { get; set; } = "";
    public Guid PolicyPeriodId { get; set; }
    public int PeriodIndex { get; set; }
    public int ClaimIndex { get; set; }
    public string? ClaimNumber { get; set; }
    public string? ClaimantName { get; set; }
    public DateOnly? DateOfInjury { get; set; }
    public DateOnly? DateReported { get; set; }
    public DateOnly? DateClosed { get; set; }
    public string? Status { get; set; }
    public string? RawStatus { get; set; }
    public string? ClaimType { get; set; }
    public string? InjuryTypeCode { get; set; }
    public string? NatureOfInjuryCode { get; set; }
    public string? CauseOfInjuryCode { get; set; }
    public string? PartOfBodyCode { get; set; }
    public string? AccidentDescription { get; set; }
    public bool? LitigationFlag { get; set; }
    public bool? CumulativeTrauma { get; set; }
    public string? JurisdictionState { get; set; }
    public string? ClassCode { get; set; }
    public decimal? IndemnityPaid { get; set; }
    public decimal? IndemnityReserve { get; set; }
    public decimal? IndemnityIncurred { get; set; }
    public decimal? MedicalPaid { get; set; }
    public decimal? MedicalReserve { get; set; }
    public decimal? MedicalIncurred { get; set; }
    public decimal? ExpensePaid { get; set; }
    public decimal? ExpenseReserve { get; set; }
    public decimal? ExpenseIncurred { get; set; }
    public decimal? TotalPaid { get; set; }
    public decimal? TotalReserve { get; set; }
    public decimal? TotalIncurred { get; set; }
    public decimal? RecoverySubrogation { get; set; }
    public decimal? NetIncurred { get; set; }
    public string ExpenseTreatment { get; set; } = "Unknown";
    public string DetailLevel { get; set; } = "Claims";
    public long? LastOverrideId { get; set; }
    public string ChangedBy { get; set; } = "";
}

/// <summary>As-reported value with lineage (append-only ledger).</summary>
public sealed class ExtractedValueRow
{
    public long Id { get; set; }
    public string DocumentId { get; set; } = "";
    public int DocumentRevision { get; set; }
    public Guid? ClaimId { get; set; }
    public string FieldPath { get; set; } = "";
    public string? ClaimField { get; set; }
    public string? Value { get; set; }
    public string? RawText { get; set; }
    public double? Confidence { get; set; }
    public string? Extractor { get; set; }
    public string? FormatRef { get; set; }
    public int? Page { get; set; }
    public string? PolygonJson { get; set; }
    public string? Sheet { get; set; }
    public string? CellA1 { get; set; }
    public string? Model { get; set; }
    public string? PromptVersion { get; set; }
    public DateTime RecordedUtc { get; set; }
}

/// <summary>An underwriter override (append-only ledger).</summary>
public sealed class OverrideRow
{
    public long Id { get; set; }
    public string DocumentId { get; set; } = "";
    public int DocumentRevision { get; set; }
    public Guid? ClaimId { get; set; }
    public string FieldPath { get; set; } = "";
    public string? ClaimField { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string Rationale { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Role { get; set; } = "";
    public DateTime AtUtc { get; set; }
    public string? CatalogVersion { get; set; }
    public string? FormatRef { get; set; }
    public string Source { get; set; } = "api";
    public string? CorrelationId { get; set; }
    public long? SupersedesOverrideId { get; set; }
}

/// <summary>Every mutation (append-only ledger).</summary>
public sealed class AuditEventRow
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }
    public string UserId { get; set; } = "";
    public string Role { get; set; } = "";
    public string Action { get; set; } = "";
    public string? UploadId { get; set; }
    public string? DocumentId { get; set; }
    public int? DocumentRevision { get; set; }
    public Guid? ClaimId { get; set; }
    public string? FieldPath { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Rationale { get; set; }
    public long? OverrideId { get; set; }
    public string? CatalogVersion { get; set; }
    public string? FormatRef { get; set; }
    public string Source { get; set; } = "system";
    public string? CorrelationId { get; set; }
}

public sealed class RuleOutcomeRow
{
    public string DocumentId { get; set; } = "";
    public int DocumentRevision { get; set; }
    public int Ordinal { get; set; }
    public string RuleId { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public string? AffectedFieldsJson { get; set; }
    public string? ClaimRef { get; set; }
    public int? PeriodIndex { get; set; }
    public string? Detail { get; set; }
}

public sealed class OutboxRow
{
    public string Id { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? QuoteId { get; set; }
    public string? Subject { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTime CreatedUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime? PublishedUtc { get; set; }
    public int PublishAttempts { get; set; }
    public string? LastError { get; set; }
    public DateTime? DeliveredUtc { get; set; }
    public string? DeliveryStatus { get; set; }
    public string? DeliveryJson { get; set; }
}

public sealed class ReviewItemRow
{
    public string DocumentId { get; set; } = "";
    public string UploadId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string State { get; set; } = "";
    public string ReasonsJson { get; set; } = "[]";
    public string? Badge { get; set; }
    public string? FormatRef { get; set; }
    public DateTime OpenedUtc { get; set; }
    public DateTime? DecidedUtc { get; set; }
    public string? DecisionJson { get; set; }
    public bool Reopened { get; set; }
}

public sealed class FormatRow
{
    public string Id { get; set; } = "";
    public string CarrierName { get; set; } = "";
    public string FileKind { get; set; } = "";
    public DateTime CreatedUtc { get; set; }

    /// <summary>Adaptive embedding threshold statistics (changeset 0010; <see cref="LossRun.Formats.EmbeddingHits"/>).</summary>
    public int? EmbeddingHits { get; set; }
    public double? EmbeddingMean { get; set; }
    public double? EmbeddingM2 { get; set; }
}

public sealed class FormatVersionRow
{
    public string FormatId { get; set; } = "";
    public int Version { get; set; }
    public string SpecJson { get; set; } = "{}";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string? Supersedes { get; set; }
    public string? VerificationGate { get; set; }
    public string? IdentityKey { get; set; }
    public string? HeaderKey { get; set; }
    public string Status { get; set; } = "";
    public int CleanReplays { get; set; }
    public string? ActivatedBy { get; set; }
    public DateTime? ActivatedUtc { get; set; }
    public string? RetiredBy { get; set; }
    public DateTime? RetiredUtc { get; set; }
    public string? RetiredReason { get; set; }
}

public sealed class FormatFieldStatsRow
{
    public string FormatId { get; set; } = "";
    public string FieldPath { get; set; } = "";
    public int SuccessCount { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public string? LastFormatVersion { get; set; }
}

public sealed class UsageEventRow
{
    public long Id { get; set; }
    public string DocumentId { get; set; } = "";
    public string RunId { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Service { get; set; } = "";
    public string Meter { get; set; } = "";
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "";
    public string? ModelOrAnalyzer { get; set; }
    public string? ModelVersion { get; set; }
    public string? DeploymentType { get; set; }
    public string Region { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
    public string? CorrelationId { get; set; }
}

public sealed class DocumentCostRow
{
    public string DocumentId { get; set; } = "";
    public string RunId { get; set; } = "";
    public string Path { get; set; } = "";
    public int LlmCalls { get; set; }
    public int EmbeddingCalls { get; set; }
    public int Pages { get; set; }
    public long TokensIn { get; set; }
    public long TokensOut { get; set; }
    public decimal CostUsd { get; set; }
    public string BreakdownJson { get; set; } = "[]";
    public string? PriceTableVersion { get; set; }
    public DateTime ComputedUtc { get; set; }
}

public sealed class LedgerDigestRow
{
    public long Id { get; set; }
    public long BlockId { get; set; }
    public string DigestJson { get; set; } = "";
    public DateTime GeneratedUtc { get; set; }
    public string Source { get; set; } = "";
}

/// <summary>dbo.FeatureSnapshot (changeset 0011): a cached analytics snapshot; InvalidatedUtc is stamped by any claim change.</summary>
public sealed class FeatureSnapshotRow
{
    public long Id { get; set; }
    public string QuoteId { get; set; } = "";
    public string RevisionKey { get; set; } = "";
    public string CatalogVersion { get; set; } = "";
    public string View { get; set; } = "";
    public string ScenarioHash { get; set; } = "";
    public string InputsHash { get; set; } = "";
    public int ClaimCount { get; set; }
    public string SnapshotJson { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? InvalidatedUtc { get; set; }
    public string? InvalidatedBy { get; set; }
}

/// <summary>dbo.QuoteContext (changeset 0012): the quote's submission context as JSON; system-versioned.</summary>
public sealed class QuoteContextRow
{
    public string QuoteId { get; set; } = "";
    public string ContextJson { get; set; } = "{}";
    public DateTime UpdatedUtc { get; set; }
    public string UpdatedBy { get; set; } = "";
}

/// <summary>dbo.Escalation (changeset 0021, brief 26): one engineering case per (reason, fingerprint); system-versioned.</summary>
public sealed class EscalationRow
{
    public string Id { get; set; } = "";
    public string ReasonCode { get; set; } = "";
    public string FormatFingerprint { get; set; } = "";
    public string? FormatRef { get; set; }
    public string? Carrier { get; set; }
    public string DocumentIdsJson { get; set; } = "[]";
    public string UploadIdsJson { get; set; } = "[]";
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public int Count { get; set; }
    public string TraceSummaryJson { get; set; } = "{}";
    public string Status { get; set; } = "";
    public string? ResolutionNote { get; set; }
    public string? TuningItemRef { get; set; }
    public int Version { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string UpdatedBy { get; set; } = "";
}

/// <summary>dbo.QuoteLabel (changeset 0017): a quote's display name and retired flag; system-versioned.</summary>
public sealed class QuoteLabelRow
{
    public string QuoteId { get; set; } = "";
    public string? DisplayName { get; set; }
    public DateTime? RetiredUtc { get; set; }
    public string? RetiredBy { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string UpdatedBy { get; set; } = "";
}

/// <summary>dbo.QuoteOwner (changeset 0022, brief 30 item 1): the Applicant who created a quote, null for an underwriter-created one; insert-if-absent.</summary>
public sealed class QuoteOwnerRow
{
    public string QuoteId { get; set; } = "";
    public string? ApplicantUserId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string CreatedBy { get; set; } = "";
}

/// <summary>dbo.vAuditTrail (changeset 0008): audit events joined to the ledger transaction that committed them.</summary>
public sealed class AuditTrailRow
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }
    public string UserId { get; set; } = "";
    public string Role { get; set; } = "";
    public string Action { get; set; } = "";
    public string? UploadId { get; set; }
    public string? DocumentId { get; set; }
    public int? DocumentRevision { get; set; }
    public string? FieldPath { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Rationale { get; set; }
    public DateTime? CommittedUtc { get; set; }
    public string? CommittedBy { get; set; }
    public long? LedgerTransactionId { get; set; }
}

/// <summary>dbo.Workspace (changeset 0014).</summary>
public sealed class WorkspaceRow
{
    public string Id { get; set; } = "";
    public string OwnerUserId { get; set; } = "";
    public string? Name { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? DeletedUtc { get; set; }
}

/// <summary>dbo.ChatTurn (changeset 0014): append-only ledger table.</summary>
public sealed class ChatTurnRow
{
    public string Id { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public DateTime AtUtc { get; set; }
    public string UserId { get; set; } = "";
    public string Role { get; set; } = "";
    public string Scope { get; set; } = "";
    public string? QuoteId { get; set; }
    public string? WorkspaceId { get; set; }
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public bool NotFound { get; set; }
    public string CitationsJson { get; set; } = "[]";
    public string ToolTraceJson { get; set; } = "[]";
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public int ModelCalls { get; set; }
    public int Embeddings { get; set; }
    public int SearchQueries { get; set; }
    public decimal CostUsd { get; set; }
    public long FirstTokenMs { get; set; }
    public long TotalMs { get; set; }
    public string UsageJson { get; set; } = "{}";
}

/// <summary>Projection for scope resolution (raw SQL over dbo.Document joined to dbo.Upload).</summary>
public sealed class ScopedDocumentRow
{
    public string Id { get; set; } = "";
    public string UploadId { get; set; } = "";
    public string? QuoteId { get; set; }
    public string? WorkspaceId { get; set; }
    public int Revision { get; set; }
    public string? FileName { get; set; }
}

/// <summary>dbo.GlossaryAlias (changeset 0018, ADR 0019): a learned glossary alias and its lifecycle; system-versioned.</summary>
public sealed class GlossaryAliasRow
{
    public string Id { get; set; } = "";
    public string Header { get; set; } = "";
    public string CanonicalField { get; set; } = "";
    public string Status { get; set; } = "";
    public string CarrierName { get; set; } = "";
    public string FormatId { get; set; } = "";
    public string FormatVersion { get; set; } = "";
    public string FirstSeenDocumentId { get; set; } = "";
    public DateTime FirstSeenUtc { get; set; }
    public string LearnedModel { get; set; } = "";
    public string? LearnedModelVersion { get; set; }
    public string PromptVersion { get; set; } = "";
    public double Confidence { get; set; }
    public bool? TotalsTie { get; set; }
    public string? VerificationGate { get; set; }
    public string? ReviewedBy { get; set; }
    public string? ActivatedBy { get; set; }
    public DateTime? ActivatedUtc { get; set; }
    public string? ActivationReason { get; set; }
    public string? RetiredBy { get; set; }
    public DateTime? RetiredUtc { get; set; }
    public string? RetiredReason { get; set; }
    public string? RetiredAuditEventId { get; set; }
    public string? EditedFrom { get; set; }
}

/// <summary>dbo.GlossaryAliasEvidence (changeset 0018): one verification of an alias; append-only ledger.</summary>
public sealed class GlossaryAliasEvidenceRow
{
    public string AliasId { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public string FormatRef { get; set; } = "";
    public string Route { get; set; } = "";
    public bool? TotalsTie { get; set; }
    public DateTime AtUtc { get; set; }
}
