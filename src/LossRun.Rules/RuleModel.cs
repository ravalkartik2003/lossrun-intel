using System.Text.Json.Nodes;

namespace LossRun.Rules;

public enum RuleSeverity
{
    /// <summary>Blocks straight-through processing; counts toward the first-pass hard-rule rate.</summary>
    Hard,

    /// <summary>Warning shown to the reviewer/underwriter.</summary>
    Soft,

    /// <summary>Routing signal, not an error (e.g. non-CA → jurisdiction split). Never counts toward pass rates.</summary>
    Flag,
}

public enum RuleScope
{
    Claim,
    Period,
    Document,
}

public enum RuleStatus
{
    Pass,
    Fail,
    NotApplicable,

    /// <summary>
    /// The rule held only by construction: a value it read was derived (by the carrier format) purely from other
    /// values the same rule read, e.g. R-02 on an incurred computed as paid + reserve. Reported apart from pass so the
    /// reviewer sees "incurred was derived, not printed"; counted with n/a in pass-rate denominators (brief 05 Answers 4).
    /// </summary>
    NotInformative,
}

/// <summary>
/// Result of one rule on one instance (one claim, period or document, and one forEach binding).
/// </summary>
/// <param name="RuleId">Catalog id, e.g. R-01.</param>
/// <param name="Severity">Effective severity after severityOverrides.</param>
/// <param name="Status">pass | fail | not-applicable.</param>
/// <param name="Message">Rule message with placeholders filled.</param>
/// <param name="AffectedFields">Canonical field paths the rule read (same format as lineage keys), for click-to-highlight.</param>
/// <param name="ClaimId">Claim number for claim-scoped rules.</param>
/// <param name="PeriodIndex">Index into policyPeriods for claim- and period-scoped rules.</param>
/// <param name="Bindings">forEach bindings for this instance, e.g. c=indemnity.</param>
/// <param name="Detail">On fail: the comparisons that failed, with values.</param>
/// <param name="Values">Values the rule emits for analytics (catalog <c>emit</c>), e.g. lagDays; invariant-culture text.</param>
public sealed record RuleOutcome(
    string RuleId,
    RuleSeverity Severity,
    RuleStatus Status,
    string Message,
    IReadOnlyList<string> AffectedFields,
    string? ClaimId = null,
    int? PeriodIndex = null,
    IReadOnlyDictionary<string, string>? Bindings = null,
    string? Detail = null,
    IReadOnlyDictionary<string, string>? Values = null);

/// <summary>Inputs that are not on the loss run itself (submission context).</summary>
public sealed record RuleContext
{
    /// <summary>Effective date of the policy being quoted (drives the stale-valuation rule).</summary>
    public DateOnly? TargetPolicyEffectiveDate { get; init; }

    /// <summary>Date the evaluation runs (reserved for future rules; never read from the clock inside rules).</summary>
    public DateOnly? EvaluationDate { get; init; }

    /// <summary>Policy years the submission asked for (year of the policy effective date); drives R-25.</summary>
    public IReadOnlyList<int>? RequestedPolicyYears { get; init; }
}

/// <summary>A parsed catalog rule.</summary>
public sealed record RuleDefinition(
    string Id,
    string Status,
    string Name,
    RuleSeverity Severity,
    RuleScope Scope,
    IReadOnlyList<ForEachDimension> ForEach,
    Expr? When,
    Expr Assert,
    IReadOnlyList<SeverityOverride> SeverityOverrides,
    JsonObject Params,
    IReadOnlyDictionary<string, Expr> Emit,
    string Message,
    string Source,
    string? CaNote);

/// <summary>A quote-level catalog rule (brief 24 step 2): today only R-33, the fields a quote needs.</summary>
public sealed record QuoteRuleDefinition(string Id, string Status, string Name, RuleSeverity Severity, IReadOnlyList<RequiredField> RequiredFields, string Message, string Source);

/// <summary>
/// One R-33 field (a lineage path; <c>[*]</c> = every period or claim), optionally not required under a named condition
/// the catalog describes (brief 24 Answers 1).
/// </summary>
public sealed record RequiredField(string Path, string? Unless = null)
{
    /// <summary>A period of a document without claim rows that is summary-only and prints its own year or range (ADR 0017).</summary>
    public const string SummaryOnlyLabelledPeriod = "summaryOnlyLabelledPeriod";

    public static readonly IReadOnlySet<string> KnownConditions = new HashSet<string>(StringComparer.Ordinal) { SummaryOnlyLabelledPeriod };
}

public sealed record ForEachDimension(string Var, IReadOnlyList<string> Values);

public sealed record SeverityOverride(Expr When, RuleSeverity Severity, string? Reason);

public sealed class RuleCatalogException(string message) : Exception(message);
