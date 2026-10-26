using System.Globalization;
using System.Text.RegularExpressions;
using LossRun.Contracts;

namespace LossRun.Formats;

/// <summary>
/// When a candidate alias becomes active without a reviewer (ADR 0019 3b). Brief 29: configuration —
/// <c>config/alias-policy.json</c> (5 documents across 3 learned formats, brief 21 Answer 4), overridden by
/// <c>LOSSRUN_ALIAS_MIN_DOCUMENTS</c> / <c>LOSSRUN_ALIAS_MIN_FORMATS</c>. A hand-spec replay counts toward documents, never
/// toward formats, unless <paramref name="CountHandFormats"/> (brief 21 Answer 2).
/// </summary>
public sealed record AliasPolicy(int MinDocuments, int MinFormats, bool CountHandFormats = false)
{
    private static readonly Lazy<AliasPolicy> Configured = new(FromConfiguration);

    /// <summary>The configured policy (the embedded file plus the environment overrides).</summary>
    public static AliasPolicy Default => Configured.Value;

    public static AliasPolicy FromConfiguration()
    {
        using var stream = typeof(AliasPolicy).Assembly.GetManifestResourceStream("LossRun.Config.alias-policy.json")
            ?? throw new InvalidOperationException("Embedded config/alias-policy.json is missing.");
        var config = System.Text.Json.JsonDocument.Parse(stream).RootElement;
        int Value(string env, string key) =>
            Environment.GetEnvironmentVariable(env) is { Length: > 0 } v ? int.Parse(v, CultureInfo.InvariantCulture) : config.GetProperty(key).GetInt32();
        return new AliasPolicy(Value("LOSSRUN_ALIAS_MIN_DOCUMENTS", "minDocuments"), Value("LOSSRUN_ALIAS_MIN_FORMATS", "minFormats"),
            config.TryGetProperty("countHandFormats", out var hand) && hand.GetBoolean());
    }

    private static readonly IReadOnlySet<string> NoFormats = new HashSet<string>();

    /// <summary>
    /// Brief 29 Answers 4: formats the registry knows as hand-written (<c>createdBy == "hand"</c>). Evidence rows are
    /// append-only, so history recorded before the <c>hand-replay</c> marker is corrected in the reading: a hand format never
    /// counts, whatever route its evidence carries. Set with <see cref="AliasPolicyRegistryExtensions.WithRegistryAsync"/>.
    /// </summary>
    public IReadOnlySet<string> HandFormatIds { get; init; } = NoFormats;

    /// <summary>The formats that count toward <see cref="MinFormats"/>: learned ones, plus hand specs when configured.</summary>
    public int FormatsOf(LearnedMapping alias) =>
        alias.Evidence.Where(e => CountHandFormats || (e.Route != AliasEvidenceRoutes.HandReplay && !HandFormatIds.Contains(e.FormatRef.Split('@')[0])))
            .Select(e => e.FormatRef.Split('@')[0]).Distinct(StringComparer.Ordinal).Count();

    public AliasPolicy WithHandFormats(IEnumerable<string> formatIds) => this with { HandFormatIds = formatIds.ToHashSet(StringComparer.Ordinal) };
}

public static class AliasPolicyRegistryExtensions
{
    /// <summary>The policy with the registry's hand-written formats excluded from the format count (brief 29 Answers 4).</summary>
    public static async Task<AliasPolicy> WithRegistryAsync(this AliasPolicy policy, IFormatRegistry registry, CancellationToken cancellationToken = default)
    {
        var hand = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in (await registry.GetIndexAsync(cancellationToken).ConfigureAwait(false)).DistinctBy(e => (e.FormatId, e.Version)))
        {
            if (!hand.Contains(entry.FormatId) && await registry.GetAsync(entry.FormatId, entry.Version, cancellationToken).ConfigureAwait(false) is { CreatedBy: "hand" })
            {
                hand.Add(entry.FormatId);
            }
        }

        return policy with { HandFormatIds = hand };
    }
}

/// <summary>Where a piece of alias evidence came from (<see cref="AliasEvidence.Route"/>).</summary>
public static class AliasEvidenceRoutes
{
    public const string Learn = "learn";
    public const string Replay = "replay";

    /// <summary>A replay of a hand-written spec: counts toward documents, never toward formats (brief 21 Answer 2).</summary>
    public const string HandReplay = "hand-replay";
}

