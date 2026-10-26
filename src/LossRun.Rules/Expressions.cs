using System.Text.Json.Nodes;

namespace LossRun.Rules;

// AST of the catalog's declarative expression language (rule-catalog.json `expressionLanguage`).

public abstract record Expr;

// ---- values ----
public sealed record PathExpr(string Path) : Expr;
public sealed record ConstExpr(JsonNode? Value) : Expr;
public sealed record ParamExpr(string Name) : Expr;
public sealed record AddExpr(IReadOnlyList<Expr> Operands, bool IgnoreNulls) : Expr;
public sealed record SubExpr(Expr Left, Expr Right) : Expr;
public sealed record SumExpr(string Over, string ItemPath) : Expr;
public sealed record CountExpr(string Over, Expr? Where) : Expr;
public sealed record DaysBetweenExpr(Expr From, Expr To) : Expr;
public sealed record CoalesceExpr(IReadOnlyList<Expr> Operands) : Expr;

/// <summary>Array of the values at <c>ItemPath</c> over a collection; <c>Transform</c> "year" maps dates to their year.</summary>
public sealed record CollectExpr(string Over, string ItemPath, string? Transform) : Expr;

// ---- predicates ----
public enum CmpOp
{
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
}

/// <param name="Normalize">Brief 35 (R-34): <c>text</c> compares strings as normalized text (case, punctuation and spacing ignored).</param>
public sealed record CmpExpr(CmpOp Op, Expr Left, Expr Right, decimal Tolerance, string? Normalize = null) : Expr;
public sealed record AndExpr(IReadOnlyList<Expr> Operands) : Expr;
public sealed record OrExpr(IReadOnlyList<Expr> Operands) : Expr;
public sealed record NotExpr(Expr Operand) : Expr;
public sealed record AllKnownExpr(IReadOnlyList<Expr> Operands) : Expr;
public sealed record InExpr(Expr Value, Expr Values) : Expr;
public sealed record IsNullExpr(Expr Operand) : Expr;
public sealed record NotNullExpr(Expr Operand) : Expr;
public sealed record NotBlankExpr(Expr Operand) : Expr;
public sealed record UniqueExpr(string Over, Expr Key, string? Normalize) : Expr;

/// <summary>True when every element of <c>Value</c> (an array) is in <c>Of</c>; unknown when Value is null or empty.</summary>
public sealed record SubsetOfExpr(Expr Value, Expr Of) : Expr;

internal static class ExprParser
{
    public static Expr Parse(JsonNode? node, string where)
    {
        if (node is not JsonObject obj || obj.Count == 0)
        {
            throw new RuleCatalogException($"{where}: expected an expression object.");
        }

        if (obj.ContainsKey("cmp"))
        {
            var op = Str(obj["cmp"], where) switch
            {
                "eq" => CmpOp.Eq,
                "ne" => CmpOp.Ne,
                "lt" => CmpOp.Lt,
                "le" => CmpOp.Le,
                "gt" => CmpOp.Gt,
                "ge" => CmpOp.Ge,
                var other => throw new RuleCatalogException($"{where}: unknown cmp operator '{other}'."),
            };
            var tolerance = obj["tolerance"] is { } t ? t.GetValue<decimal>() : 0m;
            var normalize = obj["normalize"]?.GetValue<string>() switch
            {
                null => null,
                "text" => "text",
                var other => throw new RuleCatalogException($"{where}: unknown cmp normalizer '{other}'."),
            };
            return new CmpExpr(op, Parse(obj["left"], where + ".left"), Parse(obj["right"], where + ".right"), tolerance, normalize);
        }

        if (obj.Count != 1)
        {
            throw new RuleCatalogException($"{where}: expression must have exactly one operator key, got [{string.Join(", ", obj.Select(p => p.Key))}].");
        }

        var (key, arg) = obj.First();
        var at = $"{where}.{key}";
        return key switch
        {
            "path" => new PathExpr(Str(arg, at)),
            "const" => new ConstExpr(arg?.DeepClone()),
            "param" => new ParamExpr(Str(arg, at)),
            "add" => new AddExpr(List(arg, at), IgnoreNulls: false),
            "sumNonNull" => new AddExpr(List(arg, at), IgnoreNulls: true),
            "sub" => Pair(arg, at, (l, r) => new SubExpr(l, r)),
            "sum" => new SumExpr(Str(arg?["over"], at + ".over"), Str(arg?["path"], at + ".path")),
            "count" => new CountExpr(Str(arg?["over"], at + ".over"), arg?["where"] is { } w ? Parse(w, at + ".where") : null),
            "daysBetween" => Pair(arg, at, (from, to) => new DaysBetweenExpr(from, to)),
            "coalesce" => new CoalesceExpr(List(arg, at)),
            "collect" => new CollectExpr(Str(arg?["over"], at + ".over"), Str(arg?["path"], at + ".path"), Transform(arg?["transform"], at + ".transform")),
            "subsetOf" => new SubsetOfExpr(Parse(arg?["value"], at + ".value"), Parse(arg?["of"], at + ".of")),
            "and" => new AndExpr(List(arg, at)),
            "or" => new OrExpr(List(arg, at)),
            "not" => new NotExpr(Parse(arg, at)),
            "allKnown" => new AllKnownExpr(List(arg, at)),
            "in" => new InExpr(Parse(arg?["value"], at + ".value"), ParseValues(arg?["values"], at + ".values")),
            "isNull" => new IsNullExpr(Parse(arg, at)),
            "notNull" => new NotNullExpr(Parse(arg, at)),
            "notBlank" => new NotBlankExpr(Parse(arg, at)),
            "unique" => new UniqueExpr(Str(arg?["over"], at + ".over"), Parse(arg?["key"], at + ".key"), Normalizer(arg?["normalize"], at + ".normalize")),
            _ => throw new RuleCatalogException($"{where}: unknown operator '{key}'."),
        };
    }

