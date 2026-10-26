using System.Data.Common;
using Azure.Core;
using LossRun.Ingestion;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LossRun.Data;

/// <summary>
/// Keyless Entra auth for Azure SQL through the repo's credential chain (<see cref="AzureCredentials.Default"/>).
/// SqlClient's own "Active Directory Default" stops at a failed managed-identity probe on a developer machine (the same
/// Azure.Identity behaviour AzureCredentials works around), so connection strings that ask for it are rewritten to use
/// an access-token callback instead. Other connection strings (SQL Server containers) pass through unchanged.
/// </summary>
public static class SqlConnections
{
    private static readonly string[] Scopes = ["https://database.windows.net/.default"];
    private static readonly Lazy<TokenCredential> Credential = new(AzureCredentials.Default);

    public static bool UsesEntra(string connectionString) =>
        new SqlConnectionStringBuilder(connectionString).Authentication == SqlAuthenticationMethod.ActiveDirectoryDefault;

    /// <summary>
    /// The connection string without the Authentication keyword (the token callback authenticates instead), with a
    /// connect timeout long enough for a serverless database to resume from auto-pause (≈ 1 minute; 08b: the deployed
    /// API returned 500 on the first uploads after an idle period with the 15 s default).
    /// </summary>
    public static string WithoutAuthentication(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { Authentication = SqlAuthenticationMethod.NotSpecified };
        builder.ConnectTimeout = Math.Max(builder.ConnectTimeout, ResumeConnectTimeoutSeconds);
        return builder.ConnectionString;
    }

    public const int ResumeConnectTimeoutSeconds = 90;

    /// <summary>Errors worth retrying on serverless Azure SQL besides EF's transient set: resuming (40613), timeouts (-2, 258).</summary>
    public static readonly int[] ResumeErrors = [40613, -2, 258, 40197, 40501];

    public static SqlConnection Create(string connectionString)
    {
        if (!UsesEntra(connectionString))
        {
            return new SqlConnection(connectionString);
        }

        return new SqlConnection(WithoutAuthentication(connectionString)) { AccessTokenCallback = TokenAsync };
    }

    /// <summary>Opens a connection, waiting out a serverless database resuming from auto-pause (40613) and transient throttling.</summary>
    public static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var connection = Create(connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch (SqlException e) when (ResumeErrors.Contains(e.Number) && attempt < 10)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<SqlAuthenticationToken> TokenAsync(SqlAuthenticationParameters parameters, CancellationToken cancellationToken)
    {
        var token = await Credential.Value.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken).ConfigureAwait(false);
        return new SqlAuthenticationToken(token.Token, token.ExpiresOn);
    }
}

/// <summary>Sets the token callback on every connection EF opens (Entra connection strings only).</summary>
internal sealed class EntraTokenInterceptor : DbConnectionInterceptor
{
    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        Attach(connection);
        return result;
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        Attach(connection);
        return ValueTask.FromResult(result);
    }

    private static void Attach(DbConnection connection)
    {
        if (connection is SqlConnection { AccessTokenCallback: null } sql)
        {
            sql.AccessTokenCallback = SqlConnections.TokenAsync;
        }
    }
}