/// <summary>A reviewer's decision on one alias in the confirm-format review: accept, edit (to <see cref="NewField"/>) or reject.</summary>
public sealed record AliasDecision(string Header, string CanonicalField, string Action, string? NewField = null)
{
    public const string Accept = "accept";
    public const string Edit = "edit";
    public const string Reject = "reject";
}

/// <summary>What one lifecycle step changed, for the audit trail (<c>glossary.alias.*</c> events) and run reports.</summary>
public sealed record AliasChange(string Kind, LearnedMapping Alias, string Detail);

/// <summary>
/// ADR 0019: the lifecycle of learned glossary aliases. Pure functions over a <see cref="LearnedGlossary"/>; the registry
/// persists the result (<see cref="IFormatRegistry.UpdateLearnedGlossaryAsync"/>). No model call.
/// </summary>
public static partial class AliasLifecycle
{
    /// <summary>
    /// A learn verified and published a spec: each mapping the seed glossary does not resolve becomes a candidate (or adds its
    /// evidence to the existing alias). A retired alias is not re-learned.
    /// </summary>
    public static List<AliasChange> RecordLearned(LearnedGlossary glossary, IEnumerable<LearnedMapping> introduced, AliasPolicy policy)
    {
        var changes = new List<AliasChange>();
        foreach (var mapping in introduced)
        {
            if (glossary.Find(mapping.Header, mapping.CanonicalField) is not { } alias)
            {
                mapping.Status = AliasStatus.Candidate;
                mapping.Id ??= mapping.Key;
                glossary.Mappings.Add(mapping);
                changes.Add(new AliasChange("glossary.alias.candidate", mapping, $"learned by {mapping.FormatId}@{mapping.FormatVersion} on {mapping.FirstSeenDocumentId}"));
                alias = mapping;
            }
            else if (alias.Status == AliasStatus.Retired)
            {
                continue;
            }
            else
            {
                AddEvidence(alias, mapping.Evidence);
            }

            if (Promote(alias, policy, mapping.FirstSeenUtc) is { } promoted)
            {
                changes.Add(promoted);
            }
        }

        return changes;
    }

    /// <summary>A document verified with <paramref name="spec"/> (a replay accepted): each candidate or active alias it maps gains the document as evidence.</summary>
    public static List<AliasChange> RecordVerified(LearnedGlossary glossary, CarrierFormat spec, string documentId, string route, bool? totalsTie, DateTimeOffset at, AliasPolicy policy)
    {
        var changes = new List<AliasChange>();
        foreach (var (source, target) in spec.ColumnMap.Select(c => (c.Source, c.Target)).Distinct())
        {
            if (glossary.Find(source, target) is not { Status: AliasStatus.Candidate or AliasStatus.Active } alias)
            {
                continue;
            }

            var evidenceRoute = spec.CreatedBy == "hand" && route == AliasEvidenceRoutes.Replay ? AliasEvidenceRoutes.HandReplay : route;
            AddEvidence(alias, [new AliasEvidence { DocumentId = documentId, FormatRef = spec.Ref, Route = evidenceRoute, TotalsTie = totalsTie, AtUtc = at }]);
            if (Promote(alias, policy, at) is { } promoted)
            {
                changes.Add(promoted);
            }
        }

        return changes;
    }

