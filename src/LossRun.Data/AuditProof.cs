using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace LossRun.Data;

public sealed record ProofCheck(string Name, bool Pass, string Detail);

public sealed record ProofReport(IReadOnlyList<ProofCheck> Checks, int Claims, int ClaimsWithHistory, int Overrides, int AuditEvents, TimeSpan Elapsed)
{
    public bool Pass => Checks.All(c => c.Pass);
}

/// <summary>
/// "100 % of edits tracked" (brief 09 §6; underwriter-analytics.md KQ4), as four checks against the live database:
/// (a) every Claim history version is one decision, and every column it changed has an override or re-derive audit row
/// of that decision; (b) UPDATE and DELETE on each ledger table fail in the engine; (c) the ledger verifies against a
/// freshly generated digest (and against automatic digest storage when configured); (d) a temporal AS OF query returns
/// what the underwriter saw before each override.
/// </summary>
public static class AuditProof
{
    /// <summary>Brief 30 item 35: the append-only ledger tables are read from the deployed database (sys.tables), never a hard-coded list.</summary>
    private const string LedgerTablesQuery =
        "SELECT name FROM sys.tables WHERE ledger_type_desc = N'APPEND_ONLY_LEDGER_TABLE' AND SCHEMA_NAME(schema_id) = N'dbo' ORDER BY name";

    private static readonly string[] Bookkeeping = [nameof(ClaimRow.LastOverrideId), nameof(ClaimRow.ChangedBy)];

    public static async Task<ProofReport> RunAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await using var db = new LossRunDbContext(LossRunDbContext.Options(connectionString));
        var checks = new List<ProofCheck>
        {
            await ReconcileAsync(db, cancellationToken).ConfigureAwait(false),
        };
        checks.AddRange(await TamperAsync(connectionString, cancellationToken).ConfigureAwait(false));
        checks.AddRange(await VerifyLedgerAsync(connectionString, cancellationToken).ConfigureAwait(false));
        checks.Add(await AsOfAsync(db, cancellationToken).ConfigureAwait(false));

