using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LossRun.Contracts;

namespace LossRun.Formats;

/// <summary>Deterministic fingerprint of a graph: identity (who printed it) and header tuple (how the table looks).</summary>
public sealed record GraphFingerprint(string FileKind, string IdentityText, string IdentityKey, string HeaderText, string HeaderKey)
{
    /// <summary>Brief 26: identity and header keys, 12 hex characters each — who printed it and how the table looks, no text.</summary>
    public string ShortKey => $"fp:{IdentityKey[..Math.Min(12, IdentityKey.Length)]}:{HeaderKey[..Math.Min(12, HeaderKey.Length)]}";
}

/// <summary>Text normalizations shared by both tiers. Changing them invalidates stored keys/centroids: bump <see cref="Normalization"/>.</summary>
public static partial class FingerprintText
{
    public const string Normalization = "fp-text-1";
    private const int EmbeddingChars = 2000;

    public static GraphFingerprint Compute(DocumentGraph graph)
    {
        var spreadsheet = graph.Engine == "spreadsheet-native";
        var inputs = graph.FingerprintInputs;
        string identity;
        string header;
        if (spreadsheet)
        {
            // CSV "sheet" names are the file name: never part of the identity (a renamed export must still match).
            var sheets = graph.SourceFile.Kind == FileKind.Csv ? [] : inputs.SheetNames.Select(StripDigits);
            identity = Join([StripDigits(inputs.TitleText), .. sheets]);
            header = Join([.. inputs.FirstTableHeaderCells.Select(StripDigits), $"sheets {inputs.SheetNames.Count}"]);
        }
        else
        {
            var firstPage = graph.Pages.FirstOrDefault()?.Number;
            identity = Join(graph.Paragraphs.Where(p => p.Page == firstPage && p.Role is "title" or "pageHeader").Select(p => StripDigits(p.Text)));
            header = Join(inputs.FirstTableHeaderCells.Select(StripDigits));
        }

        var kind = spreadsheet ? "spreadsheet" : "pdf";
        return new GraphFingerprint(kind, identity, Key(kind + "|" + identity), header, Key(kind + "|" + header));
    }

    /// <summary>
    /// Text sent to the embedding model: the first page's markdown with HTML/markdown markup removed, lower-cased,
    /// punctuation folded, digits masked to 0 (so dates and amounts do not move the vector), truncated.
    /// </summary>
    public static string ForEmbedding(DocumentGraph graph)
    {
        var markdown = graph.FingerprintInputs.FirstPageMarkdown ?? graph.Pages.FirstOrDefault()?.Markdown ?? "";
        var text = CanonicalComparer.NormalizeText(Markup().Replace(markdown, " "));
        text = Digits().Replace(text, "0");
        return text.Length <= EmbeddingChars ? text : text[..EmbeddingChars];
    }