    /// <summary>The confirm-format review decides the aliases its format introduced: accept and edit activate, reject retires.</summary>
    public static List<AliasChange> Review(LearnedGlossary glossary, IEnumerable<AliasDecision> decisions, string user, DateTimeOffset at)
    {
        var changes = new List<AliasChange>();
        foreach (var decision in decisions)
        {
            if (glossary.Find(decision.Header, decision.CanonicalField) is not { } alias)
            {
                throw new InvalidOperationException($"No learned alias \"{decision.Header}\" → {decision.CanonicalField}.");
            }

            // Brief 30 item 17: a retired alias stays retired; accept or edit never re-activates it (reject is a no-op on it).
            if (alias.Status == AliasStatus.Retired && decision.Action is AliasDecision.Accept or AliasDecision.Edit)
            {
                throw new InvalidOperationException($"Learned alias \"{alias.Header}\" → {alias.CanonicalField} is retired (by {alias.RetiredBy}); it cannot be re-activated in a review.");
            }

            switch (decision.Action)
            {
                case AliasDecision.Accept:
                    Activate(alias, user, at, "reviewer");
                    changes.Add(new AliasChange("glossary.alias.accepted", alias, $"accepted by {user}"));
                    break;
                case AliasDecision.Edit when decision.NewField is { Length: > 0 } field && field != alias.CanonicalField:
                    if (glossary.Find(alias.Header, field) is { } other && other != alias)
                    {
                        glossary.Mappings.Remove(other);
                        AddEvidence(alias, other.Evidence);
                    }

                    alias.EditedFrom = alias.CanonicalField;
                    alias.CanonicalField = field;
                    Activate(alias, user, at, "reviewer");
                    changes.Add(new AliasChange("glossary.alias.edited", alias, $"{alias.EditedFrom} → {field} by {user}"));
                    break;
                case AliasDecision.Reject:
                    Retire(alias, user, at, "rejected in the confirm-format review", null);
                    changes.Add(new AliasChange("glossary.alias.rejected", alias, $"rejected by {user}"));
                    break;
                default:
                    throw new InvalidOperationException($"Alias decision \"{decision.Action}\" must be accept, edit (with a new field) or reject.");
            }
        }

        return changes;
    }

    /// <summary>
    /// A reviewer corrected <paramref name="fieldPath"/> on a document read with <paramref name="spec"/>: when the corrected
    /// field is read through a column whose header is a learned (non-retired) alias, that alias is retired and the audit event
    /// of the correction is linked.
    /// </summary>
    public static List<AliasChange> RetireOnCorrection(LearnedGlossary glossary, CarrierFormat spec, string fieldPath, string user, DateTimeOffset at, string auditEventId)
    {
        var changes = new List<AliasChange>();
        if (CanonicalFieldOf(fieldPath) is not { } field)
        {
            return changes;
        }

        foreach (var column in spec.ColumnMap.Where(c => c.Target == field))
        {
            if (glossary.Find(column.Source, field) is { Status: not AliasStatus.Retired } alias)
            {
                Retire(alias, user, at, $"a reviewer corrected {fieldPath}, read through this alias", auditEventId);
                changes.Add(new AliasChange("glossary.alias.retired", alias, $"correction of {fieldPath} (audit event {auditEventId})"));
            }
        }

        return changes;
    }

    /// <summary>"policyPeriods[0].claims[3].indemnity.paid" → "claim.indemnity.paid"; "policyPeriods[1].policyNumber" → "period.policyNumber"; "valuationDate" → "document.valuationDate".</summary>
    public static string? CanonicalFieldOf(string fieldPath)
    {
        var claim = ClaimPath().Match(fieldPath);
        if (claim.Success)
        {
            return $"claim.{claim.Groups["rest"].Value}";
        }

        var period = PeriodPath().Match(fieldPath);
        return period.Success ? $"period.{period.Groups["rest"].Value}" : fieldPath.Contains('[', StringComparison.Ordinal) ? null : $"document.{fieldPath}";
    }

    private static void AddEvidence(LearnedMapping alias, IEnumerable<AliasEvidence> evidence)
    {
        foreach (var e in evidence.Where(e => alias.Evidence.All(x => x.DocumentId != e.DocumentId)))
        {
            alias.Evidence.Add(e);
        }
    }

    private static AliasChange? Promote(LearnedMapping alias, AliasPolicy policy, DateTimeOffset at)
    {
        var formats = policy.FormatsOf(alias);
        if (alias.Status != AliasStatus.Candidate || alias.DistinctDocuments < policy.MinDocuments || formats < policy.MinFormats)
        {
            return null;
        }

        Activate(alias, "system", at, string.Create(CultureInfo.InvariantCulture,
            $"verified on {alias.DistinctDocuments} documents across {formats} {(policy.CountHandFormats ? "" : "learned ")}formats"));
        return new AliasChange("glossary.alias.activated", alias, alias.ActivationReason!);
    }

    private static void Activate(LearnedMapping alias, string user, DateTimeOffset at, string reason)
    {
        alias.Status = AliasStatus.Active;
        (alias.ActivatedBy, alias.ActivatedUtc, alias.ActivationReason) = (user, at, reason);
        if (reason == "reviewer")
        {
            alias.ReviewedBy = user;
        }

        (alias.RetiredBy, alias.RetiredUtc, alias.RetiredReason, alias.RetiredAuditEventId) = (null, null, null, null);
    }

