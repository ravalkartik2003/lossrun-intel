using LossRun.Contracts;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace LossRun.Ingestion;

/// <param name="IsSingleLossRun">True when the classifier can be skipped.</param>
/// <param name="HeaderResolution">Best glossary resolution ratio of a page-1 line (0 when no text layer).</param>
/// <param name="Reason">Why the check passed or failed (shown in the run log).</param>
public sealed record FirstPageCheckResult(bool IsSingleLossRun, double HeaderResolution, string Reason);

/// <summary>
/// Free pre-classification check on a PDF's text layer (brief 04 Answers 3): skip the CU classifier when page 1 has a
/// line whose cells resolve against the glossary at ≥ the threshold (a loss-run table header) AND every later page looks
/// like a continuation (same title line as page 1, or its own loss-run header line). Scanned PDFs have no text layer,
/// so they always fail the check and go to the classifier.
/// </summary>
public static class FirstPageCheck
{
    private const int MinHeaderCells = 4;
    private const double CellGapPoints = 6.0;

    /// <summary>Whether page 1 carries a text layer (letters a PDF reader can extract); a scan has none.</summary>
    public static bool HasTextLayer(byte[] pdf)
    {
        try
        {
            using var document = PdfDocument.Open(pdf);
            return document.NumberOfPages > 0 && document.GetPage(1).Letters.Count(l => !string.IsNullOrWhiteSpace(l.Value)) >= 20;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException or UglyToad.PdfPig.Core.PdfDocumentFormatException)
        {
            return false;
        }
    }

    /// <summary>Resolution of the largest image on page 1 (pixels across ÷ placed width in inches); null when page 1 has none.</summary>
    public static int? ScanDpi(byte[] pdf)
    {
        try
        {
            using var document = PdfDocument.Open(pdf);
            var image = document.NumberOfPages == 0 ? null : document.GetPage(1).GetImages().MaxBy(i => i.BoundingBox.Width * i.BoundingBox.Height);
            return image is null || image.BoundingBox.Width <= 0 ? null : (int)Math.Round(image.WidthInSamples / (image.BoundingBox.Width / 72.0));
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException or UglyToad.PdfPig.Core.PdfDocumentFormatException)
        {
            return null;
        }
    }

    public static FirstPageCheckResult Evaluate(byte[] pdf, Glossary glossary, double threshold = 0.8)
    {
        using var document = PdfDocument.Open(pdf);
        var first = Lines(document.GetPage(1));
        if (first.Count == 0)
        {
            return new(false, 0, "no text layer on page 1 (scanned)");
        }

        var best = first.Select(line => Resolution(line, glossary)).DefaultIfEmpty(0).Max();
        if (best < threshold)
        {
            return new(false, best, $"page 1 header resolution {best:0.00} < {threshold:0.00}");
        }

        var title = Normalize(first[0]);
        for (var p = 2; p <= document.NumberOfPages; p++)
        {
            var lines = Lines(document.GetPage(p));
            var continuation = lines.Count > 0 && (Normalize(lines[0]) == title || lines.Any(l => Resolution(l, glossary) >= threshold));
            if (!continuation)
            {
                return new(false, best, $"page {p} does not continue the page-1 loss run (second logical document)");
            }
        }

        return new(true, best, $"page 1 header resolution {best:0.00}; {document.NumberOfPages} page(s) form one loss run");
    }

    /// <summary>Words clustered into lines (top to bottom) by baseline; each line split into cells at large x-gaps.</summary>
    private static List<List<string>> Lines(Page page)
    {
        var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (words.Count == 0)
        {
            return [];
        }

        var height = words.Select(w => w.BoundingBox.Height).Order().ElementAt(words.Count / 2);
        var lines = new List<List<Word>>();
        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
        {
            var line = lines.LastOrDefault();
            if (line is not null && Math.Abs(line[0].BoundingBox.Bottom - word.BoundingBox.Bottom) <= height * 0.5)
            {
                line.Add(word);
            }
            else
            {
                lines.Add([word]);
            }
        }

        return lines.Select(line =>
        {
            var cells = new List<string>();
            var current = "";
            double? right = null;
            foreach (var word in line.OrderBy(w => w.BoundingBox.Left))
            {
                if (right is not null && word.BoundingBox.Left - right > CellGapPoints)
                {
                    cells.Add(current);
                    current = "";
                }

                current = current.Length == 0 ? word.Text : $"{current} {word.Text}";
                right = word.BoundingBox.Right;
            }

            cells.Add(current);
            return cells;
        }).ToList();
    }

    private static double Resolution(List<string> cells, Glossary glossary) =>
        cells.Count < MinHeaderCells ? 0 : (double)cells.Count(c => glossary.ResolveHeader(c) is not null) / cells.Count;

    private static string Normalize(List<string> line) => CanonicalComparer.NormalizeText(string.Join(" ", line));
}
