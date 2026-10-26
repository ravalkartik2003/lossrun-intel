using System.Text.RegularExpressions;
using LossRun.Contracts;

namespace LossRun.Ingestion;

/// <summary>What a loss-run-classified graph is, beyond "a loss run" (ADR 0021); null kind = a complete loss run.</summary>
/// <param name="Kind"><c>loss-analysis</c>, <c>changes-only</c> or null.</param>
/// <param name="Category">The manifest classification of the kept segment.</param>
public sealed record ReportKindVerdict(string? Kind, string Category, string? Reason, string? Evidence);

/// <summary>
/// ADR 0021 (brief 22 Answers 3 and 5): two report pages that carriers issue with a loss run and that the classifier calls a
/// loss run, found from what the page prints, before any learn (no model call):
/// <list type="bullet">
/// <item><c>loss-analysis</c> (classification <c>LossAnalysis</c>): an analysis of the claims — lag to report, frequency or
/// severity by a dimension — headed so ("Claim Lag Analysis", "Frequency Analysis") or printing a lag-period or a
/// percent-of-total column, with no claim table. Its rows are buckets, not claims or policy periods.</item>
/// <item><c>changes-only</c> (classification <c>LossRun</c>): a run headed as the changes since the last report ("Detail
/// Changes", "Changes Only"). It lists only the claims whose values moved, so its counts and totals understate the period.</item>
/// </list>
/// Both are kept, not processed: listed in the manifest with the reason, indexed for chat, kept out of the numbers.
/// Layout-generic: no carrier identity is read.
/// </summary>
public static partial class ReportKindDetector
{
    public const string LossAnalysisCategory = "LossAnalysis";
    public const string LossAnalysisReason = "analysis report, not a loss run; kept, not processed";
    public const string ChangesOnlyReason = "changes since the last report, not a complete run; kept, not processed";

    public static ReportKindVerdict Detect(DocumentGraph graph, string lossRunCategory)
    {
        var claimTable = graph.Tables.Any(t => t.HeaderFields.Contains("claim.claimNumber") && t.RowCount > t.HeaderRows.Count);
        var headings = graph.Paragraphs.Where(p => p.Page <= 1 || p.Role is "title" or "sectionHeading").Select(p => p.Text)
            .Concat(graph.Tables.SelectMany(t => t.HeaderTexts))
            .Select(t => Regex.Replace(t ?? "", @"\s+", " ").Trim())
            .Where(t => t.Length > 0)
            .ToList();

        if (!claimTable)
        {
            var heading = headings.FirstOrDefault(h => AnalysisHeading().IsMatch(h));
            var column = graph.Tables.SelectMany(t => t.HeaderTexts).FirstOrDefault(h => AnalysisColumn().IsMatch(h ?? ""));
            if ((heading ?? column) is { } found)
            {
                return new ReportKindVerdict("loss-analysis", LossAnalysisCategory, LossAnalysisReason,
                    $"{(heading is not null ? "heading" : "column")} \"{Regex.Replace(found, @"\s+", " ").Trim()}\", and no claim table");
            }
        }

        if (headings.FirstOrDefault(h => ChangesHeading().IsMatch(h)) is { } changes)
        {
            return new ReportKindVerdict("changes-only", lossRunCategory, ChangesOnlyReason, $"heading \"{changes}\"");
        }

        return new ReportKindVerdict(null, lossRunCategory, null, null);
    }

    [GeneratedRegex(@"(?i)\b(?:(?:claim\s+)?lag\s+analysis|frequency\s+analysis|severity\s+analysis|(?:frequency|severity)\s+(?:and|&)\s+(?:frequency|severity)|cause\s+(?:of\s+(?:loss|injury)\s+)?analysis|loss\s+stratification)\b")]
    private static partial Regex AnalysisHeading();

    [GeneratedRegex(@"(?i)\blag\s+(?:period|days)\b|\bpercent(?:age)?\s+(?:to|of)\s+total\b|\bdays\s+to\s+report\b")]
    private static partial Regex AnalysisColumn();

    [GeneratedRegex(@"(?i)\b(?:changes?\s+only|detail\s+changes|changes\s+since\s+(?:the\s+)?(?:last|previous|prior)\s+report|activity\s+since\s+(?:the\s+)?(?:last|previous|prior)\s+report)\b")]
    private static partial Regex ChangesHeading();
}