    private static void Retire(LearnedMapping alias, string user, DateTimeOffset at, string reason, string? auditEventId)
    {
        alias.Status = AliasStatus.Retired;
        (alias.RetiredBy, alias.RetiredUtc, alias.RetiredReason, alias.RetiredAuditEventId) = (user, at, reason, auditEventId);
    }

    [GeneratedRegex(@"^policyPeriods\[\d+\]\.claims\[\d+\]\.(?<rest>.+)$")]
    private static partial Regex ClaimPath();

    [GeneratedRegex(@"^policyPeriods\[\d+\]\.(?<rest>[^\[]+)$")]
    private static partial Regex PeriodPath();
}

/// <summary>
/// <c>lossrun glossary export</c> (ADR 0019): the learned aliases as a reviewable <c>glossary.learned.json</c>, sorted by status,
/// field and header, with each alias's provenance and evidence counts. An alias moves into the hand-curated glossary.json
/// only through a pull request (docs/operations/tuning-loop.md, tier 1).
/// </summary>
public static class LearnedGlossaryExport
{
    public const string Version = "glossary-learned-export-0.1.0";

    public static string Json(LearnedGlossary glossary, string source, DateTimeOffset exportedAt)
    {
        var order = new[] { AliasStatus.Active, AliasStatus.Candidate, AliasStatus.Retired };
        var aliases = glossary.Mappings
            .OrderBy(m => Array.IndexOf(order, m.Status)).ThenBy(m => m.CanonicalField, StringComparer.Ordinal).ThenBy(m => m.Header, StringComparer.Ordinal)
            .Select(m => new
            {
                m.Id, m.Header, m.CanonicalField, m.Status, introducedBy = $"{m.FormatId}@{m.FormatVersion}", m.CarrierName, m.FirstSeenDocumentId, m.FirstSeenUtc,
                learnedBy = m.LearnedBy, m.TotalsTie, m.VerificationGate, documents = m.DistinctDocuments, formats = m.DistinctFormats,
                m.ActivatedBy, m.ActivatedUtc, m.ActivationReason, m.RetiredBy, m.RetiredUtc, m.RetiredReason, m.RetiredAuditEventId, m.EditedFrom,
                evidence = m.Evidence.Select(e => new { e.DocumentId, e.FormatRef, e.Route, e.TotalsTie, e.AtUtc }),
            })
            .ToList();
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            exportVersion = Version, sidecarVersion = glossary.SidecarVersion, source, exportedAt,
            counts = order.ToDictionary(s => s, s => glossary.Mappings.Count(m => m.Status == s)),
            aliases,
        }, new System.Text.Json.JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = true });
    }
}

/// <summary>
/// Brief 29 §2 (brief 21 Answer 3): <c>lossrun glossary export --promote</c>. Proposes moving long-lived active aliases into
/// the hand-curated <c>docs/evaluation/glossary.json</c>: active for at least <c>minWeeks</c>, no retirement, at least
/// <c>minDocuments</c> verified documents, not already in the seed. It writes a proposal and a pull-request description and
/// never edits glossary.json: the glossary is rendered into the learn prompt, so a change to it needs its corpus run.
/// </summary>
public static class GlossaryPromotion
{
    public const string Version = "glossary-promote-0.1.0";

    public sealed record Candidate(string Field, string Header, string Source, double Confidence, object Provenance);

    public sealed record Skip(string Field, string Header, string Reason);

