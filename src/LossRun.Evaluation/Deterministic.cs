using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LossRun.Evaluation;

/// <summary>
/// Makes generated files byte-identical for the same seed (brief 08: "generated deterministically"). PDFsharp writes a
/// random font-subset tag, the wall-clock creation date and a random trailer /ID; System.IO.Packaging names the xlsx core
/// properties part with a random GUID and stamps zip entries with the current time. PDF fixes are same-length byte
/// substitutions, so xref offsets stay valid. Encrypted PDFs are left as they are: their key derives from /ID.
/// </summary>
public static partial class Deterministic
{
    private const string FixedPdfDate = "D:20260101000000+00'00'";
    private static readonly DateTimeOffset FixedZipTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static byte[] Pdf(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        if (text.Contains("/Encrypt", StringComparison.Ordinal))
        {
            return pdf;
        }

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        text = SubsetTag().Replace(text, m =>
        {
            if (!tags.TryGetValue(m.Groups[1].Value, out var tag))
            {
                var n = tags.Count;
                tag = tags[m.Groups[1].Value] = new string([.. Enumerable.Range(0, 6).Select(i => (char)('A' + (n / (int)Math.Pow(26, 5 - i) % 26)))]);
            }

            return $"/{tag}+";
        });
        text = PdfDate().Replace(text, m => m.Groups[1].Value + "(" + Fit(m.Groups[2].Value) + ")");
        text = XmpDate().Replace(text, m => m.Groups[1].Value + Zero(m.Groups[2].Value) + "<");
        var uuids = 0;
        text = XmpUuid().Replace(text, m => m.Groups[1].Value + $"{++uuids:x8}-0000-0000-0000-000000000000"[..m.Groups[2].Value.Length] + "<");

        // /ID from a hash of everything else, so it still changes when the content does.
        var withoutId = TrailerId().Replace(text, "");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.Latin1.GetBytes(withoutId)));
        text = TrailerId().Replace(text, m => $"{m.Groups[1].Value}<{Hex(hash, m.Groups[2].Value.Length)}>{m.Groups[3].Value}<{Hex(hash, m.Groups[4].Value.Length)}>{m.Groups[5].Value}");
        return Encoding.Latin1.GetBytes(text);

        static string Fit(string date) => date.Length == FixedPdfDate.Length ? FixedPdfDate : new string([.. date.Select(c => char.IsAsciiDigit(c) ? '0' : c)]);
        static string Zero(string date) => new([.. date.Select(c => char.IsAsciiDigit(c) ? '0' : c)]);
        static string Hex(string hash, int length) => string.Concat(Enumerable.Repeat(hash, length / hash.Length + 1))[..length];
    }

    /// <summary>Rewrites the package with a fixed core-properties part name and fixed entry times.</summary>
    public static byte[] Xlsx(byte[] xlsx)
    {
        using var input = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
        var renames = input.Entries.Where(e => e.FullName.EndsWith(".psmdcp", StringComparison.Ordinal))
            .ToDictionary(e => e.FullName, e => e.FullName[..(e.FullName.LastIndexOf('/') + 1)] + "core.psmdcp", StringComparer.Ordinal);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in input.Entries)
            {
                using var source = entry.Open();
                using var buffer = new MemoryStream();
                source.CopyTo(buffer);
                var bytes = buffer.ToArray();
                if (entry.FullName == "_rels/.rels")
                {
                    // Package-level relationships (never referenced by id from a part): random "R…" ids become sequential.
                    var rels = renames.Aggregate(Encoding.UTF8.GetString(bytes), (s, r) => s.Replace(r.Key, r.Value, StringComparison.Ordinal));
                    var n = 0;
                    bytes = Encoding.UTF8.GetBytes(RandomRelationshipId().Replace(rels, _ => $"Id=\"Rpkg{++n}\""));
                }
                else if (entry.FullName.EndsWith("core.xml", StringComparison.Ordinal) || renames.ContainsKey(entry.FullName))
                {
                    bytes = Encoding.UTF8.GetBytes(CoreTimestamp().Replace(Encoding.UTF8.GetString(bytes), m => $"{m.Groups[1].Value}2026-01-01T00:00:00Z<"));
                }

                var target = zip.CreateEntry(renames.GetValueOrDefault(entry.FullName, entry.FullName), CompressionLevel.Optimal);
                target.LastWriteTime = FixedZipTime;
                using var sink = target.Open();
                sink.Write(bytes);
            }
        }

        return output.ToArray();
    }

    [GeneratedRegex(@"/([A-Z]{6})\+")]
    private static partial Regex SubsetTag();

    [GeneratedRegex(@"(/(?:CreationDate|ModDate)\s*)\(([^)]*)\)")]
    private static partial Regex PdfDate();

    [GeneratedRegex(@"(<xmp:(?:CreateDate|ModifyDate|MetadataDate)>)([^<]*)<")]
    private static partial Regex XmpDate();

    [GeneratedRegex(@"(<xmpMM:(?:DocumentID|InstanceID)>uuid:)([^<]*)<")]
    private static partial Regex XmpUuid();

    [GeneratedRegex(@"(/ID\s*\[\s*)<([0-9A-Fa-f]+)>(\s*)<([0-9A-Fa-f]+)>(\s*\])")]
    private static partial Regex TrailerId();

    [GeneratedRegex(@"Id=""R[0-9A-Fa-f]{8,}""")]
    private static partial Regex RandomRelationshipId();

    [GeneratedRegex(@"(<dcterms:(?:created|modified)[^>]*>)[^<]*<")]
    private static partial Regex CoreTimestamp();
}
