using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LossRun.DbDeploy;

/// <summary>One Liquibase formatted-SQL changeset (ADR 0006).</summary>
/// <param name="Checksum">SHA-256 of the body before parameter substitution (line endings normalized, trailing blanks trimmed), so per-environment parameters never read as drift.</param>
public sealed record Changeset(string Author, string Id, string File, string Body, IReadOnlyList<string> Rollback, string? Comment, bool RunOnChange, bool RunAlways,
    string Checksum)
{
    public string Key => $"{Author}:{Id}";

    public string Render(IReadOnlyDictionary<string, string> parameters) => Changelog.Substitute(Body, parameters);
}

/// <summary>Reads db/master.xml and the formatted-SQL files it includes, in order.</summary>
public static partial class Changelog
{
    public static IReadOnlyList<Changeset> Load(string masterXml)
    {
        var master = XDocument.Load(masterXml);
        var root = Path.GetDirectoryName(Path.GetFullPath(masterXml))!;
        var ns = master.Root!.Name.Namespace;
        var changesets = new List<Changeset>();
        foreach (var include in master.Root.Elements(ns + "include"))
        {
            var file = include.Attribute("file")!.Value;
            var path = Path.Combine(root, file);
            changesets.AddRange(Parse(File.ReadAllText(path), file.Replace('\\', '/')));
        }

        var duplicate = changesets.GroupBy(c => c.Key).FirstOrDefault(g => g.Count() > 1);
        return duplicate is null ? changesets : throw new InvalidDataException($"Changeset {duplicate.Key} is declared more than once.");
    }

    /// <summary>Default changelog parameters declared in master.xml (&lt;property name value/&gt;).</summary>
    public static Dictionary<string, string> Properties(string masterXml)
    {
        var master = XDocument.Load(masterXml);
        var ns = master.Root!.Name.Namespace;
        return master.Root.Elements(ns + "property").ToDictionary(p => p.Attribute("name")!.Value, p => p.Attribute("value")?.Value ?? "", StringComparer.Ordinal);
    }

    public static IEnumerable<Changeset> Parse(string text, string file)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "--liquibase formatted sql")
        {
            throw new InvalidDataException($"{file}: first line must be '--liquibase formatted sql'.");
        }

        Header? header = null;
        var body = new List<string>();
        var rollback = new List<string>();
        string? comment = null;
        foreach (var line in lines.Skip(1).Append("--changeset end:of-file"))
        {
            var match = ChangesetLine().Match(line);
            if (match.Success)
            {
                if (header is { } h)
                {
                    var bodyText = string.Join("\n", body.Select(b => b.TrimEnd())).Trim();
                    if (rollback.Count == 0)
                    {
                        throw new InvalidDataException($"{file}: changeset {h.Author}:{h.Id} has no --rollback.");
                    }

                    yield return new Changeset(h.Author, h.Id, file, bodyText, [.. rollback], comment, h.Attributes.GetValueOrDefault("runOnChange") == "true",
                        h.Attributes.GetValueOrDefault("runAlways") == "true", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bodyText))));
                }

                if (match.Groups["author"].Value == "end" && match.Groups["id"].Value == "of-file")
                {
                    yield break;
                }

                header = new Header(match.Groups["author"].Value, match.Groups["id"].Value,
                    match.Groups["attrs"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(a => a.Split(':', 2)).Where(a => a.Length == 2)
                        .ToDictionary(a => a[0], a => a[1], StringComparer.Ordinal));
                (body, rollback, comment) = ([], [], null);
            }
            else if (line.StartsWith("--rollback ", StringComparison.Ordinal))
            {
                rollback.Add(line["--rollback ".Length..]);
            }
            else if (line.StartsWith("--comment:", StringComparison.Ordinal))
            {
                comment = line["--comment:".Length..].Trim();
            }
            else if (header is not null)
            {
                body.Add(line);
            }
        }
    }

    /// <summary>Replaces ${name} with the parameter value (Liquibase changelog-parameter syntax); unknown names are an error.</summary>
    public static string Substitute(string sql, IReadOnlyDictionary<string, string> parameters) =>
        Parameter().Replace(sql, m => parameters.TryGetValue(m.Groups[1].Value, out var v)
            ? v.Replace("'", "''", StringComparison.Ordinal).Replace("]", "]]", StringComparison.Ordinal)
            : throw new InvalidDataException($"Changelog parameter ${{{m.Groups[1].Value}}} has no value (pass --param {m.Groups[1].Value}=...)."));

    private sealed record Header(string Author, string Id, Dictionary<string, string> Attributes);

    [GeneratedRegex(@"^--changeset\s+(?<author>[^:\s]+):(?<id>\S+)(?<attrs>.*)$")]
    private static partial Regex ChangesetLine();

    [GeneratedRegex(@"\$\{([A-Za-z0-9_.]+)\}")]
    private static partial Regex Parameter();
}
