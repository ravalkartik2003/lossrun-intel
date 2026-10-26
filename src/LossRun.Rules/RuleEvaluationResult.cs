using System.Collections;

namespace LossRun.Rules;

/// <summary>All outcomes of one evaluation, plus the views the pipeline routes on.</summary>
public sealed class RuleEvaluationResult(string catalogVersion, IReadOnlyList<RuleOutcome> outcomes) : IReadOnlyList<RuleOutcome>
{
    /// <summary>Catalog version that produced these outcomes (persist it with the result).</summary>
    public string CatalogVersion { get; } = catalogVersion;

    public IReadOnlyList<RuleOutcome> Outcomes { get; } = outcomes;

    /// <summary>Hard failures: route the document to review; these are what the first-pass hard-rule rate counts.</summary>
    public IReadOnlyList<RuleOutcome> HardFailures => Failed(RuleSeverity.Hard);

    /// <summary>Soft failures: warnings for the reviewer/underwriter.</summary>
    public IReadOnlyList<RuleOutcome> SoftFailures => Failed(RuleSeverity.Soft);

    /// <summary>Raised flags (failed flag-severity rules): routing signals such as non-CA or non-WC, report lag, large loss.</summary>
    public IReadOnlyList<RuleOutcome> Flags => Failed(RuleSeverity.Flag);

    /// <summary>Checks that held only because a value was derived from the others (brief 05 Answers 4).</summary>
    public IReadOnlyList<RuleOutcome> NotInformative => Outcomes.Where(o => o.Status == RuleStatus.NotInformative).ToList();

    /// <summary>True when no hard rule failed; soft failures and flags never block.</summary>
    public bool PassesHardRules => HardFailures.Count == 0;

    public int Count => Outcomes.Count;

    public RuleOutcome this[int index] => Outcomes[index];

    public IEnumerator<RuleOutcome> GetEnumerator() => Outcomes.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private List<RuleOutcome> Failed(RuleSeverity severity) =>
        Outcomes.Where(o => o.Status == RuleStatus.Fail && o.Severity == severity).ToList();
}
