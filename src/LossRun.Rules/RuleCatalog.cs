using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LossRun.Contracts;

namespace LossRun.Rules;

/// <summary>
/// The validation rule catalog (docs/evaluation/rule-catalog.json), parsed and validated.
/// Loading is pure: pass the JSON text, or use <see cref="Embedded"/> for the copy compiled into this assembly.
/// </summary>
public sealed partial class RuleCatalog
{
    private static readonly Lazy<RuleCatalog> EmbeddedCatalog = new(() => Load(ReadEmbedded()));

    private static readonly IReadOnlyDictionary<RuleScope, string[]> AllowedRoots = new Dictionary<RuleScope, string[]>
    {
        [RuleScope.Claim] = ["claim", "period", "document", "context"],
        [RuleScope.Period] = ["period", "document", "context"],
        [RuleScope.Document] = ["document", "context"],
    };

    private static readonly IReadOnlyDictionary<RuleScope, string[]> AllowedCollections = new Dictionary<RuleScope, string[]>
    {
        [RuleScope.Claim] = ["period.claims", "document.claims", "document.policyPeriods", "period.keptRows", "document.keptRows"],
        [RuleScope.Period] = ["period.claims", "document.claims", "document.policyPeriods", "period.keptRows", "document.keptRows"],
        [RuleScope.Document] = ["document.claims", "document.policyPeriods", "document.keptRows"],
    };

    private RuleCatalog(string catalogVersion, string schemaVersion, IReadOnlyList<RuleDefinition> rules, IReadOnlyList<QuoteRuleDefinition> quoteRules,
        IReadOnlySet<string>? contextRules = null)
    {
        CatalogVersion = catalogVersion;
        SchemaVersion = schemaVersion;
        Rules = rules;
        QuoteRules = quoteRules;
        ContextRules = contextRules ?? new HashSet<string>();
    }

    /// <summary>
    /// Brief 29 §3: rules that read the submission context (<c>context.*</c>: R-15, R-25). They judge the submission, not the
    /// extraction, so routing does not count them toward a delta repair; they still go to the reviewer.
    /// </summary>
    public IReadOnlySet<string> ContextRules { get; }

    public string CatalogVersion { get; }
    public string SchemaVersion { get; }

    /// <summary>The document rules: what <see cref="RuleEvaluator"/> runs and what routing reads.</summary>
    public IReadOnlyList<RuleDefinition> Rules { get; }

    /// <summary>
    /// Brief 24 step 2: quote-level rules (catalog <c>quoteRules</c>). The evaluator never reads them, so they cannot change a
    /// document's routing; quote readiness and field status do.
    /// </summary>
    public IReadOnlyList<QuoteRuleDefinition> QuoteRules { get; }

    /// <summary>The R-33 field paths a quote needs (lineage paths; <c>[*]</c> = every period or claim).</summary>
    public IReadOnlyList<RequiredField> RequiredForQuoting => QuoteRules.FirstOrDefault(q => q.RequiredFields.Count > 0)?.RequiredFields ?? [];

    /// <summary>The catalog embedded from docs/evaluation/rule-catalog.json at build time.</summary>
    public static RuleCatalog Embedded => EmbeddedCatalog.Value;

    /// <summary>Brief 30 item 41: the tolerance a rule's assertion compares with (its first <c>cmp</c>), so code that pre-checks the same
    /// reconciliation (replay's total-row realignment) uses the catalog's number, not its own.</summary>
    public decimal ToleranceOf(string ruleId) => Rules.FirstOrDefault(r => r.Id == ruleId) is { } rule ? FirstTolerance(rule.Assert) ?? 0m : 0m;

    private static decimal? FirstTolerance(Expr expr) => expr switch
    {
        CmpExpr c => c.Tolerance,
        OrExpr o => o.Operands.Select(FirstTolerance).FirstOrDefault(t => t is not null),
        AndExpr a => a.Operands.Select(FirstTolerance).FirstOrDefault(t => t is not null),
        NotExpr n => FirstTolerance(n.Operand),
        _ => null,
    };

