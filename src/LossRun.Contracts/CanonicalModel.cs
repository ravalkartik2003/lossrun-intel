namespace LossRun.Contracts;

// Canonical loss run model. Mirrors docs/evaluation/canonical-schema.json property for property;
// LossRun.Contracts.Tests fails if the two drift. Serialize with CanonicalJson.Options.

public sealed class LossRunDocument
{
    public string SchemaVersion { get; set; } = CanonicalData.SchemaVersion;
    public string DocumentId { get; set; } = "";
    public string? CarrierName { get; set; }
    public string? TpaName { get; set; }
    public string? InsuredName { get; set; }
    public string? LineOfBusiness { get; set; }
    public string? JurisdictionState { get; set; }
    public DateOnly? ValuationDate { get; set; }

    /// <summary>Brief 23 Answers 2: <c>printed</c>, or <c>submission</c> when the run prints none and the submitter stated it (R-32).</summary>
    public string? ValuationDateSource { get; set; }
    public DateOnly? ReportRunDate { get; set; }
    public bool NoLossesStatement { get; set; }

    /// <summary>A "no losses" statement no policy period could take (ADR 0018, R-28).</summary>
    public bool UnattributedNoLossesStatement { get; set; }

    /// <summary>Components the run does not print at all (brief 19 Answers 1): medical { present: false }.</summary>
    public ComponentPresence? ComponentPresence { get; set; }

    /// <summary>What the printed totals sum (ADR 0018); null reads as all components.</summary>
    public TotalComposition? TotalComposition { get; set; }

    public ExpenseTreatment ExpenseTreatment { get; set; } = ExpenseTreatment.Unknown;
    public List<PolicyPeriod> PolicyPeriods { get; set; } = [];
    public Totals? ReportTotals { get; set; }

    /// <summary>Per-value lineage keyed by canonical field path, e.g. <c>policyPeriods[0].claims[2].indemnity.paid</c>.</summary>
    public Dictionary<string, LineageRecord>? Lineage { get; set; }
}

public sealed class PolicyPeriod
{
    public string? PolicyNumber { get; set; }
    public string? CarrierName { get; set; }
    public DateOnly? EffectiveDate { get; set; }
    public DateOnly? ExpirationDate { get; set; }

    /// <summary>What the period is: a policy term, or the year a summary row reports by (ADR 0017).</summary>
    public PeriodKind? PeriodKind { get; set; }

    /// <summary>Printed label when the period is not a policy number or a date range, e.g. a fiscal year (ADR 0017).</summary>
    public string? PeriodLabel { get; set; }

    /// <summary>The period prints a "no claims / no losses" statement (ADR 0016); R-14 then requires zero claim rows.</summary>
    public bool NoLossesStatement { get; set; }

    public DetailLevel DetailLevel { get; set; } = DetailLevel.Claims;
    public List<Claim> Claims { get; set; } = [];

    /// <summary>Brief 23 §2 (ADR 0020 amendment): rows of another line of business, kept, not extracted; never in claims or totals.</summary>
    public List<Claim>? KeptRows { get; set; }
    public Totals? Subtotals { get; set; }
}

public sealed class Claim
{
    public string? ClaimNumber { get; set; }
    public string? ClaimantName { get; set; }
    public DateOnly? DateOfInjury { get; set; }
    public DateOnly? DateReported { get; set; }
    public DateOnly? DateClosed { get; set; }

    /// <summary>The claimant's last day worked, when the carrier prints it (post-termination CT; report 10 answer 1).</summary>
    public DateOnly? LastDayWorked { get; set; }
    public ClaimStatus? Status { get; set; }
    public string? RawStatus { get; set; }
    public ClaimType? ClaimType { get; set; }
    public string? InjuryTypeCode { get; set; }
    public string? TypeOfLossCode { get; set; }
    public bool? CumulativeTrauma { get; set; }
    public CodedValue? NatureOfInjury { get; set; }
    public CodedValue? CauseOfInjury { get; set; }
    public CodedValue? PartOfBody { get; set; }
    public string? AccidentDescription { get; set; }
    public bool? LitigationFlag { get; set; }

    /// <summary>Brief 22 Answers 4: recorded for reporting only, without financials (census-only status, incident indicator).</summary>
    public bool? ReportOnly { get; set; }

