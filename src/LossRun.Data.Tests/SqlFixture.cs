using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using LossRun.DbDeploy;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace LossRun.Data.Tests;

/// <summary>
/// Runs when a SQL Server 2022-or-later target is available (<see cref="SqlFixture"/>: a connection string, LocalDB, or
/// Docker); skipped otherwise, with the reason. Never fails for want of SQL.
/// </summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (SqlFixture.Target.Mode == SqlTargetMode.None)
        {
            Skip = SqlFixture.Target.Description;
        }
    }
}

public enum SqlTargetMode { ConnectionString, LocalDb, Docker, None }

/// <param name="Server">A connection string to the server's master database, or null for Docker (known after the container starts).</param>
public sealed record SqlTarget(SqlTargetMode Mode, string? Server, string Description);

/// <summary>
/// Brief 28: the SQL the tests run against, chosen in this order (the organisation does not allow Docker):
/// <list type="number">
/// <item><c>LOSSRUN_TEST_SQL</c> — a connection string to a SQL Server 2022+ or Azure SQL server where the login may create
/// databases (a server set aside for tests, never the working database).</item>
/// <item>LocalDB on Windows: an existing instance of version 16 (SQL Server 2022) or later, for ledger tables —
/// <c>MSSQLLocalDB</c> first, then any other. The fixture never creates or changes LocalDB instances (Answers 2): with only
/// older ones it skips with <see cref="OldLocalDbReason"/>.</item>
/// <item>Docker — a SQL Server 2022 container (Testcontainers), as before.</item>
/// <item>None: every SQL test is skipped with the reason.</item>
/// </list>
/// Each test class gets its own database, <c>lossrun_tests_{run}_{name}</c>, deployed with the normal changesets and
/// dropped at the end. One database per run, reset between classes (Answers 1), is not possible: a dropped ledger table
/// stays in the database under a generated name with its constraints, so redeploying the changesets collides with their
/// constraint names; and every table is in <c>dbo</c>, so a schema per class would mean rewriting every changeset.
/// </summary>
public sealed class SqlFixture : IAsyncLifetime
{
    public const string NoSqlReason = "no SQL available: set LOSSRUN_TEST_SQL or install LocalDB 2022 or Docker";
    public const string OldLocalDbReason = "LocalDB version too old for ledger tables (need 2022+); install SQL Server 2025 Express LocalDB or set LOSSRUN_TEST_SQL";

    /// <summary>Every test database starts with this, so it is recognisably a test database (and easy to find if a run is killed).</summary>
    public const string Prefix = "lossrun_tests_";

    private static readonly Lazy<SqlTarget> Chosen = new(Choose);
    private readonly string _run = Guid.NewGuid().ToString("N")[..6];
    private readonly List<string> _created = [];
    private MsSqlContainer? _container;
    private string? _server;

    public static SqlTarget Target => Chosen.Value;

    public static string Master => FindUp("db/master.xml");

    public async Task InitializeAsync()
    {
        switch (Target.Mode)
        {
            case SqlTargetMode.Docker:
                _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
                await _container.StartAsync();
                _server = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = "master", TrustServerCertificate = true }.ConnectionString;
                break;
            case SqlTargetMode.ConnectionString or SqlTargetMode.LocalDb:
                _server = Target.Server;
                break;
        }

