using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Serialization;

namespace LossRun.Analytics;

/// <summary>One metric of the analytics catalog (docs/analytics/catalog.yaml).</summary>
public sealed record MetricDef(string Name, string Label, string Type, string Unit, string? Breakdown, JsonObject Params, JsonObject Tolerance,
    IReadOnlyList<string> InputFields)
{
    /// <summary>Fixed scale of the metric's integer representation: money in cents, ratios and rates in millionths, days in tenths.</summary>
    public int Scale => Unit switch
    {
        "money" => 2,
        "ratio" or "rate" => 6,
        "days" => 1,
        _ => 0,
    };

    public long MoneyParam(string name, long fallbackDollars) => (Params[name]?.GetValue<long>() ?? fallbackDollars) * 100;

    public IReadOnlyList<long> MoneyParams(string name) =>
        Params[name] is JsonArray a ? [.. a.Select(v => v!.GetValue<long>() * 100)] : [];

    public int IntParam(string name, int fallback) => (int)(Params[name]?.GetValue<long>() ?? fallback);
}

/// <summary>
/// The analytics catalog, loaded from the YAML embedded at build time (the docs file is the source of truth, CLAUDE.md).
/// <see cref="Json"/> is the catalog the UI and the TypeScript twin read (<c>GET /catalog</c>, <c>catalog.json</c>).
/// </summary>
public sealed class Catalog
{
    private static readonly Lazy<Catalog> Lazy = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LossRun.Analytics.catalog.yaml")
            ?? throw new InvalidOperationException("catalog.yaml is not embedded");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    public static Catalog Embedded => Lazy.Value;

    public string Version { get; }
    public IReadOnlyList<MetricDef> Metrics { get; }
    public JsonObject Json { get; }

    private Catalog(JsonObject json)
    {
        Json = json;
        Version = json["catalogVersion"]!.GetValue<string>();
        Metrics = [.. json["metrics"]!.AsArray().Select(m => new MetricDef(
            m!["name"]!.GetValue<string>(), m["label"]!.GetValue<string>(), m["type"]!.GetValue<string>(), m["unit"]!.GetValue<string>(),
            m["breakdown"]?.GetValue<string>(), m["params"]?.AsObject().DeepClone().AsObject() ?? [], m["tolerance"]!.AsObject().DeepClone().AsObject(),
            [.. m["inputs"]!.AsArray().Select(i => i!["field"]!.GetValue<string>())]))];
    }

    public MetricDef this[string name] => Metrics.First(m => m.Name == name);

    /// <summary>Parses catalog YAML: scalars that look like numbers or booleans become JSON numbers/booleans, the rest strings.</summary>
    public static Catalog Parse(string yaml)
    {
        var graph = new DeserializerBuilder().Build().Deserialize<object>(yaml);
        return new Catalog(ToNode(graph)!.AsObject());
    }

    /// <summary>The catalog as indented JSON (catalog.json for the client twin and <c>GET /catalog</c>).</summary>
    public string ToJson() => Json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        IDictionary<object, object> map => new JsonObject(map.Select(kv => KeyValuePair.Create(Convert.ToString(kv.Key, CultureInfo.InvariantCulture)!, ToNode(kv.Value)))),
        IList<object> list => new JsonArray([.. list.Select(ToNode)]),
        string s => Scalar(s),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    private static JsonNode? Scalar(string s) =>
        s is "true" or "false" ? JsonValue.Create(s == "true")
        : s is "null" or "~" ? null
        : long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) ? JsonValue.Create(l)
        : s.Contains('.') && !s.Contains("..") && s.Count(c => c == '.') == 1 && decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)
            ? JsonValue.Create(d)
        : JsonValue.Create(s);
}