    /// <summary>Brief 23 §2: on a row in <see cref="PolicyPeriod.KeptRows"/>, why it was kept ("line of business: auto (claim-type code \"PP\")").</summary>
    public string? KeptReason { get; set; }
    public string? JurisdictionState { get; set; }
    public string? ClassCode { get; set; }
    public string? LineOfBusiness { get; set; }
    public MoneyTriplet? Indemnity { get; set; }
    public MoneyTriplet? Medical { get; set; }
    public MoneyTriplet? Expense { get; set; }
    public MoneyTriplet? Total { get; set; }
    public decimal? RecoverySubrogation { get; set; }
    public decimal? NetIncurred { get; set; }
}

public sealed class MoneyTriplet
{
    public decimal? Paid { get; set; }
    public decimal? Reserve { get; set; }
    public decimal? Incurred { get; set; }

    public MoneyTriplet() { }

    public MoneyTriplet(decimal? paid, decimal? reserve, decimal? incurred)
    {
        Paid = paid;
        Reserve = reserve;
        Incurred = incurred;
    }
}

public sealed class CodedValue
{
    public string? Code { get; set; }
    public string? Text { get; set; }
    public string? CodeSystem { get; set; }
}

public sealed class Totals
{
    public int? ClaimCount { get; set; }
    public int? OpenCount { get; set; }
    public int? ClosedCount { get; set; }
    public MoneyTriplet? Indemnity { get; set; }
    public MoneyTriplet? Medical { get; set; }
    public MoneyTriplet? Expense { get; set; }
    public MoneyTriplet? Total { get; set; }
    public decimal? RecoverySubrogation { get; set; }

    /// <summary>Recoveries other than subrogation, by kind (ADR 0017).</summary>
    public RecoveryAmounts? Recoveries { get; set; }
}

/// <summary>Recovery amounts by kind (ADR 0017); subrogation stays <see cref="Totals.RecoverySubrogation"/>.</summary>
public sealed class RecoveryAmounts
{
    public decimal? Deductible { get; set; }
    public decimal? Reinsurance { get; set; }
    public decimal? Sif { get; set; }
    public decimal? Other { get; set; }
}

public sealed class LineageRecord
{
    public string DocumentId { get; set; } = "";
    public int? Page { get; set; }
    public List<double>? Polygon { get; set; }
    public string? Sheet { get; set; }
    public string? Cell { get; set; }
    public string? SourceText { get; set; }

    /// <summary>replay | derived | spec | learn | delta | manual.</summary>
    public string? Extractor { get; set; }
    public string? FormatId { get; set; }
    public string? FormatVersion { get; set; }
    public string? Model { get; set; }
    public string? PromptVersion { get; set; }
    public double? Confidence { get; set; }

    /// <summary>Position of the value in a stacked cell (ADR 0018); null for a whole cell.</summary>
    public int? Part { get; set; }

    /// <summary>
    /// Brief 31 Answers 1: a repair replay applied to the printed text (<see cref="SourceText"/> stays what was printed), e.g.
    /// <c>ocrRepair: l→I</c> for a scanned name whose capital I was read as l. Null when the value is the printed text.
    /// </summary>
    public string? Repair { get; set; }
}

/// <summary>Per loss component, whether the source reports it (brief 19 Answers 1).</summary>
public sealed class ComponentPresence
{
    public Presence? Indemnity { get; set; }
    public Presence? Medical { get; set; }
    public Presence? Expense { get; set; }
}

public sealed class Presence
{
    public bool Present { get; set; }
}

/// <summary>What the printed total columns sum (ADR 0018).</summary>
public enum TotalComposition
{
    AllComponents,
    IndemnityMedical,
}

public enum ClaimStatus
{
    Open,
    Closed,
    Reopened,

    /// <summary>The format prints no status and its close-date convention was not verified (brief 19 Answers 2; R-30).</summary>
    Unknown,
}

public enum ClaimType
{
    MedicalOnly,
    Indemnity,
    Unknown,
}

/// <summary>How the run treats expense/ALAE (set from the carrier format spec).</summary>
public enum ExpenseTreatment
{
    Separate,
    MergedIntoMedical,
    Excluded,
    Unknown,
}

/// <summary>What a policy period is (ADR 0017).</summary>
public enum PeriodKind
{
    PolicyTerm,
    FiscalYear,
    ReportYear,
    AccidentYear,
    CalendarYear,
}

/// <summary>Whether a policy period lists claim rows or only totals.</summary>
public enum DetailLevel
{
    Claims,
    SummaryOnly,
}
