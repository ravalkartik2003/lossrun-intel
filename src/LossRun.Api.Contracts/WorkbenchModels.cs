using LossRun.Contracts;

namespace LossRun.Api.Contracts;

// Brief 11: the reads and actions the UI workbench needs that briefs 07–10 did not expose (who am I, quote list, quote
// context, audit trail, cost summary, memo export, flag push, follow-ups). Shapes are in docs/api/openapi.yaml.

/// <summary><c>GET /me</c>: the caller as the API sees them (the UI's role-aware navigation reads this).</summary>
/// <param name="CanRead">Brief 26: reads what a reviewer reads (true for Engineer, who cannot decide or override).</param>
public sealed record MeView(string UserId, string Role, bool CanReview, bool CanRead = false);

/// <summary><c>GET /quotes</c> entry: a quote with loss-run documents (the top bar's quote selector).</summary>
public sealed record QuoteSummary(string QuoteId, int Documents, int Claims, string? InsuredName, DateOnly? ValuationDate, int PendingReview,
    DateTimeOffset? UpdatedAt, string? DisplayName = null, DateTimeOffset? RetiredAt = null);

/// <summary>
/// A quote's display name and retired state (brief 13 demo hygiene). The quote id never changes (ledger rows name it);
/// a retired quote is left out of <c>GET /quotes</c> unless <c>includeRetired=true</c>, and nothing is deleted.
/// </summary>
public sealed record QuoteLabel(string QuoteId, string? DisplayName, DateTimeOffset? RetiredAt, string? RetiredBy, DateTimeOffset UpdatedAt, string UpdatedBy);

/// <summary><c>PATCH /quotes/{quoteId}</c> (UWManager, Admin): rename and/or retire / restore; the rationale is audited.</summary>
/// <summary>Brief 30 item 1: who may attach uploads and context to a quote. <paramref name="ApplicantUserId"/> is the Applicant who created it;
/// null when an underwriter role created it (or it predates the owner record): then only underwriter roles attach to it.</summary>
public sealed record QuoteOwner(string QuoteId, string? ApplicantUserId, DateTimeOffset CreatedAt, string CreatedBy);

public sealed record QuoteLabelRequest(string Rationale, string? DisplayName = null, bool? Retired = null);

/// <summary>Brief 32 item 4: <c>POST /quotes/{quoteId}/owner</c> (UWManager, Admin): the Applicant who may attach to the quote, or null for underwriter roles only.</summary>
public sealed record QuoteOwnerRequest(string Rationale, string? ApplicantUserId = null);

/// <summary>Brief 26: <c>PATCH /escalations/{id}</c> — new | investigating | resolved | wont-fix, with a required note.</summary>
public sealed record EscalationStatusRequest(string Status, string Note);

/// <summary>Brief 26: <c>POST /escalations/{id}/tuning-items</c> — cause category a–g (tuning-loop.md) and a note.</summary>
public sealed record TuningItemRequest(string Category, string Note);

/// <summary>Brief 26: <c>POST /documents/{id}/flag</c> — "Flag for engineering" with the reviewer's note.</summary>
public sealed record FlagRequest(string Note);

/// <summary>An exposure for one policy period: payroll in dollars or an FTE count (report 10 answer 1).</summary>
public sealed record PeriodExposure(DateOnly EffectiveDate, decimal Amount, string? PolicyNumber = null);

/// <summary>
/// <c>GET|PUT /quotes/{quoteId}/context</c>: account-level inputs the loss runs do not carry. Frequency metrics compute
/// from the exposures; post-termination CT from the claimant's last day worked (per claim when the carrier prints it,
/// else <see cref="LastDayWorked"/>). Feature snapshots key on it.
/// </summary>
/// <param name="TargetPolicyEffectiveDate">Brief 29 §3: the effective date of the policy being quoted (R-15: valuation within 100 days of it).</param>
/// <param name="RequestedPolicyYears">Brief 29 §3: the policy years the submission asks for (R-25: each present in the loss runs).</param>
public sealed record QuoteContext(string QuoteId, IReadOnlyList<PeriodExposure>? PayrollByPeriod = null, IReadOnlyList<PeriodExposure>? FteByPeriod = null,
    DateOnly? LastDayWorked = null, DateTimeOffset? UpdatedAt = null, string? UpdatedBy = null, DateOnly? TargetPolicyEffectiveDate = null,
    IReadOnlyList<int>? RequestedPolicyYears = null)
{
    /// <summary>The rule context this quote supplies (null when it carries neither field: R-15 and R-25 stay not-applicable).</summary>
    public LossRun.Rules.RuleContext? ToRuleContext() => TargetPolicyEffectiveDate is null && RequestedPolicyYears is not { Count: > 0 } ? null
        : new LossRun.Rules.RuleContext { TargetPolicyEffectiveDate = TargetPolicyEffectiveDate, RequestedPolicyYears = RequestedPolicyYears };
}

