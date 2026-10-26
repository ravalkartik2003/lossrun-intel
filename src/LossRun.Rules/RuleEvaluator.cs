using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LossRun.Contracts;

namespace LossRun.Rules;

/// <summary>
/// Evaluates a <see cref="RuleCatalog"/> against a canonical document. Pure and deterministic: no I/O, no clock,
/// no model calls; the same document, context and catalog always produce the same outcomes in the same order.
/// </summary>
public sealed partial class RuleEvaluator(RuleCatalog catalog)
{
    public RuleCatalog Catalog { get; } = catalog;

    /// <summary>Evaluator over the embedded catalog.</summary>
    public static RuleEvaluator Default { get; } = new(RuleCatalog.Embedded);

    public RuleEvaluationResult Evaluate(LossRunDocument document, RuleContext? context = null)
    {
        var root = JsonNode.Parse(CanonicalJson.Serialize(document))!.AsObject();
        var contextNode = JsonNode.Parse(JsonSerializer.Serialize(context ?? new RuleContext(), CanonicalJson.Options))!.AsObject();
        var outcomes = new List<RuleOutcome>();
        foreach (var rule in Catalog.Rules)
        {
            foreach (var instance in Instances(rule.Scope, root))
            {
                foreach (var bindings in Bindings(rule.ForEach))
                {
                    var env = instance with { Context = contextNode, Params = rule.Params, Vars = bindings, Touched = [], Failures = [] };
                    outcomes.Add(EvaluateInstance(rule, env));
                }
            }
        }

        return new RuleEvaluationResult(Catalog.CatalogVersion, outcomes);
    }

    /// <summary>True when no hard rule failed (soft failures are warnings; flags are routing signals).</summary>
    public static bool PassesHardRules(IEnumerable<RuleOutcome> outcomes) =>
        !outcomes.Any(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard);

    private static RuleOutcome EvaluateInstance(RuleDefinition rule, Env env)
    {
        var status = RuleStatus.NotApplicable;
        if (rule.When is null || Predicate(rule.When, env) == Tri.True)
        {
            env.Failures.Clear();
            status = Predicate(rule.Assert, env) switch
            {
                Tri.True => RuleStatus.Pass,
                Tri.False => RuleStatus.Fail,
                _ => RuleStatus.NotApplicable,
            };
            if (status == RuleStatus.Pass && IsTautology(env))
            {
                status = RuleStatus.NotInformative;
            }
        }

        var severity = rule.Severity;
        foreach (var o in rule.SeverityOverrides)
        {
            var probe = env with { Touched = [], Failures = [] };
            if (Predicate(o.When, probe) == Tri.True)
            {
                severity = o.Severity;
                break;
            }
        }

        Dictionary<string, string>? values = null;
        foreach (var (name, expr) in rule.Emit)
        {
            var value = Evaluate(expr, env with { Touched = [], Failures = [] });
            if (!value.IsNull)
            {
                (values ??= [])[name] = value.AsText();
            }
        }

        return new RuleOutcome(
            rule.Id,
            severity,
            status,
            FillMessage(rule, env),
            env.Touched.Distinct().ToList(),
            env.Claim is null ? null : env.Claim["claimNumber"]?.GetValue<string>(),
            env.PeriodIndex,
            env.Vars.Count == 0 ? null : env.Vars,
            status == RuleStatus.Fail && env.Failures.Count > 0 ? string.Join("; ", env.Failures) : null,
            values);
    }

