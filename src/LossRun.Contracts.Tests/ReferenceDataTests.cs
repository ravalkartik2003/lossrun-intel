using LossRun.Contracts;

namespace LossRun.Contracts.Tests;

public class ReferenceDataTests
{
    private static readonly Glossary Glossary = CanonicalData.Glossary;
    private static readonly CodeSets CodeSets = CanonicalData.CodeSets;

    [Fact]
    public void Glossary_aliases_cite_a_known_source_and_a_valid_confidence()
    {
        var carriers = Glossary.CarrierProvenance.Select(c => c.CarrierId).ToHashSet();
        var entries = Glossary.Fields.SelectMany(f => f.Value.Aliases.Select(a => (f.Key, a.Header, a.Source, a.Confidence)))
            .Concat(Glossary.Fields.Where(f => f.Value.ValueAliases is not null)
                .SelectMany(f => f.Value.ValueAliases!.SelectMany(v => v.Value.Select(a => (f.Key, a.Value, a.Source, a.Confidence)))))
            .Concat(Glossary.CompositeHeaders.Components.Concat(Glossary.CompositeHeaders.Measures)
                .SelectMany(c => c.Value.Select(w => (c.Key, w.Word, w.Source, w.Confidence))));

        foreach (var (field, text, source, confidence) in entries)
        {
            Assert.True(Glossary.Sources.ContainsKey(source) || carriers.Contains(source), $"{field} '{text}': unknown source '{source}'");
            Assert.InRange(confidence, 0.0, 1.0);
        }
    }

    [Fact]
    public void Conditional_words_and_ignored_headers_cite_a_known_source()
    {
        Assert.All(Glossary.CompositeHeaders.ConditionalComponents.Words.Select(w => w.Source).Concat(Glossary.IgnoredHeaders.Headers.Select(h => h.Source)),
            source => Assert.True(Glossary.Sources.ContainsKey(source), $"unknown source '{source}'"));
        Assert.All(Glossary.IgnoredHeaders.Headers, h => Assert.Null(Glossary.ResolveHeader(h.Header)));
    }

    [Fact]
    public void Loss_is_indemnity_only_beside_a_separate_medical_column()
    {
        // Brief 17 Answers 2: "Loss" is conditional, not a fixed alias.
        var withMedical = Glossary.ResolveTableHeaders(["Claim Number", "Loss Paid", "Medical Paid", "Expense Paid"]);
        Assert.Equal("claim.indemnity.paid", withMedical.Matches[1]!.Field);
        Assert.Empty(withMedical.Ambiguous);

        var withoutMedical = Glossary.ResolveTableHeaders(["Claim Number", "Loss Paid", "Loss Reserve", "Expense Paid"]);
        Assert.Null(withoutMedical.Matches[1]);
        Assert.Null(withoutMedical.Matches[2]);
        Assert.Equal([1, 2], withoutMedical.Ambiguous);
        Assert.Equal("claim.expense.paid", withoutMedical.Matches[3]!.Field);
        Assert.Equal("claim.claimNumber", withoutMedical.Matches[0]!.Field);
    }

    [Theory]
    [InlineData("Paid CLM + MED + EXP", "claim.total.paid", "indemnity,medical,expense")]
    [InlineData("Incurred CLM+MED+EXP", "claim.total.incurred", "indemnity,medical,expense")]
    [InlineData("Total Incurred (Ind+Med)", "claim.total.incurred", "indemnity,medical")]
    [InlineData("Reserve (Ind & Med)", "claim.total.reserve", "indemnity,medical")]
    public void A_header_naming_its_components_is_their_total(string header, string field, string components)
    {
        // ADR 0018: "Incurred CLM + MED + EXP" resolved to medical incurred before (brief 18, the Travelers manual).
        var match = Glossary.ResolveHeader(header)!;

        Assert.Equal(field, match.Field);
        Assert.Equal("component-sum", match.Method);
        Assert.Equal(components.Split(','), match.Components);
    }

    [Fact]
    public void A_plus_sign_without_named_components_is_not_a_component_sum()
    {
        Assert.Equal("claim.total.incurred", Glossary.ResolveHeader("Paid + Reserve")!.Field); // an exact alias
        Assert.NotEqual("component-sum", Glossary.ResolveHeader("Med Paid + Fees")?.Method);
    }