    public static RuleCatalog Load(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new RuleCatalogException("Rule catalog must be a JSON object.");
        var catalogVersion = RequiredString(root, "catalogVersion", "catalog");
        var schemaVersion = RequiredString(root, "schemaVersion", "catalog");
        if (schemaVersion != CanonicalData.SchemaVersion)
        {
            throw new RuleCatalogException(
                $"Rule catalog targets schemaVersion {schemaVersion} but the canonical schema is {CanonicalData.SchemaVersion}; bump both together.");
        }

        if (root["rules"] is not JsonArray rulesNode || rulesNode.Count == 0)
        {
            throw new RuleCatalogException("Rule catalog has no rules.");
        }

        var rules = rulesNode.Select((node, i) => ParseRule(node as JsonObject ?? throw new RuleCatalogException($"rules[{i}] is not an object."), i)).ToList();
        var contextRules = rulesNode.OfType<JsonObject>().Where(r => (r["assert"]?.ToJsonString() + r["when"]?.ToJsonString()).Contains("\"context.", StringComparison.Ordinal))
            .Select(r => r["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var quoteRules = root["quoteRules"] is JsonArray quoteNode
            ? quoteNode.Select((node, i) => ParseQuoteRule(node as JsonObject ?? throw new RuleCatalogException($"quoteRules[{i}] is not an object."), i)).ToList()
            : [];
        var duplicate = rules.Select(r => r.Id).Concat(quoteRules.Select(q => q.Id)).GroupBy(id => id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new RuleCatalogException($"Duplicate rule id {duplicate.Key}.");
        }

        return new RuleCatalog(catalogVersion, schemaVersion, rules, quoteRules, contextRules);
    }

    private static QuoteRuleDefinition ParseQuoteRule(JsonObject node, int index)
    {
        var where = $"quoteRules[{index}]";
        var id = RequiredString(node, "id", where);
        where = $"rule {id}";
        if (!RuleIdPattern().IsMatch(id))
        {
            throw new RuleCatalogException($"{where}: id must look like R-01.");
        }

        if (RequiredString(node, "scope", where) != "quote")
        {
            throw new RuleCatalogException($"{where}: a quoteRules entry has scope quote.");
        }

        var conditions = node["conditions"] as JsonObject ?? [];
        RequiredField Field(JsonNode? f)
        {
            var (path, unless) = f switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => (s, null),
                JsonObject o => (o["path"]?.GetValue<string>(), o["unless"]?.GetValue<string>()),
                _ => (null, null),
            };
            if (path is null || !FieldPathPattern().IsMatch(path))
            {
                throw new RuleCatalogException($"{where}: requiredFields entries are lineage paths such as policyPeriods[*].claims[*].claimNumber.");
            }

            if (unless is not null && (!RequiredField.KnownConditions.Contains(unless) || !conditions.ContainsKey(unless)))
            {
                throw new RuleCatalogException($"{where}: condition '{unless}' must be one the code knows and described under conditions.");
            }

            return new RequiredField(path, unless);
        }

        var fields = node["requiredFields"] is JsonArray list
            ? list.Select(Field).ToList()
            : throw new RuleCatalogException($"{where}: missing requiredFields.");
        return new QuoteRuleDefinition(id, RequiredString(node, "status", where), RequiredString(node, "name", where),
            ParseSeverity(RequiredString(node, "severity", where), where), fields, RequiredString(node, "message", where), RequiredString(node, "source", where));
    }

    private static RuleDefinition ParseRule(JsonObject node, int index)
    {
        var where = $"rules[{index}]";
        var id = RequiredString(node, "id", where);
        where = $"rule {id}";
        if (!RuleIdPattern().IsMatch(id))
        {
            throw new RuleCatalogException($"{where}: id must look like R-01.");
        }

        var status = RequiredString(node, "status", where);
        if (status is not ("provisional" or "confirmed"))
        {
            throw new RuleCatalogException($"{where}: status must be provisional or confirmed.");
        }

        var severity = ParseSeverity(RequiredString(node, "severity", where), where);
        var scope = RequiredString(node, "scope", where) switch
        {
            "claim" => RuleScope.Claim,
            "period" => RuleScope.Period,
            "document" => RuleScope.Document,
            var other => throw new RuleCatalogException($"{where}: unknown scope '{other}'."),
        };

        var forEach = node["forEach"] is JsonArray dims
            ? dims.Select((d, i) => new ForEachDimension(
                RequiredString(d as JsonObject ?? throw new RuleCatalogException($"{where}.forEach[{i}] is not an object."), "var", $"{where}.forEach[{i}]"),
                (d!["in"] as JsonArray ?? throw new RuleCatalogException($"{where}.forEach[{i}].in must be an array."))
                    .Select(v => v!.GetValue<string>()).ToList())).ToList()
            : [];
        if (forEach.GroupBy(f => f.Var).Any(g => g.Count() > 1) || forEach.Any(f => f.Values.Count == 0))
        {
            throw new RuleCatalogException($"{where}: forEach vars must be unique and non-empty.");
        }

        var when = node["when"] is { } w ? ExprParser.Parse(w, $"{where}.when") : null;
        var assert = ExprParser.Parse(node["assert"] ?? throw new RuleCatalogException($"{where}: missing assert."), $"{where}.assert");
        var overrides = node["severityOverrides"] is JsonArray ovs
            ? ovs.Select((o, i) => new SeverityOverride(
                ExprParser.Parse(o?["when"], $"{where}.severityOverrides[{i}].when"),
                ParseSeverity(RequiredString((JsonObject)o!, "severity", $"{where}.severityOverrides[{i}]"), where),
                o?["reason"]?.GetValue<string>())).ToList()
            : [];
        var parameters = node["params"] as JsonObject ?? [];
        var emit = node["emit"] is JsonObject emitNode
            ? emitNode.ToDictionary(
                p => IdentifierPattern().IsMatch(p.Key) ? p.Key : throw new RuleCatalogException($"{where}: emit name '{p.Key}' is not an identifier."),
                p => ExprParser.Parse(p.Value, $"{where}.emit.{p.Key}"))
            : new Dictionary<string, Expr>();

        var rule = new RuleDefinition(
            id, status, RequiredString(node, "name", where), severity, scope, forEach, when, assert, overrides,
            (JsonObject)parameters.DeepClone(), emit, RequiredString(node, "message", where), RequiredString(node, "source", where),
            node["caNote"]?.GetValue<string>());

        ValidateReferences(rule, where);
        return rule;
    }

    private static void ValidateReferences(RuleDefinition rule, string where)
    {
        var expressions = new List<Expr> { rule.Assert };
        if (rule.When is not null)
        {
            expressions.Add(rule.When);
        }

        expressions.AddRange(rule.SeverityOverrides.Select(o => o.When));
        expressions.AddRange(rule.Emit.Values);
        var vars = rule.ForEach.Select(f => f.Var).ToHashSet();
        var itemScoped = expressions.SelectMany(ExprParser.ItemScopedPaths).ToHashSet();

        foreach (var expr in expressions)
        {
            foreach (var param in ExprParser.ParamNames(expr))
            {
                if (!rule.Params.ContainsKey(param))
                {
                    throw new RuleCatalogException($"{where}: references undefined param '{param}'.");
                }
            }

            foreach (var path in ExprParser.Paths(expr))
            {
                foreach (Match m in Placeholder().Matches(path))
                {
                    if (!vars.Contains(m.Groups[1].Value))
                    {
                        throw new RuleCatalogException($"{where}: path '{path}' uses undefined forEach var '{m.Groups[1].Value}'.");
                    }
                }

                var rootName = path.Split('.')[0];
                if (rootName == "item")
                {
                    if (!itemScoped.Contains(path))
                    {
                        throw new RuleCatalogException($"{where}: 'item' path '{path}' used outside sum/count/unique.");
                    }

                    continue;
                }

                if (!AllowedRoots[rule.Scope].Contains(rootName))
                {
                    throw new RuleCatalogException($"{where}: path '{path}' is not allowed in a {rule.Scope.ToString().ToLowerInvariant()}-scoped rule.");
                }
            }

            foreach (var over in Collections(expr))
            {
                if (!AllowedCollections[rule.Scope].Contains(over))
                {
                    throw new RuleCatalogException($"{where}: collection '{over}' is not available in a {rule.Scope.ToString().ToLowerInvariant()}-scoped rule.");
                }
            }
        }

        var messageNames = vars.Concat(rule.Params.Select(p => p.Key)).ToHashSet();
        if (rule.Scope == RuleScope.Claim)
        {
            messageNames.Add("claimNumber");
        }

        if (rule.Scope is RuleScope.Claim or RuleScope.Period)
        {
            messageNames.Add("policyNumber");
        }

        foreach (Match m in Placeholder().Matches(rule.Message))
        {
            if (!messageNames.Contains(m.Groups[1].Value))
            {
                throw new RuleCatalogException($"{where}: message placeholder '{{{m.Groups[1].Value}}}' is not a forEach var, param, or scope field.");
            }
        }
    }

    private static IEnumerable<string> Collections(Expr expr) => expr switch
    {
        SumExpr s => [s.Over],
        CountExpr c => c.Where is null ? [c.Over] : Collections(c.Where).Prepend(c.Over),
        UniqueExpr u => [u.Over],
        CollectExpr c => [c.Over],
        SubsetOfExpr s => Collections(s.Value).Concat(Collections(s.Of)),
        DaysBetweenExpr d => Collections(d.From).Concat(Collections(d.To)),
        CmpExpr c => Collections(c.Left).Concat(Collections(c.Right)),
        AddExpr a => a.Operands.SelectMany(Collections),
        SubExpr s => Collections(s.Left).Concat(Collections(s.Right)),
        CoalesceExpr c => c.Operands.SelectMany(Collections),
        AndExpr a => a.Operands.SelectMany(Collections),
        OrExpr o => o.Operands.SelectMany(Collections),
        AllKnownExpr a => a.Operands.SelectMany(Collections),
        NotExpr n => Collections(n.Operand),
        _ => [],
    };

    private static RuleSeverity ParseSeverity(string value, string where) => value switch
    {
        "hard" => RuleSeverity.Hard,
        "soft" => RuleSeverity.Soft,
        "flag" => RuleSeverity.Flag,
        _ => throw new RuleCatalogException($"{where}: severity must be hard, soft or flag."),
    };

    private static string RequiredString(JsonObject node, string name, string where) =>
        node[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : throw new RuleCatalogException($"{where}: missing required string '{name}'.");

    private static string ReadEmbedded()
    {
        using var stream = typeof(RuleCatalog).Assembly.GetManifestResourceStream("LossRun.Data.rule-catalog.json")
            ?? throw new RuleCatalogException("Embedded rule-catalog.json not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"^R-\d{2}$")]
    private static partial Regex RuleIdPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*(\[(\*|\d+)\])?(\.[A-Za-z][A-Za-z0-9]*(\[(\*|\d+)\])?)*$")]
    private static partial Regex FieldPathPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9]*)\}")]
    internal static partial Regex Placeholder();
}
