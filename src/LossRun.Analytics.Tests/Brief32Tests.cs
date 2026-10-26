using LossRun.Contracts;

namespace LossRun.Analytics.Tests;

/// <summary>Brief 32 item 5: the as-reported revert never throws on a stored value, and an override path names an index the document has.</summary>
public class Brief32Tests
{
    private static LossRunDocument Document() => new()
    {
        DocumentId = "doc-1",
        PolicyPeriods = [new PolicyPeriod { Claims = [new Claim { ClaimNumber = "WC-1", Medical = new MoneyTriplet { Paid = 500m } }] }],
    };

    private static OverrideRecord Override(string path, string? old) => new("o-1", "doc-1", path, old, "1", "fix", "uw-1", DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_legacy_formatted_number_is_reverted_by_the_currency_parser()
    {
        // An override before brief 30 stored the old value as display text.
        var reverted = AccountBuilder.Revert(Document(), [Override("policyPeriods[0].claims[0].medical.paid", "1,234.00")]); // before brief 32: JsonException → 500
        Assert.Equal(1234.00m, reverted.PolicyPeriods[0].Claims[0].Medical!.Paid);
    }

    [Theory]
    [InlineData("policyPeriods[0].claims[0].medical.paid", "n/a")]
    [InlineData("policyPeriods[0].claims[0].natureOfInjury", "{\"code\":\"52\",\"codeSystem\":\"WCIO-Na")] // clipped at the column's 4000 characters
    [InlineData("policyPeriods[0].claims[0].litigationFlag", "maybe")]
    public void A_stored_value_that_is_not_of_the_field_s_type_is_flagged_not_thrown(string path, string old)
    {
        var unrevertable = new HashSet<string>(StringComparer.Ordinal);
        var reverted = AccountBuilder.Revert(Document(), [Override(path, old)], unrevertable);

        Assert.Equal([path], unrevertable);
        Assert.Equal(500m, reverted.PolicyPeriods[0].Claims[0].Medical!.Paid); // the current value is kept

        var input = AccountBuilder.Build("Q-1", [new AccountDocument("doc-1", Document(), [Override(path, old)])], AccountBuilder.AsReported);
        Assert.True(Assert.Single(input.Overrides).Unrevertable);
        Assert.Null(Assert.Single(AccountBuilder.Build("Q-1", [new AccountDocument("doc-1", Document(), [Override(path, old)])]).Overrides).Unrevertable); // as adjusted: nothing reverted
    }

    [Theory]
    [InlineData("policyPeriods[0].claims[0].medical.paid", null)]
    [InlineData("policyPeriods[0].claims[0].indemnity.paid", null)] // an absent object is created by the override
    [InlineData("policyPeriods[0].claims[99].medical.paid", "policyPeriods[0].claims[99].medical.paid: policyPeriods[0].claims[99] does not exist (the document has 1).")]
    [InlineData("policyPeriods[3].claims[0].status", "policyPeriods[3].claims[0].status: policyPeriods[3] does not exist (the document has 1).")]
    public void An_override_path_must_name_an_index_the_document_has(string path, string? expected) =>
        Assert.Equal(expected, OverrideTypes.IndexError(Document(), path)); // before brief 32: claims[99] got a 202 and failed in the activity
}
