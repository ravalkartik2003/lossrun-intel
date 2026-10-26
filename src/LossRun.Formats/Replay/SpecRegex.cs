using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace LossRun.Formats.Replay;

/// <summary>
/// Brief 30 item 37: the patterns a format spec carries (row kinds, locators, record lines, counts) are data, learned by a model, so
/// each is compiled once with <see cref="RegexOptions.CultureInvariant"/> and a match timeout: a catastrophic pattern fails the
/// replay with <see cref="RegexMatchTimeoutException"/> (recovery skips the candidate) instead of hanging the activity.
/// </summary>
internal static class SpecRegex
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    private const int MaxCached = 4096;
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    public static Regex Of(string pattern)
    {
        if (Cache.Count > MaxCached)
        {
            Cache.Clear();
        }

        return Cache.GetOrAdd(pattern, p => new Regex(p, RegexOptions.CultureInvariant, Timeout));
    }

    public static Match Match(string input, string pattern) => Of(pattern).Match(input);

    public static bool IsMatch(string input, string pattern) => Of(pattern).IsMatch(input);
}
