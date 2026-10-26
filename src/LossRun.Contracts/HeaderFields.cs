using System.Text.RegularExpressions;

namespace LossRun.Contracts;

/// <summary>A document-level value printed as "Label: value" in a report header or parameters block (brief 23 §1).</summary>
/// <param name="Field">Canonical path (<c>document.valuationDate</c>, <c>period.policyNumber</c>, …) or <see cref="HeaderFields.LossWindow"/>.</param>
/// <param name="Label">The printed label, as printed.</param>
/// <param name="Value">The printed value (for a window: "from – to").</param>
/// <param name="Printed">Brief 31 Answers 1: the printed text when <paramref name="Value"/> is a repair of it (the l→I name repair); null otherwise.</param>
public sealed record HeaderField(string Field, string Label, string Value, int Page, List<double>? Polygon, double Confidence, string? Printed = null);

/// <summary>
/// Brief 23 §1: document-level fields read from the printed word stream, line by line (OCR words clustered by height), the
/// same technique as the line-of-business labels (ADR 0020): "Valued as of 09/30/2024", "Insured: …", "Policy Number: …",
/// "Policy Period: 01/01/2024 – 01/01/2025", "Eff 1/1/24 Exp 1/1/25". A losses window ("Accident Date Range: … to …",
/// "Losses From: … To …") is recorded as <see cref="LossWindow"/>: it bounds the claims listed, it is not a policy period.
/// Labels come from the glossary's aliases of the fields (with <c>source</c>); a label must be followed by a colon or by the
/// value's own shape (a date after "As of"), so a column header ("Policy") is not read as a label. Used at ingestion
/// (evidence for the learn prompt) and by replay (locator <c>headerLabel</c>), so both read the same printed value.
/// </summary>
public static partial class HeaderFields
{
    public const string LossWindow = "report.lossWindow";

