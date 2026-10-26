using LossRun.Contracts;

namespace LossRun.Formats.Replay;

/// <summary>A line as printed on the page, rebuilt from the OCR words.</summary>
/// <param name="Parts">The line split at gaps wider than two word heights (values printed apart on one line).</param>
public sealed record PrintedLine(int Page, double Top, string Text, IReadOnlyList<double> Polygon, double Confidence, IReadOnlyList<string> Parts);

/// <summary>
/// The printed lines of a PDF, rebuilt from its OCR words (brief 20). Content Understanding merges consecutive printed
/// lines into one paragraph ("Workers' Compensation Loss Run Insured: …") and differently from one document of a layout
/// to the next, so a document-field regex anchored on a printed label ("^Insured:") is matched against the printed lines
/// as well as the paragraphs. Deterministic: same graph, same lines.
/// </summary>
public static class PrintedText
{
    public static List<PrintedLine> Lines(DocumentGraph graph, Func<GraphWord, bool>? include = null)
    {
        var lines = new List<PrintedLine>();
        var words = graph.Words.Where(w => w.Polygon is { Count: >= 8 } && w.Text.Trim().Length > 0 && (include is null || include(w)))
            .Select(w => (Word: w, Box: Geometry.Box(w.Polygon!)));
        foreach (var page in words.GroupBy(w => w.Word.Page).OrderBy(g => g.Key))
        {
            var ordered = page.OrderBy(w => w.Box.MinY).ToList();
            var height = ordered.Select(w => w.Box.MaxY - w.Box.MinY).Order().ElementAt(ordered.Count / 2);
            var clusters = new List<List<(GraphWord Word, (double MinX, double MinY, double MaxX, double MaxY) Box)>>();
            foreach (var word in ordered)
            {
                if (clusters.Count == 0 || Math.Abs((word.Box.MinY + word.Box.MaxY) / 2 - (clusters[^1][0].Box.MinY + clusters[^1][0].Box.MaxY) / 2) > height * 0.5)
                {
                    clusters.Add([]);
                }

                clusters[^1].Add(word);
            }

            foreach (var cluster in clusters)
            {
                var byX = cluster.OrderBy(w => w.Box.MinX).ToList();
                var (x1, y1, x2, y2) = (byX.Min(w => w.Box.MinX), byX.Min(w => w.Box.MinY), byX.Max(w => w.Box.MaxX), byX.Max(w => w.Box.MaxY));
                var parts = new List<List<GraphWord>> { new() };
                for (var i = 0; i < byX.Count; i++)
                {
                    if (i > 0 && byX[i].Box.MinX - byX[i - 1].Box.MaxX > height * 2)
                    {
                        parts.Add([]);
                    }

                    parts[^1].Add(byX[i].Word);
                }

                lines.Add(new PrintedLine(page.Key, y1, JoinWords([.. byX.Select(w => w.Word)]), Geometry.Rectangle(x1, y1, x2, y2), WordConfidence.Min(byX.Select(w => w.Word)),
                    [.. parts.Select(JoinWords)]));
            }
        }

        return lines;
    }

    /// <summary>
    /// OCR words as printed: words adjacent in the text span join without a space (OCR splits "SPC-23-00001" into
    /// "SPC", "-", "23-00001" and "Type:" into "Type", ":").
    /// </summary>
    public static string JoinWords(IReadOnlyList<GraphWord> words)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0 && !(words[i].SpanOffset is { } offset && words[i - 1].SpanOffset is { } previous && offset == previous + words[i - 1].Text.Length))
            {
                sb.Append(' ');
            }

            sb.Append(words[i].Text);
        }

        return sb.ToString();
    }
}
