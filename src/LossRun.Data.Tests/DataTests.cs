using System.Diagnostics;
using LossRun.Api.Contracts;
using LossRun.DbDeploy;
using LossRun.Formats;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace LossRun.Data.Tests;

public class ChangelogTests
{
    [Fact]
    public void Every_changeset_parses_has_a_rollback_and_a_stable_checksum()
    {
        var changesets = Changelog.Load(SqlFixture.Master);

        Assert.True(changesets.Count >= 26);
        Assert.All(changesets, c => Assert.NotEmpty(c.Rollback));
        Assert.Equal(changesets.Select(c => c.Checksum), Changelog.Load(SqlFixture.Master).Select(c => c.Checksum));
        Assert.Equal("0001-schema-change-log", changesets[0].Id);
        Assert.All(changesets, c => Assert.DoesNotContain("CREATE SCHEMA", c.Body, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_changeset_without_rollback_is_rejected_and_parameters_are_substituted()
    {
        Assert.Throws<InvalidDataException>(() => Changelog.Parse("--liquibase formatted sql\n--changeset a:1\nSELECT 1;\n", "x.sql").ToList());

        var c = Changelog.Parse("--liquibase formatted sql\n--changeset a:1 runOnChange:true\nSELECT N'${who}';\n--rollback SELECT 1;\n", "x.sql").Single();

        Assert.True(c.RunOnChange);
        Assert.Equal("SELECT N'o''brien';", c.Render(new Dictionary<string, string> { ["who"] = "o'brien" }));
        Assert.Throws<InvalidDataException>(() => c.Render(new Dictionary<string, string>()));
    }

    [Fact]
    public void The_sqlcmd_script_guards_every_changeset_and_maps_parameters()
    {
        var script = Deployer.Script(Changelog.Load(SqlFixture.Master), new Dictionary<string, string> { ["appPrincipalName"] = "" });

        Assert.Contains(":setvar appPrincipalName \"\"", script, StringComparison.Ordinal);
        Assert.Contains("$(appPrincipalName)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("${", script, StringComparison.Ordinal);
        Assert.Equal(Changelog.Load(SqlFixture.Master).Count, script.Split("-- changeset ").Length - 1);
    }

    [Fact]
    public void Entra_connections_wait_long_enough_for_a_serverless_resume()
    {
        var rewritten = new SqlConnectionStringBuilder(SqlConnections.WithoutAuthentication(
            "Server=tcp:x.database.windows.net,1433;Database=d;Authentication=Active Directory Default;Encrypt=True;"));

        Assert.Equal(SqlAuthenticationMethod.NotSpecified, rewritten.Authentication);
        Assert.True(rewritten.ConnectTimeout >= 90);
        Assert.Contains(-2, SqlConnections.ResumeErrors);
        Assert.Contains(40613, SqlConnections.ResumeErrors);
    }

    [Fact]
    public void Claim_paths_resolve_to_positions_and_columns()
    {
        Assert.Equal((0, 2, "medical.paid"), JsonPath.ClaimPosition("policyPeriods[0].claims[2].medical.paid"));
        Assert.Null(JsonPath.ClaimPosition("policyPeriods[0].subtotals.claimCount"));
        Assert.Equal("MedicalPaid", ClaimColumns.Column("medical.paid"));
        Assert.Equal("TotalIncurred", ClaimColumns.Column("total.incurred"));
        Assert.Equal(Ids.Claim("d", 0, 1), Ids.Claim("d", 0, 1));
        Assert.NotEqual(Ids.Claim("d", 0, 1), Ids.Claim("d", 1, 0));
    }
}

[Collection("sql")]
public class DeployTests(SqlFixture sql, ITestOutputHelper output)
{
    [SqlFact]
    public async Task Deploying_twice_applies_nothing_the_second_time_and_validate_passes()
    {
        var connection = await sql.DatabaseAsync("deploy_twice", deploy: false);
        var changesets = Changelog.Load(SqlFixture.Master);
        var parameters = Changelog.Properties(SqlFixture.Master);
        var watch = Stopwatch.StartNew();

        var first = await new Deployer(connection, changesets, parameters, output.WriteLine).DeployAsync();
        output.WriteLine($"first deploy {watch.ElapsedMilliseconds} ms");
        var second = await new Deployer(connection, changesets, parameters, output.WriteLine).DeployAsync();
        var plan = await new Deployer(connection, changesets, parameters).PlanAsync();

        Assert.All(first, s => Assert.Equal("apply", s.Action));
        Assert.All(second.Where(s => !s.Changeset.RunAlways), s => Assert.Equal("skip", s.Action));
        Assert.DoesNotContain(plan, s => s.Action == "drift");
    }

    [SqlFact]
    public async Task A_changed_changeset_is_drift_unless_runOnChange()
    {
        var connection = await sql.DatabaseAsync("drift");
        var changesets = Changelog.Load(SqlFixture.Master).ToList();
        var table = changesets.First(c => c.Id == "0002-upload");
        changesets[changesets.IndexOf(table)] = table with { Body = table.Body + "\n-- edited", Checksum = "edited-table-checksum" };
        var view = changesets.First(c => c.Id == "0008-v-cost-by-path");
        changesets[changesets.IndexOf(view)] = view with { Checksum = "edited-view-checksum" };

        var plan = await new Deployer(connection, changesets, Changelog.Properties(SqlFixture.Master)).PlanAsync();

        Assert.Equal("drift", plan.Single(s => s.Changeset.Id == "0002-upload").Action);
        Assert.Equal("rerun", plan.Single(s => s.Changeset.Id == "0008-v-cost-by-path").Action);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new Deployer(connection, changesets, Changelog.Properties(SqlFixture.Master), _ => { }).DeployAsync());
    }

    [SqlFact]
    public async Task The_ef_model_matches_the_deployed_tables_and_ledger_tables_are_append_only()
    {
        var connection = await sql.DatabaseAsync("efmodel");
        await using var db = new LossRunDbContext(LossRunDbContext.Options(connection));

        // Every mapped column exists: a full select per entity fails on any mismatch.
        foreach (var query in new Func<Task>[]
                 {
                     () => db.Uploads.Include(u => u.Items).ToListAsync(), () => db.Documents.ToListAsync(), () => db.PolicyPeriods.ToListAsync(),
                     () => db.Claims.TemporalAll().ToListAsync(), () => db.ExtractedValues.ToListAsync(), () => db.Overrides.ToListAsync(), () => db.AuditEvents.ToListAsync(),
                     () => db.RuleOutcomes.ToListAsync(), () => db.ReviewItems.ToListAsync(), () => db.Formats.ToListAsync(), () => db.FormatVersions.ToListAsync(),
                     () => db.FormatFieldStats.ToListAsync(), () => db.UsageEvents.ToListAsync(), () => db.DocumentCosts.ToListAsync(), () => db.LedgerDigests.ToListAsync(),
                     () => db.GlossaryAliases.TemporalAll().ToListAsync(), () => db.GlossaryAliasEvidence.ToListAsync(),
                 })
        {
            await query();
        }

        var kinds = await db.Database.SqlQueryRaw<string>(
            "SELECT name + ':' + ledger_type_desc COLLATE DATABASE_DEFAULT AS Value FROM sys.tables WHERE ledger_type <> 0 AND is_dropped_ledger_table = 0 AND schema_id = SCHEMA_ID('dbo') ORDER BY name").ToListAsync();
        Assert.Equal(["AuditEvent:APPEND_ONLY_LEDGER_TABLE", "ChatTurn:APPEND_ONLY_LEDGER_TABLE", "ExtractedValue:APPEND_ONLY_LEDGER_TABLE",
            "GlossaryAliasEvidence:APPEND_ONLY_LEDGER_TABLE", "LedgerDigest:APPEND_ONLY_LEDGER_TABLE", "Override:APPEND_ONLY_LEDGER_TABLE", "UsageEvent:APPEND_ONLY_LEDGER_TABLE"], kinds);
        Assert.Equal("SYSTEM_VERSIONED_TEMPORAL_TABLE",
            await db.Database.SqlQueryRaw<string>("SELECT temporal_type_desc COLLATE DATABASE_DEFAULT AS Value FROM sys.tables WHERE name = 'Claim'").SingleAsync());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sys.schemas WHERE schema_id BETWEEN 5 AND 16383").ToListAsync());
    }
}

[Collection("sql")]
public class CommandTests(SqlFixture sql, ITestOutputHelper output)
{
    [SqlFact]
    public async Task Item6_the_same_decision_applied_twice_writes_one_override_one_outbox_set_and_one_revision_bump()
    {
        var connection = await sql.DatabaseAsync("idempotent");
        var store = SqlRecordStore.Create(connection);
        var manifest = Samples.Manifest("up-i6", "doc-i6");
        manifest.Submission = manifest.Submission with { QuoteId = "Q-SQL-I6" };
        await store.UpdateManifestAsync("up-i6", _ => manifest, Actor.System);
        var before = Samples.Result("up-i6", "doc-i6");
        await store.SaveResultAsync(before, Actor.System);
        await store.OpenReviewAsync(new ReviewItem { DocumentId = "doc-i6", UploadId = "up-i6", Kind = ReviewKinds.DocumentReview, State = "pending", OpenedAt = DateTimeOffset.UtcNow });
        var decision = Samples.Override("doc-i6", "uw-1", ("policyPeriods[0].claims[0].medical.paid", 250m, "per adjuster note")) with { DecisionId = "dec-sql-i6" };
        var (after, changes) = DocumentEdits.Apply(before, decision);
        IReadOnlyList<OutboxMessage> Outbox() =>
        [
            OutboxMessage.Event(OutboxKinds.ReviewDecided, "Q-SQL-I6", "documents/doc-i6", new { quoteRevision = QuoteRevisions.Token }, "uw-1", decision.At).KeyedBy(decision.Key),
            OutboxMessage.Event(OutboxKinds.QuoteRevised, "Q-SQL-I6", "quotes/Q-SQL-I6", new { quoteRevision = QuoteRevisions.Token }, "uw-1", decision.At).KeyedBy(decision.Key),
        ];
        var record = new DecisionRecord("up-i6", before, after, decision, changes, false, new QuoteRevisionBump("Q-SQL-I6", QuoteRevisions.ReviewDecided));

        Assert.Equal(DecisionApplyResult.Applied, await store.ApplyDecisionAsync(record, default, Outbox()));
        // The retried activity re-read the document (now one revision on) and recomputed: same decision id, so it is recognised.
        var (again, againChanges) = DocumentEdits.Apply(after, decision);
        Assert.Equal(DecisionApplyResult.AlreadyApplied, await store.ApplyDecisionAsync(record with { Before = after, After = again, Changes = againChanges }, default, Outbox()));

        await using var db = new LossRunDbContext(LossRunDbContext.Options(connection));
        Assert.Equal(1, await db.Overrides.CountAsync(o => o.DocumentId == "doc-i6"));
        Assert.Equal(2, await db.Outbox.CountAsync(o => o.QuoteId == "Q-SQL-I6"));
        Assert.Equal(1, await store.QuoteRevisionAsync("Q-SQL-I6"));
        Assert.Equal(2, (await store.GetResultAsync("doc-i6"))!.Revision);
    }

    [SqlFact]
    public async Task Brief32_item4_the_owner_is_transferred_and_cleared_with_an_audit_row_and_its_history_kept()
    {
        var connection = await sql.DatabaseAsync("quoteowner");
        var store = SqlRecordStore.Create(connection);
        var at = new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
        await store.ClaimQuoteOwnerAsync(new QuoteOwner("Q-SQL-OWN", "applicant-a", at, "applicant-a"));
        var manager = new Actor("mgr-1", Roles.UWManager, "api");

        await store.SetQuoteOwnerAsync(new QuoteOwner("Q-SQL-OWN", null, at.AddHours(1), "mgr-1"), "reserved id", manager);
        Assert.Null((await store.GetQuoteOwnerAsync("Q-SQL-OWN"))!.ApplicantUserId);
        Assert.Equal(("applicant-a", at), ((await store.GetQuoteOwnerAsync("Q-SQL-OWN"))!.CreatedBy, (await store.GetQuoteOwnerAsync("Q-SQL-OWN"))!.CreatedAt)); // who created it stays
        await store.SetQuoteOwnerAsync(new QuoteOwner("Q-SQL-NEW", "applicant-b", at, "mgr-1"), "assigned to the broker", manager); // no row yet: inserted

        Assert.Equal("applicant-b", (await store.GetQuoteOwnerAsync("Q-SQL-NEW"))!.ApplicantUserId);
        var audit = (await store.AuditTrailAsync(null, "Q-SQL-OWN", 20)).Entries;
        Assert.Contains(audit, e => e.Action == "quote.owner" && e.Rationale == "reserved id" && e.NewValue!.Contains(QuoteAudit.UnderwritersOnly, StringComparison.Ordinal));
        await using var db = new LossRunDbContext(LossRunDbContext.Options(connection));
        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM dbo.QuoteOwnerHistory WHERE QuoteId = 'Q-SQL-OWN'").SingleAsync()); // system-versioned
    }

    [SqlFact]
    public async Task Item15_a_direct_override_keeps_a_pending_confirm_format_review_open()
    {
        var connection = await sql.DatabaseAsync("keepopen");
        var store = SqlRecordStore.Create(connection);
        await store.UpdateManifestAsync("up-i15", _ => Samples.Manifest("up-i15", "doc-i15"), Actor.System);
        var before = Samples.Result("up-i15", "doc-i15");
        await store.SaveResultAsync(before, Actor.System);
        await store.OpenReviewAsync(new ReviewItem { DocumentId = "doc-i15", UploadId = "up-i15", Kind = ReviewKinds.ConfirmFormat, State = "pending", OpenedAt = DateTimeOffset.UtcNow });
        var decision = Samples.Override("doc-i15", "uw-1", ("policyPeriods[0].claims[0].medical.paid", 250m, "letter"));
        var (after, changes) = DocumentEdits.Apply(before, decision);

        Assert.Equal(DecisionApplyResult.Applied, await store.ApplyDecisionAsync(new DecisionRecord("up-i15", before, after, decision, changes, false, KeepReviewOpen: true)));

        Assert.Equal("pending", (await store.GetReviewAsync("doc-i15"))!.State);
        Assert.Equal(2, (await store.GetResultAsync("doc-i15"))!.Revision);
    }

    [SqlFact]
    public async Task Persist_override_and_proof_track_every_edit()
    {
        var connection = await sql.DatabaseAsync("commands");
        var store = SqlRecordStore.Create(connection);
        await store.UpdateManifestAsync("up-1", _ => Samples.Manifest("up-1", "doc-1"), new Actor("uw-1", Roles.Underwriter, "api"));
        var before = Samples.Result("up-1", "doc-1");
        var watch = Stopwatch.StartNew();
        await store.SaveResultAsync(before, Actor.System);
        await store.SaveResultAsync(before, Actor.System); // retried activity: no duplicate rows
        output.WriteLine($"persist 3 claims: {watch.ElapsedMilliseconds} ms");
        await store.OpenReviewAsync(new ReviewItem { DocumentId = "doc-1", UploadId = "up-1", Kind = ReviewKinds.DocumentReview, State = "pending", OpenedAt = DateTimeOffset.UtcNow });

        // Two decisions: two fields on claim 0, then one field on claim 0 again and one on claim 2.
        var first = Samples.Override("doc-1", "uw-1", ("policyPeriods[0].claims[0].medical.paid", 250m, "per adjuster note"), ("policyPeriods[0].claims[0].claimantName", "Synthetic Claimant A", "typo"));
        var (afterFirst, firstChanges) = DocumentEdits.Apply(before, first);
        await store.ApplyDecisionAsync(new DecisionRecord("up-1", before, afterFirst, first, firstChanges, false));
        var second = Samples.Override("doc-1", "uw-2", ("policyPeriods[0].claims[0].medical.paid", 260m, "second letter"), ("policyPeriods[0].claims[2].indemnity.reserve", 0m, "closed"));
        var (afterSecond, secondChanges) = DocumentEdits.Apply(afterFirst, second);
        Assert.Equal(DecisionApplyResult.Applied, await store.ApplyDecisionAsync(new DecisionRecord("up-1", afterFirst, afterSecond, second, secondChanges, false)));
        // Brief 30 item 7: a replayed stale decision is refused and says so (it used to be reported as applied).
        Assert.Equal(DecisionApplyResult.Conflict, await store.ApplyDecisionAsync(new DecisionRecord("up-1", afterFirst, afterSecond, second, secondChanges, false)));
        var review = (await store.GetReviewAsync("doc-1"))!;

        await using var db = new LossRunDbContext(LossRunDbContext.Options(connection));
        Assert.Equal("decided", review.State);
        Assert.Equal(3, (await store.GetResultAsync("doc-1"))!.Revision);
        Assert.Equal(4, await db.Overrides.CountAsync());
        Assert.Equal(1, await db.Overrides.CountAsync(o => o.SupersedesOverrideId != null)); // medical.paid twice
        Assert.Equal(3, await db.Claims.TemporalAll().CountAsync(c => c.Id == Ids.Claim("doc-1", 0, 0))); // original + 2 decisions
        Assert.Equal(2, await db.Claims.TemporalAll().CountAsync(c => c.Id == Ids.Claim("doc-1", 0, 2)));
        Assert.Equal(1, await db.Claims.TemporalAll().CountAsync(c => c.Id == Ids.Claim("doc-1", 0, 1)));
        Assert.Equal(260m, (await db.Claims.SingleAsync(c => c.Id == Ids.Claim("doc-1", 0, 0))).MedicalPaid);
        Assert.Equal(360m, (await db.Claims.SingleAsync(c => c.Id == Ids.Claim("doc-1", 0, 0))).MedicalIncurred); // re-derived
        Assert.Contains(await db.AuditEvents.Select(a => a.Action).ToListAsync(), a => a == "rederive");
        Assert.Equal(1, await db.Documents.CountAsync());
        Assert.Equal(3 * 10, await db.ExtractedValues.CountAsync()); // 8 printed + 2 derived per claim, revision 1 only

        var asReported = await db.Database.SqlQueryRaw<decimal>("SELECT MedicalPaid AS Value FROM dbo.vClaimAsReported WHERE ClaimId = {0}", Ids.Claim("doc-1", 0, 0)).SingleAsync();
        var adjusted = await db.Database.SqlQueryRaw<int>("SELECT OverrideCount AS Value FROM dbo.vClaimAsAdjusted WHERE ClaimId = {0}", Ids.Claim("doc-1", 0, 0)).SingleAsync();
        Assert.Equal(200m, asReported);
        Assert.Equal(3, adjusted);
        Assert.NotEmpty(await db.Database.SqlQueryRaw<string>("SELECT CommittedBy AS Value FROM dbo.vAuditTrail").ToListAsync());

        var proof = await AuditProof.RunAsync(connection);
        foreach (var check in proof.Checks)
        {
            output.WriteLine($"{(check.Pass ? "PASS" : "FAIL")} {check.Name}: {check.Detail}");
        }

        Assert.True(proof.Pass);
        Assert.Contains(proof.Checks, c => c.Name.StartsWith("b. UPDATE dbo.AuditEvent", StringComparison.Ordinal) && c.Pass);

        // Brief 30 item 35: every append-only ledger table the database has is tampered with, not a hard-coded four.
        var ledger = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sys.tables WHERE ledger_type_desc = N'APPEND_ONLY_LEDGER_TABLE' AND SCHEMA_NAME(schema_id) = N'dbo'").ToListAsync();
        Assert.True(ledger.Count >= 7, string.Join(", ", ledger));
        Assert.All(ledger, t => Assert.Contains(proof.Checks, c => c.Name == $"b. DELETE dbo.{t}" && c.Pass));
    }

    [SqlFact]
    public async Task Quote_documents_overrides_and_feature_snapshots_round_trip_and_a_decision_invalidates_the_snapshots()
    {
        var connection = await sql.DatabaseAsync("analytics");
        var store = SqlRecordStore.Create(connection);
        await store.UpdateManifestAsync("up-a", _ => Samples.Manifest("up-a", "doc-a"), new Actor("uw-1", Roles.Underwriter, "api"));
        var before = Samples.Result("up-a", "doc-a");
        await store.SaveResultAsync(before, Actor.System);
        Assert.Equal([new QuoteDocument("doc-a", 1, "pending")], await store.DocumentsOfQuoteAsync("Q-100")); // brief 30 item 16: with its review state

        var key = new FeatureSnapshotKey("Q-100", "rev-1", "1.0.0", "adjusted", "baseline");
        await store.SaveFeatureSnapshotAsync(new StoredFeatureSnapshot(key, new string('a', 64), 3, "{\"metrics\":[]}", DateTimeOffset.UtcNow));
        Assert.Equal("{\"metrics\":[]}", (await store.GetFeatureSnapshotAsync(key))!.Json);
        Assert.Null(await store.GetFeatureSnapshotAsync(key with { View = "reported" }));

        var decision = Samples.Override("doc-a", "uw-1", ("policyPeriods[0].claims[1].indemnity.reserve", 99m, "adjuster update"));
        var (after, changes) = DocumentEdits.Apply(before, decision);
        await store.ApplyDecisionAsync(new DecisionRecord("up-a", before, after, decision, changes, false));

        Assert.Null(await store.GetFeatureSnapshotAsync(key)); // invalidated in the decision's transaction
        var overrides = await store.OverridesOfDocumentAsync("doc-a");
        var o = Assert.Single(overrides);
        Assert.Equal(("policyPeriods[0].claims[1].indemnity.reserve", "99", "adjuster update"), (o.FieldPath, o.NewValue, o.Rationale));
        await using var db = new LossRunDbContext(LossRunDbContext.Options(connection));
        Assert.Equal("uw-1", (await db.FeatureSnapshots.SingleAsync()).InvalidatedBy);
    }

    [Fact]
    public async Task An_unaudited_claim_update_breaks_the_proof()
    {
        var connection = await sql.DatabaseAsync("tamper");
        var store = SqlRecordStore.Create(connection);
        await store.UpdateManifestAsync("up-2", _ => Samples.Manifest("up-2", "doc-2"), Actor.System);
        await store.SaveResultAsync(Samples.Result("up-2", "doc-2"), Actor.System);

        // A write that bypasses ClaimCommandService (Claim is temporal, not ledger: the engine allows it, the proof must catch it).
        await using (var sqlConnection = new SqlConnection(connection))
        {
            await sqlConnection.OpenAsync();
            await using var update = new SqlCommand("UPDATE dbo.Claim SET IndemnityPaid = IndemnityPaid + 1 WHERE ClaimIndex = 1", sqlConnection);
            await update.ExecuteNonQueryAsync();
        }

        var proof = await AuditProof.RunAsync(connection);

        Assert.False(proof.Pass);
        Assert.Contains(proof.Checks, c => c.Name.StartsWith("a.", StringComparison.Ordinal) && !c.Pass);
    }

    /// <summary>Round trips per decision (each ~41–54 ms between East US 2 and the Central US database): one, the stored procedure (brief 13).</summary>
    [SqlFact]
    public async Task A_decision_takes_a_bounded_number_of_round_trips()
    {
        var connection = await sql.DatabaseAsync("roundtrips");
        var counter = new CommandCounter();
        var options = new DbContextOptionsBuilder<LossRunDbContext>(LossRunDbContext.Options(connection)).AddInterceptors(counter).Options;
        LossRunDbContext Factory() => new(options);
        var store = new SqlRecordStore(Factory, new ClaimCommandService(Factory));
        await store.UpdateManifestAsync("up-rt", _ => Samples.Manifest("up-rt", "doc-rt"), Actor.System);
        var before = Samples.Result("up-rt", "doc-rt");
        await store.SaveResultAsync(before, Actor.System);

        var decision = Samples.Override("doc-rt", "uw-1", ("policyPeriods[0].claims[0].medical.reserve", 500m, "load test"));
        var (after, changes) = DocumentEdits.Apply(before, decision);
        counter.Reset();
        Assert.True(await store.TryApplyDecisionAsync(new DecisionRecord("up-rt", before, after, decision, changes, false)));
        foreach (var (text, n) in counter.ByText.OrderByDescending(kv => kv.Value))
        {
            output.WriteLine($"{n,3} × {text}");
        }

        output.WriteLine($"{counter.Total} commands");
        Assert.Equal(1, counter.Total); // brief 13: dbo.ApplyDecision, one round trip (was 51, then 7)

        // The analytics account loads in two queries, whatever the number of documents.
        var quoteId = (await store.GetManifestAsync("up-rt"))!.Submission.QuoteId!;
        counter.Reset();
        var account = Assert.Single(await store.QuoteResultsAsync(quoteId));
        Assert.Equal(2, counter.Total);
        Assert.Equal((2, 1), (account.Result.Revision, account.Overrides.Count));
    }

    private sealed class CommandCounter : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public Dictionary<string, int> ByText { get; } = [];
        public int Total => ByText.Values.Sum();

        public void Reset() => ByText.Clear();

        private void Count(System.Data.Common.DbCommand c)
        {
            var key = c.CommandText.Replace("\n", " ", StringComparison.Ordinal).Trim();
            key = key.Length > 140 ? key[..140] : key;
            lock (ByText)
            {
                ByText[key] = ByText.GetValueOrDefault(key) + 1;
            }
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Count(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object>> ScalarExecutingAsync(System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Count(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [SqlFact]
    public async Task The_audit_row_and_its_outbox_rows_commit_together_or_not_at_all()
    {
        var connection = await sql.DatabaseAsync("outbox");
        var store = SqlRecordStore.Create(connection);
        var at = DateTimeOffset.UtcNow;
        var command = OutboxMessage.Command(OutboxKinds.Flags, "Q-1", new { flags = Array.Empty<object>() }, "uw-1", at);
        AuditEvent Audit(string action) => new(at, "uw-1", Roles.Underwriter, action, "", null, "quotes/Q-1");

        await store.AuditWithOutboxAsync(Audit("quote.flags.pushed"), new Actor("uw-1", Roles.Underwriter, "api"), [command]);
        // Same outbox id again (primary key violation): the second audit row must roll back with it.
        await Assert.ThrowsAnyAsync<Exception>(() => store.AuditWithOutboxAsync(Audit("quote.flags.pushed.again"), new Actor("uw-1", Roles.Underwriter, "api"), [command]));

        await using var db = new LossRunDbContext(LossRunDbContext.Options(connection));
        Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.Action == "quote.flags.pushed"));
        Assert.Equal(0, await db.AuditEvents.CountAsync(a => a.Action == "quote.flags.pushed.again"));
        Assert.Single(await store.PendingOutboxAsync(at.AddMinutes(1), 10));

        await store.MarkOutboxPublishedAsync(command.Id, at.AddSeconds(1));
        await store.MarkOutboxPublishedAsync(command.Id, at.AddSeconds(2)); // second mark is a no-op (published once)
        await store.RecordOutboxDeliveryAsync(command.Id, "delivered", "{\"status\":201}", at.AddSeconds(3));
        var row = (await store.GetOutboxAsync(command.Id))!;
        Assert.Equal((1, "delivered"), (row.PublishAttempts, row.DeliveryStatus));
        Assert.Empty(await store.PendingOutboxAsync(at.AddMinutes(1), 10));
    }

    [SqlFact]
    public async Task An_escalation_is_versioned_refuses_a_stale_write_and_commits_with_its_audit_and_event()
    {
        var connection = await sql.DatabaseAsync("escalation");
        var store = SqlRecordStore.Create(connection);
        await store.UpdateManifestAsync("up-1", _ => Samples.Manifest("up-1", "doc-1"), Actor.System);
        await store.SaveResultAsync(Samples.Result("up-1", "doc-1"), Actor.System);
        var at = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var signal = new EscalationSignal(EscalationReasons.LearnExhausted, "fp:aaaaaaaaaaaa:bbbbbbbbbbbb", at, new EscalationTrace { RuleIds = ["R-10"], Step = "learn" })
        {
            DocumentIds = ["doc-1"], UploadIds = ["up-1"],
        };
        var (created, _) = EscalationLedger.Merge(null, signal, "system");
        var audit = new AuditEvent(at, "system", "system", "escalation.raised", "up-1", "doc-1", $"escalations/{created.Id}");
        Assert.True(await store.SaveEscalationAsync(created, 0, audit, Actor.System, [EscalationLedger.Event(created, true, "raised", "system", at)]));
        Assert.False(await store.SaveEscalationAsync(created, 0, audit, Actor.System, [])); // a second first occurrence: re-read and count

        var stored = (await store.GetEscalationAsync(created.Id))!;
        Assert.Equal((1, 1, "R-10", "learn"), (stored.Version, stored.Count, stored.TraceSummary.RuleIds.Single(), stored.TraceSummary.Step));
        var (counted, _) = EscalationLedger.Merge(stored, signal with { DocumentIds = ["doc-2"] }, "system");
        Assert.True(await store.SaveEscalationAsync(counted, 1, audit with { Action = "escalation.updated" }, Actor.System, []));
        Assert.False(await store.SaveEscalationAsync(counted, 1, audit, Actor.System, [])); // stale version
        Assert.Equal((2, 2), ((await store.GetEscalationAsync(created.Id))!.Count, (await store.ListEscalationsAsync()).Single().Version));

        await using var db = new LossRunDbContext(LossRunDbContext.Options(connection));
        Assert.Equal(["escalation.raised", "escalation.updated"], await db.AuditEvents.Where(a => a.FieldPath == $"escalations/{created.Id}").OrderBy(a => a.Id).Select(a => a.Action).ToListAsync());
        Assert.Equal(1, await db.Outbox.CountAsync(o => o.Kind == OutboxKinds.EscalationRaised));
        Assert.Equal(2, await db.Escalations.TemporalAll().CountAsync(e => e.Id == created.Id)); // version 1 is history

        var observation = Assert.Single(await store.EscalationObservationsAsync(at.AddYears(-5)));
        Assert.Equal(("doc-1", "up-1"), (observation.DocumentId, observation.UploadId));
    }

    [SqlFact]
    public async Task A_quote_label_is_versioned_and_each_change_is_audited()
    {
        var connection = await sql.DatabaseAsync("quotelabel");
        var store = SqlRecordStore.Create(connection);
        var actor = new Actor("mgr-1", Roles.UWManager, "api");
        var at = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

        await store.SaveQuoteLabelAsync(new QuoteLabel("Q-LOAD-1000", "Q-DEMO-1000 · Large Account (synthetic)", null, null, at, "mgr-1"), "demo name", actor);
        await store.SaveQuoteLabelAsync(new QuoteLabel("Q-LOAD-1000", "Q-DEMO-1000 · Large Account (synthetic)", at.AddMinutes(1), "mgr-1", at.AddMinutes(1), "mgr-1"), "retire", actor);

        var label = (await store.GetQuoteLabelAsync("Q-LOAD-1000"))!;
        Assert.Equal(("Q-DEMO-1000 · Large Account (synthetic)", at.AddMinutes(1)), (label.DisplayName, label.RetiredAt));
        await using var db = new LossRunDbContext(LossRunDbContext.Options(connection));
        var actions = await db.AuditEvents.Where(a => a.FieldPath == "quotes/Q-LOAD-1000").OrderBy(a => a.Id).Select(a => a.Action).ToListAsync();
        Assert.Equal(["quote.rename", "quote.retire"], actions);
        Assert.Equal(1, await db.QuoteLabels.TemporalAll().CountAsync(q => q.QuoteId == "Q-LOAD-1000" && q.RetiredUtc == null)); // the pre-retire version is history
    }

    [SqlFact]
    public async Task Concurrent_manifest_updates_do_not_lose_items()
    {
        var connection = await sql.DatabaseAsync("manifest");
        var store = SqlRecordStore.Create(connection);
        await store.UpdateManifestAsync("up-3", _ => Samples.Manifest("up-3"), Actor.System);

        await Parallel.ForEachAsync(Enumerable.Range(0, 12), new ParallelOptions { MaxDegreeOfParallelism = 6 }, async (i, ct) =>
            await store.UpdateManifestAsync("up-3", m =>
            {
                m!.Items.Add(new ManifestItem { DocumentId = $"doc-{i}", FileName = "f.pdf", Classification = "LossRun", Status = ItemStatuses.Processed });
                return m;
            }, Actor.System, ct));

        var manifest = (await store.GetManifestAsync("up-3"))!;
        Assert.Equal(12, manifest.Items.Count);
        Assert.Equal("Q-100", manifest.Submission.QuoteId);
        await using var db = new LossRunDbContext(LossRunDbContext.Options(connection));
        Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.Action == "upload.create"));
    }

    [SqlFact]
    public async Task The_sql_format_registry_keeps_specs_lifecycle_and_stats()
    {
        var connection = await sql.DatabaseAsync("formats");
        var options = LossRunDbContext.Options(connection);
        var registry = new SqlFormatRegistry(() => new LossRunDbContext(options), new InMemoryFormatRegistry());
        var spec = FormatJson.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(SqlFixture.Master)!, "..", "src", "TestShared", "formats", "synthetic-mutual-pdf", "1.json")));
        spec.Status = FormatLifecycle.Shadow;

        await registry.PublishAsync(spec);
        await Assert.ThrowsAsync<FormatAlreadyPublishedException>(() => registry.PublishAsync(spec));
        await FormatLifecycle.InitializeAsync(registry, spec, null, DateTimeOffset.UtcNow);
        await registry.UpdateStatsAsync(spec.FormatId, s => s.Fields["claim.claimNumber"] = new FieldStat { SuccessCount = 7, LastFormatVersion = spec.Ref });
        Assert.Equal(FormatLifecycle.Shadow, await FormatLifecycle.StatusAsync(registry, spec));
        await FormatLifecycle.ApproveAsync(registry, spec.FormatId, spec.Version, "uw-1", DateTimeOffset.UtcNow);

        Assert.Equal(FormatLifecycle.Active, await FormatLifecycle.StatusAsync(registry, spec));
        Assert.Equal(7, (await registry.GetStatsAsync(spec.FormatId)).Fields["claim.claimNumber"].SuccessCount);
        await registry.UpdateStatsAsync(spec.FormatId, s =>
        {
            foreach (var cosine in new[] { 0.97, 0.96, 0.98, 0.95, 0.96 })
            {
                (s.Embedding ??= new EmbeddingHits()).Add(cosine);
            }
        });
        Assert.Equal(5, (await registry.GetStatsAsync(spec.FormatId)).Embedding!.Count);
        Assert.NotNull((await registry.GetStatsAsync(spec.FormatId)).Embedding!.Threshold());
        Assert.Equal(spec.Ref, (await registry.GetAsync(spec.FormatId))!.Ref);
        Assert.Single(await registry.GetIndexAsync());
    }

    [SqlFact]
    public async Task Learned_aliases_live_in_sql_with_their_history_and_append_only_evidence()
    {
        // ADR 0019: dbo.GlossaryAlias (system-versioned) and dbo.GlossaryAliasEvidence (append-only ledger).
        var connection = await sql.DatabaseAsync("aliases");
        var options = LossRunDbContext.Options(connection);
        var registry = new SqlFormatRegistry(() => new LossRunDbContext(options), new InMemoryFormatRegistry());
        var at = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        LossRun.Contracts.LearnedMapping Learned(string formatId, string documentId) => new()
        {
            Header = "Comp Pd", CanonicalField = "claim.indemnity.paid", CarrierName = "Synthetic", FormatId = formatId, FormatVersion = "1", FirstSeenDocumentId = documentId,
            FirstSeenUtc = at, LearnedBy = new LossRun.Contracts.LearnedBy { Model = "learn", PromptVersion = "learn-v8" }, Confidence = 0.9, TotalsTie = true,
            Evidence = [new LossRun.Contracts.AliasEvidence { DocumentId = documentId, FormatRef = $"{formatId}@1", Route = "learn", TotalsTie = true, AtUtc = at }],
        };
        CarrierFormat Spec(string formatId) => new() { FormatId = formatId, Version = 1, ColumnMap = [new ColumnMapping { Source = "Comp Pd", Target = "claim.indemnity.paid" }] };

        await registry.AppendLearnedMappingsAsync([Learned("fmt-a", "d1")]);
        await registry.UpdateLearnedGlossaryAsync(g => AliasLifecycle.RecordVerified(g, Spec("fmt-a"), "d2", "replay", true, at.AddHours(1), new AliasPolicy(3, 2)));
        var changes = await registry.UpdateLearnedGlossaryAsync(g => AliasLifecycle.RecordVerified(g, Spec("fmt-b"), "d3", "replay", true, at.AddHours(2), new AliasPolicy(3, 2)));
        await registry.UpdateLearnedGlossaryAsync(g => AliasLifecycle.RetireOnCorrection(g, Spec("fmt-b"), "policyPeriods[0].claims[0].indemnity.paid", "uw-1", at.AddDays(1), "override:d3@2:x"));

        Assert.Equal("glossary.alias.activated", Assert.Single(changes).Kind);
        var alias = Assert.Single((await registry.GetLearnedGlossaryAsync()).Mappings);
        Assert.Equal((LossRun.Contracts.AliasStatus.Retired, 3, "override:d3@2:x"), (alias.Status, alias.Evidence.Count, alias.RetiredAuditEventId));
        Assert.Equal(at, alias.FirstSeenUtc);
        await using var db = new LossRunDbContext(options);
        Assert.Equal(["active", "candidate", "retired"], (await db.GlossaryAliases.TemporalAll().Select(a => a.Status).ToListAsync()).Distinct().Order());
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("UPDATE dbo.GlossaryAliasEvidence SET Route = N'x'")); // ledger: append-only
    }