    /// <summary>The fields read from a header block, with their kind.</summary>
    public static readonly IReadOnlyDictionary<string, string> Kinds = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["document.valuationDate"] = "date", ["document.reportRunDate"] = "date", ["document.insuredName"] = "text", ["document.carrierName"] = "text",
        ["period.policyNumber"] = "id", ["period.effectiveDate"] = "window", ["period.expirationDate"] = "window",
    };

    /// <summary>Labels of a losses window: the claims' dates, not the policy term.</summary>
    public static readonly string[] LossWindowLabels =
        ["Accident Date Range", "Loss Date Range", "Date of Loss Range", "Dates of Loss", "Accident Dates", "Losses From", "Loss Dates From", "Accidents From"];

    /// <summary>Labels of a policy-period window ("Policy Period: d – d").</summary>
    public static readonly string[] PolicyWindowLabels = ["Policy Period", "Policy Term", "Coverage Period", "Policy Dates", "Term"];

    private const string DatePattern = @"(?:\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4}|\d{4}-\d{2}-\d{2}|[A-Z][a-z]{2,8}\.?\s+\d{1,2},?\s+\d{4})";

    /// <summary>A printed line: its text and its words with character offsets.</summary>
    public sealed record Line(int Page, string Text, IReadOnlyList<(GraphWord Word, int Start)> Words);

    /// <summary>The page's words clustered into printed lines (centre y within half the median word height), left to right.</summary>
    public static List<Line> Lines(DocumentGraph graph)
    {
        var lines = new List<Line>();
        foreach (var page in graph.Words.Where(w => w.Polygon is { Count: >= 8 } && w.Text.Trim().Length > 0).GroupBy(w => w.Page).OrderBy(g => g.Key))
        {
            var words = page.Select(w => (Word: w, Y: (w.Polygon![1] + w.Polygon[3] + w.Polygon[5] + w.Polygon[7]) / 4, H: Math.Abs(w.Polygon[7] - w.Polygon[1]), X: w.Polygon[0]))
                .OrderBy(w => w.Y).ToList();
            var median = words.Select(w => w.H).Order().ElementAt(words.Count / 2);
            var tolerance = Math.Max(median, 0.01) * 0.5;
            var current = new List<(GraphWord Word, double Y, double H, double X)>();
            void Flush()
            {
                if (current.Count == 0)
                {
                    return;
                }

                var ordered = current.OrderBy(w => w.X).ToList();
                var parts = new List<(GraphWord, int)>();
                var text = "";
                foreach (var w in ordered)
                {
                    text += text.Length == 0 ? "" : " ";
                    parts.Add((w.Word, text.Length));
                    text += w.Word.Text.Trim();
                }

                lines.Add(new Line(page.Key, text, parts));
                current.Clear();
            }

            foreach (var w in words)
            {
                if (current.Count > 0 && Math.Abs(w.Y - current.Average(c => c.Y)) > tolerance)
                {
                    Flush();
                }

                current.Add(w);
            }

            Flush();
        }

        return lines;
    }

    /// <summary>Every header field the graph prints, for the fields in <see cref="Kinds"/> and the losses window.</summary>
    public static List<HeaderField> Detect(DocumentGraph graph, Glossary glossary)
    {
        var found = new List<HeaderField>();
        var lines = Lines(graph);
        foreach (var label in LossWindowLabels)
        {
            if (Find(lines, label, LossWindow, "window") is { } window)
            {
                found.Add(window);
                break;
            }
        }

        foreach (var (field, kind) in Kinds)
        {
            if (field == "period.expirationDate" || found.Any(f => f.Field == field))
            {
                continue;
            }

            var labels = field == "period.effectiveDate" ? PolicyWindowLabels : LabelsOf(glossary, field);
            foreach (var label in labels)
            {
                // A period label printed with different values on several lines heads each period block (a multi-period run):
                // the periods come from the blocks, not from a header.
                if (field.StartsWith("period.", StringComparison.Ordinal) && FindAll(lines, label, field, kind).Select(h => h.Value).Distinct(StringComparer.Ordinal).Count() > 1)
                {
                    break;
                }

                if (Find(lines, label, field, kind) is { } hit && !(hit.Field == "period.effectiveDate" && found.Any(f => f.Field == LossWindow && f.Label == hit.Label)))
                {
                    found.Add(hit);
                    if (field == "period.effectiveDate" && Window(hit.Value) is { } w)
                    {
                        found[^1] = hit with { Value = w.From };
                        found.Add(hit with { Field = "period.expirationDate", Value = w.To });
                    }

                    break;
                }
            }
        }

        if (!found.Any(f => f.Field == "period.effectiveDate") && EffExp(lines) is { } pair)
        {
            found.AddRange(pair);
        }

        return found;
    }

    /// <summary>The pages that print a document header block: a valuation date, insured or carrier label with its value.</summary>
    public static HashSet<int> HeaderPages(DocumentGraph graph, Glossary glossary)
    {
        var lines = Lines(graph);
        var pages = new HashSet<int>();
        foreach (var field in new[] { "document.valuationDate", "document.insuredName", "document.carrierName" })
        {
            foreach (var label in LabelsOf(glossary, field))
            {
                pages.UnionWith(FindAll(lines, label, field, Kinds[field]).Select(h => h.Page));
            }
        }

        return pages;
    }

    /// <summary>Replay (locator <c>headerLabel</c>): the value printed after <paramref name="label"/> for a field of <paramref name="field"/>'s kind.</summary>
    public static HeaderField? Read(DocumentGraph graph, string label, string field)
    {
        var kind = field == HeaderFields.LossWindow ? "window" : Kinds.GetValueOrDefault(field, "text");
        var hit = Find(Lines(graph), label, field, kind);
        if (hit is not null && kind == "text" && graph.SourceFile.TextLayer != true && TitleCaseNameRepair(hit.Value) is var repaired && repaired != hit.Value)
        {
            hit = hit with { Value = repaired, Printed = hit.Value }; // brief 31: OCR read a name's capital I as l; the printed text is kept
        }

        if (hit is null || kind != "window" || field == LossWindow)
        {
            return hit;
        }

        return Window(hit.Value) is { } w ? hit with { Value = field == "period.expirationDate" ? w.To : w.From } : null;
    }

    /// <summary>
    /// Brief 31 (the all-fields study on corpus v5): OCR reads a sans-serif capital I as l. A header name printed in Title Case whose first word
    /// came back as "l" + lower case ("lota Staffing") is read with a capital I. Only the first word, only when every other word is capitalised
    /// (inside a name, "lite" or "lndustrial" cannot be told apart without a dictionary), only on a scan; the printed text stays in lineage.
    /// </summary>
    public static string TitleCaseNameRepair(string value)
    {
        var words = value.Split(' ');
        // The name's own words: a parenthetical ("(synthetic)", "( a division of … )") is not part of its Title Case.
        var letters = words.TakeWhile(w => !w.Contains('(', StringComparison.Ordinal)).Where(w => w.Length > 0 && char.IsLetter(w[0])).ToList();
        if (letters.Count < 2 || words[0].Length < 2 || words[0][0] != 'l' || !words[0][1..].All(char.IsLower)
            || !letters.Skip(1).All(w => char.IsUpper(w[0])))
        {
            return value;
        }

        words[0] = "I" + words[0][1..];
        return string.Join(' ', words);
    }

    private static IEnumerable<string> LabelsOf(Glossary glossary, string field) =>
        glossary.Fields.TryGetValue(field, out var entry) ? entry.Aliases.Select(a => a.Header).Where(h => h.Length >= 2).OrderByDescending(h => h.Length) : [];

    private static HeaderField? Find(List<Line> lines, string label, string field, string kind) => FindAll(lines, label, field, kind).FirstOrDefault();

    private static IEnumerable<HeaderField> FindAll(List<Line> lines, string label, string field, string kind)
    {
        // A label is printed at a word boundary and followed by a colon (or "#"), or — for "as of" style date labels — directly by a date.
        var labelPattern = Regex.Escape(label).Replace(@"\ ", @"\s+", StringComparison.Ordinal);
        var value = kind switch
        {
            "date" => $"(?<value>{DatePattern})",
            "window" => $@"(?<value>{DatePattern}\s*(?:-|–|—|to|thru|through)\s*{DatePattern})",
            "id" => @"(?<value>[A-Za-z0-9][A-Za-z0-9/-]*\d[A-Za-z0-9/-]*)",
            // Brief 31: a colon ends a text value too (on a skewed scan the next line's ": 03/04/2024" lands on this one).
            _ => @"(?<value>\S[^;:]*?)(?=\s{2,}|\s+[A-Z][A-Za-z .]{1,24}:|\s*:|\s*[.;](?:\s|$)|$)",
        };
        var colon = kind is "date" or "window" ? @"\s*[:#]?\s*" : @"\s*[:#]\s*";
        var regex = new Regex($@"(?i)(?<![A-Za-z]){labelPattern}{colon}{value}", RegexOptions.CultureInvariant);
        foreach (var line in lines)
        {
            var m = regex.Match(line.Text);
            if (!m.Success)
            {
                continue;
            }

            var group = m.Groups["value"];
            var text = group.Value.Trim();
            if (text.Length == 0 || kind == "text" && text.EndsWith(':'))
            {
                continue;
            }

            var words = line.Words.Where(w => w.Start < group.Index + group.Length && w.Start + w.Word.Text.Length > group.Index).Select(w => w.Word).ToList();
            yield return new HeaderField(field, line.Text.Substring(m.Index, group.Index - m.Index).TrimEnd(' ', ':', '#'), text, line.Page, Union(words),
                WordConfidence.Min(words));
        }
    }

    /// <summary>"Eff 1/1/24 Exp 1/1/25" (two labels, two dates on one line).</summary>
    private static List<HeaderField>? EffExp(List<Line> lines)
    {
        var regex = new Regex($@"(?i)(?<![A-Za-z])(?<el>eff(?:ective)?(?:\s+date)?)\s*[:#]?\s*(?<eff>{DatePattern}).{{0,40}}?(?<![A-Za-z])(?<xl>exp(?:iration|iry)?(?:\s+date)?)\s*[:#]?\s*(?<exp>{DatePattern})");
        // Printed with different dates on several lines: each period block's heading, not a header (see Detect).
        if (lines.Select(l => regex.Match(l.Text)).Where(m => m.Success).Select(m => m.Groups["eff"].Value + "|" + m.Groups["exp"].Value).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            return null;
        }

        foreach (var line in lines)
        {
            if (regex.Match(line.Text) is { Success: true } m)
            {
                List<double>? At(Group g) => Union([.. line.Words.Where(w => w.Start < g.Index + g.Length && w.Start + w.Word.Text.Length > g.Index).Select(w => w.Word)]);
                return
                [
                    new HeaderField("period.effectiveDate", m.Groups["el"].Value, m.Groups["eff"].Value, line.Page, At(m.Groups["eff"]), 1.0),
                    new HeaderField("period.expirationDate", m.Groups["xl"].Value, m.Groups["exp"].Value, line.Page, At(m.Groups["exp"]), 1.0),
                ];
            }
        }

        return null;
    }

    /// <summary>A window's two dates.</summary>
    public static (string From, string To)? Window(string value) =>
        Regex.Match(value, $@"^(?<from>{DatePattern})\s*(?:-|–|—|to|thru|through)\s*(?<to>{DatePattern})$", RegexOptions.IgnoreCase) is { Success: true } m
            ? (m.Groups["from"].Value, m.Groups["to"].Value)
            : null;

    private static List<double>? Union(IReadOnlyList<GraphWord> words)
    {
        var polygons = words.Where(w => w.Polygon is { Count: >= 8 }).Select(w => w.Polygon!).ToList();
        if (polygons.Count == 0)
        {
            return null;
        }

        var xs = polygons.SelectMany(p => p.Where((_, i) => i % 2 == 0)).ToList();
        var ys = polygons.SelectMany(p => p.Where((_, i) => i % 2 == 1)).ToList();
        var (x0, x1, y0, y1) = (xs.Min(), xs.Max(), ys.Min(), ys.Max());
        return [x0, y0, x1, y0, x1, y1, x0, y1];
    }
}
