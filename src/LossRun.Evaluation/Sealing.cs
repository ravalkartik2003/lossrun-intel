using System.Security.Cryptography;
using System.Text.Json;
using LossRun.Contracts;

namespace LossRun.Evaluation;

public sealed record SealedEntry(string Id, string File, string Sha256, string Carrier, string InputType, IReadOnlyList<string> Strata);

public sealed record SealManifest(string Protocol, int Seed, DateTimeOffset SealedAt, string CorpusGeneratorVersion, int? CorpusSeed, IReadOnlyList<SealedEntry> Tuning,
    IReadOnlyList<SealedEntry> Blind, string BlindManifestSha256);

/// <summary>
/// <c>lossrun eval seal</c> (brief 08 §4; research A4): splits a corpus into a tuning set released to every entrant and a
/// blind set held by the insurer, stratified by carrier × input type × edge case, and records a SHA-256 per blind file
/// plus a hash over the blind list so nobody can swap documents after sealing.
/// </summary>
public static class Sealing
{
    /// <param name="policiesCsv">Brief 31 item 3: the data team's document → policy map (columns <c>file,policyNumber</c>, one row per document and
    /// policy). With it the split unit is the policy cluster, so no policy spans tuning and blind; without it, a warning and the logical document.</param>
    public static int Seal(string corpusDir, string outDir, int tuningSize, int seed, string? policiesCsv = null)
    {
        if (policiesCsv is null)
        {
            Console.WriteLine("seal: WARNING no --policies map: the split unit is the logical document, so one policy can have documents on both sides "
                + "(the tuning loop then sees blind claims of that policy as archive-only; tuning-loop.md §1). Ask the data team for the document → policy map.");
        }

        var sealedManifest = Split(corpusDir, tuningSize, seed, policiesCsv);
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "seal-manifest.json"), JsonSerializer.Serialize(sealedManifest, new JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = true }));
        var protocol = EvalRunner.FindUp("docs/evaluation") is { } docs ? Path.Combine(docs, "scoring-protocol.md") : null;
        if (protocol is not null && File.Exists(protocol))
        {
            File.Copy(protocol, Path.Combine(outDir, "scoring-protocol.md"), overwrite: true);
        }

        Console.WriteLine($"sealed: {sealedManifest.Tuning.Count} tuning, {sealedManifest.Blind.Count} blind; blind manifest sha256 {sealedManifest.BlindManifestSha256}");
        return 0;
    }

    public static SealManifest Split(string corpusDir, int tuningSize, int seed, string? policiesCsv = null)
    {
        var manifest = CorpusManifest.Load(corpusDir);
        var random = new Random(seed);

        // The unit is the logical document: every rendering of it goes to the same side, so a blind scan is never a
        // rendering of a tuning native. With a policy map (brief 31 item 3) the unit is the policy cluster: logical documents
        // that share a policy, transitively. Strata are carrier × input-type set × edge case (of the unit's first document); each gets a
        // share of the tuning files proportional to its files (largest remainder over units), at least one unit when the stratum has two or more.
        var logical = Units(manifest, policiesCsv);
        var strata = logical.GroupBy(l => (l[0].Carrier, Inputs: string.Join("+", l.Select(e => e.InputType).Order(StringComparer.Ordinal)), Edge: l[0].Strata.FirstOrDefault() ?? "none"))
            .OrderBy(g => g.Key).ToList();
        var filesPerDoc = (double)manifest.Entries.Count / logical.Count;
        var targetDocs = (int)Math.Round(tuningSize / filesPerDoc);
        var quotas = strata.Select(g => (Group: g, Exact: (double)g.Sum(l => l.Count) * tuningSize / manifest.Entries.Count / (g.Sum(l => l.Count) / (double)g.Count()))).ToList();
        var allocation = quotas.ToDictionary(q => q.Group.Key, q => Math.Min(q.Group.Count() - (q.Group.Count() >= 2 ? 1 : 0), Math.Max(q.Group.Count() >= 2 ? 1 : 0, (int)q.Exact)));
        var remaining = targetDocs - allocation.Values.Sum();
        foreach (var q in quotas.OrderByDescending(q => q.Exact - Math.Floor(q.Exact)).ThenBy(q => q.Group.Key))
        {
            if (remaining <= 0)
            {
                break;
            }

            if (allocation[q.Group.Key] < q.Group.Count() - 1)
            {
                allocation[q.Group.Key]++;
                remaining--;
            }
        }

        var tuningIds = strata.SelectMany(g => g.OrderBy(l => l[0].LogicalId, StringComparer.Ordinal).OrderBy(_ => random.Next()).Take(allocation[g.Key]))
            .SelectMany(l => l.Select(e => e.Id)).ToHashSet(StringComparer.Ordinal);
        var tuning = manifest.Entries.Where(e => tuningIds.Contains(e.Id)).ToList();
        var blind = manifest.Entries.Where(e => !tuningIds.Contains(e.Id)).ToList();
        SealedEntry Sealed(ManifestEntry e) => new(e.Id, e.File, e.Sha256, e.Carrier, e.InputType, e.Strata);
        var blindSealed = blind.Select(Sealed).ToList();
        return new SealManifest("docs/evaluation/scoring-protocol.md", seed, DateTimeOffset.UtcNow, manifest.GeneratorVersion, manifest.Seed, [.. tuning.Select(Sealed)], blindSealed,
            BlindListHash(blindSealed));
    }

    /// <summary>Split units: logical documents, merged (union–find) where the policy map links them through a shared policy number.</summary>
    internal static List<List<ManifestEntry>> Units(CorpusManifest manifest, string? policiesCsv)
    {
        var logical = manifest.Entries.GroupBy(e => e.LogicalId).Select(g => g.ToList()).ToList();
        if (policiesCsv is null)
        {
            return logical;
        }

        var records = Csv.Parse(File.ReadAllText(policiesCsv));
        if (records.Count == 0)
        {
            throw new InvalidDataException($"{policiesCsv}: empty; expected a header file,policyNumber");
        }

        var header = records[0].Fields.Select(h => h.Trim()).ToList();
        var (fileCol, policyCol) = (header.FindIndex(h => h.Equals("file", StringComparison.OrdinalIgnoreCase)),
            header.FindIndex(h => h.Equals("policyNumber", StringComparison.OrdinalIgnoreCase)));
        if (fileCol < 0 || policyCol < 0)
        {
            throw new InvalidDataException($"{policiesCsv}: the header must name file and policyNumber");
        }

        var unitOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < logical.Count; i++)
        {
            foreach (var e in logical[i])
            {
                unitOf[e.Id] = i;
                unitOf[e.File] = i;
                unitOf[Path.GetFileName(e.File)] = i;
            }
        }

        var parent = Enumerable.Range(0, logical.Count).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        var firstWithPolicy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unmatched = 0;
        foreach (var (line, fields) in records.Skip(1))
        {
            if (fields.Length <= Math.Max(fileCol, policyCol))
            {
                throw new InvalidDataException($"{policiesCsv}: line {line} has {fields.Length} field(s), the header {header.Count}");
            }

            var policy = CanonicalComparer.NormalizeClaimNumber(fields[policyCol]);
            if (string.IsNullOrEmpty(policy) || !unitOf.TryGetValue(fields[fileCol].Trim(), out var unit))
            {
                unmatched++;
                continue;
            }

            if (firstWithPolicy.TryGetValue(policy, out var other))
            {
                parent[Find(unit)] = Find(other);
            }
            else
            {
                firstWithPolicy[policy] = unit;
            }
        }

        if (unmatched > 0)
        {
            Console.WriteLine($"seal: {unmatched} row(s) of {Path.GetFileName(policiesCsv)} name no document of the corpus (or no policy) and were ignored");
        }

        return [.. Enumerable.Range(0, logical.Count).GroupBy(Find).Select(g => g.SelectMany(i => logical[i]).OrderBy(e => e.LogicalId, StringComparer.Ordinal).ToList())
            .OrderBy(u => u[0].LogicalId, StringComparer.Ordinal)];
    }

    /// <summary>The published hash over the blind list: SHA-256 of "file sha256" lines, in seal order.</summary>
    public static string BlindListHash(IEnumerable<SealedEntry> blind) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", blind.Select(b => $"{b.File} {b.Sha256}")))));

    public static SealManifest Load(string sealPath) =>
        JsonSerializer.Deserialize<SealManifest>(File.ReadAllText(sealPath), CanonicalJson.Options) ?? throw new InvalidDataException($"not a seal manifest: {sealPath}");

    /// <summary>
    /// <c>lossrun eval verify [--published-hash &lt;sha256&gt;]</c>: 0 when the blind list still hashes to the manifest's (and the
    /// published) <c>blindManifestSha256</c> and every blind file still matches its sealed hash, else 1 and what does not.
    /// </summary>
    public static int VerifyCommand(string corpusDir, string sealPath, string? publishedHash = null)
    {
        var seal = Load(sealPath);
        var broken = Verify(corpusDir, seal, publishedHash);
        if (broken.Count == 0)
        {
            Console.WriteLine($"seal verified: {seal.Blind.Count} blind file(s) match; blind manifest sha256 {seal.BlindManifestSha256} (recomputed" +
                $"{(publishedHash is null ? "; pass --published-hash to compare with the published value" : ", equals the published value")})");
            return 0;
        }

        Console.Error.WriteLine($"SEAL BROKEN: {broken.Count} problem(s) since sealing — do not run or score (docs/operations/blind-run.md, section 6):");
        foreach (var file in broken)
        {
            Console.Error.WriteLine("  " + file);
        }

        return 1;
    }

    /// <summary>
    /// Verifies the seal before an entrant's results are scored: the blind list hash recomputed from <c>seal.Blind</c> against
    /// the manifest field and the published value (an edited list, e.g. a swapped file with its new hash written in, fails
    /// here), then each blind file against its sealed hash. Returns the problems: the list-hash lines first, then the files.
    /// </summary>
    public static IReadOnlyList<string> Verify(string corpusDir, SealManifest seal, string? publishedHash = null)
    {
        var problems = new List<string>();
        var recomputed = BlindListHash(seal.Blind);
        if (!string.Equals(recomputed, seal.BlindManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"seal-manifest.json: the blind list hashes to {recomputed}, not the recorded blindManifestSha256 {seal.BlindManifestSha256} (the list was edited)");
        }

        if (publishedHash is not null && !string.Equals(recomputed, publishedHash.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"seal-manifest.json: the blind list hashes to {recomputed}, not the published {publishedHash.Trim()}");
        }

        problems.AddRange(seal.Blind.Where(b => !File.Exists(CorpusManifest.DocumentPath(corpusDir, b.File))
            || Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(CorpusManifest.DocumentPath(corpusDir, b.File)))) != b.Sha256).Select(b => b.File));
        return problems;
    }
}
