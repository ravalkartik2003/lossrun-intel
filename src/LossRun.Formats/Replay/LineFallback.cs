using LossRun.Contracts;

namespace LossRun.Formats.Replay;

/// <summary>
/// Rebuilds table rows from OCR words when Content Understanding returned no table for a block (brief 04 Answers 2):
/// words are deskewed by the page angle, clustered into lines by their centre y, the header line is found from the
/// format's column headers, and every data word goes to the rightmost column whose header starts at or left of the
/// word's centre (printed headers are left-aligned in their cells; values may be right-aligned).
/// </summary>
internal static class LineFallbackReader
{
    private const double ColumnSlack = 0.05;

    private sealed record PlacedWord(GraphWord Word, double X1, double Y1, double X2, double Y2)
    {
        public double CenterX => (X1 + X2) / 2;
        public double CenterY => (Y1 + Y2) / 2;
        public string Norm { get; } = CanonicalComparer.NormalizeText(Word.Text);
    }

    /// <summary>
    /// Rows of the block between <paramref name="startAnchor"/> and <paramref name="endAnchor"/> (next block's anchor, or
    /// null for the page end), or empty when no header line is found. The rotation is estimated from all the page's
    /// words and the block bounds are applied after rotating the anchors too: on a skewed page a row's right end can
    /// sit below the next anchor's unrotated top.
    /// </summary>
    public static List<SourceRow> Read(GraphPage page, IReadOnlyList<GraphWord> words, IReadOnlyList<double> startAnchor, IReadOnlyList<double>? endAnchor,
        IReadOnlyList<string> headers, Formats.LineFallback settings)
    {
        var pageWords = words.Where(w => w.Polygon is { Count: >= 8 }).ToList();
        if (pageWords.Count == 0)
        {
            return [];
        }

        // Deskew. CU's page angle is a hint, not ground truth: on the 2°-rotated fixture it reported 1.51°, and the
        // remaining 0.5° split a 10-inch header row into two "lines". So search rotations ourselves: CU's ±angle plus a
        // 0.1° grid over ±3°, keeping the one with the fewest lines, then the tightest lines (sum of in-line y spread).
        // Candidates are evaluated in a fixed order and ties keep the first, so the choice is deterministic.
        var angles = new List<double> { 0.0 };
        if (settings.Deskew)
        {
            if (page.Angle is { } a && Math.Abs(a) > 0.05)
            {
                angles.InsertRange(0, [-a, a]);
            }

            angles.AddRange(Enumerable.Range(-30, 61).Select(i => i / 10.0).Where(x => x != 0));
        }

        var chosen = 0.0;
        var best = (Count: int.MaxValue, Spread: double.MaxValue);
        foreach (var angle in angles)
        {
            var candidate = Cluster(Place(pageWords, page, angle), settings.LineTolerance);
            var score = (candidate.Count, Spread: Math.Round(candidate.Sum(l => l.Max(w => w.CenterY) - l.Min(w => w.CenterY)), 6));
            if (score.Count < best.Count || (score.Count == best.Count && score.Spread < best.Spread))
            {
                (chosen, best) = (angle, score);
            }
        }

        var top = Rotate(startAnchor, page, chosen).MaxY;
        var bottom = endAnchor is null ? double.MaxValue : Rotate(endAnchor, page, chosen).MinY;
        var region = Place(pageWords, page, chosen).Where(w => w.CenterY > top && w.CenterY < bottom).ToList();
        if (region.Count == 0)
        {
            return [];
        }

        var lines = Cluster(region, settings.LineTolerance);

        var normalizedHeaders = headers.Select(h => (Header: h, Tokens: CanonicalComparer.NormalizeText(h).Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Where(h => h.Tokens.Length > 0)
            .OrderByDescending(h => h.Tokens.Length)
            .ThenBy(h => h.Header, StringComparer.Ordinal)
            .ToList();

        for (var li = 0; li < lines!.Count; li++)
        {
            var columns = MatchHeader(lines[li], normalizedHeaders);
            if (columns.Count < settings.MinHeaderMatch * normalizedHeaders.Count)
            {
                continue;
            }

            var ordered = columns.OrderBy(c => c.Left).ToList();
            return lines.Skip(li + 1).Select(line => ToRow(line, ordered)).Where(r => r.Cells.Count > 0).ToList();
        }

        return [];
    }

    private static (double MinY, double MaxY) Rotate(IReadOnlyList<double> polygon, GraphPage page, double angleDegrees)
    {
        var placed = Place([new GraphWord { Polygon = [.. polygon] }], page, angleDegrees)[0];
        return (placed.Y1, placed.Y2);
    }

    private static List<PlacedWord> Place(List<GraphWord> words, GraphPage page, double angleDegrees)
    {
        var cx = (page.Width ?? 0) / 2;
        var cy = (page.Height ?? 0) / 2;
        var theta = angleDegrees * Math.PI / 180;
        var cos = Math.Cos(theta);
        var sin = Math.Sin(theta);
        return words.Select(w =>
        {
            var p = w.Polygon!;
            var xs = new double[4];
            var ys = new double[4];
            for (var i = 0; i < 4; i++)
            {
                var x = p[2 * i] - cx;
                var y = p[2 * i + 1] - cy;
                xs[i] = cx + x * cos - y * sin;
                ys[i] = cy + x * sin + y * cos;
            }

            return new PlacedWord(w, xs.Min(), ys.Min(), xs.Max(), ys.Max());
        }).ToList();
    }

    private static List<List<PlacedWord>> Cluster(List<PlacedWord> words, double tolerance)
    {
        var heights = words.Select(w => w.Y2 - w.Y1).Order().ToList();
        var limit = heights[heights.Count / 2] * tolerance;
        var lines = new List<List<PlacedWord>>();
        foreach (var word in words.OrderBy(w => w.CenterY).ThenBy(w => w.X1))
        {
            var line = lines.LastOrDefault();
            if (line is not null && Math.Abs(line.Average(w => w.CenterY) - word.CenterY) <= limit)
            {
                line.Add(word);
            }
            else
            {
                lines.Add([word]);
            }
        }

        return lines.Select(l => l.OrderBy(w => w.X1).ToList()).ToList();
    }

    private static List<(string Header, double Left)> MatchHeader(List<PlacedWord> line, List<(string Header, string[] Tokens)> headers)
    {
        var tokens = line.Where(w => w.Norm.Length > 0).ToList();
        var found = new List<(string, double)>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < tokens.Count;)
        {
            var matched = false;
            foreach (var (header, parts) in headers)
            {
                if (used.Contains(header) || i + parts.Length > tokens.Count)
                {
                    continue;
                }

                if (parts.Select((part, k) => tokens[i + k].Norm == part).All(x => x))
                {
                    found.Add((header, tokens[i].X1));
                    used.Add(header);
                    i += parts.Length;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                i++;
            }
        }

        return found;
    }

    private static SourceRow ToRow(List<PlacedWord> line, List<(string Header, double Left)> columns)
    {
        var byColumn = new SortedDictionary<int, List<PlacedWord>>();
        foreach (var word in line)
        {
            var index = 0;
            for (var c = 0; c < columns.Count; c++)
            {
                if (columns[c].Left <= word.CenterX + ColumnSlack)
                {
                    index = c;
                }
            }

            if (!byColumn.TryGetValue(index, out var list))
            {
                byColumn[index] = list = [];
            }

            list.Add(word);
        }

        var cells = new List<(string Key, SourceCell Cell)>();
        foreach (var (index, words) in byColumn)
        {
            var text = string.Join(" ", words.OrderBy(w => w.X1).Select(w => w.Word.Text));
            var boxes = words.Select(w => Geometry.Box(w.Word.Polygon!)).ToList();
            var polygon = Geometry.Rectangle(boxes.Min(b => b.MinX), boxes.Min(b => b.MinY), boxes.Max(b => b.MaxX), boxes.Max(b => b.MaxY));
            cells.Add((CanonicalComparer.NormalizeText(columns[index].Header),
                new SourceCell(text, words[0].Word.Page, polygon, null, null, WordConfidence.Min(words.Select(w => w.Word)))));
        }

        var row = new SourceRow { Text = string.Join(" ", cells.Select(c => c.Cell.Text)), Label = cells.FirstOrDefault().Cell, Origin = "lineFallback" };
        foreach (var (key, cell) in cells)
        {
            row.Cells[key] = cell;
        }

        return row;
    }
}
