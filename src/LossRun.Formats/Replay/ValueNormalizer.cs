using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LossRun.Contracts;

namespace LossRun.Formats.Replay;

/// <summary>Turns a printed value into a canonical JSON value per the format's normalizer. Pure; null means "could not normalize".</summary>
internal static partial class ValueNormalizer
{
    public static JsonNode? Normalize(NormalizerSpec spec, string raw, Glossary glossary)
    {
        var text = Spaces().Replace(raw, " ").Trim();
        if (text.Length == 0)
        {
            return null;
        }

        switch (spec.Type)
        {
            case "text":
                return JsonValue.Create(text);
            case "date":
                return CanonicalComparer.NormalizeDate(text) is { } date ? JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : null;
            case "currency":
                return CanonicalComparer.NormalizeCurrency(text) is { } money ? Number(money) : null;
            case "integer":
                return CanonicalComparer.NormalizeCurrency(text) is { } n && n == decimal.Truncate(n) ? Number(n) : null;
            case "boolean":
                return CanonicalComparer.NormalizeBoolean(text) is { } b ? JsonValue.Create(b) : null;
            case "enum":
                return spec.GlossaryField is not null && glossary.ResolveValue(spec.GlossaryField, text) is { } value ? JsonValue.Create(value) : null;
            case "code":
                return Code(text, spec.CodeSystem) is { } code ? JsonValue.Create(code) : null;
            case "claimNumber":
                if (spec.Pattern is not null && spec.Format is not null && Regex.Match(text, spec.Pattern) is { Success: true } m)
                {
                    var formatted = Placeholder().Replace(spec.Format, g => m.Groups[g.Groups[1].Value].Value);
                    return JsonValue.Create(formatted);
                }

                return JsonValue.Create(text);
            default:
                throw new InvalidOperationException($"Unknown normalizer '{spec.Type}'.");
        }
    }

    /// <summary>
    /// Brief 30 item 14: a printed code, padded to its system's width and checked against code-sets.json: a member of a complete table,
    /// the table's width and alphabet for a partial or format-only one (WCIRB class codes), and for an unknown system a short token with
    /// no spaces. Descriptive text ("STRAIN OR TEAR") or a code outside the table is null: an issue, never a code.
    /// </summary>
    internal static string? Code(string text, string? codeSystem)
    {
        if (CanonicalComparer.Default.NormalizeCode(text, codeSystem) is not { } code)
        {
            return null;
        }

        if (codeSystem is null || !CanonicalData.CodeSets.Tables.TryGetValue(codeSystem, out var table))
        {
            return CodeShape().IsMatch(code) ? code : null;
        }

        if (table.Completeness == "complete" && table.Codes.Count > 0)
        {
            return table.Codes.Any(c => c.Code == code) ? code : null;
        }

        var digits = table.Codes.Count == 0 || table.Codes.All(c => c.Code.All(char.IsAsciiDigit));
        return code.Length == table.CodeWidth && (digits ? code.All(char.IsAsciiDigit) : code.All(char.IsAsciiLetterOrDigit)) ? code : null;
    }

    /// <summary>Decimal as a JSON number parsed from invariant text, so serialization is identical across runs and machines.</summary>
    public static JsonNode Number(decimal value) => JsonNode.Parse(value.ToString(CultureInfo.InvariantCulture))!;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9.\-]{0,7}$")]
    private static partial Regex CodeShape();

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9]*)\}")]
    private static partial Regex Placeholder();
}
