using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LossRun.Contracts;
using LossRun.Ingestion;

namespace LossRun.Evaluation;

/// <summary>One file the import accepted or skipped (the summary and <c>import-report.json</c>).</summary>
public sealed record ImportedFile(string File, string? Id, string? InputType, string? Carrier, int Pages, long Bytes, int? ScanDpi, string? Skipped);

/// <summary>
/// <c>lossrun eval import --in &lt;folder&gt; --out &lt;corpus&gt; [--carriers carriers.csv] [--copy]</c> (brief 14; passwords
/// for protected files come only from the environment, <c>LOSSRUN_PASSWORDS</c>, never the command line; the files are
/// referenced in place unless <c>--copy</c> copies them to <c>&lt;corpus&gt;/files</c>): turns a folder of real archive files into a corpus that <c>seal</c>, <c>run</c> and <c>score</c> read. The
/// synthetic generator was the only writer of <c>manifest.json</c> until now. Per file it records a SHA-256, an input
/// type by sniffing (native PDF, scan by DPI band, fax, XLSX/CSV, ZIP package, password-protected), pages, bytes and a
/// carrier. The carrier comes from the carrier map (<c>file,carrier</c>; a row may name a file, a file name or a
/// top-level folder), else the first sub-folder, else <c>unknown</c>. No truth is written:
/// truth comes from the keyed archive through <c>align</c> and adjudication, and <c>score</c> reports operational
/// numbers until then. The source folder is never modified.
/// </summary>
public static class Import
{
    public const string Generator = "import";

    private static readonly HashSet<string> Accepted = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".xlsx", ".xlsm", ".xls", ".csv", ".zip", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp" };

    /// <summary>The claims the data team is asked for: one row per carrier × policy the pool prints (brief 14 follow-up).</summary>
    public sealed record TruthRequestRow(string Carrier, string? PolicyNumber, DateOnly? PeriodStart, DateOnly? PeriodEnd, int Documents, string? Note);