        if (Target.Mode != SqlTargetMode.None)
        {
            Console.WriteLine($"SQL tests: {Target.Description}");
        }
    }

    public async Task DisposeAsync()
    {
        if (_server is not null && Target.Mode is SqlTargetMode.ConnectionString or SqlTargetMode.LocalDb)
        {
            SqlConnection.ClearAllPools();
            foreach (var name in _created)
            {
                try
                {
                    await using var connection = new SqlConnection(_server);
                    await connection.OpenAsync();
                    var sql = await IsAzureAsync(connection) ? $"DROP DATABASE IF EXISTS [{name}];"
                        : $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
                    await using var drop = new SqlCommand(sql, connection) { CommandTimeout = 300 };
                    await drop.ExecuteNonQueryAsync();
                }
                catch (SqlException e)
                {
                    Console.WriteLine($"SQL tests: could not drop {name} ({e.Number}); drop it by hand");
                }
            }
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>A fresh database with the changelog deployed (or empty when <paramref name="deploy"/> is false).</summary>
    public async Task<string> DatabaseAsync(string name, bool deploy = true)
    {
        var database = Target.Mode == SqlTargetMode.Docker ? name : $"{Prefix}{_run}_{name}";
        await using (var connection = new SqlConnection(_server))
        {
            await connection.OpenAsync();
            // Azure SQL Database defaults (snapshot isolation is required by sp_verify_database_ledger); Azure SQL has them on already.
            var sql = await IsAzureAsync(connection)
                ? $"IF DB_ID(N'{database}') IS NULL CREATE DATABASE [{database}];"
                : $"IF DB_ID(N'{database}') IS NULL CREATE DATABASE [{database}]; ALTER DATABASE [{database}] SET ALLOW_SNAPSHOT_ISOLATION ON; ALTER DATABASE [{database}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;";
            await using var create = new SqlCommand(sql, connection) { CommandTimeout = 600 };
            await create.ExecuteNonQueryAsync();
        }

        if (Target.Mode != SqlTargetMode.Docker)
        {
            _created.Add(database);
        }

        var connectionString = new SqlConnectionStringBuilder(_server) { InitialCatalog = database }.ConnectionString;
        if (deploy)
        {
            await new Deployer(connectionString, Changelog.Load(Master), Changelog.Properties(Master), _ => { }).DeployAsync();
        }

        return connectionString;
    }

    private static async Task<bool> IsAzureAsync(SqlConnection connection)
    {
        await using var edition = new SqlCommand("SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)", connection);
        return (int)(await edition.ExecuteScalarAsync())! == 5;
    }

    // ------------------------------------------------------------------ choosing the target

    private static SqlTarget Choose()
    {
        if (Environment.GetEnvironmentVariable("LOSSRUN_TEST_SQL") is { Length: > 0 } configured)
        {
            var server = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" };
            return new SqlTarget(SqlTargetMode.ConnectionString, server.ConnectionString, $"LOSSRUN_TEST_SQL ({server.DataSource}), databases {Prefix}<run>_<name>, dropped at the end");
        }

        string? localDbProblem = null;
        if (OperatingSystem.IsWindows() && LocalDb(out localDbProblem) is { } local)
        {
            return local;
        }

        if (DockerEngine())
        {
            return new SqlTarget(SqlTargetMode.Docker, null, "Docker (Testcontainers SQL Server 2022)");
        }

        return new SqlTarget(SqlTargetMode.None, null, localDbProblem ?? NoSqlReason);
    }

    private static bool DockerEngine() => Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 }
        || (OperatingSystem.IsWindows()
            ? Directory.GetFiles(@"\\.\pipe\").Any(p => p.EndsWith("docker_engine", StringComparison.Ordinal) || p.EndsWith("dockerDesktopLinuxEngine", StringComparison.Ordinal))
            : File.Exists("/var/run/docker.sock"));

    /// <summary>
    /// An existing LocalDB instance of version 16 or later (MSSQLLocalDB first); never creates one. <paramref name="problem"/>
    /// says why not when LocalDB is installed but every instance is older.
    /// </summary>
    private static SqlTarget? LocalDb(out string? problem)
    {
        problem = null;
        if (Tool("info") is not { } list)
        {
            return null; // no LocalDB
        }

        var instances = list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(i => i.Equals("MSSQLLocalDB", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(i => i, StringComparer.OrdinalIgnoreCase);
        foreach (var instance in instances)
        {
            if (Major(Tool($"info \"{instance}\"")) >= 16 && Connectable(instance))
            {
                return new SqlTarget(SqlTargetMode.LocalDb, $@"Server=(localdb)\{instance};Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=60",
                    $@"LocalDB (localdb)\{instance}, databases {Prefix}<run>_<name>, dropped at the end");
            }
        }

        problem = OldLocalDbReason;
        return null;
    }

    private static bool Connectable(string instance)
    {
        try
        {
            using var connection = new SqlConnection($@"Server=(localdb)\{instance};Database=master;Integrated Security=true;Connect Timeout=60");
            connection.Open();
            using var version = new SqlCommand("SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int)", connection);
            return (int)version.ExecuteScalar()! >= 16;
        }
        catch (SqlException)
        {
            return false;
        }
    }

    private static int Major(string? info) => info is not null && Regex.Match(info, @"Version:\s+(\d+)\.") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;

    private static string? Tool(string arguments)
    {
        try
        {
            var start = new ProcessStartInfo("sqllocaldb", arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(60_000);
            return process.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, relative)))
            {
                return Path.Combine(dir.FullName, relative);
            }
        }

        throw new FileNotFoundException(relative);
    }
}

[CollectionDefinition("sql")]
public sealed class SqlCollection : ICollectionFixture<SqlFixture>;
