using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LossRun.Contracts;

namespace LossRun.Data;

/// <summary>Brief 26 (ADR 0025): why a case went to engineering. The dedupe key is (reason, fingerprint).</summary>
public static class EscalationReasons
{
    public const string LearnExhausted = "learn-exhausted";

    /// <summary>Brief 26 Answers 4: a delta repair that did not publish — on an active format, a drift signal (learn failures are a coverage signal).</summary>
    public const string DeltaExhausted = "delta-exhausted";
    public const string ReplayFailedActiveFormat = "replay-failed-active-format";
    public const string UnhandledException = "unhandled-exception";
    public const string TooManySegments = "too-many-segments";
    public const string CostOverLimit = "cost-over-limit";
    public const string RepeatedReview = "repeated-review";
    public const string RuleDominant = "rule-dominant";
    public const string ReviewRateSpike = "review-rate-spike";
    public const string RepeatedReopen = "repeated-reopen";
    public const string ManualFlag = "manual-flag";

    /// <summary>Brief 30 item 6: a reviewer's decision the API acknowledged could not be applied; the document is back in Review.</summary>
    public const string DecisionApplyFailed = "decision-apply-failed";

    /// <summary>Brief 30 item 22: a metered call whose model has no price in config/azure-prices.json (its cost is reported unpriced, never 0).</summary>
    public const string MissingPrice = "missing-price";

    /// <summary>
    /// Brief 32 item 3: a step after a committed decision (format approval, alias review, chat re-index, the manifest) failed after its retries.
    /// The decision stands; the trace names the step, and an engineer completes it.
    /// </summary>
    public const string DecisionPostCommitFailed = "decision-postcommit-failed";

    /// <summary>Brief 32 item 5: an override's stored old value is not a value of its field's type; the as-reported view shows the current value for it.</summary>
    public const string OverrideUnrevertable = "override-unrevertable";

    public static readonly IReadOnlyList<string> All =
        [LearnExhausted, DeltaExhausted, ReplayFailedActiveFormat, UnhandledException, TooManySegments, CostOverLimit, RepeatedReview, RuleDominant, ReviewRateSpike, RepeatedReopen, ManualFlag,
         DecisionApplyFailed, MissingPrice, DecisionPostCommitFailed, OverrideUnrevertable];
}

public static class EscalationStatuses
{
    public const string New = "new";
    public const string Investigating = "investigating";
    public const string Resolved = "resolved";
    public const string WontFix = "wont-fix";

    public static readonly IReadOnlyList<string> All = [New, Investigating, Resolved, WontFix];

    public static bool IsOpen(string status) => status is New or Investigating;
}

/// <summary>
/// What engineering needs to start, without opening a document: rule ids, verifier reasons per attempt (numbers and
/// quoted text removed, see <see cref="EscalationTraceText"/>), counts (header resolution, segments, model calls), the
/// pipeline step and an exception type. Never a document value.
/// </summary>
public sealed record EscalationTrace
{
    public IReadOnlyList<string> RuleIds { get; init; } = [];
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public IReadOnlyDictionary<string, decimal> Counts { get; init; } = new Dictionary<string, decimal>();
    public string? Step { get; init; }
    public string? ExceptionType { get; init; }
}

/// <summary>One occurrence the pipeline, the pattern detector or a reviewer reports. Merged into an <see cref="Escalation"/> by its key.</summary>
public sealed record EscalationSignal(string ReasonCode, string FormatFingerprint, DateTimeOffset At, EscalationTrace Trace)
{
    public string? FormatRef { get; init; }
    public string? Carrier { get; init; }
    public IReadOnlyList<string> DocumentIds { get; init; } = [];
    public IReadOnlyList<string> UploadIds { get; init; } = [];

    /// <summary>A reviewer's note on a manual flag (audited as the rationale; kept out of the trace).</summary>
    public string? Note { get; init; }
}

/// <summary>dbo.Escalation (changeset 0021): one row per (reason, fingerprint), counted, with its documents and status.</summary>
public sealed record Escalation
{
    public string Id { get; init; } = "";
    public string ReasonCode { get; init; } = "";
    public string FormatFingerprint { get; init; } = "";
    public string? FormatRef { get; init; }
    public string? Carrier { get; init; }
    public IReadOnlyList<string> DocumentIds { get; init; } = [];
    public IReadOnlyList<string> UploadIds { get; init; } = [];
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; init; }
    public int Count { get; init; }
    public EscalationTrace TraceSummary { get; init; } = new();
    public string Status { get; init; } = EscalationStatuses.New;
    public string? ResolutionNote { get; init; }
    public string? TuningItemRef { get; init; }

    /// <summary>Optimistic concurrency: a save expects the version it read (0 = create).</summary>
    public int Version { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string UpdatedBy { get; init; } = "";

    public static string IdFor(string reasonCode, string fingerprint) =>
        "esc-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{reasonCode}|{fingerprint}")))[..16];
}