    [SqlFact]
    public async Task Hand_specs_under_the_sql_registry_keep_stats_and_can_be_retired()
    {
        var connection = await sql.DatabaseAsync("handspecs");
        var options = LossRunDbContext.Options(connection);
        var hand = new InMemoryFormatRegistry();
        await FormatDirectory.LoadIntoAsync(Path.Combine(Path.GetDirectoryName(SqlFixture.Master)!, "..", "src", "TestShared", "formats"), hand);
        var registry = new LayeredFormatRegistry(hand, new SqlFormatRegistry(() => new LossRunDbContext(options), new InMemoryFormatRegistry(), hand));

        await registry.UpdateStatsAsync("synthetic-mutual-xlsx", s => s.Fields["claim.claimNumber"] = new FieldStat { SuccessCount = 1 });
        await FormatLifecycle.RetireAsync(registry, "synthetic-mutual-xlsx", 1, "admin", "superseded", DateTimeOffset.UtcNow);

        var spec = (await registry.GetAsync("synthetic-mutual-xlsx", 1))!;
        Assert.Equal(FormatLifecycle.Retired, await FormatLifecycle.StatusAsync(registry, spec));
        Assert.Equal(1, (await registry.GetStatsAsync("synthetic-mutual-xlsx")).Fields["claim.claimNumber"].SuccessCount);
        Assert.Equal((await hand.GetIndexAsync()).Count, (await registry.GetIndexAsync()).Count); // no duplicate index entries
    }

