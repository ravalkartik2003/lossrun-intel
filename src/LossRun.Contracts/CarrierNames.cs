namespace LossRun.Contracts;

/// <summary>
/// Brief 35 Answers 1–2: one normalization of carrier names, shared by the replay carrier check (a format knows its carrier) and the
/// blind-run alignment key (an archive row names the carrier the way the data team keys it, the page the way the carrier prints it).
/// </summary>
public static class CarrierNames
{
    /// <summary>
    /// Words a carrier's legal names add around its own name ("X Indemnity Co", "X Casualty Insurance Co", "The X Group").
    /// Generic corporate words only, never a carrier's name (tools/generic-guard.sh).
    /// </summary>
    public static readonly IReadOnlySet<string> SuffixWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "the", "inc", "incorporated", "co", "company", "corp", "corporation", "insurance", "ins", "assurance", "indemnity", "casualty", "mutual", "group",
    };

    /// <summary>
    /// A carrier name reduced to what tells carriers apart: normalized text (<see cref="CanonicalComparer.NormalizeText"/>: case, punctuation,
    /// spacing), without the corporate words of <see cref="SuffixWords"/> and a trailing "of America". A name made only of those words keeps them all.
    /// </summary>
    public static string Normalize(string? raw)
    {
        var words = CanonicalComparer.NormalizeText(raw).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count >= 2 && words[^2] == "of" && words[^1] == "america")
        {
            words.RemoveRange(words.Count - 2, 2);
        }

        var core = words.Where(w => !SuffixWords.Contains(w)).ToList();
        return string.Join(' ', core.Count > 0 ? core : words);
    }
}