    /// <summary>Normalized tokens printed on the first page (paragraphs and cells; all cells for spreadsheets).</summary>
    public static HashSet<string> PageTokens(DocumentGraph graph)
    {
        var first = graph.Pages.FirstOrDefault()?.Number;
        var texts = graph.Paragraphs.Where(p => p.Page == first).Select(p => p.Text)
            .Concat(graph.Cells.Where(c => c.Page == first).Select(c => c.RawValue))
            .Concat(graph.Words.Where(w => w.Page == first).Select(w => w.Text));
        return texts.SelectMany(t => CanonicalComparer.NormalizeText(t).Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Token containment |identity ∩ page| / |identity| over normalized, digit-free identity tokens (0..1).</summary>
    public static double Containment(string identityText, HashSet<string> pageTokens)
    {
        var tokens = identityText.Split([' ', '|'], StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
        return tokens.Count == 0 ? 1.0 : Math.Round((double)tokens.Count(pageTokens.Contains) / tokens.Count, 4);
    }

    private static string StripDigits(string? text) => Spaces().Replace(Digits().Replace(CanonicalComparer.NormalizeText(text), " "), " ").Trim();

    private static string Join(IEnumerable<string> parts) => string.Join("|", parts.Where(p => p.Length > 0));

    private static string Key(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

    [GeneratedRegex(@"<[^>]+>|<!--.*?-->|[#*_`|>\-]{1,}")]
    private static partial Regex Markup();

    [GeneratedRegex(@"\d")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <param name="Format">Matched format (latest version for the tier), or null on a miss.</param>
/// <param name="Tier"><c>deterministic</c>, <c>embedding</c> or <c>miss</c>.</param>
/// <param name="Score">1.0 for a deterministic hit; best cosine for the embedding tier.</param>
/// <param name="Drift">Identity matched a known format but the header tuple differs from its latest version.</param>
/// <param name="EmbeddingCalls">Embedding requests made (0 on a deterministic hit).</param>
/// <param name="IdentityScores">Identity-guard score per format considered by the embedding tier.</param>
/// <param name="CarrierMismatch">The submission's carrier hint disagrees with the matched format's carrier.</param>
/// <param name="Thresholds">Embedding threshold applied per format (adaptive after enough hits; 08b).</param>
/// <param name="RecoveredFrom">Set when the format was recovered after a fingerprint miss (carrier hint / identity tokens; 08b).</param>
public sealed record FingerprintMatch(
    CarrierFormat? Format,
    string Tier,
    double Score,
    bool Drift,
    GraphFingerprint Fingerprint,
    int EmbeddingCalls,
    IReadOnlyDictionary<string, double> Similarities,
    IReadOnlyList<UsageEvent> Usage,
    IReadOnlyDictionary<string, double>? IdentityScores = null,
    bool CarrierMismatch = false,
    IReadOnlyDictionary<string, double>? Thresholds = null,
    string? RecoveredFrom = null,
    IReadOnlyList<string>? Ambiguous = null)
{
    public bool Hit => Format is not null && !Drift && !CarrierMismatch;
}

/// <summary>
/// Two-tier routing of a graph to a CarrierFormat (carrier-format-memory.md KQ1): deterministic identity + header keys
/// first (free); otherwise an embedding of the normalized first page compared with stored centroids (cosine ≥ threshold).
/// The embedding call is metered as a UsageEvent (stage "fingerprint") and is not an LLM call.
/// </summary>
public sealed class FormatFingerprinter(IFormatRegistry registry, IEmbeddingService? embeddings)
{
    /// <summary>Identity guard (brief 05 Answers 2): share of the spec's identity tokens found on the document's first page.</summary>
    public const double IdentityGuardThreshold = 0.8;

    /// <summary>Carrier names are "the same" at normalized Levenshtein similarity ≥ this.</summary>
    public const double CarrierMatchThreshold = 0.8;

    public async Task<FingerprintMatch> MatchAsync(DocumentGraph graph, string runId, SubmissionContext? context = null, CancellationToken cancellationToken = default)
    {
        var fp = FingerprintText.Compute(graph);
        var index = (await registry.GetIndexAsync(cancellationToken).ConfigureAwait(false)).Where(e => e.FileKind == fp.FileKind).ToList();
        index = await WithoutRetiredAsync(index, cancellationToken).ConfigureAwait(false);
        var hint = context?.CarrierHint;

        if (fp.IdentityText.Length > 0 || fp.FileKind == "spreadsheet")
        {
            // With an empty identity (CSV) several carriers may share a header tuple: prefer the one the submission names.
            // Brief 30 item 11: never pick between formats by version. More than one format left → the embedding tier
            // (restricted to them) separates them, or the document goes to Review as ambiguous.
            var exacts = index.Where(e => e.IdentityKey == fp.IdentityKey && e.HeaderKey == fp.HeaderKey).ToList();
            var named = exacts.Where(e => hint is not null && SameCarrier(hint, e.CarrierName)).ToList();
            var candidates = named.Count > 0 ? named : exacts;
            var formats = candidates.Select(e => e.FormatId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (formats.Count > 1)
            {
                return await SeparateAsync(graph, runId, fp, index.Where(e => formats.Contains(e.FormatId)).ToList(), formats, hint, cancellationToken).ConfigureAwait(false);
            }

            var exact = candidates.MaxBy(e => e.Version);
            if (exact is not null)
            {
                var spec = await registry.GetAsync(exact.FormatId, exact.Version, cancellationToken).ConfigureAwait(false);
                return new FingerprintMatch(spec, "deterministic", 1.0, false, fp, 0, new Dictionary<string, double>(), [],
                    CarrierMismatch: hint is not null && spec is not null && !SameCarrier(hint, spec.CarrierName));
            }

            // Drift needs a real identity (a printed title); an empty identity cannot say "same carrier".
            var sameCarrier = fp.IdentityText.Length == 0 ? null : index.Where(e => e.IdentityKey == fp.IdentityKey).MaxBy(e => e.Version);
            if (sameCarrier is not null)
            {
                var spec = await registry.GetAsync(sameCarrier.FormatId, sameCarrier.Version, cancellationToken).ConfigureAwait(false);
                return new FingerprintMatch(spec, "deterministic", 1.0, true, fp, 0, new Dictionary<string, double>(), [],
                    CarrierMismatch: hint is not null && spec is not null && !SameCarrier(hint, spec.CarrierName));
            }
        }

        if (embeddings is null || fp.FileKind == "spreadsheet")
        {
            return new FingerprintMatch(null, "miss", 0, false, fp, 0, new Dictionary<string, double>(), []);
        }

        return await EmbeddingTierAsync(graph, runId, fp, index, hint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Brief 30 item 11: formats sharing the exact keys, separated by the embedding tier when it can; otherwise ambiguous (→ Review).</summary>
    private async Task<FingerprintMatch> SeparateAsync(DocumentGraph graph, string runId, GraphFingerprint fp, List<FormatIndexEntry> index, IReadOnlyList<string> formats,
        string? hint, CancellationToken cancellationToken)
    {
        if (embeddings is not null && fp.FileKind != "spreadsheet")
        {
            var separated = await EmbeddingTierAsync(graph, runId, fp, index, hint, cancellationToken).ConfigureAwait(false);
            if (separated.Format is not null)
            {
                return separated;
            }

            return separated with { Tier = "ambiguous", Ambiguous = formats };
        }

        return new FingerprintMatch(null, "ambiguous", 1.0, false, fp, 0, new Dictionary<string, double>(), [], Ambiguous: formats);
    }

    private async Task<FingerprintMatch> EmbeddingTierAsync(DocumentGraph graph, string runId, GraphFingerprint fp, List<FormatIndexEntry> index, string? hint,
        CancellationToken cancellationToken)
    {
        var text = FingerprintText.ForEmbedding(graph);
        var embedding = await embeddings!.EmbedAsync(text, graph.DocumentId, runId, cancellationToken).ConfigureAwait(false);
        var similarities = new Dictionary<string, double>();
        var identityScores = new Dictionary<string, double>();
        var thresholds = new Dictionary<string, double>();
        var pageTokens = FingerprintText.PageTokens(graph);
        CarrierFormat? best = null;
        var bestScore = 0.0;
        foreach (var formatId in index.Select(e => e.FormatId).Distinct().Order(StringComparer.Ordinal))
        {
            var centroid = await registry.GetCentroidAsync(formatId, cancellationToken).ConfigureAwait(false);
            // Brief 30 item 44: a centroid built from text normalized another way (FingerprintText.Normalization bumped) is stale, like one from another model.
            if (centroid is null || centroid.Model != embedding.Model || centroid.TextNormalization != FingerprintText.Normalization)
            {
                continue;
            }

            var score = Math.Round(Vectors.Cosine(embedding.Vector, centroid.Vector), 6);
            similarities[formatId] = score;
            var latest = index.Where(e => e.FormatId == formatId).MaxBy(e => e.Version)!;
            var spec = await registry.GetAsync(formatId, latest.Version, cancellationToken).ConfigureAwait(false);
            // Adaptive per-format threshold after enough accepted documents (08 Answers 2), else the spec's own.
            var adaptive = (await registry.GetStatsAsync(formatId, cancellationToken).ConfigureAwait(false)).Embedding?.Threshold();
            var threshold = adaptive is { } t ? Math.Min(t, spec?.Fingerprint.Embedding?.Threshold ?? 0.95) : spec?.Fingerprint.Embedding?.Threshold ?? 0.95;
            thresholds[formatId] = threshold;

            // Identity guard: a layout twin from another carrier can clear the cosine threshold, so a spec that has
            // identity text also needs that text (its tokens) on the document's first page.
            var identity = spec is null || spec.Fingerprint.IdentityText.Length == 0 ? 1.0 : FingerprintText.Containment(spec.Fingerprint.IdentityText, pageTokens);
            identityScores[formatId] = identity;
            if (score >= threshold && identity >= IdentityGuardThreshold && score > bestScore)
            {
                (best, bestScore) = (spec, score);
            }
        }

        return new FingerprintMatch(best, best is null ? "miss" : "embedding", best is null ? similarities.Values.DefaultIfEmpty(0).Max() : bestScore,
            false, fp, 1, similarities, embedding.Usage, identityScores,
            CarrierMismatch: hint is not null && best is not null && !SameCarrier(hint, best.CarrierName), Thresholds: thresholds);
    }

    /// <summary>Retired versions take no part in matching (07 Answers 4); one stats read per format of this file kind.</summary>
    private async Task<List<FormatIndexEntry>> WithoutRetiredAsync(List<FormatIndexEntry> index, CancellationToken cancellationToken)
    {
        var retired = new HashSet<(string, int)>();
        foreach (var formatId in index.Select(e => e.FormatId).Distinct(StringComparer.Ordinal))
        {
            var stats = await registry.GetStatsAsync(formatId, cancellationToken).ConfigureAwait(false);
            foreach (var (version, live) in stats.Lifecycle.Where(l => l.Value.Status == FormatLifecycle.Retired))
            {
                retired.Add((formatId, int.Parse(version, System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        return retired.Count == 0 ? index : index.Where(e => !retired.Contains((e.FormatId, e.Version))).ToList();
    }

    /// <summary>
    /// Two carrier names name one carrier: their cores (<see cref="CarrierNames.Normalize"/>: corporate words dropped, brief 35
    /// Answers 2) are equal, or close enough for an OCR misread (<see cref="CarrierMatchThreshold"/>).
    /// </summary>
    public static bool SameCarrier(string a, string b) =>
        CanonicalComparer.TextSimilarity(CarrierNames.Normalize(a), CarrierNames.Normalize(b)) >= CarrierMatchThreshold;

    /// <summary>Brief 35 Answers 2: the name is one of the format's carrier names (its learned name or a name a reviewer added).</summary>
    public static bool KnowsCarrier(CarrierFormat spec, string name) => spec.KnownCarrierNames.Any(known => SameCarrier(name, known));
}

public static class Vectors
{
    public static double Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count != b.Count)
        {
            throw new ArgumentException($"Vector length mismatch ({a.Count} vs {b.Count}).");
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += a[i] * (double)b[i];
            na += a[i] * (double)a[i];
            nb += b[i] * (double)b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>L2-normalized mean of the exemplar vectors.</summary>
    public static float[] Centroid(IReadOnlyList<IReadOnlyList<float>> vectors)
    {
        var dims = vectors[0].Count;
        var sum = new double[dims];
        foreach (var v in vectors)
        {
            var norm = Math.Sqrt(v.Sum(x => x * (double)x));
            for (var i = 0; i < dims; i++)
            {
                sum[i] += v[i] / norm;
            }
        }

        var length = Math.Sqrt(sum.Sum(x => x * x));
        return sum.Select(x => (float)(x / length)).ToArray();
    }
}