    [SqlFact]
    public async Task A_thousand_claim_account_queries_fast()
    {
        var connection = await sql.DatabaseAsync("volume");
        var store = SqlRecordStore.Create(connection);
        await store.UpdateManifestAsync("up-4", _ => Samples.Manifest("up-4", [.. Enumerable.Range(0, 10).Select(i => $"vol-{i}")]), Actor.System);
        var persist = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            var watch = Stopwatch.StartNew();
            await store.SaveResultAsync(Samples.Result("up-4", $"vol-{i}", claims: 100), Actor.System);
            persist.Add(watch.Elapsed.TotalMilliseconds);
        }

        await using var db = new LossRunDbContext(LossRunDbContext.Options(connection));
        var timings = new List<double>();
        for (var i = 0; i < 40; i++)
        {
            var watch = Stopwatch.StartNew();
            var rows = await db.Database.SqlQueryRaw<decimal>("SELECT SUM(TotalIncurred) AS Value FROM dbo.vClaimAsAdjusted WHERE QuoteId = {0}", "Q-100").SingleAsync();
            timings.Add(watch.Elapsed.TotalMilliseconds);
            Assert.True(rows > 0);
        }

        timings.Sort();
        persist.Sort();
        output.WriteLine($"persist 100-claim document p50 {persist[5]:0} ms, max {persist[^1]:0} ms; 1,000-claim account query p95 {timings[(int)(0.95 * timings.Count) - 1]:0.0} ms");
        Assert.Equal(1000, await db.Claims.CountAsync());
    }
}