    /// <summary>
    /// True when some field the rule read was derived (lineage extractor "derived") and every source of that derivation
    /// (lineage sourceText "a + b", full paths) is also read by the rule: the check then holds by construction.
    /// </summary>
    private static bool IsTautology(Env env)
    {
        if (env.Document["lineage"] is not JsonObject lineage)
        {
            return false;
        }

        var touched = env.Touched.Where(p => !p.Contains("[*]", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var derived = touched.Where(p => lineage[p]?["extractor"]?.GetValue<string>() == "derived").ToList();
        return derived.Count > 0 && derived.All(p =>
            (lineage[p]!["sourceText"]?.GetValue<string>() ?? "").Split(" + ", StringSplitOptions.RemoveEmptyEntries).All(touched.Contains));
    }

    // ---------------------------------------------------------------- scopes

    private static IEnumerable<Env> Instances(RuleScope scope, JsonObject root)
    {
        var periods = root["policyPeriods"]?.AsArray() ?? [];
        switch (scope)
        {
            case RuleScope.Document:
                yield return new Env(root);
                break;
            case RuleScope.Period:
                for (var p = 0; p < periods.Count; p++)
                {
                    yield return new Env(root) { Period = periods[p]!.AsObject(), PeriodIndex = p };
                }

                break;
            case RuleScope.Claim:
                for (var p = 0; p < periods.Count; p++)
                {
                    var claims = periods[p]!["claims"]?.AsArray() ?? [];
                    for (var c = 0; c < claims.Count; c++)
                    {
                        yield return new Env(root) { Period = periods[p]!.AsObject(), PeriodIndex = p, Claim = claims[c]!.AsObject(), ClaimIndex = c };
                    }
                }

                break;
        }
    }

    private static IEnumerable<IReadOnlyDictionary<string, string>> Bindings(IReadOnlyList<ForEachDimension> dims)
    {
        IEnumerable<Dictionary<string, string>> combos = [new Dictionary<string, string>()];
        foreach (var dim in dims)
        {
            combos = combos.SelectMany(c => dim.Values.Select(v => new Dictionary<string, string>(c) { [dim.Var] = v })).ToList();
        }

        return combos;
    }

    private static List<(JsonObject Node, string Path)> Collection(string over, Env env)
    {
        var periods = env.Document["policyPeriods"]?.AsArray() ?? [];
        var items = new List<(JsonObject, string)>();
        switch (over)
        {
            case "period.claims" or "period.keptRows":
                // Brief 23 §2: keptRows are rows of another line of business, kept apart from the claims.
                var key = over == "period.claims" ? "claims" : "keptRows";
                var claims = env.Period?[key]?.AsArray() ?? [];
                for (var c = 0; c < claims.Count; c++)
                {
                    items.Add((claims[c]!.AsObject(), $"policyPeriods[{env.PeriodIndex}].{key}[{c}]"));
                }

                break;
            case "document.claims" or "document.keptRows":
                var documentKey = over == "document.claims" ? "claims" : "keptRows";
                for (var p = 0; p < periods.Count; p++)
                {
                    var pc = periods[p]![documentKey]?.AsArray() ?? [];
                    for (var c = 0; c < pc.Count; c++)
                    {
                        items.Add((pc[c]!.AsObject(), $"policyPeriods[{p}].{documentKey}[{c}]"));
                    }
                }

                break;
            case "document.policyPeriods":
                for (var p = 0; p < periods.Count; p++)
                {
                    items.Add((periods[p]!.AsObject(), $"policyPeriods[{p}]"));
                }

                break;
            default:
                throw new RuleCatalogException($"Unknown collection '{over}'.");
        }

        return items;
    }

    private static string WildcardPath(string over, Env env) => over switch
    {
        "period.claims" => $"policyPeriods[{env.PeriodIndex}].claims[*]",
        "document.claims" => "policyPeriods[*].claims[*]",
        "period.keptRows" => $"policyPeriods[{env.PeriodIndex}].keptRows[*]",
        "document.keptRows" => "policyPeriods[*].keptRows[*]",
        "document.policyPeriods" => "policyPeriods[*]",
        _ => over,
    };

    // ---------------------------------------------------------------- predicates

    private static Tri Predicate(Expr expr, Env env)
    {
        switch (expr)
        {
            case CmpExpr c:
                return Compare(c, env);
            case AndExpr a:
            {
                var results = a.Operands.Select(o => Predicate(o, env)).ToList();
                return results.Contains(Tri.False) ? Tri.False : results.Contains(Tri.Unknown) ? Tri.Unknown : Tri.True;
            }

            case OrExpr o:
            {
                var results = o.Operands.Select(x => Predicate(x, env)).ToList();
                return results.Contains(Tri.True) ? Tri.True : results.Contains(Tri.Unknown) ? Tri.Unknown : Tri.False;
            }

            case AllKnownExpr a:
            {
                var results = a.Operands.Select(o => Predicate(o, env)).ToList();
                return results.Contains(Tri.False) ? Tri.False : results.Contains(Tri.True) ? Tri.True : Tri.Unknown;
            }

            case NotExpr n:
                return Predicate(n.Operand, env) switch { Tri.True => Tri.False, Tri.False => Tri.True, _ => Tri.Unknown };
            case InExpr i:
            {
                var value = Evaluate(i.Value, env);
                if (value.IsNull)
                {
                    return Tri.Unknown;
                }

                var values = Evaluate(i.Values, env);
                if (values.Kind != ValueKind.Array)
                {
                    throw new RuleCatalogException("'in' values must be an array.");
                }

                var match = values.Array!.Any(v => FromNode(v).AsText() == value.AsText());
                if (!match)
                {
                    env.Failures.Add($"{Describe(i.Value, env)}={value.AsText()} not in [{string.Join(", ", values.Array!.Select(v => FromNode(v).AsText()))}]");
                }

                return match ? Tri.True : Tri.False;
            }

            case IsNullExpr n:
                return Evaluate(n.Operand, env).IsNull ? Tri.True : Tri.False;
            case NotNullExpr n:
            {
                var result = !Evaluate(n.Operand, env).IsNull;
                if (!result)
                {
                    env.Failures.Add($"{Describe(n.Operand, env)} is null");
                }

                return result ? Tri.True : Tri.False;
            }

            case NotBlankExpr n:
            {
                var v = Evaluate(n.Operand, env);
                var result = !v.IsNull && !(v.Kind == ValueKind.String && string.IsNullOrWhiteSpace(v.Str));
                if (!result)
                {
                    env.Failures.Add($"{Describe(n.Operand, env)} is blank");
                }

                return result ? Tri.True : Tri.False;
            }

            case UniqueExpr u:
                return Unique(u, env);
            case SubsetOfExpr s:
            {
                var subset = Evaluate(s.Value, env);
                if (subset.IsNull || subset.Kind != ValueKind.Array || subset.Array!.Count == 0)
                {
                    return Tri.Unknown;
                }

                var of = Evaluate(s.Of, env);
                var available = of.Kind == ValueKind.Array ? of.Array!.Select(n => FromNode(n).AsText()).ToHashSet() : [];
                var missing = subset.Array!.Select(n => FromNode(n).AsText()).Where(v => !available.Contains(v)).ToList();
                if (missing.Count > 0)
                {
                    env.Failures.Add($"missing from {Describe(s.Of, env)}: {string.Join(", ", missing)}");
                }

                return missing.Count == 0 ? Tri.True : Tri.False;
            }

            default:
                throw new RuleCatalogException($"Expression {expr.GetType().Name} is a value, not a predicate.");
        }
    }

    private static Tri Compare(CmpExpr c, Env env)
    {
        var left = Evaluate(c.Left, env);
        var right = Evaluate(c.Right, env);
        if (left.IsNull || right.IsNull)
        {
            return Tri.Unknown;
        }

        bool result;
        if (left.Kind == ValueKind.Number && right.Kind == ValueKind.Number)
        {
            var (l, r, t) = (left.Num, right.Num, c.Tolerance);
            result = c.Op switch
            {
                CmpOp.Eq => Math.Abs(l - r) <= t,
                CmpOp.Ne => Math.Abs(l - r) > t,
                CmpOp.Lt => l < r - t,
                CmpOp.Le => l <= r + t,
                CmpOp.Gt => l > r + t,
                CmpOp.Ge => l >= r - t,
                _ => throw new ArgumentOutOfRangeException(nameof(c)),
            };
        }
        else if (left.Kind == ValueKind.Date && right.Kind == ValueKind.Date)
        {
            var cmp = left.Date.CompareTo(right.Date);
            result = c.Op switch
            {
                CmpOp.Eq => cmp == 0,
                CmpOp.Ne => cmp != 0,
                CmpOp.Lt => cmp < 0,
                CmpOp.Le => cmp <= 0,
                CmpOp.Gt => cmp > 0,
                CmpOp.Ge => cmp >= 0,
                _ => throw new ArgumentOutOfRangeException(nameof(c)),
            };
        }
        else if (left.Kind == right.Kind && left.Kind is ValueKind.String or ValueKind.Bool && c.Op is CmpOp.Eq or CmpOp.Ne)
        {
            var equal = c.Normalize == "text"
                ? CanonicalComparer.NormalizeText(left.AsText()) == CanonicalComparer.NormalizeText(right.AsText())
                : left.AsText() == right.AsText();
            result = c.Op == CmpOp.Eq ? equal : !equal;
        }
        else
        {
            throw new RuleCatalogException(
                $"Cannot apply {c.Op} to {left.Kind} ({Describe(c.Left, env)}) and {right.Kind} ({Describe(c.Right, env)}).");
        }

        if (!result)
        {
            var tolerance = c.Tolerance > 0 ? $" (±{c.Tolerance.ToString(CultureInfo.InvariantCulture)})" : "";
            env.Failures.Add($"{Describe(c.Left, env)}={left.AsText()} {Symbol(c.Op)} {Describe(c.Right, env)}={right.AsText()} is false{tolerance}");
        }

        return result ? Tri.True : Tri.False;
    }

    private static Tri Unique(UniqueExpr u, Env env)
    {
        var keyed = new List<(string Key, string Path)>();
        foreach (var (node, path) in Collection(u.Over, env))
        {
            var itemEnv = env with { Item = node, ItemPath = path, Touched = [] };
            var value = Evaluate(u.Key, itemEnv);
            if (value.IsNull)
            {
                continue;
            }

            var key = u.Normalize switch
            {
                null => value.AsText(),
                "claimNumber" => CanonicalComparer.NormalizeClaimNumber(value.AsText()),
                var other => throw new RuleCatalogException($"Unknown unique normalizer '{other}'."),
            };
            if (key is not null)
            {
                keyed.Add((key, itemEnv.Touched.FirstOrDefault() ?? path));
            }
        }

        env.Touched.Add(WildcardPath(u.Over, env) + "." + ItemRest(u.Key));
        if (keyed.Count == 0)
        {
            return Tri.Unknown;
        }

        var duplicates = keyed.GroupBy(k => k.Key).Where(g => g.Count() > 1).ToList();
        foreach (var group in duplicates)
        {
            env.Touched.AddRange(group.Select(g => g.Path));
            env.Failures.Add($"'{group.Key}' appears {group.Count()} times ({string.Join(", ", group.Select(g => g.Path))})");
        }

        return duplicates.Count == 0 ? Tri.True : Tri.False;
    }

    // ---------------------------------------------------------------- values

    private static Value Evaluate(Expr expr, Env env)
    {
        switch (expr)
        {
            case PathExpr p:
                return Resolve(p.Path, env);
            case ConstExpr c:
                return FromNode(c.Value);
            case ParamExpr p:
                return FromNode(env.Params[p.Name]);
            case AddExpr a:
            {
                var values = a.Operands.Select(o => Evaluate(o, env)).ToList();
                if (a.IgnoreNulls)
                {
                    values = values.Where(v => !v.IsNull).ToList();
                    if (values.Count == 0)
                    {
                        return Value.Null;
                    }
                }
                else if (values.Any(v => v.IsNull))
                {
                    return Value.Null;
                }

                return Value.Number(values.Sum(v => v.AsNumber()));
            }

            case SubExpr s:
            {
                var l = Evaluate(s.Left, env);
                var r = Evaluate(s.Right, env);
                return l.IsNull || r.IsNull ? Value.Null : Value.Number(l.AsNumber() - r.AsNumber());
            }

            case SumExpr s:
            {
                var items = Collection(s.Over, env);
                if (items.Count > 0 || !s.Over.EndsWith(".keptRows", StringComparison.Ordinal))
                {
                    // Kept rows enter a check only when the document has some (brief 23 §2).
                    env.Touched.Add(WildcardPath(s.Over, env) + "." + ItemRest(Expand(s.ItemPath, env.Vars)));
                }
                if (items.Count == 0)
                {
                    return Value.Number(0);
                }

                var values = items.Select(i => Resolve(s.ItemPath, env with { Item = i.Node, ItemPath = i.Path, Touched = [] }))
                    .Where(v => !v.IsNull).ToList();
                return values.Count == 0 ? Value.Null : Value.Number(values.Sum(v => v.AsNumber()));
            }

            case CountExpr c:
            {
                var items = Collection(c.Over, env);
                if (items.Count > 0 || !c.Over.EndsWith(".keptRows", StringComparison.Ordinal))
                {
                    env.Touched.Add(WildcardPath(c.Over, env));
                }
                var count = c.Where is null
                    ? items.Count
                    : items.Count(i => Predicate(c.Where, env with { Item = i.Node, ItemPath = i.Path, Touched = [], Failures = [] }) == Tri.True);
                return Value.Number(count);
            }

            case DaysBetweenExpr d:
            {
                var from = Evaluate(d.From, env);
                var to = Evaluate(d.To, env);
                return from.Kind == ValueKind.Date && to.Kind == ValueKind.Date ? Value.Number(to.Date.DayNumber - from.Date.DayNumber) : Value.Null;
            }

            case CollectExpr c:
            {
                var items = Collection(c.Over, env);
                env.Touched.Add(WildcardPath(c.Over, env) + "." + ItemRest(c.ItemPath));
                var array = new JsonArray();
                foreach (var (node, path) in items)
                {
                    var v = Resolve(c.ItemPath, env with { Item = node, ItemPath = path, Touched = [] });
                    if (v.IsNull)
                    {
                        continue;
                    }

                    // Parsed from text so every JsonValue is JsonElement-backed (uniform numeric reads in FromNode).
                    array.Add(JsonNode.Parse(c.Transform == "year"
                        ? (v.Kind == ValueKind.Date ? v.Date.Year.ToString(CultureInfo.InvariantCulture) : throw new RuleCatalogException("'year' transform needs dates."))
                        : JsonSerializer.Serialize(v.AsText())));
                }

                return new Value(ValueKind.Array) { Array = array };
            }

            case CoalesceExpr c:
                foreach (var operand in c.Operands)
                {
                    var v = Evaluate(operand, env);
                    if (!v.IsNull)
                    {
                        return v;
                    }
                }

                return Value.Null;
            default:
                throw new RuleCatalogException($"Expression {expr.GetType().Name} is a predicate, not a value.");
        }
    }

    private static Value Resolve(string template, Env env)
    {
        var path = Expand(template, env.Vars);
        var segments = path.Split('.');
        JsonNode? node;
        string? basePath;
        switch (segments[0])
        {
            case "document":
                (node, basePath) = (env.Document, "");
                break;
            case "period":
                (node, basePath) = (env.Period, $"policyPeriods[{env.PeriodIndex}]");
                break;
            case "claim":
                (node, basePath) = (env.Claim, $"policyPeriods[{env.PeriodIndex}].claims[{env.ClaimIndex}]");
                break;
            case "item":
                (node, basePath) = (env.Item, env.ItemPath);
                break;
            case "context":
                (node, basePath) = (env.Context, null);
                break;
            default:
                throw new RuleCatalogException($"Unknown path root in '{path}'.");
        }

        for (var i = 1; i < segments.Length && node is not null; i++)
        {
            node = node is JsonObject obj ? obj[segments[i]] : null;
        }

        if (basePath is not null)
        {
            var rest = string.Join('.', segments.Skip(1));
            env.Touched.Add(basePath.Length == 0 ? rest : basePath + "." + rest);
        }

        return FromNode(node);
    }

    private static string Expand(string template, IReadOnlyDictionary<string, string> vars) =>
        vars.Count == 0 ? template : RuleCatalog.Placeholder().Replace(template, m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    private static string ItemRest(Expr key) => key is PathExpr p ? ItemRest(p.Path) : "";

    private static string ItemRest(string itemPath) => itemPath.StartsWith("item.", StringComparison.Ordinal) ? itemPath[5..] : itemPath;

    private static Value FromNode(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return Value.Null;
            case JsonArray array:
                return new Value(ValueKind.Array) { Array = array };
            case JsonObject:
                return new Value(ValueKind.Object);
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.Number:
                        return Value.Number(v.GetValue<decimal>());
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        return new Value(ValueKind.Bool) { Bool = v.GetValue<bool>() };
                    case JsonValueKind.String:
                        var s = v.GetValue<string>();
                        return IsoDate().IsMatch(s) && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                            ? new Value(ValueKind.Date) { Date = d }
                            : new Value(ValueKind.String) { Str = s };
                    case JsonValueKind.Null:
                        return Value.Null;
                }

                break;
        }

        throw new RuleCatalogException($"Unsupported JSON value {node.ToJsonString()}.");
    }

    // ---------------------------------------------------------------- messages

    private static string FillMessage(RuleDefinition rule, Env env) =>
        RuleCatalog.Placeholder().Replace(rule.Message, m =>
        {
            var name = m.Groups[1].Value;
            if (env.Vars.TryGetValue(name, out var bound))
            {
                return bound;
            }

            if (rule.Params[name] is { } p)
            {
                return p is JsonValue ? FromNode(p).AsText() : p.ToJsonString();
            }

            return name switch
            {
                "claimNumber" => env.Claim?["claimNumber"]?.GetValue<string>() ?? "(no claim number)",
                "policyNumber" => env.Period?["policyNumber"]?.GetValue<string>() ?? "(no policy number)",
                _ => m.Value,
            };
        });

    private static string Describe(Expr expr, Env env) => expr switch
    {
        PathExpr p => Expand(p.Path, env.Vars),
        ConstExpr c => c.Value?.ToJsonString() ?? "null",
        ParamExpr p => p.Name,
        SumExpr s => $"sum({Expand(s.Over, env.Vars)}.{ItemRest(Expand(s.ItemPath, env.Vars))})",
        CountExpr c => c.Where is null ? $"count({c.Over})" : $"count({c.Over} where …)",
        AddExpr a => string.Join(" + ", a.Operands.Select(o => Describe(o, env))),
        SubExpr s => $"{Describe(s.Left, env)} - {Describe(s.Right, env)}",
        DaysBetweenExpr d => $"days({Describe(d.From, env)} → {Describe(d.To, env)})",
        CoalesceExpr c => string.Join(" ?? ", c.Operands.Select(o => Describe(o, env))),
        CollectExpr c => c.Transform is null ? $"{c.Over}.{ItemRest(c.ItemPath)}" : $"{c.Transform}({c.Over}.{ItemRest(c.ItemPath)})",
        _ => expr.GetType().Name,
    };

    private static string Symbol(CmpOp op) => op switch
    {
        CmpOp.Eq => "==",
        CmpOp.Ne => "!=",
        CmpOp.Lt => "<",
        CmpOp.Le => "<=",
        CmpOp.Gt => ">",
        CmpOp.Ge => ">=",
        _ => op.ToString(),
    };

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex IsoDate();

    // ---------------------------------------------------------------- types

    private enum Tri
    {
        False,
        True,
        Unknown,
    }

    private enum ValueKind
    {
        Null,
        Number,
        Date,
        String,
        Bool,
        Array,
        Object,
    }

    private readonly record struct Value(ValueKind Kind)
    {
        public static Value Null => new(ValueKind.Null);

        public decimal Num { get; init; }
        public DateOnly Date { get; init; }
        public string? Str { get; init; }
        public bool Bool { get; init; }
        public JsonArray? Array { get; init; }

        public bool IsNull => Kind == ValueKind.Null;

        public static Value Number(decimal n) => new(ValueKind.Number) { Num = n };

        public decimal AsNumber() => Kind == ValueKind.Number ? Num : throw new RuleCatalogException($"Expected a number, got {Kind}.");

        public string AsText() => Kind switch
        {
            ValueKind.Number => Num.ToString(CultureInfo.InvariantCulture),
            ValueKind.Date => Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ValueKind.String => Str!,
            ValueKind.Bool => Bool ? "true" : "false",
            ValueKind.Null => "null",
            _ => Kind.ToString(),
        };
    }

    private sealed record Env(JsonObject Document)
    {
        public JsonObject? Period { get; init; }
        public int? PeriodIndex { get; init; }
        public JsonObject? Claim { get; init; }
        public int? ClaimIndex { get; init; }
        public JsonObject? Item { get; init; }
        public string? ItemPath { get; init; }
        public JsonObject Context { get; init; } = [];
        public JsonObject Params { get; init; } = [];
        public IReadOnlyDictionary<string, string> Vars { get; init; } = new Dictionary<string, string>();
        public List<string> Touched { get; init; } = [];
        public List<string> Failures { get; init; } = [];
    }
}
