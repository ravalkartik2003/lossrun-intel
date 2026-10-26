using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LossRun.Contracts;

/// <summary>Kind of canonical field being compared; selects the normalization (docs/evaluation/comparator.md).</summary>
public enum FieldKind
{
    Date,
    Currency,
    Integer,
    ClaimNumber,
    Code,
    Enum,
    Boolean,
    ShortText,
    FreeText,
}

public sealed record ComparerOptions
{
    /// <summary>Normalized Levenshtein similarity required for free text (RealDocBench: long strings fuzzy ≥ 92).</summary>
    public double FreeTextThreshold { get; init; } = 0.92;

    /// <summary>Absolute tolerance for currency equality in evaluation (exact to the cent).</summary>
    public decimal CurrencyTolerance { get; init; } = 0.005m;
}

/// <param name="Match">True when the two values are equal after normalization.</param>
/// <param name="Expected">Normalized expected value (null when absent or unparseable).</param>
/// <param name="Actual">Normalized actual value (null when absent or unparseable).</param>
/// <param name="Similarity">Text similarity for FreeText comparisons.</param>
/// <param name="Reason">Short machine-readable reason (equal, both-absent, missing-expected, missing-actual, unparseable, different, below-threshold).</param>
public sealed record ComparisonResult(bool Match, string? Expected, string? Actual, double? Similarity, string Reason);

/// <summary>
/// Typed comparator used by the evaluation harness: compares a gold value with an extracted value
/// after field-kind normalization. Rules are documented in docs/evaluation/comparator.md.
/// </summary>
public sealed partial class CanonicalComparer
{
    private static readonly CultureInfo Invariant = CreateCulture();

    /// <summary>A comma followed by one or two digits at the end, or a comma after the last point: a decimal comma.</summary>
    [GeneratedRegex(@"(,\d{1,2}-?$)|(\.\d*,)")]
    private static partial Regex DecimalComma();