    public static (IReadOnlyList<Candidate> Promote, IReadOnlyList<Skip> Skipped) Select(LearnedGlossary learned, string seedGlossaryJson, DateTimeOffset now, int minWeeks,
        int minDocuments, AliasPolicy policy)
    {
        var seed = System.Text.Json.JsonDocument.Parse(seedGlossaryJson).RootElement.GetProperty("fields");
        bool InSeed(string field, string header) => seed.TryGetProperty(field, out var f) && f.TryGetProperty("aliases", out var aliases)
            && aliases.EnumerateArray().Any(a => string.Equals(CanonicalComparer.NormalizeText(a.GetProperty("header").GetString() ?? ""), CanonicalComparer.NormalizeText(header), StringComparison.Ordinal));
        var promote = new List<Candidate>();
        var skipped = new List<Skip>();
        foreach (var m in learned.Mappings.Where(m => m.Status == AliasStatus.Active).OrderBy(m => m.CanonicalField, StringComparer.Ordinal).ThenBy(m => m.Header, StringComparer.Ordinal))
        {
            var weeks = m.ActivatedUtc is { } at ? (now - at).TotalDays / 7 : 0;
            var reason = InSeed(m.CanonicalField, m.Header) ? "already in glossary.json"
                : m.RetiredUtc is not null || m.RetiredReason is not null ? "has been retired"
                : weeks < minWeeks ? string.Create(CultureInfo.InvariantCulture, $"active {weeks:0.0} weeks (< {minWeeks})")
                : m.DistinctDocuments < minDocuments ? string.Create(CultureInfo.InvariantCulture, $"{m.DistinctDocuments} verified documents (< {minDocuments})")
                : null;
            if (reason is not null)
            {
                skipped.Add(new Skip(m.CanonicalField, m.Header, reason));
                continue;
            }

            promote.Add(new Candidate(m.CanonicalField, m.Header, $"learned:adr-0019:{m.FormatId}@{m.FormatVersion}", Math.Round(m.Confidence, 2), new
            {
                aliasId = m.Id, introducedBy = $"{m.FormatId}@{m.FormatVersion}", m.LearnedBy, m.FirstSeenUtc, m.ActivatedBy, m.ActivatedUtc, m.ActivationReason,
                documents = m.DistinctDocuments, formats = m.DistinctFormats, learnedFormats = policy.FormatsOf(m), weeksActive = Math.Round(weeks, 1),
            }));
        }

        return (promote, skipped);
    }

    public static string Json(IReadOnlyList<Candidate> promote, IReadOnlyList<Skip> skipped, string source, DateTimeOffset at, int minWeeks, int minDocuments) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            promoteVersion = Version, source, generatedAt = at, criteria = new { minWeeks, minDocuments, status = "active", retired = false },
            note = "A proposed diff to docs/evaluation/glossary.json: add each alias under fields.<field>.aliases. Apply by pull request with a corpus v5 run attached: the glossary is rendered into the learn prompt (tuning-loop.md §3).",
            additions = promote.Select(c => new { c.Field, alias = new { header = c.Header, source = c.Source, confidence = c.Confidence }, provenance = c.Provenance }),
            skipped,
        }, new System.Text.Json.JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = true });

    public static string PullRequest(IReadOnlyList<Candidate> promote, string source, int minWeeks, int minDocuments)
    {
        var md = new System.Text.StringBuilder();
        md.AppendLine("## Tier 1 (data): promote learned glossary aliases into glossary.json").AppendLine();
        md.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"From `lossrun glossary export --promote` ({source}): aliases **active for at least {minWeeks} weeks, never retired, verified on at least {minDocuments} documents**, not already in the seed (ADR 0019, brief 21 Answer 3).")).AppendLine();
        md.AppendLine("| Field | Header | Introduced by | Documents | Learned formats | Active since |").AppendLine("|---|---|---|---|---|---|");
        foreach (var c in promote)
        {
            var p = System.Text.Json.JsonSerializer.SerializeToElement(c.Provenance, CanonicalJson.Options);
            md.AppendLine($"| `{c.Field}` | “{c.Header}” | {p.GetProperty("introducedBy").GetString()} | {p.GetProperty("documents").GetInt32()} | {p.GetProperty("learnedFormats").GetInt32()} | {p.GetProperty("activatedUtc")} |");
        }

        if (promote.Count == 0)
        {
            md.AppendLine("| — | none qualifies | | | | |");
        }

        md.AppendLine().AppendLine("**Checklist (tuning-loop.md §3):**");
        md.AppendLine("- [ ] Each alias added under `fields.<field>.aliases` in `docs/evaluation/glossary.json` with the source and confidence from `glossary.promote.json`");
        md.AppendLine("- [ ] glossary.json is rendered into the learn prompt (PromptBuilder): attach a corpus v5 run (`eval generate --v5`, `eval run`, `eval score`)");
        md.AppendLine("- [ ] Deck table reproduces; generic guard and sample guard pass");
        md.AppendLine("- [ ] The promoted aliases stay in the learned store (history); they now resolve from the seed first");
        return md.ToString();
    }
}

