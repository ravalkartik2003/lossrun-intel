using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;
using PdfSharpDocument = PdfSharp.Pdf.PdfDocument;
using PdfSharpReader = PdfSharp.Pdf.IO.PdfReader;
using PdfSharpOpenMode = PdfSharp.Pdf.IO.PdfDocumentOpenMode;

namespace LossRun.Ingestion;

/// <param name="Bytes">Unencrypted PDF bytes ready for Content Understanding; null when the file could not be opened.</param>
/// <param name="WasEncrypted">The input was encrypted (user and/or owner password).</param>
/// <param name="PageCount">Page count read by PdfPig.</param>
/// <param name="Error">Why the file could not be opened (e.g. no configured password matched).</param>
public sealed record PdfUnlockResult(byte[]? Bytes, bool WasEncrypted, int PageCount, string? Error)
{
    public bool Ok => Bytes is not null;
}

/// <summary>
/// Opens PDFs with PdfPig, trying the configured passwords, and returns an unencrypted copy (Content Understanding
/// and Document Intelligence reject locked PDFs). PdfPig reads but does not write, so the decrypted copy is produced
/// by importing the pages into a new document with PDFsharp using the password PdfPig accepted.
/// </summary>
public static class PdfUnlocker
{
    public static PdfUnlockResult Unlock(byte[] pdf, IReadOnlyList<string> passwords)
    {
        foreach (var password in new[] { "" }.Concat(passwords))
        {
            try
            {
                using var document = PdfDocument.Open(pdf, new ParsingOptions { Password = password });
                if (!document.IsEncrypted)
                {
                    return new PdfUnlockResult(pdf, false, document.NumberOfPages, null);
                }

                return new PdfUnlockResult(Decrypt(pdf, password), true, document.NumberOfPages, null);
            }
            catch (PdfDocumentEncryptedException)
            {
                // wrong or missing password: try the next one
            }
        }

        return new PdfUnlockResult(null, true, 0, passwords.Count == 0
            ? "PDF is password-protected and no passwords are configured."
            : $"PDF is password-protected and none of the {passwords.Count} configured passwords opened it.");
    }

    private static byte[] Decrypt(byte[] pdf, string password)
    {
        using var input = new MemoryStream(pdf);
        using var source = PdfSharpReader.Open(input, password, PdfSharpOpenMode.Import);
        using var output = new PdfSharpDocument();
        foreach (var page in source.Pages)
        {
            output.AddPage(page);
        }

        using var stream = new MemoryStream();
        output.Save(stream, closeStream: false);
        return stream.ToArray();
    }
}