    [GeneratedRegex(@"(?<!\d)\d{4}(?!\d)")]
    private static partial Regex FourDigitYear();

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy", "M/d/yy", "MM/dd/yy", "M-d-yyyy", "MM-dd-yyyy", "M-d-yy",
        "yyyyMMdd", "yyyy/MM/dd", "d-MMM-yyyy", "dd-MMM-yyyy", "d-MMM-yy", "dd-MMM-yy",
        "MMM d, yyyy", "MMMM d, yyyy", "MMM dd, yyyy", "MMMM dd, yyyy", "d MMM yyyy", "dd MMM yyyy",
    ];

    private readonly CodeSets _codeSets;
    private readonly Glossary _glossary;
    private readonly ComparerOptions _options;

    public CanonicalComparer(CodeSets codeSets, Glossary glossary, ComparerOptions? options = null)
    {
        _codeSets = codeSets;
        _glossary = glossary;
        _options = options ?? new ComparerOptions();
    }

    /// <summary>Comparer over the embedded code sets and glossary with default options.</summary>
    public static CanonicalComparer Default { get; } = new(CanonicalData.CodeSets, CanonicalData.Glossary);

    /// <summary>
    /// Compares two printed/canonical values of the same field.
    /// <paramref name="qualifier"/> is the code system for <see cref="FieldKind.Code"/> and the canonical field
    /// (e.g. <c>claim.status</c>) for <see cref="FieldKind.Enum"/>.
    /// Both absent is a match; one absent is a mismatch (an absent value is not zero).
    /// </summary>
    public ComparisonResult Compare(FieldKind kind, string? expected, string? actual, string? qualifier = null)
    {
        var expectedAbsent = string.IsNullOrWhiteSpace(expected);
        var actualAbsent = string.IsNullOrWhiteSpace(actual);
        if (expectedAbsent && actualAbsent)
        {
            return new(true, null, null, null, "both-absent");
        }

        if (expectedAbsent || actualAbsent)
        {
            return new(false, expectedAbsent ? null : Normalize(kind, expected, qualifier), actualAbsent ? null : Normalize(kind, actual, qualifier), null,
                expectedAbsent ? "missing-expected" : "missing-actual");
        }

        if (kind == FieldKind.FreeText)
        {
            var e = NormalizeText(expected);
            var a = NormalizeText(actual);
            var similarity = TextSimilarity(e, a);
            return new(similarity >= _options.FreeTextThreshold, e, a, similarity, similarity >= _options.FreeTextThreshold ? "equal" : "below-threshold");
        }

        if (kind == FieldKind.Currency)
        {
            var e = NormalizeCurrency(expected);
            var a = NormalizeCurrency(actual);
            if (e is null || a is null)
            {
                return new(false, Format(e), Format(a), null, "unparseable");
            }

            var match = Math.Abs(e.Value - a.Value) <= _options.CurrencyTolerance;
            return new(match, Format(e), Format(a), null, match ? "equal" : "different");
        }

        var ne = Normalize(kind, expected, qualifier);
        var na = Normalize(kind, actual, qualifier);
        if (ne is null || na is null)
        {
            return new(false, ne, na, null, "unparseable");
        }

        var equal = string.Equals(ne, na, StringComparison.Ordinal);
        return new(equal, ne, na, null, equal ? "equal" : "different");
    }

    /// <summary>
    /// Compares coded values (nature/cause/part of body). Codes win when both sides have one: the code systems must
    /// agree (a null system is treated as unknown and does not block) and the padded codes must be equal. When either
    /// side lacks a code, falls back to free-text comparison of the printed text.
    /// </summary>
    public ComparisonResult CompareCoded(CodedValue? expected, CodedValue? actual)
    {
        var eCode = expected?.Code;
        var aCode = actual?.Code;
        if (!string.IsNullOrWhiteSpace(eCode) && !string.IsNullOrWhiteSpace(aCode))
        {
            var eSystem = expected!.CodeSystem;
            var aSystem = actual!.CodeSystem;
            if (eSystem is not null && aSystem is not null && !string.Equals(eSystem, aSystem, StringComparison.OrdinalIgnoreCase))
            {
                return new(false, $"{eSystem}:{eCode}", $"{aSystem}:{aCode}", null, "different-code-system");
            }

            var system = eSystem ?? aSystem;
            var e = NormalizeCode(eCode, system);
            var a = NormalizeCode(aCode, system);
            var equal = e is not null && e == a;
            return new(equal, e, a, null, equal ? "equal" : "different");
        }

        return Compare(FieldKind.FreeText, expected?.Text, actual?.Text);
    }

    private string? Normalize(FieldKind kind, string? raw, string? qualifier) => kind switch
    {
        FieldKind.Date => NormalizeDate(raw)?.ToString("yyyy-MM-dd", Invariant),
        FieldKind.Currency => Format(NormalizeCurrency(raw)),
        FieldKind.Integer => NormalizeCurrency(raw) is { } n && n == decimal.Truncate(n) ? ((long)n).ToString(Invariant) : null,
        FieldKind.ClaimNumber => NormalizeClaimNumber(raw),
        FieldKind.Code => NormalizeCode(raw, qualifier),
        FieldKind.Enum => qualifier is null ? NormalizeText(raw) : _glossary.ResolveValue(qualifier, raw) ?? NormalizeText(raw),
        FieldKind.Boolean => NormalizeBoolean(raw)?.ToString(),
        FieldKind.ShortText or FieldKind.FreeText => NormalizeText(raw),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Pads a numeric code to its code system's width (code-sets.json <c>codeWidth</c>); trims and upper-cases otherwise.</summary>
    public string? NormalizeCode(string? raw, string? codeSystem)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var code = raw.Trim().ToUpperInvariant();
        if (code.EndsWith(".0", StringComparison.Ordinal))
        {
            code = code[..^2]; // spreadsheet numeric cell 52.0
        }

        var width = _codeSets.CodeWidth(codeSystem);
        return width is { } w && code.All(char.IsAsciiDigit) && code.Length < w ? code.PadLeft(w, '0') : code;
    }

    /// <summary>
    /// Parses a printed date into a calendar date: ISO, US M/D/Y (2- or 4-digit year; 2-digit years 00–49 → 20xx,
    /// 50–99 → 19xx), month-name forms, compact yyyyMMdd, Excel serial numbers, and date-times (time dropped).
    /// </summary>
    public static DateOnly? NormalizeDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var s = raw.Trim();
        if (DateTime.TryParseExact(s, DateFormats, Invariant, DateTimeStyles.AllowWhiteSpaces, out var exact))
        {
            return DateOnly.FromDateTime(exact);
        }

        if (double.TryParse(s, NumberStyles.Float, Invariant, out var serial) && serial is >= 20000 and <= 80000)
        {
            return DateOnly.FromDateTime(DateTime.FromOADate(Math.Floor(serial))); // Excel 1900 date system
        }

        // Brief 30 item 12: the general fallback (date-times, offsets) never takes the year from the clock and never converts
        // to the machine's time zone: a year must be printed, and the date is the one printed (offset kept, not applied).
        if (FourDigitYear().IsMatch(s)
            && DateTimeOffset.TryParse(s, Invariant, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var any))
        {
            return DateOnly.FromDateTime(any.DateTime);
        }

        return null;
    }

    /// <summary>
    /// Parses a printed amount: strips $, USD, commas and spaces; (1,234.00), trailing minus (1,234.00-) and a CR
    /// suffix are negative; a lone dash means zero, also with a currency sign around it (Excel's Accounting zero
    /// <c>$ -</c>, brief 30 item 5); en and em dashes are minus signs; blank is null (absent is not zero). A decimal comma
    /// (<c>1.234,56</c>, <c>1,5</c>) is refused rather than read as thousands. Unparseable returns null.
    /// </summary>
    public static decimal? NormalizeCurrency(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var s = raw.Trim();
        if (s is "-" or "–" or "—" or "--")
        {
            return 0m;
        }

        var negative = false;
        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            negative = true;
            s = s[1..^1];
        }

        if (s.EndsWith("CR", StringComparison.OrdinalIgnoreCase))
        {
            negative = true;
            s = s[..^2];
        }

        s = s.Replace("USD", "", StringComparison.OrdinalIgnoreCase).Replace("$", "").Replace(" ", "").Trim();
        if (s is "-" or "–" or "—" or "−" or "--")
        {
            return 0m;
        }

        s = s.Replace('–', '-').Replace('—', '-').Replace('−', '-');
        if (DecimalComma().IsMatch(s))
        {
            return null;
        }

        s = s.Replace(",", "");
        if (s.EndsWith('-'))
        {
            negative = true;
            s = s[..^1];
        }

        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            negative = true;
            s = s[1..^1];
        }

        if (!decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Invariant, out var value))
        {
            return null;
        }

        return negative ? -Math.Abs(value) : value;
    }

    /// <summary>Trims, upper-cases, and removes whitespace and the separators - _ . / \ #. Leading zeros are kept.</summary>
    public static string? NormalizeClaimNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = ClaimNumberSeparators().Replace(raw.ToUpperInvariant(), "");
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>Y/Yes/True/X/1 → true; N/No/False/0 → false; anything else null.</summary>
    public static bool? NormalizeBoolean(string? raw) => raw?.Trim().ToUpperInvariant() switch
    {
        "Y" or "YES" or "TRUE" or "T" or "X" or "1" => true,
        "N" or "NO" or "FALSE" or "F" or "0" => false,
        _ => null,
    };

    /// <summary>Lower-cases, folds punctuation to spaces and collapses whitespace.</summary>
    public static string NormalizeText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }

        return Whitespace().Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>1 − Levenshtein(a, b) / max(|a|, |b|); 1.0 for two empty strings.</summary>
    public static double TextSimilarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
        {
            return 1.0;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return 1.0 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }

    private static string? Format(decimal? value) => value?.ToString("0.00", Invariant);

    private static CultureInfo CreateCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.Calendar.TwoDigitYearMax = 2049;
        return CultureInfo.ReadOnly(culture);
    }

    [GeneratedRegex(@"[\s\-_./\\#]+")]
    private static partial Regex ClaimNumberSeparators();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