/// <summary>What the hourly detector reads per document (identifiers, flags and rule ids only).</summary>
/// <param name="Reviewed">A document review was opened for it (format-confirm reviews do not count).</param>
/// <param name="FailedRuleIds">Rules that failed at the document's current revision.</param>
/// <param name="Reopens">How many times its review was reopened.</param>
public sealed record EscalationObservation(string DocumentId, string UploadId, string? Carrier, string? Fingerprint, string? FormatRef, DateTimeOffset ProcessedAt,
    bool Reviewed, IReadOnlyList<string> FailedRuleIds, int Reopens);

/// <summary>A case written into the tuning loop (brief 27 reads these): cause category (a)–(g) from <c>docs/operations/tuning-loop.md</c>.</summary>
public sealed record TuningItem(string Id, string Category, string EscalationId, string ReasonCode, string FormatFingerprint, string? FormatRef,
    IReadOnlyList<string> DocumentIds, string Note, DateTimeOffset CreatedAt, string CreatedBy)
{
    public static readonly IReadOnlyDictionary<string, string> Categories = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["a"] = "glossary gap", ["b"] = "layout not expressible", ["c"] = "classifier miss", ["d"] = "scan quality", ["e"] = "rule too strict or loose",
        ["f"] = "source inconsistency", ["g"] = "tool bug",
    };
}

/// <summary>config/escalation.json: thresholds, never in code.</summary>
public sealed record EscalationConfig
{
    public string Version { get; init; } = "";
    public decimal MaxCostUsd { get; init; }
    public int MaxSegments { get; init; }
    public int FingerprintReviewCount { get; init; }
    public int FingerprintReviewWindowDays { get; init; }
    public double RuleShareOfCarrierDocuments { get; init; }
    public int RuleShareMinDocuments { get; init; }
    public double ReviewRateDelta { get; init; }
    public int ReviewRateBaselineDays { get; init; }
    public int ReviewRateMinDocuments { get; init; }
    public int MaxReopens { get; init; }
    public int DigestHourUtc { get; init; }

    private static readonly Lazy<EscalationConfig> EmbeddedConfig = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LossRun.Config.escalation.json")
            ?? throw new InvalidOperationException("config/escalation.json is not embedded");
        return Parse(stream);
    });

    public static EscalationConfig Embedded => EmbeddedConfig.Value;

    public static EscalationConfig Parse(Stream json) =>
        JsonSerializer.Deserialize<EscalationConfig>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("escalation.json");
}

/// <summary>
/// Keeps document values out of a trace: verifier reasons and rule messages can quote a cell ("total 12,345.00 ≠ 12,000.00",
/// "header 'Clmt Name'"). Digits become <c>#</c>, quoted text becomes <c>"…"</c>, and a reason is cut at 160 characters.
/// </summary>
public static partial class EscalationTraceText
{
    /// <summary>Double or typographic quotes anywhere; single quotes only where a quotation can start (not "carrier's").</summary>
    [GeneratedRegex(@"[""“”][^""“”]{0,200}[""“”]|‘[^’]{0,200}’|(?<=^|[\s(:=\[])'[^']{0,200}'")]
    private static partial Regex Quoted();

    /// <summary>Numbers, amounts and dates, except the digits of a rule id (R-10 stays R-10).</summary>
    [GeneratedRegex(@"(?<!\bR-\d*)\d+(?:[.,]\d+)*")]
    private static partial Regex Digits();

    /// <summary>Parenthesized examples ("(“Clmt” → “Claimant”)", "(row 4: 1,200 ≠ 1,250)") and header lists after "header(s):".</summary>
    [GeneratedRegex(@"\((?!s\))[^()]*\)|(?<=header\(s\)|headers):[^;]*")]
    private static partial Regex Examples();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    public static string Sanitize(string reason)
    {
        var text = Examples().Replace(reason, m => m.Value.StartsWith('(') ? "(…)" : ": …");
        text = Space().Replace(Digits().Replace(Quoted().Replace(text, "\"…\""), "#"), " ").Trim();
        return text.Length <= 160 ? text : text[..159] + "…";
    }