    public static int Run(string inDir, string outDir, string? carriersCsv, bool copy, IReadOnlyList<string> passwords)
    {
        var (manifest, files, request) = Build(inDir, outDir, carriersCsv, copy, passwords);
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "truth-request.csv"), TruthRequestCsv(request));
        File.WriteAllText(Path.Combine(outDir, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = true }));
        File.WriteAllText(Path.Combine(outDir, "import-report.json"), JsonSerializer.Serialize(files, new JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = true }));
        Console.WriteLine(Summary(manifest, files));
        Console.WriteLine($"truth-request.csv: {request.Count(r => r.PolicyNumber is not null)} policy row(s), {request.Count(r => r.PolicyNumber is null)} carrier row(s) for files not readable before OCR");
        return 0;
    }

    /// <summary>
    /// The request for the data team. It lists the whole imported pool, not the blind set, so it does not reveal the draw.
    /// Columns: carrier, policy_number, period_start, period_end, documents, note.
    /// </summary>
    public static string TruthRequestCsv(IEnumerable<TruthRequestRow> rows)
    {
        static string Cell(string? v) => v is null ? "" : v.Contains(',', StringComparison.Ordinal) || v.Contains('"', StringComparison.Ordinal) ? $"\"{v.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : v;
        var sb = new StringBuilder("carrier,policy_number,period_start,period_end,documents,note\n");
        foreach (var r in rows)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{Cell(r.Carrier)},{Cell(r.PolicyNumber)},{r.PeriodStart:yyyy-MM-dd},{r.PeriodEnd:yyyy-MM-dd},{r.Documents},{Cell(r.Note)}");
        }

        return sb.ToString();
    }

    public static (CorpusManifest Manifest, List<ImportedFile> Files, List<TruthRequestRow> Request) Build(string inDir, string outDir, string? carriersCsv, bool copy,
        IReadOnlyList<string> passwords)
    {
        var source = Path.GetFullPath(inDir);
        var corpus = Path.GetFullPath(outDir);
        if (corpus.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || corpus == source)
        {
            throw new ArgumentException("--out must be outside --in (the source folder is never modified)");
        }

        var map = carriersCsv is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : ReadCarriers(carriersCsv);
        var docsRoot = copy ? "files" : Path.GetRelativePath(corpus, source).Replace('\\', '/');
        var entries = new List<ManifestEntry>();
        var files = new List<ImportedFile>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var policies = new Dictionary<(string Carrier, string Number), (DateOnly? Start, DateOnly? End, HashSet<string> Files)>();
        var unreadable = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(source, path).Replace('\\', '/');
            var ext = Path.GetExtension(path);
            if (!Accepted.Contains(ext))
            {
                files.Add(new ImportedFile(relative, null, null, null, 0, new FileInfo(path).Length, null, $"not a loss-run file type ({(ext.Length == 0 ? "no extension" : ext)})"));
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            var (inputType, pages, dpi, strata, problem) = Sniff(bytes, relative, passwords);
            if (problem is not null)
            {
                files.Add(new ImportedFile(relative, null, null, null, 0, bytes.Length, null, problem));
                continue;
            }

            // Carrier: the map by file path, file name or first folder (a folder row fixes a folder name that cannot hold the
            // carrier's exact name, e.g. a trailing dot on Windows), else the first folder, else unknown.
            var folder = relative.Contains('/', StringComparison.Ordinal) ? relative[..relative.IndexOf('/', StringComparison.Ordinal)] : null;
            var carrier = map.GetValueOrDefault(relative) ?? map.GetValueOrDefault(Path.GetFileName(relative))
                ?? (folder is null ? null : map.GetValueOrDefault(folder) ?? map.GetValueOrDefault(folder + "/")) ?? folder ?? "unknown";
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var id = UniqueId(relative, sha, ids);
            if (copy)
            {
                var target = Path.Combine(corpus, "files", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
            }

            // A package keeps its members (and their input types) and is read member by member for the truth request.
            var members = inputType == InputTypes.Zip ? Members(bytes, passwords) : null;
            foreach (var (memberName, memberBytes) in inputType == InputTypes.Zip ? Unpack(bytes) : [(relative, bytes)])
            {
                var (found, readable) = PolicyReader.Read(memberBytes, memberName, passwords);
                if (!readable || found.Count == 0)
                {
                    unreadable[carrier] = unreadable.GetValueOrDefault(carrier) + 1;
                }

                foreach (var p in found)
                {
                    var key = (carrier, p.Number);
                    var known = policies.GetValueOrDefault(key, (null, null, new HashSet<string>(StringComparer.Ordinal)));
                    known.Files.Add(relative);
                    policies[key] = (known.Start ?? p.Start, known.End ?? p.End, known.Files);
                }
            }

            entries.Add(new ManifestEntry(id, relative, carrier, "unknown", inputType, id, strata, sha, bytes.Length, members?.Sum(m => m.Pages) ?? pages, members));
            files.Add(new ImportedFile(relative, id, inputType, carrier, pages, bytes.Length, dpi, null));
        }

        var manifest = new CorpusManifest { GeneratorVersion = Generator, Seed = null, GeneratedAt = DateTimeOffset.UtcNow, DocsRoot = docsRoot, Entries = entries };
        var request = policies.OrderBy(p => p.Key.Carrier, StringComparer.Ordinal).ThenBy(p => p.Key.Number, StringComparer.Ordinal)
            .Select(p => new TruthRequestRow(p.Key.Carrier, p.Key.Number, p.Value.Start, p.Value.End, p.Value.Files.Count, null))
            .Concat(unreadable.OrderBy(u => u.Key, StringComparer.Ordinal).Select(u => new TruthRequestRow(u.Key, null, null, null, u.Value,
                $"{u.Value} file(s) of this carrier print no policy readable before OCR (scans, images, protected files): request every policy of this carrier for the accounts in the pool")))
            .ToList();
        return (manifest, files, request);
    }

    /// <summary>Input type, pages, scan DPI and the strata a file shows by itself (edge strata are left to adjudication).</summary>
    internal static (string InputType, int Pages, int? Dpi, List<string> Strata, string? Problem) Sniff(byte[] bytes, string name, IReadOnlyList<string> passwords)
    {
        var strata = new List<string>();
        switch (FileSniffer.Sniff(bytes, name))
        {
            case FileKind.Pdf:
                var unlocked = PdfUnlocker.Unlock(bytes, passwords);
                if (unlocked.WasEncrypted)
                {
                    strata.Add(Strata.Password);
                }

                if (!unlocked.Ok)
                {
                    // Kept: a protected file the evaluator holds no password for is still part of the set (it scores as not extracted).
                    return (InputTypes.NativePdf, 0, null, strata, null);
                }

                if (FirstPageCheck.HasTextLayer(unlocked.Bytes!))
                {
                    return (InputTypes.NativePdf, unlocked.PageCount, null, strata, null);
                }

                var dpi = FirstPageCheck.ScanDpi(unlocked.Bytes!);
                return (ScanType(dpi), unlocked.PageCount, dpi, strata, null);
            case FileKind.Xlsx or FileKind.Xls or FileKind.Xlsb:
                return (InputTypes.Xlsx, 1, null, strata, null);
            case FileKind.Csv:
                return (InputTypes.Csv, 1, null, strata, null);
            case FileKind.Zip:
                strata.Add(Strata.Bundle);
                return (InputTypes.Zip, 0, null, strata, null);
            case FileKind.Png or FileKind.Jpeg or FileKind.Tiff or FileKind.Bmp:
                var imageDpi = ImageDpi(bytes);
                return (ScanType(imageDpi), 1, imageDpi, strata, null);
            default:
                return ("", 0, null, strata, "content does not match a supported type");
        }
    }

    /// <summary>The DPI bands of the deck table; an unknown DPI is treated as the middle band, not as clean.</summary>
    private static string ScanType(int? dpi) => dpi switch
    {
        >= 280 => InputTypes.Scan300,
        < 180 => InputTypes.Fax150,
        _ => InputTypes.Scan200,
    };

    internal static IEnumerable<(string Name, byte[] Bytes)> Unpack(byte[] zip)
    {
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip), System.IO.Compression.ZipArchiveMode.Read);
        var members = new List<(string, byte[])>();
        foreach (var entry in archive.Entries.Where(e => e.Length > 0 && !e.FullName.EndsWith('/')).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            members.Add((entry.FullName, buffer.ToArray()));
        }

        return members;
    }

    /// <summary>A package's member files: name, input type (sniffed as for a top-level file), pages, bytes.</summary>
    private static List<PackageMember> Members(byte[] zip, IReadOnlyList<string> passwords) =>
        [.. Unpack(zip).Select(m => (m.Name, m.Bytes, Sniffed: Sniff(m.Bytes, m.Name, passwords)))
            .Select(m => new PackageMember(m.Name, m.Sniffed.Problem is null ? m.Sniffed.InputType : "unsupported", m.Sniffed.Pages, m.Bytes.Length))];

    /// <summary>PNG pHYs or JPEG JFIF density, when the image records one.</summary>
    internal static int? ImageDpi(byte[] b)
    {
        if (b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P')
        {
            for (var i = 8; i + 21 <= b.Length;)
            {
                var length = (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
                var type = Encoding.ASCII.GetString(b, i + 4, 4);
                if (type == "pHYs" && b[i + 16] == 1)
                {
                    var ppm = (b[i + 8] << 24) | (b[i + 9] << 16) | (b[i + 10] << 8) | b[i + 11];
                    return (int)Math.Round(ppm * 0.0254);
                }

                if (type == "IDAT" || length < 0)
                {
                    return null;
                }

                i += 12 + length;
            }
        }

        if (b.Length > 18 && b[0] == 0xFF && b[1] == 0xD8 && Encoding.ASCII.GetString(b, 6, 4) == "JFIF" && b[13] == 1)
        {
            return (b[14] << 8) | b[15];
        }

        return null;
    }

    private static string UniqueId(string relative, string sha, HashSet<string> taken)
    {
        var stem = new string([.. Path.ChangeExtension(relative, null).ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
        while (stem.Contains("--", StringComparison.Ordinal))
        {
            stem = stem.Replace("--", "-", StringComparison.Ordinal);
        }

        stem = stem.Trim('-');
        var id = $"{(stem.Length > 60 ? stem[..60].TrimEnd('-') : stem)}-{sha[..8]}";
        for (var n = 2; !taken.Add(id); n++)
        {
            id = $"{id}-{n}";
        }

        return id;
    }

    private static Dictionary<string, string> ReadCarriers(string csv)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = File.ReadAllLines(csv).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0 || !lines[0].Replace(" ", "", StringComparison.Ordinal).Equals("file,carrier", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("the carrier map must start with the header: file,carrier");
        }

        foreach (var line in lines.Skip(1))
        {
            var comma = line.IndexOf(',', StringComparison.Ordinal);
            if (comma > 0)
            {
                map[line[..comma].Trim().Replace('\\', '/')] = line[(comma + 1)..].Trim().Trim('"');
            }
        }

        return map;
    }

    /// <summary>Carrier × input type counts, unknown carriers, skipped files, and strata too small to split across tuning and blind.</summary>
    public static string Summary(CorpusManifest manifest, IReadOnlyList<ImportedFile> files)
    {
        var sb = new StringBuilder();
        var types = manifest.Entries.Select(e => e.InputType).Distinct().Order(StringComparer.Ordinal).ToList();
        sb.AppendLine(CultureInfo.InvariantCulture, $"imported {manifest.Entries.Count} file(s), {manifest.Entries.Sum(e => e.Pages)} page(s), {manifest.Entries.Sum(e => e.Bytes) / 1024.0 / 1024:0.0} MB; skipped {files.Count(f => f.Skipped is not null)}");
        sb.AppendLine();
        sb.AppendLine($"| carrier | {string.Join(" | ", types)} | total |");
        sb.AppendLine($"|---|{string.Concat(types.Select(_ => "---:|"))}---:|");
        foreach (var g in manifest.Entries.GroupBy(e => e.Carrier).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {g.Key} | {string.Join(" | ", types.Select(t => g.Count(e => e.InputType == t)))} | {g.Count()} |");
        }

        var thin = manifest.Entries.GroupBy(e => (e.Carrier, e.InputType)).Where(g => g.Count() < 2).Select(g => $"{g.Key.Carrier} × {g.Key.InputType}").ToList();
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"strata with fewer than 2 files (cannot appear on both the tuning and the blind side): {(thin.Count == 0 ? "none" : string.Join("; ", thin))}");
        var unknown = manifest.Entries.Count(e => e.Carrier == "unknown"); // generic-guard: allow (the import's placeholder, not a carrier)
        if (unknown > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"carrier unknown for {unknown} file(s): add them to the carrier map (file,carrier) or put them in a carrier sub-folder");
        }

        var packages = manifest.Entries.Where(e => e.Members is { Count: > 0 }).ToList();
        if (packages.Count > 0)
        {
            var inside = packages.SelectMany(p => p.Members!).GroupBy(m => m.InputType).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"packages (stratum bundle): {packages.Count}, containing {string.Join(", ", inside)}");
        }

        var blind = manifest.Entries.Count - 100;
        sb.AppendLine(CultureInfo.InvariantCulture, $"with a 100-file tuning set the blind set would hold {Math.Max(0, blind)} file(s) (protocol: 300–500)");
        foreach (var f in files.Where(f => f.Skipped is not null))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"skipped {f.File}: {f.Skipped}");
        }

        return sb.ToString();
    }
}
