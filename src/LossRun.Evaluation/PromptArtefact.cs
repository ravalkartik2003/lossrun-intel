using System.Security.Cryptography;
using System.Text;

namespace LossRun.Evaluation;

/// <summary>
/// Brief 20 Answers 3: the learn-prompt artefact, recorded in every run's run.json so the CI gate (tools/prompt-gate.py)
/// can check that a pull request changing the prompt, the schema descriptions or the lint rules attaches a corpus v4 run
/// made from that code. Same algorithm as the script: each file of <c>config/learn-prompt-artefact.paths</c> hashed with
/// carriage returns removed, combined as sha256 over "&lt;sha256&gt;  &lt;path&gt;\n" lines sorted by path.
/// </summary>
public static class PromptArtefact
{
    public const string PathsFile = "config/learn-prompt-artefact.paths";

    /// <summary>The artefact of the repository containing <paramref name="from"/> (default: the working directory); null outside one.</summary>
    public static string? Compute(string? from = null)
    {
        var root = RepositoryRoot(from ?? Directory.GetCurrentDirectory());
        if (root is null)
        {
            return null;
        }

        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(Path.Combine(root, PathsFile)).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var folder = Path.Combine(root, Path.GetDirectoryName(line) ?? "");
            foreach (var file in Directory.GetFiles(folder, Path.GetFileName(line)))
            {
                var bytes = File.ReadAllBytes(file).Where(b => b != (byte)'\r').ToArray();
                files[Path.GetRelativePath(root, file).Replace('\\', '/')] = Convert.ToHexStringLower(SHA256.HashData(bytes));
            }
        }

        var combined = string.Concat(files.Select(f => $"{f.Value}  {f.Key}\n"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(combined)));
    }

    /// <summary>
    /// Brief 31 Answers 3: the never-run artefacts the current one supersedes (<c>supersedes</c> in docs/evaluation/learn-prompt-artefact.json, kept by
    /// <c>prompt-gate.py --write</c>), when the committed artefact is the current code's; null when there are none or the file is stale.
    /// </summary>
    public static IReadOnlyList<string>? Supersedes(string? from = null)
    {
        var root = RepositoryRoot(from ?? Directory.GetCurrentDirectory());
        var path = root is null ? null : Path.Combine(root, "docs", "evaluation", "learn-prompt-artefact.json");
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        var stored = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
        if (stored?["sha256"]?.GetValue<string>() != Compute(from) || stored?["supersedes"] is not System.Text.Json.Nodes.JsonArray chain || chain.Count == 0)
        {
            return null;
        }

        return [.. chain.Select(s => s!.GetValue<string>())];
    }

    private static string? RepositoryRoot(string from)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(from)); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, PathsFile)))
            {
                return dir.FullName;
            }
        }

        return null;
    }
}
