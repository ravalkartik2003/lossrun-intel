using System.Globalization;
using System.Text.RegularExpressions;

namespace LossRun.Chat;

/// <summary>
/// Brief 24 step 5 (review plan 2026-10-25 §5 change 5): the runtime check behind the prompt rule "never name a person,
/// date or reason no tool returned". Every quoted passage, every date and every reviewer named after "by" / "reviewer" in
/// an answer must appear in what this turn's tools returned (<see cref="TurnRecorder.ToolStrings"/>); anything else is
/// returned as unverified, and the turn's trace says so. The citation check covers refs; this covers the facts between them.
/// </summary>
public static partial class FactGuard
{
    /// <summary>Quoted passages shorter than this are words, not quotations, and are not checked.</summary>
    public const int MinQuoteChars = 12;

    public static IReadOnlyList<string> Unverified(string answer, IReadOnlyCollection<string> toolStrings)
    {
        var corpus = Normalize(string.Join("\n", toolStrings));
        var exact = Typographic(string.Join("\n", toolStrings));
        var dates = toolStrings.SelectMany(DatesIn).ToHashSet();
        var flagged = new List<string>();

        // Brief 24 step 7: a quotation is an exact substring of a tool value (case included); only whitespace and typographic
        // quote marks are normalised.
        foreach (Match m in Quote().Matches(answer))
        {
            var quoted = m.Groups["q"].Value.Trim();
            if (quoted.Length >= MinQuoteChars && !exact.Contains(Typographic(quoted), StringComparison.Ordinal))
            {
                flagged.Add($"quote \"{quoted}\"");
            }
        }

        foreach (Match m in Reviewer().Matches(answer))
        {
            var name = m.Groups["name"].Value.TrimEnd('.', ',', ';', ':');
            if (!corpus.Contains(Normalize(name), StringComparison.Ordinal))
            {
                flagged.Add($"reviewer {name}");
            }
        }

        foreach (var (text, date) in DateMentions(answer))
        {
            if (!dates.Contains(date))
            {
                flagged.Add($"date {text}");
            }
        }

        return [.. flagged.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>At most this many rationales returned in a turn must each be quoted; above it, at least one.</summary>
    public const int MaxRequiredRationales = 5;

    /// <summary>
    /// Brief 24 step 7: when explain_value returned reviewers' rationales this turn, the answer quotes them exactly (each one when
    /// there are at most <see cref="MaxRequiredRationales"/>, otherwise at least one). Returns the rationales not quoted exactly.
    /// </summary>
    public static IReadOnlyList<string> MissingRationales(string answer, IReadOnlyCollection<string> rationales)
    {
        // Brief 25 step 0: only an answer that talks about a correction or its reason must quote; a printed-value answer need not.
        if (!MentionsCorrection().IsMatch(answer))
        {
            return [];
        }

        var text = Typographic(answer);
        var missing = rationales.Distinct(StringComparer.Ordinal).Where(r => !text.Contains(Typographic(r), StringComparison.Ordinal)).ToList();
        return rationales.Count == 0 || (rationales.Distinct(StringComparer.Ordinal).Count() > MaxRequiredRationales && missing.Count < rationales.Distinct(StringComparer.Ordinal).Count())
            ? [] : missing;
    }

    /// <summary>Whitespace collapsed and typographic quotes and apostrophes made plain; case kept.</summary>
    private static string Typographic(string s) => Whitespace().Replace(s.Replace('“', '"').Replace('”', '"').Replace('’', '\'').Replace('‘', '\''), " ").Trim();

    private static string Normalize(string s) => Whitespace().Replace(s.Replace('“', '"').Replace('”', '"').Replace('’', '\''), " ").Trim().ToLowerInvariant();

    /// <summary>Dates in a tool value: ISO (2026-10-23, 2026-10-23T18:04Z) and US (10/23/2026).</summary>
    private static IEnumerable<DateOnly> DatesIn(string s) => DateMentions(s).Select(d => d.Date);

    private static IEnumerable<(string Text, DateOnly Date)> DateMentions(string s)
    {
        foreach (Match m in IsoDate().Matches(s))
        {
            if (DateOnly.TryParseExact(m.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                yield return (m.Value, d);
            }
        }

        foreach (Match m in UsDate().Matches(s))
        {
            if (DateOnly.TryParseExact(m.Value, ["M/d/yyyy", "MM/dd/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                yield return (m.Value, d);
            }
        }

        foreach (Match m in WordDate().Matches(s))
        {
            var text = m.Value.Replace(",", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal);
            if (DateOnly.TryParseExact(text, ["d MMM yyyy", "d MMMM yyyy", "MMM d yyyy", "MMMM d yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d))
            {
                yield return (m.Value, d);
            }
        }
    }

    [GeneratedRegex(@"[""“](?<q>[^""“”]{2,400})[""”]")]
    private static partial Regex Quote();

    // "changed by uw-1", "approved by M. Chen", "reviewer Jane Doe": an id, an e-mail, or one to three capitalised words.
    [GeneratedRegex(@"\b(?:by|reviewer)\s+(?<name>[\w.+-]+@[\w.-]+|[a-z][\w.-]*\d[\w.-]*|[A-Z][\w'-]*\.?(?:\s+[A-Z][\w'-]*\.?){0,2})")]
    private static partial Regex Reviewer();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"\b\d{1,2}/\d{1,2}/\d{4}\b")]
    private static partial Regex UsDate();

    [GeneratedRegex(@"\b(?:\d{1,2}\s+(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)[a-z]*\.?\s+\d{4}|(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)[a-z]*\.?\s+\d{1,2},?\s+\d{4})\b")]
    private static partial Regex WordDate();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Words that make an answer about a correction or its reason (brief 25 step 0).</summary>
    [GeneratedRegex(@"\b(correct\w*|chang\w*|overrid\w*|adjust\w*|amend\w*|updat\w*|revis\w*|reason\w*|rationale\w*|because|why|wrote|explain\w*|reviewer\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MentionsCorrection();

    /// <summary>Money amounts and dates an answer states (the facts a restate must keep, brief 25 step 0).</summary>
    public static IReadOnlySet<string> Facts(string answer) =>
        Fact().Matches(answer).Select(m => m.Value.Replace(",", "", StringComparison.Ordinal).TrimStart('$')).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\$?\d{1,3}(?:,\d{3})*(?:\.\d{2})|\b\d{4}-\d{2}-\d{2}\b")]
    private static partial Regex Fact();
}
