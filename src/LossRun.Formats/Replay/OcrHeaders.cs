using System.Globalization;
using System.Text;
using LossRun.Contracts;

namespace LossRun.Formats.Replay;

/// <summary>
/// OCR-misread table headers (brief 08b; 08 run: "Clalm No", "Cis Dt", "DOJ", "Εχρ Pd" on fax and rotated scans).
/// On a scanned PDF a printed header that matches no spec source exactly, but is a near-miss of a source the table
/// does not otherwise carry, is the same column misread: it is read as that source instead of being reported as
/// unmapped (which sent the document to a delta repair the OCR made impossible). Native PDFs and spreadsheets match
/// exactly, as before, so a real renamed column there is still drift.
/// </summary>
public static class OcrHeaders
{
    /// <summary>Similarity (folded, 1 − Levenshtein/len) at or above which a misread header takes a missing source.</summary>
    public const double MinSimilarity = 0.8;

    /// <summary>
    /// A scanned PDF: no text layer on page 1 (recorded at ingestion). CU OCRs born-digital pages too, so word confidence
    /// cannot tell them apart (08b corpus v2: native 0.970 vs clean scan 0.967); it is only the fallback for graphs
    /// ingested before the text-layer flag existed.
    /// </summary>
    public static bool IsScanned(DocumentGraph graph) =>
        graph.SourceFile.Kind == FileKind.Pdf && (graph.SourceFile.TextLayer is { } textLayer ? !textLayer
            : WordConfidence.Mean(graph.Words) is { } mean && mean < 0.995); // brief 31: no known confidence is no evidence of a scan

    /// <summary>
    /// Column index → source key for <paramref name="headerKeys"/> (normalized header texts): exact matches, then,
    /// when <paramref name="scanned"/>, one-to-one near-misses to sources absent from the table (best pairs first).
    /// </summary>
    public static string?[] Resolve(IReadOnlyList<string> headerKeys, IReadOnlySet<string> sourceKeys, bool scanned)
    {
        var resolved = headerKeys.Select(h => sourceKeys.Contains(h) ? h : null).ToArray();
        if (!scanned)
        {
            return resolved;
        }

        var missing = sourceKeys.Where(s => !headerKeys.Contains(s)).ToList();
        var pairs = new List<(int Column, string Source, double Score)>();
        for (var i = 0; i < headerKeys.Count; i++)
        {
            if (resolved[i] is not null || headerKeys[i].Length < 2)
            {
                continue;
            }

            var folded = Fold(headerKeys[i]);
            foreach (var source in missing)
            {
                var target = Fold(source);
                var score = CanonicalComparer.TextSimilarity(folded, target);
                var edits = (int)Math.Round((1 - score) * Math.Max(folded.Length, target.Length));
                if (score >= MinSimilarity || Math.Max(folded.Length, target.Length) <= 5 && edits <= 1)
                {
                    pairs.Add((i, source, score));
                }
            }
        }

        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (column, source, _) in pairs.OrderByDescending(p => p.Score).ThenBy(p => p.Column))
        {
            if (resolved[column] is null && taken.Add(source))
            {
                resolved[column] = source;
            }
        }

        return resolved;
    }

    /// <summary>Folds OCR look-alikes: diacritics dropped, Greek/Cyrillic letters that print like Latin ones mapped to them.</summary>
    public static string Fold(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (var ch in key.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(ch switch
            {
                'α' or 'а' => 'a', 'β' or 'в' => 'b', 'ε' or 'е' => 'e', 'η' or 'п' => 'n', 'ι' or 'і' => 'i', 'κ' or 'к' => 'k', 'ν' => 'v', 'ο' or 'о' => 'o',
                'ρ' or 'р' => 'p', 'τ' or 'т' => 't', 'υ' or 'у' => 'y', 'χ' or 'х' => 'x', 'с' => 'c', 'м' => 'm', 'ѕ' => 's', 'ј' => 'j',
                _ => ch,
            });
        }

        return sb.ToString();
    }
}