/// <summary>One row of <c>GET /documents/{id}/audit</c> or <c>GET /quotes/{id}/audit</c> (dbo.vAuditTrail; Blob audit log locally).</summary>
/// <param name="LedgerTransactionId">The SQL ledger transaction that committed the row; null in Blob mode.</param>
/// <param name="CommittedAt">The engine's commit time for that transaction.</param>
public sealed record AuditTrailEntry(long? Id, DateTimeOffset At, string User, string Role, string Action, string? UploadId, string? DocumentId, int? Revision,
    string? FieldPath, string? OldValue, string? NewValue, string? Rationale, long? LedgerTransactionId = null, DateTimeOffset? CommittedAt = null,
    string? CommittedBy = null);

/// <summary>The audit trail plus how it is protected: <c>ledger</c> (SQL ledger tables, digest time) or <c>append-only-blob</c>.</summary>
public sealed record AuditTrailView(IReadOnlyList<AuditTrailEntry> Entries, string Protection, DateTimeOffset? LastDigestAt);

public sealed record CostByPath(string Path, int Documents, decimal TotalUsd, decimal MeanUsd, int ModelCalls);

/// <summary>Learn once vs replay for one carrier: what its replays saved against a learn per document, and the document where the learn paid back.</summary>
public sealed record CostByCarrier(string Carrier, decimal LearnUsd, int ReplayDocs, decimal ReplayMeanUsd, decimal ReplaySavingsUsd, int? BreakEvenDoc);

public sealed record DocumentCostLine(string DocumentId, string? Carrier, string Path, decimal CostUsd, int Pages, int ModelCalls, DateTimeOffset ComputedAt,
    IReadOnlyList<CostLine> Lines);

/// <summary>Billed-vs-metered reconciliation (brief 13); null until the cost export is wired.</summary>
/// <summary>AI-services metered vs billed for a period (brief 13 §1); platform costs are standing and listed separately.</summary>
public sealed record CostReconciliation(decimal MeteredUsd, decimal BilledUsd, double Ratio, DateOnly ExportDate, DateOnly? From = null, decimal? PlatformBilledUsd = null,
    string? Scope = null);

/// <summary><c>GET /costs</c>: POC-to-date metered cost (cost-tracking.md §3).</summary>
public sealed record CostSummary(decimal PocToDateUsd, int DocumentsProcessed, decimal AvgCostPerDocumentUsd, double ZeroModelCallShare,
    IReadOnlyList<CostByPath> ByPath, IReadOnlyList<CostByCarrier> ByCarrier, IReadOnlyList<DocumentCostLine> Documents, CostReconciliation? Reconciliation);

/// <summary><c>POST /quotes/{quoteId}/flags</c>: push the account's per-claim flags to the quote (recorded and audited; Service Bus delivery is brief 12b).</summary>
public sealed record FlagPushRequest(IReadOnlyList<string>? Flags = null, string? Rationale = null);

public sealed record FlagPushResult(string QuoteId, int Claims, IReadOnlyDictionary<string, int> Flags, string Delivery, DateTimeOffset At);

/// <summary><c>POST /quotes/{quoteId}/follow-ups</c>: assign a follow-up action (request the missing year, confirm a reserve, …).</summary>
public sealed record FollowUpRequest(string Assignee, string Action, string? Note = null, IReadOnlyList<string>? ClaimIds = null, DateOnly? Due = null);

public sealed record FollowUp(string Id, string QuoteId, string Assignee, string Action, string? Note, IReadOnlyList<string> ClaimIds, DateOnly? Due,
    string CreatedBy, DateTimeOffset CreatedAt);