    public static IReadOnlyList<string> Sanitize(IEnumerable<string> reasons) => [.. reasons.Select(Sanitize).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).Take(20)];

    /// <summary>A carrier's name hashed for keys (carrier-level patterns have no format fingerprint).</summary>
    public static string CarrierKey(string? carrier) =>
        "carrier:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes((carrier ?? "").Trim().ToUpperInvariant())))[..12];
}

/// <summary>
/// Merges a signal into the record with its key (pure; the stores only load and save). A repeat of a known case counts
/// only when it brings something new — a document not seen before, or no documents at all (an exception, a rate) — so the
/// hourly detector re-finding the same pattern is not a new occurrence.
/// </summary>
public static class EscalationLedger
{
    public const int MaxDocuments = 50;

    public enum Outcome { Created, Counted, Reopened, Unchanged }

    public static (Escalation Record, Outcome Outcome) Merge(Escalation? existing, EscalationSignal signal, string actor)
    {
        if (existing is null)
        {
            return (new Escalation
            {
                Id = Escalation.IdFor(signal.ReasonCode, signal.FormatFingerprint), ReasonCode = signal.ReasonCode, FormatFingerprint = signal.FormatFingerprint,
                FormatRef = signal.FormatRef, Carrier = signal.Carrier, DocumentIds = [.. signal.DocumentIds.Distinct(StringComparer.Ordinal).Take(MaxDocuments)],
                UploadIds = [.. signal.UploadIds.Distinct(StringComparer.Ordinal).Take(MaxDocuments)], FirstSeen = signal.At, LastSeen = signal.At,
                Count = Math.Max(1, signal.DocumentIds.Distinct(StringComparer.Ordinal).Count()),
                TraceSummary = signal.Trace, Status = EscalationStatuses.New, UpdatedAt = signal.At, UpdatedBy = actor,
            }, Outcome.Created);
        }

        var newDocuments = signal.DocumentIds.Except(existing.DocumentIds, StringComparer.Ordinal).ToList();
        if (signal.DocumentIds.Count > 0 && newDocuments.Count == 0)
        {
            return (existing, Outcome.Unchanged);
        }

        // Brief 30 item 31: a signal about an upload only (an unhandled exception) is one occurrence per upload, not one per retry.
        if (signal.DocumentIds.Count == 0 && signal.UploadIds.Count > 0 && signal.UploadIds.All(u => existing.UploadIds.Contains(u, StringComparer.Ordinal)))
        {
            return (existing, Outcome.Unchanged);
        }

        // A resolved case that happens again is new again (a regression); won't-fix keeps counting quietly.
        var reopened = existing.Status == EscalationStatuses.Resolved;
        var merged = existing with
        {
            FormatRef = signal.FormatRef ?? existing.FormatRef, Carrier = signal.Carrier ?? existing.Carrier,
            DocumentIds = [.. existing.DocumentIds.Concat(newDocuments).TakeLast(MaxDocuments)],
            UploadIds = [.. existing.UploadIds.Concat(signal.UploadIds.Except(existing.UploadIds, StringComparer.Ordinal)).TakeLast(MaxDocuments)],
            LastSeen = signal.At > existing.LastSeen ? signal.At : existing.LastSeen,
            Count = existing.Count + Math.Max(1, newDocuments.Count),
            TraceSummary = signal.Trace,
            Status = reopened ? EscalationStatuses.New : existing.Status,
            ResolutionNote = reopened ? $"reopened: happened again after it was resolved ({existing.ResolutionNote})" : existing.ResolutionNote,
            UpdatedAt = signal.At, UpdatedBy = actor,
        };
        return (merged, reopened ? Outcome.Reopened : Outcome.Counted);
    }

    /// <summary>The CloudEvent body (<c>lossrun.escalation.raised</c> / <c>.updated</c>): identifiers and counts, like the trace.</summary>
    public static object EventBody(Escalation e, string change) => new
    {
        escalationId = e.Id, e.ReasonCode, e.FormatFingerprint, e.FormatRef, e.Status, e.Count, documents = e.DocumentIds.Count, firstSeen = e.FirstSeen, lastSeen = e.LastSeen, change,
    };

    public static OutboxMessage Event(Escalation e, bool raised, string change, string actor, DateTimeOffset at) =>
        OutboxMessage.Event(raised ? OutboxKinds.EscalationRaised : OutboxKinds.EscalationUpdated, null, $"escalations/{e.Id}", EventBody(e, change), actor, at);

    public static string TraceJson(EscalationTrace trace) => JsonSerializer.Serialize(trace, CanonicalJson.Options);
}
