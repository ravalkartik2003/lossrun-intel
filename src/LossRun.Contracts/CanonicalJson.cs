using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace LossRun.Contracts;

/// <summary>The one serializer configuration for canonical data (matches canonical-schema.json naming).</summary>
public static class CanonicalJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize(LossRunDocument document) => JsonSerializer.Serialize(document, Options);

    public static LossRunDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<LossRunDocument>(json, Options)
        ?? throw new JsonException("Canonical document JSON was null.");

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null, // lineage keys are field paths; keep verbatim
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }
}
