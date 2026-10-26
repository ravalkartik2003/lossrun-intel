using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using LossRun.Contracts;

namespace LossRun.Learning;

/// <summary>Per-file-kind gate settings (docs/evaluation/learn-gate.json → fileKinds).</summary>
public sealed record GateFileKind(bool RequirePrintedTotals, double MinCoverage, bool RequireCarrierFromContext, IReadOnlyList<string> ConditionalFromContext);

/// <summary>The gate for a summary-only loss run (learn-gate.json → summaryOnly; ADR 0017).</summary>
/// <param name="EveryAmountChecked">Brief 22 Answers 6: every money amount a summary-only replay reads must be the subject of a
/// passing printed-total check (R-26 for that component and measure); otherwise the spec is not published.</param>
public sealed record SummaryGate(IReadOnlyList<string> RequiredFields, IReadOnlyList<string> PeriodIdentityAnyOf, IReadOnlyList<string> TotalsRules,
    bool EveryAmountChecked = false);

/// <summary>
/// The learn publish gate as data (06b Answers 1): required fields, always-required fields, the totals rules and the
/// per-file-kind policy are read from <c>docs/evaluation/learn-gate.json</c> (embedded); code never redefines them.
/// </summary>
public sealed class LearnGate
{
    public string Version { get; }
    public IReadOnlyList<string> RequiredFields { get; }
    public IReadOnlyList<string> AlwaysRequired { get; }
    public IReadOnlyList<string> TotalsRules { get; }
    public IReadOnlyDictionary<string, GateFileKind> FileKinds { get; }
    public SummaryGate SummaryOnly { get; private init; } = new([], [], []);

    /// <summary>Brief 30 item 19: how many of the format's last accepted documents a drift repair is also verified on (<c>exemplars.count</c>).</summary>
    public int Exemplars { get; private init; } = 2;

    public static LearnGate Default { get; } = Load(ReadEmbedded());

    private LearnGate(string version, IReadOnlyList<string> required, IReadOnlyList<string> always, IReadOnlyList<string> totals, IReadOnlyDictionary<string, GateFileKind> kinds) =>
        (Version, RequiredFields, AlwaysRequired, TotalsRules, FileKinds) = (version, required, always, totals, kinds);

    public static LearnGate Load(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        static List<string> Strings(JsonNode? node) => node is JsonArray a ? a.Select(x => x!.GetValue<string>()).ToList() : [];
        var kinds = root["fileKinds"]!.AsObject().ToDictionary(k => k.Key, k => new GateFileKind(
            k.Value!["requirePrintedTotals"]!.GetValue<bool>(), k.Value["minCoverage"]!.GetValue<double>(), k.Value["requireCarrierFromContext"]!.GetValue<bool>(),
            Strings(k.Value["conditionalFromContext"])), StringComparer.Ordinal);
        var summary = root["summaryOnly"];
        return new LearnGate(root["learnGateVersion"]!.GetValue<string>(), Strings(root["requiredFields"]), Strings(root["alwaysRequired"]), Strings(root["totalsRules"]), kinds)
        {
            SummaryOnly = new SummaryGate(Strings(summary?["requiredFields"]), Strings(summary?["periodIdentityAnyOf"]), Strings(summary?["totalsRules"]),
                summary?["everyAmountChecked"]?.GetValue<bool>() ?? false),
            Exemplars = root["exemplars"]?["count"]?.GetValue<int>() ?? 2,
        };
    }

    /// <summary>Every policy period is summary-only (ADR 0017): no claim rows, period totals only.</summary>
    /// <remarks>A period with neither claims nor printed totals is a replay that read nothing, not a summary (brief 19 corpus v3
    /// regression: the summary gate then hid "no claim row was read" from the learn feedback).</remarks>
    public static bool IsSummaryOnly(LossRunDocument document) =>
        document.PolicyPeriods.Count > 0 && document.PolicyPeriods.All(p => p.DetailLevel == DetailLevel.SummaryOnly && p.Subtotals is not null);

    /// <summary>The totals rules that decide the gate for this document: R-26 for a summary-only run, else R-10/R-12.</summary>
    public IReadOnlyList<string> TotalsRulesFor(LossRunDocument document) => IsSummaryOnly(document) ? SummaryOnly.TotalsRules : TotalsRules;

