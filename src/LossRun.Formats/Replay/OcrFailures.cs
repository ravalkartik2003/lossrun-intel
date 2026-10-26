using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LossRun.Contracts;
using LossRun.Rules;

namespace LossRun.Formats.Replay;

/// <summary>
/// Hard rule failures that OCR caused rather than the format (08 Answers 2, brief 08b §2). On a scanned document a
/// failure is OCR-level when a value it reads came from words recognised with confidence &lt; <see cref="LowConfidence"/>,
/// or when it is a totals tie that is off by a non-round amount (a misread digit; printing errors in the source, like a
/// subtotal off by exactly 100.00, are round). A model repair cannot fix either, so such documents go to Review with the
/// replay as pre-fill instead of spending repair calls. Failures on native PDFs and spreadsheets are never OCR-level.
/// </summary>
public static partial class OcrFailures
{
    public const double LowConfidence = 0.9;

    public sealed record Classification(int Ocr, int Structural, IReadOnlyList<string> Examples)
    {
        /// <summary>Every hard failure is OCR-level (and there is at least one).</summary>
        public bool AllOcr => Ocr > 0 && Structural == 0;
    }

    public static Classification Classify(ReplayResult replay, bool scanned)
    {
        var hard = replay.RuleOutcomes.Where(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard).ToList();
        if (!scanned || hard.Count == 0)
        {
            return new Classification(0, hard.Count, []);
        }

        var node = JsonSerializer.SerializeToNode(replay.Document, CanonicalJson.Options)!;
        var lineage = replay.Document.Lineage ?? [];
        int ocr = 0, structural = 0;
        var examples = new List<string>();
        foreach (var outcome in hard)
        {
            var paths = outcome.AffectedFields.SelectMany(f => Expand(node, f)).Distinct(StringComparer.Ordinal).ToList();
            var confidence = paths.Select(p => Confidence(p, lineage)).OfType<double>().DefaultIfEmpty(1.0).Min();
            var delta = TotalsDelta(node, outcome.AffectedFields);
            var isOcr = confidence < LowConfidence || delta is { } d && d % 100m != 0m;
            if (isOcr)
            {
                ocr++;
                if (examples.Count < 3)
                {
                    examples.Add($"{outcome.RuleId}: {(confidence < LowConfidence ? $"word confidence {confidence:0.00}" : $"off by {delta:0.00}")}");
                }
            }
            else
            {
                structural++;
            }
        }

        return new Classification(ocr, structural, examples);
    }

    /// <summary>Lineage confidence of a value; a derived value ("a + b") takes the lowest confidence of its sources.</summary>
    private static double? Confidence(string path, IReadOnlyDictionary<string, LineageRecord> lineage)
    {
        if (!lineage.TryGetValue(path, out var record))
        {
            return null;
        }

        if (record.Extractor == "derived")
        {
            return (record.SourceText ?? "").Split(" + ", StringSplitOptions.RemoveEmptyEntries)
                .Select(s => lineage.GetValueOrDefault(s.Trim())?.Confidence).OfType<double>().DefaultIfEmpty(1.0).Min();
        }

        return record.Confidence;
    }

    /// <summary>|printed total − sum of claim values| for a totals-tie rule (a total path plus a claims[*] path), else null.</summary>
    private static decimal? TotalsDelta(JsonNode root, IReadOnlyList<string> fields)
    {
        var total = fields.FirstOrDefault(f => !f.Contains('*', StringComparison.Ordinal) && (f.Contains(".subtotals.", StringComparison.Ordinal) || f.StartsWith("reportTotals.", StringComparison.Ordinal))
            && MeasurePath().IsMatch(f));
        var rows = fields.FirstOrDefault(f => f.Contains("claims[*]", StringComparison.Ordinal));
        if (total is null || rows is null || Number(Get(root, total)) is not { } printed)
        {
            return null;
        }

        var sum = Expand(root, rows).Select(p => Number(Get(root, p)) ?? 0m).Sum();
        return Math.Abs(printed - sum);
    }

    /// <summary>Concrete paths for a pattern with [*] wildcards ("policyPeriods[0].claims[*].indemnity.paid").</summary>
    private static IEnumerable<string> Expand(JsonNode root, string pattern)
    {
        var star = pattern.IndexOf("[*]", StringComparison.Ordinal);
        if (star < 0)
        {
            yield return pattern;
            yield break;
        }

        var prefix = pattern[..star];
        if (Get(root, prefix) is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                foreach (var path in Expand(root, $"{prefix}[{i}]{pattern[(star + 3)..]}"))
                {
                    yield return path;
                }
            }
        }
    }

    private static JsonNode? Get(JsonNode root, string path)
    {
        JsonNode? node = root;
        foreach (var part in path.Split('.'))
        {
            var name = part.Split('[')[0];
            node = (node as JsonObject)?[name];
            foreach (var index in part.Split('[').Skip(1).Select(i => i.TrimEnd(']')))
            {
                node = node is JsonArray a && int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n < a.Count ? a[n] : null;
            }
        }

        return node;
    }

    private static decimal? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : null;

    [GeneratedRegex(@"\.(paid|reserve|incurred)$")]
    private static partial Regex MeasurePath();
}
