using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LossRun.Contracts;

namespace LossRun.Rules;

/// <summary>A field's review status: approved | unresolved | rejected.</summary>
public static class FieldStates
{
    public const string Approved = "approved";
    public const string Unresolved = "unresolved";
    public const string Rejected = "rejected";
}

/// <summary>
/// One field's status at a document revision. <paramref name="Reason"/>: <c>document-rejected</c>, <c>review-pending</c>, the id of
/// the hard rule that still fails on it (e.g. <c>R-10</c>), <c>R-33</c> (required for quoting and null), or <c>override</c> (a
/// reviewer set it).
/// </summary>
public sealed record FieldStatus(string Status, string? Reason = null);

/// <summary>
/// Brief 24 step 2 (review plan 2026-10-25 §1): field status as a view derived from facts that already exist — the document's
/// review state, the hard-rule outcomes at the current revision, the <c>manual</c> lineage of overrides and the R-33 required
/// fields. No new mutable state; the same inputs always give the same statuses. Pure: no I/O, no clock.
/// </summary>
public static partial class FieldStatusEvaluator
{
    /// <summary>
    /// The status of every field with lineage, plus every R-33 required field the document should carry (absent ones are
    /// unresolved). Keys are lineage paths, e.g. <c>policyPeriods[0].claims[3].total.incurred</c>.
    /// </summary>
    /// <param name="reviewState">The document's review state: null (straight through), pending, reopened, accept, override or reject.</param>
    public static IReadOnlyDictionary<string, FieldStatus> Evaluate(LossRunDocument document, string? reviewState, IEnumerable<RuleOutcome> outcomes,
        IReadOnlyList<RequiredField>? requiredFields = null)
    {
        requiredFields ??= RuleCatalog.Embedded.RequiredForQuoting;
        var root = JsonSerializer.SerializeToNode(document, CanonicalJson.Options)!.AsObject();
        root.Remove("lineage");
        var lineage = document.Lineage ?? [];
        var required = requiredFields.SelectMany(r => Expand(root, r.Path).Where(p => !Excused(document, r.Unless, p))).ToHashSet(StringComparer.Ordinal);
        var paths = lineage.Keys.Concat(required).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

        var failing = outcomes.Where(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard)
            .SelectMany(o => o.AffectedFields.Select(f => (o.RuleId, Pattern: Wildcard(f)))).ToList();
        var result = new Dictionary<string, FieldStatus>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            result[path] = Status(path, reviewState, required.Contains(path) && IsNull(Get(root, path)),
                lineage.GetValueOrDefault(path)?.Extractor == "manual", failing.FirstOrDefault(f => f.Pattern.IsMatch(path)).RuleId);
        }

        return result;
    }

    /// <summary>Whether a required path is not required here (brief 24 Answers 1: the R-33 sub-conditions on period dates).</summary>
    private static bool Excused(LossRunDocument document, string? unless, string path)
    {
        if (unless != RequiredField.SummaryOnlyLabelledPeriod || PeriodIndex().Match(path) is not { Success: true } m
            || int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) is var i && i >= document.PolicyPeriods.Count)
        {
            return false;
        }

        var period = document.PolicyPeriods[i];
        return document.PolicyPeriods.All(p => p.Claims.Count == 0) && period.DetailLevel == DetailLevel.SummaryOnly
            && (!string.IsNullOrWhiteSpace(period.PeriodLabel) || period.PeriodKind is not null);
    }

    [GeneratedRegex(@"^policyPeriods\[(\d+)\]")]
    private static partial Regex PeriodIndex();

    private static FieldStatus Status(string path, string? reviewState, bool requiredAndNull, bool manual, string? failingRule) => reviewState switch
    {
        "reject" => new(FieldStates.Rejected, "document-rejected"),
        "pending" or "reopened" => new(FieldStates.Unresolved, "review-pending"),
        _ when requiredAndNull => new(FieldStates.Unresolved, "R-33"),
        _ when manual => new(FieldStates.Approved, "override"),
        _ when failingRule is not null => new(FieldStates.Unresolved, failingRule),
        _ => new(FieldStates.Approved),
    };

    /// <summary>Concrete paths of a pattern on this document (<c>[*]</c> over every element that exists).</summary>
    internal static IEnumerable<string> Expand(JsonNode root, string pattern)
    {
        IEnumerable<(JsonNode? Node, string Path)> current = [(root, "")];
        foreach (var segment in pattern.Split('.'))
        {
            var m = SegmentPattern().Match(segment);
            var name = m.Groups[1].Value;
            var index = m.Groups[2].Success ? m.Groups[2].Value : null;
            current = current.SelectMany(c =>
            {
                var path = c.Path.Length == 0 ? name : $"{c.Path}.{name}";
                var child = (c.Node as JsonObject)?[name];
                if (index is null)
                {
                    return [(child, path)];
                }

                if (child is not JsonArray array)
                {
                    return [];
                }

                return index == "*"
                    ? array.Select((n, i) => (n, $"{path}[{i}]"))
                    : int.Parse(index, System.Globalization.CultureInfo.InvariantCulture) < array.Count
                        ? [(array[int.Parse(index, System.Globalization.CultureInfo.InvariantCulture)], $"{path}[{index}]")]
                        : (IEnumerable<(JsonNode?, string)>)[];
            }).ToList();
        }

        return current.Select(c => c.Path);
    }

    private static JsonNode? Get(JsonNode root, string path)
    {
        JsonNode? node = root;
        foreach (var segment in path.Split('.'))
        {
            var m = SegmentPattern().Match(segment);
            node = (node as JsonObject)?[m.Groups[1].Value];
            if (m.Groups[2].Success)
            {
                node = node is JsonArray a && int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) is var i && i < a.Count ? a[i] : null;
            }
        }

        return node;
    }

    private static bool IsNull(JsonNode? node) => node is null || (node is JsonValue v && v.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s));

    /// <summary>An affected-field path as a matcher: <c>[*]</c> matches any index; container paths match only themselves.</summary>
    private static Regex Wildcard(string affected) => new("^" + Regex.Escape(affected).Replace(@"\[\*]", @"\[\d+]", StringComparison.Ordinal) + "$");

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9]*)(?:\[(\*|\d+)\])?$")]
    private static partial Regex SegmentPattern();
}