    /// <summary>Required fields the document lacks (period fields per period, always-required claim fields per claim).</summary>
    public IReadOnlyList<string> Missing(LossRunDocument document)
    {
        var node = JsonSerializer.SerializeToNode(document, CanonicalJson.Options)!.AsObject();
        var periods = (node["policyPeriods"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var missing = new List<string>();
        var summary = IsSummaryOnly(document);
        if (summary)
        {
            for (var i = 0; i < periods.Count; i++)
            {
                if (SummaryOnly.PeriodIdentityAnyOf.Count > 0 && SummaryOnly.PeriodIdentityAnyOf.All(f => Blank(Get(periods[i], f))))
                {
                    missing.Add($"policyPeriods[{i}] identity ({string.Join(" | ", SummaryOnly.PeriodIdentityAnyOf)})");
                }
            }
        }

        // Brief 19 Answers 1: a component the run prints no column for ({ present: false }) is not required.
        var absent = new[] { ("indemnity", document.ComponentPresence?.Indemnity), ("medical", document.ComponentPresence?.Medical), ("expense", document.ComponentPresence?.Expense) }
            .Where(c => c.Item2 is { Present: false }).Select(c => $"claim.{c.Item1}.").ToList();
        foreach (var field in (summary ? SummaryOnly.RequiredFields : RequiredFields).Where(f => !absent.Any(a => f.StartsWith(a, StringComparison.Ordinal))))
        {
            var (scope, path) = Split(field);
            switch (scope)
            {
                case "document" when Get(node, path) is null:
                    missing.Add(field);
                    break;
                case "period":
                    missing.AddRange(periods.Select((p, i) => (p, i)).Where(x => Get(x.p, path) is null).Select(x => $"policyPeriods[{x.i}].{path}"));
                    break;
                case "claim" when AlwaysRequired.Contains(field):
                    for (var i = 0; i < periods.Count; i++)
                    {
                        var claims = (periods[i]["claims"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                        missing.AddRange(claims.Select((c, j) => (c, j)).Where(x => Blank(Get(x.c, path))).Select(x => $"policyPeriods[{i}].claims[{x.j}].{path}"));
                    }

                    break;
                case "claim" when !periods.SelectMany(p => (p["claims"] as JsonArray ?? []).OfType<JsonObject>()).Any(c => !Blank(Get(c, path))):
                    missing.Add(field);
                    break;
            }
        }

        return missing;
    }

    /// <summary>Share of required fields filled (document present; period on every period; claim on at least one claim).</summary>
    public double Coverage(LossRunDocument document)
    {
        var missing = Missing(document);
        var required = IsSummaryOnly(document) ? SummaryOnly.RequiredFields : RequiredFields;
        var filled = required.Count(f => !missing.Any(m => m == f || m.EndsWith("." + Split(f).Path, StringComparison.Ordinal) && Covers(f, m)));
        return required.Count == 0 ? 1.0 : Math.Round((double)filled / required.Count, 4);
    }

    /// <summary>A missing entry such as "policyPeriods[1].effectiveDate" belongs to the required field "period.effectiveDate".</summary>
    private static bool Covers(string field, string missing)
    {
        var (scope, _) = Split(field);
        return scope switch
        {
            "period" => missing.StartsWith("policyPeriods[", StringComparison.Ordinal) && !missing.Contains(".claims[", StringComparison.Ordinal),
            "claim" => missing.Contains(".claims[", StringComparison.Ordinal),
            _ => false,
        };
    }

    public bool IsAlwaysRequired(string missing) =>
        missing.StartsWith("claim rows", StringComparison.Ordinal) || AlwaysRequired.Any(f => Covers(f, missing) && missing.EndsWith("." + Split(f).Path, StringComparison.Ordinal));

    private static (string Scope, string Path) Split(string field)
    {
        var dot = field.IndexOf('.');
        return (field[..dot], field[(dot + 1)..]);
    }

    private static bool Blank(JsonNode? node) => node is null || node is JsonValue v && v.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s);

    private static JsonNode? Get(JsonObject root, string path)
    {
        JsonNode? node = root;
        foreach (var part in path.Split('.'))
        {
            node = (node as JsonObject)?[part];
        }

        return node;
    }

    private static string ReadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LossRun.Learning.learn-gate.json")
            ?? throw new InvalidOperationException("Embedded learn-gate.json is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
