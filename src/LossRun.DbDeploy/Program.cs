using System.Diagnostics;
using LossRun.Data;
using LossRun.DbDeploy;

// lossrun-db (brief 09): deploy [--check] | plan | validate | emit | backfill | audit-proof.
//   --connection "Server=tcp:<server>.database.windows.net,1433;Database=<sql-database>;Authentication=Active Directory Default;Encrypt=True;"
//   --master db/master.xml   --param appPrincipalName=<function app name>
// Keyless: Entra token auth via the connection string; no SQL logins.
string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var command = args.FirstOrDefault() ?? "";
var master = Option("--master") ?? FindUp("db/master.xml") ?? "db/master.xml";
var connection = Option("--connection") ?? Environment.GetEnvironmentVariable("LOSSRUN_SQL_CONNECTION");
var parameters = Changelog.Properties(master);
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--param" && args[i + 1].Split('=', 2) is [var key, var value])
    {
        parameters[key] = value;
    }
}

var changesets = Changelog.Load(master);
var watch = Stopwatch.StartNew();
switch (command)
{
    case "emit":
    {
        var output = Option("--out") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(master))!, "deploy.sql");
        File.WriteAllText(output, Deployer.Script(changesets, parameters));
        Console.WriteLine($"{changesets.Count} changesets → {output}");
        return 0;
    }

    case "validate" when connection is null:
        Console.WriteLine($"{changesets.Count} changesets parse; every one has a rollback; ids unique.");
        return 0;

    case "plan" or "validate" or "deploy" when connection is null:
        Console.Error.WriteLine("--connection (or LOSSRUN_SQL_CONNECTION) is required.");
        return 2;

    case "plan" or "validate":
    {
        var plan = await new Deployer(connection, changesets, parameters).PlanAsync();
        foreach (var step in plan)
        {
            Console.WriteLine($"  {step.Action,-7} {step.Changeset.Key} ({step.Changeset.File}){(step.Detail is null ? "" : $" — {step.Detail}")}");
        }

        var drift = plan.Count(s => s.Action == "drift");
        Console.WriteLine($"{plan.Count(s => s.Action is "apply" or "rerun")} to apply, {plan.Count(s => s.Action == "skip")} applied, {drift} drifted");
        return command == "validate" && drift > 0 ? 1 : 0;
    }

    case "deploy" when args.Contains("--check"):
    {
        // Brief 23 Answers 4: the deploy scripts refuse to deploy app code while changesets are pending (a live outage came
        // from brief 21's changeset never reaching the database). Exit 3: pending; exit 1: drifted; 0: up to date.
        var plan = await new Deployer(connection, changesets, parameters).PlanAsync();
        var pending = plan.Where(s => s.Action is "apply" or "rerun").ToList();
        var drifted = plan.Where(s => s.Action == "drift").ToList();
        foreach (var step in pending.Concat(drifted))
        {
            Console.WriteLine($"  {step.Action,-7} {step.Changeset.Key} ({step.Changeset.File})");
        }

        Console.WriteLine(pending.Count == 0 && drifted.Count == 0
            ? $"database up to date: {plan.Count} changesets applied"
            : $"database NOT up to date: {pending.Count} pending, {drifted.Count} drifted. Run lossrun-db deploy first.");
        return drifted.Count > 0 ? 1 : pending.Count > 0 ? 3 : 0;
    }

    case "deploy":
    {
        Console.WriteLine($"deploying {changesets.Count} changesets from {master}");
        var steps = await new Deployer(connection, changesets, parameters).DeployAsync();
        var applied = steps.Count(s => s.Action is "apply" or "rerun");
        Console.WriteLine(applied == 0 ? "nothing to apply" : $"{applied} applied in {watch.ElapsedMilliseconds} ms");
        return 0;
    }

    case "audit-proof" when connection is not null:
    {
        var report = await AuditProof.RunAsync(connection);
        foreach (var check in report.Checks)
        {
            Console.WriteLine($"  {(check.Pass ? "PASS" : "FAIL")} {check.Name}: {check.Detail}");
        }

        Console.WriteLine($"{(report.Pass ? "audit-proof passed" : "audit-proof FAILED")}: {report.Claims} claims ({report.ClaimsWithHistory} with history), {report.Overrides} overrides, {report.AuditEvents} audit events, {report.Elapsed.TotalSeconds:0.0} s");
        return report.Pass ? 0 : 1;
    }

    case "backfill" when connection is not null:
    {
        var blob = Option("--blob-endpoint") ?? Environment.GetEnvironmentVariable("LOSSRUN_BLOB_ENDPOINT")
            ?? throw new InvalidOperationException("Set --blob-endpoint or LOSSRUN_BLOB_ENDPOINT (terraform output storage_blob_endpoint).");
        var summary = await Backfill.RunAsync(new Uri(blob), connection, Option("--formats-container") ?? "formats", Console.WriteLine);
        Console.WriteLine(summary);
        return 0;
    }

    default:
        Console.Error.WriteLine("usage: lossrun-db deploy [--check]|plan|validate|emit|backfill|audit-proof [--connection <conn>] [--master db/master.xml] [--param name=value] [--out file]");
        return 2;
}

static string? FindUp(string relative)
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, relative)))
        {
            return Path.Combine(dir.FullName, relative);
        }
    }

    return null;
}
