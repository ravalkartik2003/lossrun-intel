using LossRun.Contracts;

namespace LossRun.Contracts.Tests;

public class CanonicalComparerTests
{
    private static readonly CanonicalComparer Comparer = CanonicalComparer.Default;

    [Theory]
    [InlineData("2024-09-30", "2024-09-30")]
    [InlineData("9/30/2024", "2024-09-30")]
    [InlineData("09/30/2024", "2024-09-30")]
    [InlineData("9/30/24", "2024-09-30")]
    [InlineData("1/5/49", "2049-01-05")]
    [InlineData("1/5/50", "1950-01-05")]
    [InlineData("30-Sep-2024", "2024-09-30")]
    [InlineData("Sep 30, 2024", "2024-09-30")]
    [InlineData("September 30, 2024", "2024-09-30")]
    [InlineData("20240930", "2024-09-30")]
    [InlineData("45565", "2024-09-30")] // Excel serial
    [InlineData("09/30/2024 00:00:00", "2024-09-30")]
    public void Dates_normalize_to_iso(string raw, string iso)
    {
        Assert.Equal(DateOnly.Parse(iso), CanonicalComparer.NormalizeDate(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("n/a")]
    [InlineData("13/45/2024")]
    public void Unparseable_dates_are_null(string raw)
    {
        Assert.Null(CanonicalComparer.NormalizeDate(raw));
    }

    [Theory]
    [InlineData("$12,000.00", "12000.00")]
    [InlineData("12000", "12000")]
    [InlineData("(1,234.56)", "-1234.56")]
    [InlineData("$(1,234.56)", "-1234.56")]
    [InlineData("1,234.56-", "-1234.56")]
    [InlineData("1,234.56 CR", "-1234.56")]
    [InlineData("-1234.56", "-1234.56")]
    [InlineData("-", "0")]
    [InlineData("—", "0")]
    [InlineData("USD 500", "500")]
    public void Currency_normalizes_to_decimal(string raw, string expected)
    {
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), CanonicalComparer.NormalizeCurrency(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("twelve")]
    public void Blank_or_unparseable_currency_is_null_not_zero(string raw)
    {
        Assert.Null(CanonicalComparer.NormalizeCurrency(raw));
    }

    [Theory]
    [InlineData("WC-22-0001", "wc 22 0001", true)]
    [InlineData("WC/22/0001", "WC220001", true)]
    [InlineData("#00123", "00123", true)]
    [InlineData("00123", "123", false)] // leading zeros are significant
    [InlineData("WC-22-0001", "WC-22-0002", false)]
    public void Claim_numbers_compare_after_normalization(string expected, string actual, bool match)
    {
        Assert.Equal(match, Comparer.Compare(FieldKind.ClaimNumber, expected, actual).Match);
    }

    [Theory]
    [InlineData("WCIO-Nature", "52", "52", true)]
    [InlineData("WCIO-Nature", "4", "04", true)]
    [InlineData("WCIO-Nature", "52.0", "52", true)]
    [InlineData("WCIRB-ClassCode", "38", "0038", true)]
    [InlineData("WCIRB-ClassCode", "8810", "8742", false)]
    public void Codes_compare_after_padding(string system, string expected, string actual, bool match)
    {
        Assert.Equal(match, Comparer.Compare(FieldKind.Code, expected, actual, system).Match);
    }

    [Fact]
    public void Coded_values_require_matching_code_systems()
    {
        var nature = new CodedValue { Code = "52", CodeSystem = "WCIO-Nature" };
        var cause = new CodedValue { Code = "52", CodeSystem = "WCIO-Cause" };

        Assert.False(Comparer.CompareCoded(nature, cause).Match);
        Assert.True(Comparer.CompareCoded(nature, new CodedValue { Code = "52" }).Match);
    }

    [Fact]
    public void Coded_values_fall_back_to_text_when_a_code_is_missing()
    {
        var expected = new CodedValue { Code = "52", Text = "Strain or Tear", CodeSystem = "WCIO-Nature" };

        Assert.True(Comparer.CompareCoded(expected, new CodedValue { Text = "strain or tear" }).Match);
        Assert.False(Comparer.CompareCoded(expected, new CodedValue { Text = "Laceration" }).Match);
    }

    [Theory]
    [InlineData("claim.status", "Re-opened", "reopened", true)]
    [InlineData("claim.status", "C", "Closed", true)]
    [InlineData("claim.status", "O", "C", false)]
    [InlineData("claim.claimType", "WCMO", "Medical Only", true)]
    public void Enums_compare_after_glossary_mapping(string field, string expected, string actual, bool match)
    {
        Assert.Equal(match, Comparer.Compare(FieldKind.Enum, expected, actual, field).Match);
    }

    [Theory]
    [InlineData("Employee strained lower back lifting boxes onto pallet", "Employee strained lower back lifting boxes onto a pallet", true)]
    [InlineData("Employee strained lower back lifting boxes", "Employee cut finger on box cutter", false)]
    [InlineData("SLIPPED ON WET FLOOR, FELL", "slipped on wet floor fell", true)]
    public void Free_text_uses_the_fuzzy_threshold(string expected, string actual, bool match)
    {
        var result = Comparer.Compare(FieldKind.FreeText, expected, actual);

        Assert.Equal(match, result.Match);
        Assert.NotNull(result.Similarity);
    }

    [Fact]
    public void Short_text_requires_exact_normalized_equality()
    {
        Assert.True(Comparer.Compare(FieldKind.ShortText, "Acme  Widgets, Inc.", "acme widgets inc").Match);
        Assert.False(Comparer.Compare(FieldKind.ShortText, "Acme Widgets", "Acme Widget").Match);
    }

    [Theory]
    [InlineData("$1,234.50", "1234.5", true)]
    [InlineData("(250.00)", "-250", true)]
    [InlineData("1,234.50", "1,234.51", false)]
    public void Currency_compares_to_the_cent(string expected, string actual, bool match)
    {
        Assert.Equal(match, Comparer.Compare(FieldKind.Currency, expected, actual).Match);
    }

    [Theory]
    [InlineData("09/30/2024", "2024-09-30", true)]
    [InlineData("9/30/24", "2024-09-29", false)]
    public void Dates_compare_after_normalization(string expected, string actual, bool match)
    {
        Assert.Equal(match, Comparer.Compare(FieldKind.Date, expected, actual).Match);
    }

    [Theory]
    [InlineData("Y", "Yes", true)]
    [InlineData("X", "true", true)]
    [InlineData("N", "Y", false)]
    public void Booleans_compare_after_normalization(string expected, string actual, bool match)
    {
        Assert.Equal(match, Comparer.Compare(FieldKind.Boolean, expected, actual).Match);
    }

    [Fact]
    public void Absent_values_match_only_absent_values()
    {
        Assert.True(Comparer.Compare(FieldKind.Currency, null, " ").Match);
        var oneSided = Comparer.Compare(FieldKind.Currency, "0.00", null);
        Assert.False(oneSided.Match); // NA is not zero
        Assert.Equal("missing-actual", oneSided.Reason);
    }

    [Fact]
    public void Text_similarity_is_normalized_levenshtein()
    {
        Assert.Equal(1.0, CanonicalComparer.TextSimilarity("abc", "abc"));
        Assert.Equal(1.0 - 1.0 / 3, CanonicalComparer.TextSimilarity("abc", "abd"), 10);
        Assert.Equal(0.0, CanonicalComparer.TextSimilarity("abc", ""));
    }
}
