using System.Text;
using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;

namespace LossRun.Orchestration.Tests;

/// <summary>Brief 30: the end-to-end audit's fixes, each with the test that failed before it.</summary>
public partial class EndToEndTests
{
    [Fact]
    public async Task Item1_an_applicant_attaches_uploads_only_to_quotes_they_created()
    {
        var rig = await RigAsync();
        var a = new Caller("applicant-a", Roles.Applicant);
        var b = new Caller("applicant-b", Roles.Applicant);
        var csv = Fixture.Bytes("lossrun-export.csv");

        Assert.Equal(202, (await rig.Api.CreateUploadAsync(b, [new UploadFile("lossrun-export.csv", csv)], "Q-B", null, "b-1",
            payrollByPeriod: [new PeriodExposure(new DateOnly(2023, 1, 1), 100m)])).Status);
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));

        // A names B's quote: refused before anything is stored or merged into B's context.
        var refused = await rig.Api.CreateUploadAsync(a, [new UploadFile("lossrun-export.csv", csv)], "Q-B", null, "a-1",
            payrollByPeriod: [new PeriodExposure(new DateOnly(2023, 1, 1), 999m)]);
        Assert.Equal(403, refused.Status);
        var context = await rig.Runtime.RecordStore.GetQuoteContextAsync("Q-B");
        Assert.Equal(100m, Assert.Single(context!.PayrollByPeriod!).Amount);

        // B again, and an underwriter, are accepted.
        Assert.Equal(202, (await rig.Api.CreateUploadAsync(b, [new UploadFile("lossrun-export.csv", csv)], "Q-B", null, "b-2")).Status);
        Assert.Equal(202, (await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile("lossrun-export.csv", csv)], "Q-B", null, "uw-1")).Status);

        // A quote an underwriter created is underwriter-only.
        Assert.Equal(202, (await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile("lossrun-export.csv", csv)], "Q-UW", null, "uw-2")).Status);
        Assert.Equal(403, (await rig.Api.CreateUploadAsync(a, [new UploadFile("lossrun-export.csv", csv)], "Q-UW", null, "a-2")).Status);
    }

    [Fact]
    public async Task Brief32_item4_a_quote_owner_can_be_cleared_and_a_quote_something_already_names_is_not_claimable()
    {
        var rig = await RigAsync();
        var applicant = new Caller("applicant-a", Roles.Applicant);
        var manager = new Caller("mgr-1", Roles.UWManager);
        var workbench = new WorkbenchApi(rig.Runtime);
        var csv = Fixture.Bytes("lossrun-export.csv");

        // The Applicant claims a new id with a real upload; an underwriter attaches to it.
        Assert.Equal(202, (await rig.Api.CreateUploadAsync(applicant, [new UploadFile("lossrun-export.csv", csv)], "Q-SQUAT", null, "a-1")).Status);
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(202, (await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile("lossrun-export.csv", csv)], "Q-SQUAT", null, "uw-1")).Status);

        // A UWManager clears the owner (before brief 32: no API; the Applicant kept attach rights for good).
        Assert.Equal(403, (await workbench.SetQuoteOwnerAsync(applicant, "Q-SQUAT", new QuoteOwnerRequest("mine"))).Status);
        Assert.Equal(400, (await workbench.SetQuoteOwnerAsync(manager, "Q-SQUAT", new QuoteOwnerRequest(" "))).Status);
        Assert.Equal(200, (await workbench.SetQuoteOwnerAsync(manager, "Q-SQUAT", new QuoteOwnerRequest("reserved id, not the applicant's submission"))).Status);
        Assert.Equal(403, (await rig.Api.CreateUploadAsync(applicant, [new UploadFile("lossrun-export.csv", csv)], "Q-SQUAT", null, "a-2")).Status);
        var audit = (await rig.Runtime.RecordStore.AuditTrailAsync(null, "Q-SQUAT", 50)).Entries;
        Assert.Contains(audit, e => e.Action == "quote.owner" && e.OldValue!.Contains("applicant-a", StringComparison.Ordinal) && e.NewValue!.Contains(QuoteAudit.UnderwritersOnly, StringComparison.Ordinal));

        // Transferred back, the Applicant attaches again.
        Assert.Equal(200, (await workbench.SetQuoteOwnerAsync(manager, "Q-SQUAT", new QuoteOwnerRequest("confirmed with the broker", "applicant-a"))).Status);
        Assert.Equal(202, (await rig.Api.CreateUploadAsync(applicant, [new UploadFile("lossrun-export.csv", csv)], "Q-SQUAT", null, "a-3")).Status);

        // A quote with no owner record, no documents and no context, but a label, is not new: before brief 32 the first Applicant claimed it.
        await rig.Runtime.RecordStore.SaveQuoteLabelAsync(new QuoteLabel("Q-OLD", "Old quote", null, null, DateTimeOffset.UnixEpoch, "mgr-1"), "name", new Actor("mgr-1", Roles.UWManager, "api"));
        Assert.Equal(403, (await rig.Api.CreateUploadAsync(applicant, [new UploadFile("lossrun-export.csv", csv)], "Q-OLD", null, "a-4")).Status);
        Assert.Null(await rig.Runtime.RecordStore.GetQuoteOwnerAsync("Q-OLD")); // the refused check wrote nothing
    }

    [Fact]
    public async Task Brief32_answers4_outside_the_poc_an_applicant_uploads_only_to_a_quote_that_exists()
    {
        var rig = await RigAsync(options: new ApiOptions(ApplicantMayCreateQuotes: false));
        var applicant = new Caller("applicant-a", Roles.Applicant);
        var csv = Fixture.Bytes("lossrun-export.csv");

        var unknown = await rig.Api.CreateUploadAsync(applicant, [new UploadFile("lossrun-export.csv", csv)], "Q-NEW", null, "a-1");
        Assert.Equal((404, "quote-not-found"), (unknown.Status, ((ApiError)unknown.Body!).Code)); // in the POC: 202, and the Applicant owns Q-NEW
        Assert.Null(await rig.Runtime.RecordStore.GetQuoteOwnerAsync("Q-NEW")); // nothing reserved

        // An underwriter creates it; once it is the Applicant's (transferred), the Applicant attaches.
        Assert.Equal(202, (await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile("lossrun-export.csv", csv)], "Q-NEW", null, "uw-1")).Status);
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(403, (await rig.Api.CreateUploadAsync(applicant, [new UploadFile("lossrun-export.csv", csv)], "Q-NEW", null, "a-2")).Status);
        Assert.Equal(200, (await new WorkbenchApi(rig.Runtime).SetQuoteOwnerAsync(new Caller("mgr-1", Roles.UWManager), "Q-NEW",
            new QuoteOwnerRequest("the applicant's submission", "applicant-a"))).Status);
        Assert.Equal(202, (await rig.Api.CreateUploadAsync(applicant, [new UploadFile("lossrun-export.csv", csv)], "Q-NEW", null, "a-3")).Status);
    }

    [Theory]
    [InlineData("https://acct.blob.core.windows.net/sqldbledgerdigests/db/digest.json", 400)]
    [InlineData("https://acct.blob.core.windows.net/uploads/../sqldbledgerdigests/db/digest.json", 400)]
    [InlineData("https://acct.blob.core.windows.net/uploads/%2e%2e/extractions/x.json", 400)]
    [InlineData("https://other.blob.core.windows.net/uploads/incoming/applicant-a/a.pdf", 400)]
    [InlineData("http://acct.blob.core.windows.net/uploads/incoming/applicant-a/a.pdf", 400)]
    [InlineData("/uploads/incoming/applicant-a/a.pdf", 400)]
    [InlineData("https://acct.blob.core.windows.net/extractions/results/d1.json", 400)]
    [InlineData("https://acct.blob.core.windows.net/uploads/incoming/applicant-b/b.pdf", 403)]
    [InlineData("https://acct.blob.core.windows.net/uploads/up-someone-else/b.pdf", 403)]
    [InlineData("https://acct.blob.core.windows.net/uploads/incoming/applicant-a/a.pdf", 200)]
    public async Task Item2_a_blob_reference_resolves_only_inside_the_uploads_container_and_the_callers_prefix(string url, int expected)
    {
        var rig = await RigAsync();
        var (path, error) = await rig.Api.ResolveBlobReferenceAsync(new Caller("applicant-a", Roles.Applicant), url, "acct.blob.core.windows.net");
        Assert.Equal(expected, error?.Status ?? 200);
        Assert.Equal(expected == 200, path is not null);
    }

    [Fact]
    public async Task Item2_an_applicant_reads_their_own_upload_and_an_underwriter_any_upload_blob()
    {
        var rig = await RigAsync();
        var a = new Caller("applicant-a", Roles.Applicant);
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(a, [new UploadFile("lossrun-export.csv", Fixture.Bytes("lossrun-export.csv"))], null, null, "own")).Body!;
        var url = $"https://acct.blob.core.windows.net/uploads/{accepted.UploadId}/lossrun-export.csv";
        Assert.Equal($"{accepted.UploadId}/lossrun-export.csv", (await rig.Api.ResolveBlobReferenceAsync(a, url, "acct.blob.core.windows.net")).Path);
        Assert.Equal(403, (await rig.Api.ResolveBlobReferenceAsync(new Caller("applicant-b", Roles.Applicant), url, "acct.blob.core.windows.net")).Error?.Status);
        Assert.NotNull((await rig.Api.ResolveBlobReferenceAsync(Underwriter, url, "acct.blob.core.windows.net")).Path);
    }

    private static FieldOverride Typed(string path, string json) => new(path, JsonDocument.Parse(json).RootElement, "typed override");

    [Fact]
    public async Task Item3_a_wrongly_typed_override_is_a_400_naming_the_field_before_any_acknowledgement()
    {
        var rig = await RigAsync();
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile("lossrun-export.csv", Fixture.Bytes("lossrun-export.csv"))], "Q-T3", null, "t3")).Body!;
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));
        var doc = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items).DocumentId;
        var commands = new DocumentCommands(rig.Runtime);
        foreach (var (path, json) in new[]
        {
            ("policyPeriods[0].claims[0].medical.paid", "\"$1,200.00\""),
            ("policyPeriods[0].claims[0].recoverySubrogation", "\"500\""),
            ("policyPeriods[0].claims[0].litigationFlag", "\"yes\""),
            ("policyPeriods[0].claims[0].dateOfInjury", "\"03/01/2024\""),
            ("policyPeriods[0].claims[0].natureOfInjury", "\"STRAIN\""),
            ("policyPeriods[0].claims[0].claimNumber", "12345"),
            ("policyPeriods[0].claims[0].noSuchField", "1"),
        })
        {
            var refused = await commands.OverrideAsync(Underwriter, doc, new OverrideRequest([Typed(path, json)], "typed"));
            Assert.Equal(400, refused.Status);
            Assert.Contains(path, ((ApiError)refused.Body!).Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Item3_each_type_round_trips_and_a_digit_only_claim_number_keeps_the_payload_working()
    {
        var rig = await RigAsync();
        // The carrier prints a digit-only claim number: the as-reported value the revert restores is "220001".
        var csv = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Fixture.Bytes("lossrun-export.csv")).Replace("WC-22-0001", "220001", StringComparison.Ordinal));
        var accepted = (UploadAccepted)(await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile("lossrun-export.csv", csv)], "Q-T3B", null, "t3b")).Body!;
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));
        var doc = Assert.Single((await ManifestAsync(rig, accepted.UploadId)).Items).DocumentId;
        var commands = new DocumentCommands(rig.Runtime);
        var applied = await commands.OverrideAsync(Underwriter, doc, new OverrideRequest(
        [
            Typed("policyPeriods[0].claims[0].claimNumber", "\"0012345\""),
            Typed("policyPeriods[0].claims[0].medical.paid", "1200.5"),
            Typed("policyPeriods[0].claims[0].recoverySubrogation", "250"),
            Typed("policyPeriods[0].claims[0].litigationFlag", "true"),
            Typed("policyPeriods[0].claims[0].dateOfInjury", "\"2024-03-01\""),
            Typed("policyPeriods[0].claims[0].natureOfInjury", "{\"code\":\"52\",\"codeSystem\":\"WCIO\"}"),
            Typed("policyPeriods[0].claims[0].partOfBody.code", "{\"code\":\"42\",\"codeSystem\":\"WCIO\"}"), // the workbench's form
        ], "typed corrections"));
        Assert.Equal(200, applied.Status);
        var claim = (await rig.Runtime.RecordStore.GetResultAsync(doc))!.Document.PolicyPeriods[0].Claims[0];
        Assert.Equal(("0012345", 1200.5m, 250m, true, new DateOnly(2024, 3, 1), "52", "WCIO"),
            (claim.ClaimNumber, claim.Medical!.Paid, claim.RecoverySubrogation, claim.LitigationFlag, claim.DateOfInjury, claim.NatureOfInjury!.Code, claim.NatureOfInjury.CodeSystem));
        Assert.Equal(("42", "WCIO"), (claim.PartOfBody!.Code, claim.PartOfBody.CodeSystem));

        // The as-reported revert puts the digit-only string back as a string (it used to coerce it to a number and 500).
        var payload = await new QuoteAnalytics(rig.Runtime).QuotePayloadAsync(Underwriter, "Q-T3B");
        Assert.Equal(200, payload.Status);
        Assert.Contains("\"0012345\"", Encoding.UTF8.GetString(payload.Bytes!), StringComparison.Ordinal);
        Assert.Equal(200, (await new QuoteAnalytics(rig.Runtime).FeaturesAsync(Underwriter, "Q-T3B", "reported")).Status);
    }
}

