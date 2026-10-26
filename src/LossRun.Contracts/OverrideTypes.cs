using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LossRun.Contracts;

/// <summary>
/// Brief 30 item 3: the declared type of a canonical field path ("policyPeriods[0].claims[1].medical.paid"), read from the canonical
/// model (which mirrors canonical-schema.json property for property). An override's <c>newValue</c> is typed JSON of that type:
/// a number for money and counts, a string for identifiers and text (a digit-only claim number stays a string), true/false for
/// flags, a <c>yyyy-MM-dd</c> string for dates, an enum's schema name, and an object (<c>{ code, codeSystem }</c>) for a coded
/// value. The API checks it before it acknowledges a decision; the as-reported revert restores old values by the same type.
/// </summary>
public static class OverrideTypes
{
    /// <summary>The kind a path's value must be: number | string | boolean | date | enum | object; null when the path names no field.</summary>
    public static string? KindOf(string fieldPath) => LeafType(fieldPath) is { } t ? Kind(t) : null;

    /// <summary>
    /// A coded value sent on its <c>.code</c> path as <c>{ code, codeSystem }</c> (the workbench's form) is the coded field itself: the override
    /// moves to the parent path so the code system is stored with the code. Anything else is returned as it is.
    /// </summary>
    public static (string FieldPath, JsonElement Value) Normalize(string fieldPath, JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && fieldPath.EndsWith(".code", StringComparison.Ordinal) && LeafType(fieldPath[..^5]) == typeof(CodedValue)
            ? (fieldPath[..^5], value)
            : (fieldPath, value);

    /// <summary>Null when <paramref name="value"/> is valid for the path (JSON null clears a field); otherwise the error, naming the path.</summary>
    public static string? Validate(string fieldPath, JsonElement value)
    {
        if (LeafType(fieldPath) is not { } type)
        {
            return $"{fieldPath}: not a canonical field.";
        }

        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var kind = Kind(type);
        var ok = kind switch
        {
            "number" => value.ValueKind == JsonValueKind.Number && TryDeserialize(value, type),
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "date" => value.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "enum" => value.ValueKind == JsonValueKind.String && TryDeserialize(value, type),
            _ => value.ValueKind == JsonValueKind.Object && TryDeserialize(value, type)
                && (type != typeof(CodedValue) || value.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String),
        };
        return ok ? null : $"{fieldPath}: expected {Describe(kind, type)}, got {Show(value)}.";
    }

    /// <summary>The stored text of a value (strings unquoted, everything else as JSON) back as a JSON node of the path's declared type.</summary>
    public static JsonNode? FromStoredText(string fieldPath, string? text) =>
        TryFromStoredText(fieldPath, text, out var node) ? node : throw new FormatException($"{fieldPath}: the stored value is not a {KindOf(fieldPath)}.");

    /// <summary>
    /// Brief 32 item 5: the stored old value as typed JSON, never throwing. A number stored as formatted text by an override before brief 30
    /// (<c>"1,234.00"</c>) is read with the canonical currency parser. False when the text is not a value of the field's type (an object clipped at
    /// the column's 4000 characters, a word in a number field): the caller leaves the field as it is and says so.
    /// </summary>
    public static bool TryFromStoredText(string fieldPath, string? text, out JsonNode? node)
    {
        node = null;
        if (text is null)
        {
            return true;
        }

        switch (KindOf(fieldPath))
        {
            case "number":
                node = ParseOrNull(text) is JsonValue number && number.TryGetValue<decimal>(out _) ? number
                    : CanonicalComparer.NormalizeCurrency(text) is { } amount ? JsonValue.Create(amount) : null;
                return node is not null;
            case "boolean":
                node = bool.TryParse(text.Trim(), out var flag) ? JsonValue.Create(flag) : null;
                return node is not null;
            case "object":
                node = ParseOrNull(text) as JsonObject;
                return node is not null;
            default:
                node = JsonValue.Create(text);
                return true;
        }
    }

    private static JsonNode? ParseOrNull(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Brief 32 item 5: null when every index in <paramref name="fieldPath"/> exists in <paramref name="document"/>; otherwise the error, naming the
    /// first index that does not (<c>claims[99]</c> on a period of 3 claims is a 400, not an activity that fails after the 202).
    /// </summary>
    public static string? IndexError(LossRunDocument document, string fieldPath)
    {
        JsonNode? node = JsonSerializer.SerializeToNode(document, CanonicalJson.Options);
        var walked = new List<string>();
        foreach (var part in fieldPath.Split('.'))
        {
            var segments = part.Split('[');
            node = (node as JsonObject)?[segments[0]];
            walked.Add(segments[0]);
            foreach (var index in segments.Skip(1).Select(s => int.TryParse(s.TrimEnd(']'), NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : -1))
            {
                var count = (node as JsonArray)?.Count ?? 0;
                if (index < 0 || index >= count)
                {
                    return $"{fieldPath}: {string.Join('.', walked)}[{index}] does not exist (the document has {count}).";
                }

                node = ((JsonArray)node!)[index];
                walked[^1] += $"[{index}]";
            }

            if (node is null)
            {
                return null; // an absent leaf or object is set by the override; only indexes must exist
            }
        }

        return null;
    }

    public static Type? LeafType(string fieldPath)
    {
        Type? type = typeof(LossRunDocument);
        foreach (var part in fieldPath.Split('.'))
        {
            var bracket = part.IndexOf('[', StringComparison.Ordinal);
            var name = bracket < 0 ? part : part[..bracket];
            var property = type?.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name) == name);
            if (property is null || name.Length == 0)
            {
                return null;
            }

            type = property.PropertyType;
            for (var rest = bracket < 0 ? "" : part[bracket..]; rest.Length > 0;)
            {
                var close = rest.IndexOf(']', StringComparison.Ordinal);
                if (rest[0] != '[' || close < 2 || !int.TryParse(rest[1..close], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    return null;
                }

                type = ElementType(type);
                if (type is null)
                {
                    return null;
                }

                rest = rest[(close + 1)..];
            }
        }

        return type is null || type == typeof(LossRunDocument) ? null : Nullable.GetUnderlyingType(type) ?? type;
    }

    private static Type? ElementType(Type type) =>
        type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type) && type.GetGenericArguments() is [var element] ? element : null;

    private static string Kind(Type t) =>
        t == typeof(string) ? "string"
        : t == typeof(bool) ? "boolean"
        : t == typeof(DateOnly) ? "date"
        : t.IsEnum ? "enum"
        : t == typeof(decimal) || t == typeof(int) || t == typeof(long) || t == typeof(double) ? "number"
        : "object";

    private static bool TryDeserialize(JsonElement value, Type type)
    {
        try
        {
            value.Deserialize(type, CanonicalJson.Options);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Describe(string kind, Type type) => kind switch
    {
        "date" => "a date string yyyy-MM-dd",
        "enum" => $"one of {string.Join(", ", Enum.GetNames(type).Select(JsonNamingPolicy.CamelCase.ConvertName))}",
        "object" when type == typeof(CodedValue) => "an object { code, codeSystem }",
        "object" => $"an object ({type.Name})",
        "boolean" => "true or false",
        _ => $"a JSON {kind}",
    };

    private static string Show(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => $"the string \"{value.GetString()}\"",
        _ => $"a JSON {value.ValueKind.ToString().ToLowerInvariant()}",
    };
}