    [Fact]
    public void Every_canonical_field_has_at_least_one_alias()
    {
        Assert.All(Glossary.Fields, f => Assert.NotEmpty(f.Value.Aliases));
    }

    [Theory]
    [InlineData("claim.status", "Re-opened", "reopened")]
    [InlineData("claim.status", "closed in subrogation", "closed")]
    [InlineData("claim.status", "O", "open")]
    [InlineData("claim.claimType", "WCMO", "medicalOnly")]
    [InlineData("claim.claimType", "Lost Time", "indemnity")]
    [InlineData("claim.litigationFlag", "Open in litigation", "true")]
    [InlineData("document.lineOfBusiness", "Workers' Comp", "WC")]
    [InlineData("claim.status", "Pending", null)]
    public void Value_aliases_resolve_to_canonical_values(string field, string printed, string? expected)
    {
        Assert.Equal(expected, Glossary.ResolveValue(field, printed));
    }

    [Fact]
    public void The_reference_carriers_are_registered_for_learned_mappings()
    {
        Assert.Equal(["amtrust", "berkshire-hathaway-direct", "pie"], Glossary.CarrierProvenance.Select(c => c.CarrierId).Order());
    }

    [Fact]
    public void Code_tables_are_well_formed()
    {
        string[] completeness = ["complete", "partial", "format-only"];
        foreach (var (key, table) in CodeSets.Tables)
        {
            Assert.Equal(key, table.System);
            Assert.InRange(table.CodeWidth, 1, 4);
            Assert.Contains(table.Completeness, completeness);
            Assert.False(string.IsNullOrWhiteSpace(table.Source));
            Assert.All(table.Codes, c => Assert.Equal(table.CodeWidth, c.Code.Length));
            Assert.Equal(table.Codes.Count, table.Codes.Select(c => c.Code).Distinct().Count());
            if (table.Completeness != "complete")
            {
                Assert.False(string.IsNullOrWhiteSpace(table.CompletenessNote), $"{key}: incomplete tables must say what is missing");
            }
        }
    }

    [Fact]
    public void Wcio_injury_tables_are_complete_with_provenance()
    {
        Assert.Equal("0.2.0", CodeSets.CodeSetsVersion);
        foreach (var (name, count) in new[] { ("WCIO-Nature", 56), ("WCIO-Cause", 77), ("WCIO-PartOfBody", 56) })
        {
            var table = CodeSets.Tables[name];
            Assert.Equal("complete", table.Completeness);
            Assert.StartsWith("https://www.dir.ca.gov/dwc/WCIS/", table.SourceUrl);
            Assert.Equal(new DateOnly(2026, 10, 21), table.RetrievedOn);
            Assert.Equal(count, table.Codes.Count);
            Assert.All(table.Codes, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
            Assert.All(table.Codes, c => Assert.Contains(table.Groups!, g => g.Name == c.Group && g.Codes!.Contains(c.Code)));
        }
    }

    [Theory]
    [InlineData("WCIO-Nature", "52", "Strain or Tear")]
    [InlineData("WCIO-Nature", "83", "COVID-19")]
    [InlineData("WCIO-Cause", "56", "Lifting")]
    [InlineData("WCIO-Cause", "97", "Repetitive Motion")]
    [InlineData("WCIO-PartOfBody", "42", "Lower Back Area")]
    public void Wcio_spot_checks_match_the_ca_dwc_tables(string table, string code, string description)
    {
        Assert.Equal(description, CodeSets.Tables[table].Codes.Single(c => c.Code == code).Description);
    }

    [Theory]
    [InlineData("06", "medicalOnly")]
    [InlineData("07", "medicalOnly")]
    [InlineData("05", "indemnity")]
    [InlineData("01", "indemnity")]
    public void Injury_type_codes_map_to_claim_type(string code, string claimType)
    {
        Assert.Equal(claimType, CodeSets.Tables["statPlan.injuryType"].Codes.Single(c => c.Code == code).ClaimType);
    }

    [Fact]
    public void Cumulative_trauma_markers_are_present()
    {
        Assert.True(CodeSets.Tables["statPlan.typeOfLoss"].Codes.Where(c => c.CumulativeTrauma == true).Select(c => c.Code).SequenceEqual(["02", "03"]));
        Assert.Contains(CodeSets.Tables["WCIO-Cause"].Codes, c => c.Code == "97" && c.CumulativeTrauma == true);
    }
}
