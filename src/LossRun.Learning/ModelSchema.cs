using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using LossRun.Contracts;
using LossRun.Formats;

namespace LossRun.Learning;

/// <summary>
/// The model writes the same spec humans write: its response schema is generated from
/// <c>docs/evaluation/carrier-format.schema.json</c>, transformed to what strict structured outputs accept — every
/// property required (optional ones nullable), <c>additionalProperties: false</c>, no pattern/format/min/max keywords,
/// <c>const</c> → <c>enum</c>, map-valued objects → arrays of entries. Fields the pipeline owns (ids, versions,
/// fingerprint keys, provenance) are removed; <see cref="ToSpec"/> fills them back in.
/// </summary>
public static class ModelSchema
{
    /// <summary>Spec properties written by code, never by the model.</summary>
    public static readonly string[] PipelineOwned = ["specSchema", "formatId", "version", "fileKind", "createdBy", "createdAt", "reviewedBy", "supersedes", "fingerprint", "learning",
        "carrierNames"]; // brief 35 Answers 2: other legal names of the carrier, added by a reviewer

    private static readonly string[] Unsupported = ["pattern", "format", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "minItems", "maxItems", "minLength",
        "maxLength", "$schema", "$id", "title", "specVersion", "default"];

    /// <summary>The checked-in spec schema text (source of truth).</summary>
    public static string SpecSchemaText { get; } = Read("carrier-format.schema.json");

    /// <summary>The hand-written synthetic-mutual-pdf spec, used as the worked example in the prompt.</summary>
    public static string ExampleSpecText { get; } = Read("example-spec.json");

    /// <summary>Strict response schema for a full spec proposal (learn).</summary>
    public static JsonObject Proposal { get; } = BuildProposal();

    /// <summary>Strict response schema for a delta: column-map additions and newly ignored headers only.</summary>
    public static JsonObject Delta { get; } = BuildDelta();

