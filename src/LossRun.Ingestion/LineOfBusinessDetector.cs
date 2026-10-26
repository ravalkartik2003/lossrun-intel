using System.Text.RegularExpressions;
using LossRun.Contracts;

namespace LossRun.Ingestion;

/// <summary>The line of business a loss-run graph prints, with the evidence counted (ADR 0020).</summary>
/// <param name="Line">workersCompensation, a line of <c>line-of-business.json</c>, or null when the graph prints no evidence.</param>
public sealed record LineOfBusinessVerdict(string? Line, int WorkersCompEvidence, int OtherEvidence, IReadOnlyList<string> Evidence)
{
    public bool OutOfScope => Line is not null && Line != CanonicalData.LineOfBusiness.InScope;
}

/// <summary>
/// ADR 0020 (brief 20 Answers 1): the POC's scope is workers' comp. Before any learn (no model call), each loss-run graph's
/// printed evidence of its line of business is counted:
/// <list type="bullet">
/// <item>values printed after a line/coverage/claim-type label ("Claim Type: Property Claim", "Line of Business: …"), read
/// from the OCR word stream because CU often splits the label and its value into two paragraphs;</item>
/// <item>the values of a table column whose header is such a label ("Policy Type" | Property);</item>
/// <item>headings that name a line ("Property Loss Run", "Workers' Compensation Loss Run").</item>
/// </list>
/// Workers' comp evidence is a glossary value alias of claim.claimType or document.lineOfBusiness WC; other lines' terms come
/// from <c>docs/evaluation/line-of-business.json</c>. A graph is out of scope when it prints another line's evidence and no
/// workers' comp evidence, or at least two pieces (or one line-of-business label) that are at least 80 % of all the evidence
/// (an all-lines run with a few workers' comp claims stays in scope). Layout-generic: no carrier identity is read.
/// </summary>
public static partial class LineOfBusinessDetector
{
    private const string WorkersComp = "workersCompensation";

    public static LineOfBusinessVerdict Detect(DocumentGraph graph, Glossary glossary, LineOfBusinessVocabulary? vocabulary = null)
    {
        vocabulary ??= CanonicalData.LineOfBusiness;
        var labels = vocabulary.Labels.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var evidence = new List<string>();
        var labelled = false;

        void Count(string value, string where, int weight = 1)
        {
            if (Classify(value, glossary, vocabulary) is not { } line)
            {
                return;
            }

            counts[line] = counts.GetValueOrDefault(line) + weight;
            if (evidence.Count < 6)
            {
                evidence.Add($"{where}: \"{value.Trim()}\" → {line}");
            }
        }

        // 1. Label: value, in the printed word stream of each page.
        foreach (var page in graph.Words.Where(w => w.Text.Trim().Length > 0).GroupBy(w => w.Page))
        {
            var text = string.Join(" ", page.OrderBy(w => w.SpanOffset ?? int.MaxValue).Select(w => w.Text));
            foreach (Match m in LabelValue().Matches(text))
            {
                if (labels.Contains(Normalize(m.Groups["label"].Value)))
                {
                    var isLob = Normalize(m.Groups["label"].Value) is "line of business" or "lob";
                    Count(m.Groups["value"].Value, $"p{page.Key} label \"{m.Groups["label"].Value.Trim()}\"", isLob ? 2 : 1);
                    labelled |= isLob;
                }
            }
        }

        // 2. Columns headed by a label.
        foreach (var table in graph.Tables)
        {
            for (var column = 0; column < table.HeaderTexts.Count; column++)
            {
                if (!labels.Contains(Normalize(table.HeaderTexts[column])))
                {
                    continue;
                }

                foreach (var cell in graph.Cells.Where(c => c.TableId == table.Id && c.Column == column && !table.HeaderRows.Contains(c.Row) && !TotalLabel().IsMatch(c.RawValue)))
                {
                    Count(cell.RawValue, $"{table.Id} column \"{table.HeaderTexts[column].ReplaceLineEndings(" ")}\"");
                }
            }
        }

        // 3. Headings naming a line ("… Loss Run", "… Loss History", "… Claims").
        foreach (var heading in graph.Paragraphs.Where(p => p.Role is "title" or "sectionHeading" or "pageHeader" && LossRunHeading().IsMatch(p.Text)))
        {
            Count(LossRunHeading().Replace(heading.Text, " "), $"p{heading.Page} heading", 2);
        }

        var wc = counts.GetValueOrDefault(WorkersComp);
        var (other, otherCount) = counts.Where(c => c.Key != WorkersComp).OrderByDescending(c => c.Value).Select(c => (c.Key, c.Value)).FirstOrDefault();
        var total = counts.Values.Sum();
        var line = total == 0 ? null
            : other is not null && (wc == 0 || (otherCount >= 2 || labelled) && otherCount >= 0.8 * total) ? other
            : wc > 0 ? WorkersComp
            : null;
        return new LineOfBusinessVerdict(line, wc, total - wc, evidence);
    }

    /// <summary>workersCompensation, another line, or null (no term, or an ambiguous one).</summary>
    internal static string? Classify(string printed, Glossary glossary, LineOfBusinessVocabulary vocabulary)
    {
        var value = Normalize(printed);
        if (value.Length < 2 || vocabulary.Ambiguous.Any(a => value == Normalize(a)))
        {
            return null;
        }

        // A line code ("WC") counts at any length; a claim-type code only from 3 characters ("M", "06" are too short to be evidence).
        if (glossary.ResolveValue("document.lineOfBusiness", printed) == "WC" || value.Length >= 3 && glossary.ResolveValue("claim.claimType", printed) is not null
            || WorkersCompTerm().IsMatch(value))
        {
            return WorkersComp;
        }

        var padded = $" {value} ";
        return vocabulary.Lines.Where(l => l.Value.Any(t => padded.Contains($" {Normalize(t)} ", StringComparison.Ordinal)))
            .Select(l => l.Key).FirstOrDefault();
    }

    private static string Normalize(string? text) => CanonicalComparer.NormalizeText(Regex.Replace(text ?? "", "[^A-Za-z0-9 ]", " "));

    /// <summary>"Claim Type : Property Claim CLM12345" → label "Claim Type", value "Property Claim" (up to three words, before a digit or the next label).</summary>
    [GeneratedRegex(@"(?i)(?<label>\b(?:claim type|coverage(?: type)?|line of business|lob|policy type|line)\b)\s*:\s*(?<value>[A-Za-z&'/\-]+(?:\s+[A-Za-z&'/\-]+){0,2}?)(?=\s*(?:$|\d|[A-Z][a-z]+\s*:|\s{2}))")]
    private static partial Regex LabelValue();

    [GeneratedRegex(@"(?i)\b(?:loss runs?|loss history|loss report|claims? (?:history|report|detail))\b")]
    private static partial Regex LossRunHeading();

    [GeneratedRegex(@"(?i)^\s*(?:totals?|grand total|policy total)\b")]
    private static partial Regex TotalLabel();

    [GeneratedRegex(@"\b(?:workers comp(?:ensation)?|work comp|workmens comp(?:ensation)?)\b")]
    private static partial Regex WorkersCompTerm();
}
