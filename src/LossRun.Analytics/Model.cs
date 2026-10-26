namespace LossRun.Analytics;

// Engine input and parameters (brief 10). Every amount is integer cents and every date is an ISO yyyy-MM-dd string, so
// the C# engine and its TypeScript twin (src/web/packages/metrics) read the same JSON and compute the same integers.

/// <summary>One claim row of an account, as the engine and the client what-if twin read it.</summary>
/// <param name="Id">Stable id: <c>{documentId}/{periodIndex}/{claimIndex}</c> (the lineage path of the claim).</param>
/// <param name="PolicyYear">Year of the policy period's effective date.</param>
/// <param name="LastDayWorked">The claimant's last day worked when the carrier prints it (report 10 answer 1); else the context's account-level date applies.</param>
public sealed record AnalyticsClaim(
    string Id,
    string? ClaimNumber,
    string? PolicyNumber,
    int? PolicyYear,
    string? DateOfInjury,
    string? DateReported,
    string? DateClosed,
    string? Status,
    string? ClaimType,
    string? NatureCode,
    string? CauseCode,
    string? BodyPartCode,
    string? TypeOfLossCode,
    string? Description,
    bool? Litigation,
    bool? CumulativeTrauma,
    string? Jurisdiction,
    string? LineOfBusiness,
    long IndemnityPaidCents,
    long IndemnityReserveCents,
    long MedicalPaidCents,
    long MedicalReserveCents,
    long ExpensePaidCents,
    long ExpenseReserveCents,
    long? RecoveryCents,
    string? LastDayWorked = null,
    bool? ReportOnly = null);

/// <summary>Account-level inputs the claims do not carry: exposures, dates, the valuation date.</summary>
public sealed record AnalyticsContext(
    string? QuoteId,
    string? ValuationDate,
    IReadOnlyDictionary<int, long>? PayrollCentsByYear = null,
    IReadOnlyDictionary<int, long>? FteByYear = null,
    string? LastDayWorked = null);

/// <summary>An override that changed an input between as-reported and as-adjusted (lineage, brief 09).</summary>
/// <param name="Field">Claim field path, e.g. <c>indemnity.paid</c>.</param>
/// <param name="Unrevertable">Brief 32 item 5: true when the as-reported view could not restore this override's stored old value (it shows the current one).</param>
public sealed record OverrideRef(string Id, string ClaimId, string Field, bool? Unrevertable = null);

/// <summary>What-if parameters (catalog.yaml <c>scenario</c>). The default instance is the baseline.</summary>
public sealed record Scenario
{
    public IReadOnlyList<string> ExcludeClaimIds { get; init; } = [];
    public bool IncludeNonCA { get; init; } = true;
    public bool IncludeCT { get; init; } = true;
    public long? ExcludeCatastrophicAboveCents { get; init; }
    public IReadOnlyDictionary<int, int> TrendFactorBpsByYear { get; init; } = new Dictionary<int, int>();
    public int ReserveFactorBps { get; init; }
    public long? CapPerClaimCents { get; init; }

    public bool IsBaseline => ExcludeClaimIds.Count == 0 && IncludeNonCA && IncludeCT && ExcludeCatastrophicAboveCents is null
        && TrendFactorBpsByYear.Count == 0 && ReserveFactorBps == 0 && CapPerClaimCents is null;
}

/// <summary>The engine's whole input: the account's claims, context and the overrides that explain as-adjusted.</summary>
public sealed record AnalyticsInput(IReadOnlyList<AnalyticsClaim> Claims, AnalyticsContext Context, IReadOnlyList<OverrideRef> Overrides);
