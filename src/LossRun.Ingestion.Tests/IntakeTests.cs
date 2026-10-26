using System.IO.Compression;
using LossRun.Contracts;
using LossRun.Ingestion;

namespace LossRun.Ingestion.Tests;

public class IntakeTests
{
    [Theory]
    [InlineData("lossrun-native.pdf", FileKind.Pdf)]
    [InlineData("lossrun-scanned.pdf", FileKind.Pdf)]
    [InlineData("lossrun-locked.pdf", FileKind.Pdf)]
    [InlineData("bundle.pdf", FileKind.Pdf)]
    [InlineData("lossrun-stacked.xlsx", FileKind.Xlsx)]
    [InlineData("lossrun-export.csv", FileKind.Csv)]
    public void Fixtures_sniff_by_magic_bytes(string name, FileKind expected)
    {
        Assert.Equal(expected, FileSniffer.Sniff(Fixture.Bytes(name), name));
    }

    [Fact]
    public void Magic_bytes_win_over_misleading_extensions()
    {
        Assert.Equal(FileKind.Pdf, FileSniffer.Sniff(Fixture.Bytes("lossrun-native.pdf"), "renamed.xlsx"));
        Assert.Equal(FileKind.Xlsx, FileSniffer.Sniff(Fixture.Bytes("lossrun-stacked.xlsx"), "report.zip"));
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "a.png", FileKind.Png)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 }, "a.jpg", FileKind.Jpeg)]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00, 0, 0 }, "a.tif", FileKind.Tiff)]
    [InlineData(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, "old.xls", FileKind.Xls)]
    [InlineData(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, "locked.xlsx", FileKind.Xlsx)]
    [InlineData(new byte[] { 0x00, 0x01, 0x02, 0x03 }, "mystery.bin", FileKind.Unknown)]
    [InlineData(new byte[] { 0x41, 0x2C, 0x42, 0x0D, 0x0A }, "data.txt", FileKind.Unknown)]
    public void Other_signatures_sniff(byte[] head, string name, FileKind expected)
    {
        var bytes = head.Concat(new byte[32]).ToArray();
        Assert.Equal(expected, FileSniffer.Sniff(bytes, name));
    }

    [Fact]
    public void Nested_zip_bundles_expand_with_their_paths()
    {
        var inner = Zip(("inner/export.csv", Fixture.Bytes("lossrun-export.csv")));
        var outer = Zip(("bundle/lossrun.xlsx", Fixture.Bytes("lossrun-stacked.xlsx")), ("bundle/nested.zip", inner), ("__MACOSX/._junk", [1, 2]));

        var entries = ArchiveExpander.Expand(outer, maxDepth: 3, maxTotalBytes: 10_000_000);

        Assert.Equal(["bundle/lossrun.xlsx", "bundle/nested.zip!inner/export.csv"], entries.Select(e => e.Path).Order());
    }

    [Fact]
    public void Archive_size_guard_refuses_bombs()
    {
        var zip = Zip(("big.bin", new byte[2_000_000]));

        Assert.Throws<InvalidDataException>(() => ArchiveExpander.Expand(zip, 3, maxTotalBytes: 1_000_000));
    }

    [Fact]
    public async Task Unsupported_members_are_reported_not_dropped()
    {
        var harness = new Harness();
        var zip = Zip(("export.csv", Fixture.Bytes("lossrun-export.csv")), ("notes.bin", [0, 1, 2, 3, 4]));

        var result = await harness.Pipeline().IngestAsync("upload.zip", zip);

        var graph = Assert.Single(result.Documents);
        Assert.Equal("export.csv", graph.SourceFile.FileName);
        Assert.Equal("export.csv", graph.SourceFile.ArchivePath);
        Assert.NotNull(graph.SourceFile.ParentDocumentId);
        var unsupported = Assert.Single(result.Unsupported);
        Assert.Equal(("notes.bin", FileKind.Unknown), (unsupported.FileName, unsupported.Kind));
        Assert.Empty(harness.Cu.Calls);
    }

    [Fact]
    public void Unencrypted_pdfs_pass_through()
    {
        var bytes = Fixture.Bytes("lossrun-native.pdf");

        var result = PdfUnlocker.Unlock(bytes, []);

        Assert.True(result.Ok);
        Assert.False(result.WasEncrypted);
        Assert.Same(bytes, result.Bytes);
        Assert.Equal(1, result.PageCount);
    }

    [Fact]
    public void Locked_pdf_without_a_matching_password_is_reported()
    {
        var result = PdfUnlocker.Unlock(Fixture.Bytes("lossrun-locked.pdf"), ["nope"]);

        Assert.False(result.Ok);
        Assert.True(result.WasEncrypted);
        Assert.Contains("none of the 1 configured passwords", result.Error);
    }

    [Fact]
    public void Locked_pdf_unlocks_to_an_unencrypted_copy()
    {
        var result = PdfUnlocker.Unlock(Fixture.Bytes("lossrun-locked.pdf"), ["wrong", Fixture.Password]);

        Assert.True(result.Ok);
        Assert.True(result.WasEncrypted);
        using var reopened = UglyToad.PdfPig.PdfDocument.Open(result.Bytes!); // no password needed any more
        Assert.False(reopened.IsEncrypted);
        Assert.Contains("Synthetic Mutual Insurance Co.", reopened.GetPage(1).Text);
    }

    [Fact]
    public async Task Pipeline_records_was_encrypted_and_sends_the_decrypted_copy()
    {
        var harness = new Harness();

        var result = await harness.Pipeline(new IngestionOptions { Passwords = [Fixture.Password] }).IngestAsync("locked.pdf", Fixture.Bytes("lossrun-locked.pdf"));

        Assert.True(Assert.Single(result.Documents).SourceFile.WasEncrypted);
        var call = Assert.Single(harness.Cu.Calls);
        Assert.Equal(("prebuilt-layout", "application/pdf"), (call.AnalyzerId, call.ContentType));
    }

    private static byte[] Zip(params (string Name, byte[] Bytes)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in files)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(bytes);
            }
        }

        return stream.ToArray();
    }
}