public class ErrorResponseTests
{
    [Fact]
    public void Item27_an_unhandled_exception_answers_a_generic_500_with_the_correlation_id_and_not_its_text()
    {
        var result = ErrorResponses.For(new InvalidOperationException("Cannot open database \"lossrun\" requested by the login; claimant Jordan Example"), "inv-123");
        var error = Assert.IsType<ApiError>(result.Body);
        Assert.Equal((500, "internal"), (result.Status, error.Code));
        Assert.Contains("inv-123", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Jordan", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Item28_a_body_that_is_not_json_is_a_400_even_when_wrapped()
    {
        var wrapped = new AggregateException(new JsonException("'x' is an invalid start of a value. Path: $ | LineNumber: 0"));
        Assert.Equal((400, "bad-json"), (ErrorResponses.For(wrapped, "inv-9").Status, ((ApiError)ErrorResponses.For(wrapped, "inv-9").Body!).Code));
    }

}

public partial class EndToEndTests
{
    [Fact]
    public async Task Item28_a_rationale_over_the_stored_length_is_refused_not_clipped()
    {
        var rig = await RigAsync();
        var refused = await rig.Api.DecideAsync(new Caller("uw-1", Roles.Underwriter), "doc-x", new ReviewDecisionRequest(ReviewActions.Accept, Rationale: new string('x', 2001)));
        Assert.Equal((400, "too-long"), (refused.Status, ((ApiError)refused.Body!).Code));
    }

    [Fact]
    public async Task Item29_formats_and_learned_aliases_are_not_open_to_applicants_or_the_quote_system()
    {
        var rig = await RigAsync();
        foreach (var caller in new[] { new Caller("a-1", Roles.Applicant), new Caller("qs", Roles.QuoteSystem) })
        {
            Assert.Equal(403, (await rig.Api.ListFormatsAsync(caller)).Status); // before brief 30: 200 for any signed-in caller
            Assert.Equal(403, (await rig.Api.GetFormatAsync(caller, "synthetic-mutual-csv", null)).Status);
            Assert.Equal(403, (await rig.Api.ListAliasesAsync(caller)).Status);
        }

        Assert.Equal(200, (await rig.Api.ListFormatsAsync(new Caller("eng", Roles.Engineer))).Status);
        Assert.Equal(200, (await rig.Api.GetFormatAsync(Underwriter, "synthetic-mutual-csv", null)).Status);
    }

    [Fact]
    public async Task Item30_a_captured_webhook_delivery_cannot_be_replayed_and_an_old_one_is_refused()
    {
        var rig = await RigAsync();
        await rig.Api.CreateUploadAsync(Underwriter, [new UploadFile("lossrun-export.csv", Fixture.Bytes("lossrun-export.csv"))], null, null, "i30");
        await rig.Orchestrations.Running!.WaitAsync(TimeSpan.FromSeconds(30));
        var hook = Assert.Single(rig.Webhooks.Sent);

        Assert.Equal(200, (await rig.Api.ReceiveWebhookAsync(hook.Body, hook.Signature, "hook-secret")).Status);
        Assert.Equal(409, (await rig.Api.ReceiveWebhookAsync(hook.Body, hook.Signature, "hook-secret")).Status); // before brief 30: 200 again

        var old = JsonSerializer.SerializeToUtf8Bytes(new WebhookEnvelope("upload.completed", "up-x", DateTimeOffset.UtcNow, "completed", 1, 0, 0,
            DateTimeOffset.UtcNow.AddMinutes(-10), "nonce-old"), CanonicalJson.Options);
        var stale = await rig.Api.ReceiveWebhookAsync(old, HmacWebhookSender.Sign("hook-secret", old), "hook-secret");
        Assert.Equal((401, "stale"), (stale.Status, ((ApiError)stale.Body!).Code));
    }
}