    private static string? Transform(JsonNode? node, string where) => node?.GetValue<string>() switch
    {
        null => null,
        "year" => "year",
        var other => throw new RuleCatalogException($"{where}: unknown transform '{other}'."),
    };

    private static string? Normalizer(JsonNode? node, string where) => node?.GetValue<string>() switch
    {
        null => null,
        "claimNumber" => "claimNumber",
        var other => throw new RuleCatalogException($"{where}: unknown normalizer '{other}'."),
    };

    private static Expr ParseValues(JsonNode? node, string where) =>
        node is JsonArray array ? new ConstExpr(array.DeepClone()) : Parse(node, where);

    private static string Str(JsonNode? node, string where) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : throw new RuleCatalogException($"{where}: expected a non-empty string.");

    private static IReadOnlyList<Expr> List(JsonNode? node, string where)
    {
        if (node is not JsonArray array || array.Count == 0)
        {
            throw new RuleCatalogException($"{where}: expected a non-empty array of expressions.");
        }

        return array.Select((n, i) => Parse(n, $"{where}[{i}]")).ToList();
    }

    private static Expr Pair(JsonNode? node, string where, Func<Expr, Expr, Expr> create)
    {
        var list = List(node, where);
        return list.Count == 2 ? create(list[0], list[1]) : throw new RuleCatalogException($"{where}: expected exactly two operands.");
    }

    /// <summary>All path-like strings in an expression (paths, sum/count/unique collections and item paths).</summary>
    public static IEnumerable<string> Paths(Expr expr) => expr switch
    {
        PathExpr p => [p.Path],
        SumExpr s => [s.Over, s.ItemPath],
        CountExpr c => c.Where is null ? [c.Over] : Paths(c.Where).Prepend(c.Over),
        UniqueExpr u => Paths(u.Key).Prepend(u.Over),
        CmpExpr c => Paths(c.Left).Concat(Paths(c.Right)),
        AddExpr a => a.Operands.SelectMany(Paths),
        SubExpr s => Paths(s.Left).Concat(Paths(s.Right)),
        DaysBetweenExpr d => Paths(d.From).Concat(Paths(d.To)),
        CoalesceExpr c => c.Operands.SelectMany(Paths),
        CollectExpr c => [c.Over, c.ItemPath],
        SubsetOfExpr s => Paths(s.Value).Concat(Paths(s.Of)),
        AndExpr a => a.Operands.SelectMany(Paths),
        OrExpr o => o.Operands.SelectMany(Paths),
        AllKnownExpr a => a.Operands.SelectMany(Paths),
        NotExpr n => Paths(n.Operand),
        InExpr i => Paths(i.Value).Concat(Paths(i.Values)),
        IsNullExpr n => Paths(n.Operand),
        NotNullExpr n => Paths(n.Operand),
        NotBlankExpr n => Paths(n.Operand),
        _ => [],
    };

    /// <summary>Paths that are evaluated with an <c>item</c> in scope (inside sum/count/unique).</summary>
    public static IEnumerable<string> ItemScopedPaths(Expr expr) => expr switch
    {
        SumExpr s => [s.ItemPath],
        CollectExpr c => [c.ItemPath],
        SubsetOfExpr s => ItemScopedPaths(s.Value).Concat(ItemScopedPaths(s.Of)),
        CountExpr { Where: { } w } => Paths(w),
        UniqueExpr u => Paths(u.Key),
        CmpExpr c => ItemScopedPaths(c.Left).Concat(ItemScopedPaths(c.Right)),
        AddExpr a => a.Operands.SelectMany(ItemScopedPaths),
        SubExpr s => ItemScopedPaths(s.Left).Concat(ItemScopedPaths(s.Right)),
        DaysBetweenExpr d => ItemScopedPaths(d.From).Concat(ItemScopedPaths(d.To)),
        CoalesceExpr c => c.Operands.SelectMany(ItemScopedPaths),
        AndExpr a => a.Operands.SelectMany(ItemScopedPaths),
        OrExpr o => o.Operands.SelectMany(ItemScopedPaths),
        AllKnownExpr a => a.Operands.SelectMany(ItemScopedPaths),
        NotExpr n => ItemScopedPaths(n.Operand),
        InExpr i => ItemScopedPaths(i.Value).Concat(ItemScopedPaths(i.Values)),
        IsNullExpr n => ItemScopedPaths(n.Operand),
        NotNullExpr n => ItemScopedPaths(n.Operand),
        NotBlankExpr n => ItemScopedPaths(n.Operand),
        _ => [],
    };

    public static IEnumerable<string> ParamNames(Expr expr) => expr switch
    {
        ParamExpr p => [p.Name],
        CmpExpr c => ParamNames(c.Left).Concat(ParamNames(c.Right)),
        AddExpr a => a.Operands.SelectMany(ParamNames),
        SubExpr s => ParamNames(s.Left).Concat(ParamNames(s.Right)),
        DaysBetweenExpr d => ParamNames(d.From).Concat(ParamNames(d.To)),
        CoalesceExpr c => c.Operands.SelectMany(ParamNames),
        CountExpr { Where: { } w } => ParamNames(w),
        SubsetOfExpr s => ParamNames(s.Value).Concat(ParamNames(s.Of)),
        AndExpr a => a.Operands.SelectMany(ParamNames),
        OrExpr o => o.Operands.SelectMany(ParamNames),
        AllKnownExpr a => a.Operands.SelectMany(ParamNames),
        NotExpr n => ParamNames(n.Operand),
        InExpr i => ParamNames(i.Value).Concat(ParamNames(i.Values)),
        _ => [],
    };
}