        var claims = await db.Claims.CountAsync(cancellationToken).ConfigureAwait(false);
        var withHistory = await db.Claims.TemporalAll().GroupBy(c => c.Id).CountAsync(g => g.Count() > 1, cancellationToken).ConfigureAwait(false);
        return new ProofReport(checks, claims, withHistory, await db.Overrides.CountAsync(cancellationToken).ConfigureAwait(false),
            await db.AuditEvents.CountAsync(cancellationToken).ConfigureAwait(false), watch.Elapsed);
    }

    // (a) history versions − 1 = decisions with overrides, and changed columns ⊆ audited columns per decision.
    private static async Task<ProofCheck> ReconcileAsync(LossRunDbContext db, CancellationToken cancellationToken)
    {
        var versions = (await db.Claims.TemporalAll().AsNoTracking()
                .Select(c => new { Row = c, From = EF.Property<DateTime>(c, "ValidFromUtc") }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(v => v.Row.Id).ToDictionary(g => g.Key, g => g.OrderBy(v => v.From).Select(v => v.Row).ToList());
        var audits = (await db.AuditEvents.AsNoTracking().Where(a => a.ClaimId != null && (a.Action == "override" || a.Action == "rederive"))
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(a => a.ClaimId!.Value).ToDictionary(g => g.Key, g => g.GroupBy(a => a.CorrelationId ?? $"#{a.Id}").OrderBy(c => c.Min(a => a.Id)).ToList());

        var problems = new List<string>();
        var checkedVersions = 0;
        foreach (var (claimId, history) in versions)
        {
            var decisions = audits.GetValueOrDefault(claimId) ?? [];
            if (history.Count - 1 != decisions.Count)
            {
                problems.Add($"claim {claimId}: {history.Count - 1} change version(s) vs {decisions.Count} audited decision(s)");
                continue;
            }

            for (var i = 1; i < history.Count; i++)
            {
                checkedVersions++;
                var audited = decisions[i - 1].Select(a => a.FieldPath is null ? null : JsonPath.ClaimPosition(a.FieldPath)?.Field).OfType<string>()
                    .Select(ClaimColumns.Column).OfType<string>().ToHashSet(StringComparer.Ordinal);
                var changed = typeof(ClaimRow).GetProperties().Where(p => !Bookkeeping.Contains(p.Name) && !Equals(p.GetValue(history[i - 1]), p.GetValue(history[i])))
                    .Select(p => p.Name).ToList();
                var unaudited = changed.Where(c => !audited.Contains(c)).ToList();
                if (unaudited.Count > 0)
                {
                    problems.Add($"claim {claimId} version {i}: {string.Join(", ", unaudited)} changed without an override/rederive audit row");
                }
            }
        }

        // An override row with no audit row (or the reverse) would also break the chain.
        var overrideIds = await db.Overrides.AsNoTracking().Select(o => o.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var auditedOverrides = await db.AuditEvents.AsNoTracking().Where(a => a.OverrideId != null).Select(a => a.OverrideId!.Value).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var orphans = overrideIds.Except(auditedOverrides).Count();
        if (orphans > 0)
        {
            problems.Add($"{orphans} override row(s) without an audit event");
        }

        return new ProofCheck("a. history versions = audited decisions", problems.Count == 0,
            problems.Count == 0 ? $"{versions.Count} claim(s), {checkedVersions} change version(s), {overrideIds.Count} override(s): every change is audited"
                : string.Join("; ", problems.Take(10)));
    }

    // (b) the engine rejects UPDATE and DELETE on append-only ledger tables (rolled back either way).
    private static async Task<IEnumerable<ProofCheck>> TamperAsync(string connectionString, CancellationToken cancellationToken)
    {
        var checks = new List<ProofCheck>();
        await using var connection = await LossRun.Data.SqlConnections.OpenAsync(connectionString, cancellationToken).ConfigureAwait(false);
        var tables = new List<(string Table, string Column)>();
        await using (var list = new SqlCommand(LedgerTablesQuery, connection))
        await using (var reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add((reader.GetString(0), ""));
            }
        }

        for (var i = 0; i < tables.Count; i++)
        {
            // Any user column will do: the engine refuses the statement on an append-only table whatever it touches.
            await using var column = new SqlCommand("SELECT TOP (1) name FROM sys.columns WHERE object_id = OBJECT_ID(@t) AND is_hidden = 0 AND generated_always_type = 0 ORDER BY column_id",
                connection);
            column.Parameters.AddWithValue("@t", $"dbo.{tables[i].Table}");
            tables[i] = (tables[i].Table, (string)(await column.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
        }

        if (tables.Count == 0)
        {
            checks.Add(new ProofCheck("b. append-only ledger tables", false, "none found in dbo"));
        }

        foreach (var (table, col) in tables)
        {
            foreach (var statement in new[] { $"UPDATE TOP (1) dbo.[{table}] SET [{col}] = [{col}]", $"DELETE TOP (1) FROM dbo.[{table}]" })
            {
                await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                string outcome;
                bool blocked;
                try
                {
                    await using var command = new SqlCommand(statement, connection, tx);
                    var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    (blocked, outcome) = (false, $"succeeded ({rows} row(s)) — NOT append-only");
                }
                catch (SqlException e)
                {
                    // 37359: "Updates are not allowed for the append only Ledger table" — any other error is not the ledger's doing.
                    (blocked, outcome) = (e.Number == 37359, $"rejected: error {e.Number}{(e.Number == 37359 ? " (append-only ledger)" : $" — {e.Message}")}");
                }
                finally
                {
                    if (tx.Connection is not null)
                    {
                        await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                checks.Add(new ProofCheck($"b. {statement.Split(' ')[0]} dbo.{table}", blocked, outcome));
            }
        }

        return checks;
    }

    // (c) generate a digest, keep it (dbo.LedgerDigest), verify the database against every kept digest; plus digest storage in Azure.
    private static async Task<IEnumerable<ProofCheck>> VerifyLedgerAsync(string connectionString, CancellationToken cancellationToken)
    {
        var checks = new List<ProofCheck>();
        await using var connection = await LossRun.Data.SqlConnections.OpenAsync(connectionString, cancellationToken).ConfigureAwait(false);

        string digest;
        await using (var generate = new SqlCommand("EXEC sys.sp_generate_database_ledger_digest", connection))
        {
            digest = (string)(await generate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        var blockId = System.Text.Json.JsonDocument.Parse(digest).RootElement.GetProperty("block_id").GetInt64();
        await using (var keep = new SqlCommand("INSERT INTO dbo.LedgerDigest (BlockId, DigestJson, GeneratedUtc, Source) VALUES (@b, @d, sysutcdatetime(), N'audit-proof')", connection))
        {
            keep.Parameters.AddWithValue("@b", blockId);
            keep.Parameters.AddWithValue("@d", digest);
            await keep.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var digests = new List<string>();
        await using (var read = new SqlCommand("SELECT DigestJson FROM dbo.LedgerDigest ORDER BY Id", connection))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                digests.Add(reader.GetString(0));
            }
        }

        try
        {
            await using var verify = new SqlCommand("EXEC sys.sp_verify_database_ledger @digests", connection) { CommandTimeout = 600 };
            verify.Parameters.Add(new SqlParameter("@digests", SqlDbType.NVarChar, -1) { Value = "[" + string.Join(",", digests) + "]" });
            await verify.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            checks.Add(new ProofCheck("c. sp_verify_database_ledger", true, $"verified against {digests.Count} digest(s), latest block {blockId}"));
        }
        catch (SqlException e)
        {
            checks.Add(new ProofCheck("c. sp_verify_database_ledger", false, e.Message));
        }

        // Automatic digest storage (Azure SQL; Terraform ledgerDigestUploads): verify against the immutable copies too.
        var locations = 0;
        await using (var count = new SqlCommand("IF OBJECT_ID(N'sys.database_ledger_digest_locations') IS NOT NULL SELECT COUNT(*) FROM sys.database_ledger_digest_locations ELSE SELECT 0", connection))
        {
            locations = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        if (locations > 0)
        {
            try
            {
                await using var verify = new SqlCommand(
                    "DECLARE @locations nvarchar(max) = (SELECT * FROM sys.database_ledger_digest_locations FOR JSON AUTO, INCLUDE_NULL_VALUES); EXEC sys.sp_verify_database_ledger_from_digest_storage @locations;",
                    connection) { CommandTimeout = 600 };
                await verify.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                checks.Add(new ProofCheck("c. verify from digest storage", true, $"{locations} digest location(s) (immutable Blob)"));
            }
            catch (SqlException e)
            {
                checks.Add(new ProofCheck("c. verify from digest storage", false, e.Message));
            }
        }

        return checks;
    }

    // (d) for each override, the claim AS OF just before its decision shows the override's old value; now shows the new one.
    private static async Task<ProofCheck> AsOfAsync(LossRunDbContext db, CancellationToken cancellationToken)
    {
        var overrides = await db.Overrides.AsNoTracking().Where(o => o.ClaimId != null && o.ClaimField != null).OrderByDescending(o => o.Id).Take(25)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (overrides.Count == 0)
        {
            return new ProofCheck("d. temporal AS OF", true, "no overrides yet (nothing to replay)");
        }

        var problems = new List<string>();
        foreach (var o in overrides)
        {
            var column = ClaimColumns.Column(o.ClaimField!);
            if (column is null)
            {
                continue;
            }

            // The version this decision created starts at the transaction time of its commit; just before it, the old value.
            var versions = await db.Claims.TemporalAll().AsNoTracking().Where(c => c.Id == o.ClaimId)
                .Select(c => new { Row = c, From = EF.Property<DateTime>(c, "ValidFromUtc") }).OrderBy(v => v.From).ToListAsync(cancellationToken).ConfigureAwait(false);
            // The version naming this override; else (an earlier override of a multi-override decision) the first version of
            // the decision. Not "the first with a larger id": EF hi/lo ids from concurrent sessions are not in time order
            // (brief 13: the 12b load test made override 2113 look wrong while its history was right).
            var created = versions.FirstOrDefault(v => v.Row.LastOverrideId == o.Id)
                ?? versions.FirstOrDefault(v => v.From >= o.AtUtc.AddSeconds(-1) && v.Row.LastOverrideId >= o.Id);
            if (created is null)
            {
                problems.Add($"override {o.Id}: no version carries it");
                continue;
            }

            var before = await db.Claims.TemporalAsOf(created.From.AddTicks(-1)).AsNoTracking().SingleOrDefaultAsync(c => c.Id == o.ClaimId, cancellationToken).ConfigureAwait(false);
            var shown = before is null ? null : Format(typeof(ClaimRow).GetProperty(column)!.GetValue(before));
            if (!SameValue(shown, o.OldValue))
            {
                problems.Add($"override {o.Id} ({o.ClaimField}): AS OF before shows {shown ?? "null"}, override recorded old value {o.OldValue ?? "null"}");
            }
        }

        return new ProofCheck("d. temporal AS OF", problems.Count == 0,
            problems.Count == 0 ? $"{overrides.Count} override(s): the claim as of just before each decision shows the recorded old value" : string.Join("; ", problems.Take(5)));
    }

    private static string? Format(object? value) => value switch
    {
        null => null,
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => value.ToString(),
    };

    private static bool SameValue(string? a, string? b) =>
        a == b || decimal.TryParse(a, NumberStyles.Number, CultureInfo.InvariantCulture, out var x) && decimal.TryParse(b, NumberStyles.Number, CultureInfo.InvariantCulture, out var y) && x == y
        || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
