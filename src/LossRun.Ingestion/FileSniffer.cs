using System.IO.Compression;
using LossRun.Contracts;

namespace LossRun.Ingestion;

/// <summary>Identifies a file by magic bytes, using the extension only to disambiguate containers and text.</summary>
public static class FileSniffer
{
    private static readonly byte[] OleSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];

    public static FileKind Sniff(byte[] bytes, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        ReadOnlySpan<byte> head = bytes.AsSpan(0, Math.Min(bytes.Length, 16));

        if (head.StartsWith("%PDF"u8))
        {
            return FileKind.Pdf;
        }

        if (head.StartsWith("PK\u0003\u0004"u8))
        {
            return SniffZipContainer(bytes);
        }

        if (head.StartsWith(OleSignature))
        {
            // OLE compound file: legacy .xls, or an encrypted .xlsx/.xlsb (EncryptedPackage).
            return ext switch
            {
                ".xlsx" => FileKind.Xlsx,
                ".xlsb" => FileKind.Xlsb,
                ".doc" => FileKind.Unknown,
                _ => FileKind.Xls,
            };
        }

        if (head.StartsWith(PngSignature))
        {
            return FileKind.Png;
        }

        if (head.StartsWith(JpegSignature))
        {
            return FileKind.Jpeg;
        }

        if (head.StartsWith("II*\0"u8) || head.StartsWith("MM\0*"u8))
        {
            return FileKind.Tiff;
        }

        if (head.StartsWith("BM"u8) && bytes.Length > 14)
        {
            return FileKind.Bmp;
        }

        if (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8)
            && (head[8..12].SequenceEqual("heic"u8) || head[8..12].SequenceEqual("heif"u8) || head[8..12].SequenceEqual("mif1"u8)))
        {
            return FileKind.Heif;
        }

        if (ext is ".csv" or ".tsv" && LooksLikeText(bytes))
        {
            return FileKind.Csv;
        }

        return FileKind.Unknown;
    }

    public static string ContentType(FileKind kind) => kind switch
    {
        FileKind.Pdf => "application/pdf",
        FileKind.Png => "image/png",
        FileKind.Jpeg => "image/jpeg",
        FileKind.Tiff => "image/tiff",
        FileKind.Bmp => "image/bmp",
        FileKind.Heif => "image/heif",
        _ => "application/octet-stream",
    };

    public static bool IsSpreadsheet(FileKind kind) => kind is FileKind.Xlsx or FileKind.Xls or FileKind.Xlsb or FileKind.Csv;

    public static bool IsImage(FileKind kind) => kind is FileKind.Png or FileKind.Jpeg or FileKind.Tiff or FileKind.Bmp or FileKind.Heif;

    private static FileKind SniffZipContainer(byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (names.Contains("[Content_Types].xml"))
            {
                if (names.Contains("xl/workbook.xml"))
                {
                    return FileKind.Xlsx;
                }

                if (names.Contains("xl/workbook.bin"))
                {
                    return FileKind.Xlsb;
                }

                if (names.Contains("word/document.xml"))
                {
                    return FileKind.Docx;
                }
            }

            return FileKind.Zip;
        }
        catch (InvalidDataException)
        {
            return FileKind.Unknown;
        }
    }

    private static bool LooksLikeText(byte[] bytes)
    {
        var sample = bytes.AsSpan(0, Math.Min(bytes.Length, 4096));
        return !sample.Contains((byte)0);
    }
}
