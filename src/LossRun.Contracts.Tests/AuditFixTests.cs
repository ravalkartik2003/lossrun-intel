using System.Globalization;

namespace LossRun.Contracts.Tests;

/// <summary>Brief 30: the audit's fixes in the comparator's normalizers, each with the test that failed before it.</summary>
public class AuditFixTests
{
    [Theory]
    [InlineData("$ -")]
    [InlineData("$-")]
    [InlineData("$ –")]
    [InlineData("( $ - )")]
    [InlineData(" $   —  ")]
    public void Item5_the_accounting_format_zero_reads_as_zero(string raw) => Assert.Equal(0m, CanonicalComparer.NormalizeCurrency(raw));

    [Theory]
    [InlineData("1.234,56")]
    [InlineData("1,5")]
    [InlineData("12,50")]
    [InlineData("1.234.567,00")]
    public void Item5_european_decimal_commas_are_refused_not_misread(string raw) => Assert.Null(CanonicalComparer.NormalizeCurrency(raw));

    [Theory]
    [InlineData("1,234.56", "1234.56")]
    [InlineData("–1,234", "-1234")]
    [InlineData("—1,234.00", "-1234.00")]
    [InlineData("1,234", "1234")]
    [InlineData("1,234,567", "1234567")]
    [InlineData("$12,000.00", "12000.00")]
    public void Item5_us_amounts_and_dash_signs_still_parse(string raw, string expected) =>
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), CanonicalComparer.NormalizeCurrency(raw));

    [Theory]
    [InlineData("11/03")]
    [InlineData("Nov 3")]
    [InlineData("3 November")]
    public void Item12_a_date_without_a_year_is_not_given_the_current_one(string raw) => Assert.Null(CanonicalComparer.NormalizeDate(raw));

    [Theory]
    [InlineData("2024-03-01T23:30:00Z", 2024, 3, 1)]
    [InlineData("2024-03-01T00:30:00+05:00", 2024, 3, 1)]
    [InlineData("2024-03-01 23:30:00", 2024, 3, 1)]
    [InlineData("3/1/2024 11:30 PM", 2024, 3, 1)]
    public void Item12_a_timestamp_keeps_its_printed_date_in_any_time_zone(string raw, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), CanonicalComparer.NormalizeDate(raw));

    [Fact]
    public void Item43_label_only_meanings_are_conditional_and_the_unverified_status_is_a_candidate()
    {
        var g = CanonicalData.Glossary;
        Assert.Equal("claim.dateReported", g.ResolveHeader("Report Date")!.Field); // a column "Report Date" is the claim's date reported
        Assert.Null(g.ResolveHeader("Run Date")); // a header-block label (valuation or run date), never a claims column
        Assert.True(g.IsLabel("Run Date"));
        Assert.Equal("period.subtotals.openCount", g.ResolveHeader("Open")!.Field); // a summary sheet's column; the grand-total row label is document-level
        Assert.Null(g.ResolveValue("claim.status", "I")); // before brief 30: "open", from an unverified meaning
        Assert.Equal("open", g.ResolveValue("claim.status", "0")); // the stat-plan code (code-sets.json statPlan.status) stays
    }
}