    private static JsonObject BuildProposal()
    {
        var source = JsonNode.Parse(SpecSchemaText)!.AsObject();
        var properties = source["properties"]!.AsObject();
        var required = source["required"]!.AsArray().Select(r => r!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var root = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        foreach (var (name, schema) in properties)
        {
            if (!PipelineOwned.Contains(name))
            {
                root["properties"]!.AsObject()[name] = schema!.DeepClone();
            }
        }

        root["required"] = new JsonArray(root["properties"]!.AsObject().Where(p => required.Contains(p.Key)).Select(p => (JsonNode)p.Key).ToArray());
        root["$defs"] = source["$defs"]!.DeepClone();
        return Strict(root);
    }

    private static JsonObject BuildDelta()
    {
        var source = JsonNode.Parse(SpecSchemaText)!.AsObject();
        var mapping = source["properties"]!["columnMap"]!["items"]!.DeepClone();
        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["additions"] = new JsonObject { ["type"] = "array", ["items"] = mapping, ["description"] = "Column-map entries for the unmapped headers only." },
                ["ignoredHeaders"] = new JsonObject
                {
                    ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "Unmapped headers that carry nothing the canonical schema stores.",
                },
            },
            ["required"] = new JsonArray("additions", "ignoredHeaders"),
            ["$defs"] = source["$defs"]!.DeepClone(),
        };
        return Strict(root);
    }

    /// <summary>Transforms a JSON Schema (2020-12 subset used by the spec schema) into the strict structured-output subset.</summary>
    internal static JsonObject Strict(JsonObject schema)
    {
        var result = new JsonObject();
        foreach (var (key, value) in schema)
        {
            if (Unsupported.Contains(key))
            {
                continue;
            }

            switch (key)
            {
                case "const":
                    result["enum"] = new JsonArray(value!.DeepClone());
                    break;
                case "properties":
                case "$defs":
                    var props = new JsonObject();
                    foreach (var (name, child) in value!.AsObject())
                    {
                        props[name] = Strict(child!.AsObject());
                    }

                    result[key] = props;
                    break;
                case "items":
                    result["items"] = Strict(value!.AsObject());
                    break;
                default:
                    result[key] = value?.DeepClone();
                    break;
            }
        }

        // A free-form value ({} — the const locator's value) becomes a scalar.
        if (schema.Count == 0)
        {
            return new JsonObject { ["anyOf"] = new JsonArray(Type("string"), Type("number"), Type("boolean"), Type("null")) };
        }

        if (result["enum"] is JsonArray values && result["type"] is null && values.All(v => v is JsonValue j && j.GetValueKind() == JsonValueKind.String))
        {
            result["type"] = "string";
        }

        if (IsObject(result))
        {
            // Map-valued objects (e.g. periods.lookup.subtotals: header → path) are not allowed: use [{key, value}].
            if (result["additionalProperties"] is JsonObject entry)
            {
                return new JsonObject
                {
                    ["type"] = Nullable(result["type"]) ? new JsonArray("array", "null") : "array",
                    ["description"] = (result["description"]?.GetValue<string>() ?? "") + " Entries as {key, value}.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["key"] = Type("string"), ["value"] = Strict(entry) },
                        ["required"] = new JsonArray("key", "value"),
                        ["additionalProperties"] = false,
                    },
                };
            }

            var properties = result["properties"] as JsonObject ?? [];
            result["properties"] = properties;
            var required = (result["required"] as JsonArray)?.Select(r => r!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? [];
            foreach (var name in properties.Select(p => p.Key).ToList())
            {
                if (!required.Contains(name))
                {
                    properties[name] = MakeNullable(properties[name]!.DeepClone().AsObject());
                }
            }

            result["required"] = new JsonArray(properties.Select(p => (JsonNode)p.Key).ToArray());
            result["additionalProperties"] = false;
        }

        return result;
    }

    private static JsonObject Type(string type) => new() { ["type"] = type };

    private static bool IsObject(JsonObject schema) =>
        schema["type"] is JsonValue v && v.GetValue<string>() == "object"
        || schema["type"] is JsonArray a && a.Any(t => t!.GetValue<string>() == "object");

    private static bool Nullable(JsonNode? type) => type is JsonArray a && a.Any(t => t!.GetValue<string>() == "null");

    private static JsonObject MakeNullable(JsonObject schema)
    {
        if (schema["anyOf"] is JsonArray any)
        {
            if (!any.Any(s => s?["type"]?.GetValue<string>() == "null"))
            {
                any.Add(Type("null"));
            }

            return schema;
        }

        if (schema["$ref"] is not null)
        {
            var description = schema["description"]?.DeepClone();
            schema.Remove("description");
            var wrapped = new JsonObject { ["anyOf"] = new JsonArray(schema, Type("null")) };
            if (description is not null)
            {
                wrapped["description"] = description;
            }

            return wrapped;
        }

        switch (schema["type"])
        {
            case JsonValue v:
                schema["type"] = new JsonArray(v.GetValue<string>(), "null");
                break;
            case JsonArray a when !Nullable(a):
                a.Add("null");
                break;
        }

        if (schema["enum"] is JsonArray e && !e.Any(x => x is null))
        {
            e.Add(null);
        }

        return schema;
    }

    // ------------------------------------------------------------------ proposal → spec

    /// <summary>
    /// Builds a spec from a model proposal: drops nulls (so defaults apply), turns entry arrays back into maps, and adds
    /// the pipeline-owned fields. Throws <see cref="JsonException"/> when the proposal does not deserialize.
    /// </summary>
    public static CarrierFormat ToSpec(string proposalJson, Action<CarrierFormat> pipelineFields)
    {
        var proposal = JsonNode.Parse(proposalJson) as JsonObject ?? throw new JsonException("The proposal is not a JSON object.");
        var cleaned = (JsonObject)DropNulls(proposal)!;
        if (cleaned["periods"]?["lookup"]?["subtotals"] is JsonArray entries)
        {
            var map = new JsonObject();
            foreach (var entry in entries.OfType<JsonObject>())
            {
                map[entry["key"]!.GetValue<string>()] = entry["value"]?.DeepClone();
            }

            cleaned["periods"]!["lookup"]!["subtotals"] = map;
        }

        foreach (var owned in PipelineOwned)
        {
            cleaned.Remove(owned);
        }

        var spec = JsonSerializer.Deserialize<CarrierFormat>(cleaned.ToJsonString(), CanonicalJson.Options) ?? throw new JsonException("Empty proposal.");
        pipelineFields(spec);
        return spec;
    }

    /// <summary>Column-map additions and ignored headers from a delta answer.</summary>
    public static (List<ColumnMapping> Additions, List<string> Ignored) ToDelta(string deltaJson)
    {
        var node = (JsonObject)DropNulls(JsonNode.Parse(deltaJson) ?? throw new JsonException("Empty delta."))!;
        var additions = node["additions"]?.Deserialize<List<ColumnMapping>>(CanonicalJson.Options) ?? [];
        var ignored = node["ignoredHeaders"]?.Deserialize<List<string>>(CanonicalJson.Options) ?? [];
        return (additions, ignored);
    }

    private static JsonNode? DropNulls(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Where(p => p.Value is not null).Select(p => KeyValuePair.Create(p.Key, DropNulls(p.Value)))),
        JsonArray a => new JsonArray(a.Where(x => x is not null).Select(DropNulls).ToArray()),
        _ => node?.DeepClone(),
    };

    private static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"LossRun.Learning.{name}")
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
