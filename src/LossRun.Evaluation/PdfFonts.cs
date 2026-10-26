using PdfSharp.Fonts;

namespace LossRun.Evaluation;

/// <summary>
/// Fonts for the synthetic PDFs (corpus generator, chat eval). Windows: the system fonts, as before. Elsewhere PDFsharp has no
/// font source of its own, so Arial and Times New Roman resolve to their metric-compatible Liberation equivalents
/// (Debian/Ubuntu <c>fonts-liberation</c>, or a folder in <c>LOSSRUN_FONT_DIR</c>). Renderings stay deterministic per machine;
/// a corpus is sealed by its hashes wherever it was generated (docs/evaluation/scoring-protocol.md).
/// </summary>
public static class PdfFonts
{
    private static readonly Lock Gate = new();

    public static void Configure()
    {
        lock (Gate)
        {
            if (OperatingSystem.IsWindows())
            {
                GlobalFontSettings.UseWindowsFontsUnderWindows = true;
            }
            else if (GlobalFontSettings.FontResolver is not LiberationResolver)
            {
                GlobalFontSettings.FontResolver = new LiberationResolver();
            }
        }
    }

    private sealed class LiberationResolver : IFontResolver
    {
        private static readonly string[] Folders =
        [
            Environment.GetEnvironmentVariable("LOSSRUN_FONT_DIR") ?? "",
            "/usr/share/fonts/truetype/liberation", "/usr/share/fonts/truetype/liberation2", "/usr/share/fonts/liberation-sans",
            "/usr/share/fonts/liberation-serif", "/usr/share/fonts/liberation", "/opt/homebrew/share/fonts", "/usr/local/share/fonts",
        ];

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
        {
            var family = familyName.Contains("Times", StringComparison.OrdinalIgnoreCase) || familyName.Contains("Serif", StringComparison.OrdinalIgnoreCase)
                ? "LiberationSerif" : familyName.Contains("Courier", StringComparison.OrdinalIgnoreCase) || familyName.Contains("Mono", StringComparison.OrdinalIgnoreCase)
                    ? "LiberationMono" : "LiberationSans";
            var style = (bold, italic) switch { (true, true) => "BoldItalic", (true, false) => "Bold", (false, true) => "Italic", _ => "Regular" };
            return new FontResolverInfo($"{family}-{style}");
        }

        public byte[] GetFont(string faceName) =>
            Folders.Where(f => f.Length > 0).Select(f => Path.Combine(f, faceName + ".ttf")).FirstOrDefault(File.Exists) is { } path
                ? File.ReadAllBytes(path)
                : throw new FileNotFoundException($"Font {faceName}.ttf not found. Install the Liberation fonts (apt: fonts-liberation) or set LOSSRUN_FONT_DIR to a folder that has them.");
    }
}
