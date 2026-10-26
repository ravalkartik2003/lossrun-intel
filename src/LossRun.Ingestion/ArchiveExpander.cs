using System.IO.Compression;

namespace LossRun.Ingestion;

/// <param name="Path">Entry path inside the archive (nested archives joined with '!').</param>
public sealed record ArchiveEntry(string Path, byte[] Bytes);

/// <summary>Expands ZIP bundles (nested up to a depth), with a total-size guard against archive bombs.</summary>
public static class ArchiveExpander
{
    public static IReadOnlyList<ArchiveEntry> Expand(byte[] zipBytes, int maxDepth, long maxTotalBytes)
    {
        var entries = new List<ArchiveEntry>();
        long total = 0;
        Walk(zipBytes, "", 1);
        return entries;

        void Walk(byte[] bytes, string prefix, int depth)
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal) || entry.Name.StartsWith("._", StringComparison.Ordinal))
                {
                    continue;
                }

                total += entry.Length;
                if (total > maxTotalBytes)
                {
                    throw new InvalidDataException($"Archive expands beyond {maxTotalBytes:N0} bytes; refusing to continue.");
                }

                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var content = buffer.ToArray();
                var path = prefix + entry.FullName;
                if (depth < maxDepth && FileSniffer.Sniff(content, entry.Name) == LossRun.Contracts.FileKind.Zip)
                {
                    Walk(content, path + "!", depth + 1);
                }
                else
                {
                    entries.Add(new ArchiveEntry(path, content));
                }
            }
        }
    }
}
