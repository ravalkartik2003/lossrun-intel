using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;
using LossRun.Formats;

namespace LossRun.Orchestration;

/// <summary>
/// ADR 0019 in the review flow: the confirm-format review decides the aliases its format introduced, and a correction of a
/// value read through a learned alias retires it. Every change is an audit event (<c>glossary.alias.*</c>, field path
/// <c>glossary/aliases/{id}</c>).
/// </summary>
internal static class LearnedAliasReview
{
    /// <summary>
    /// The format of <paramref name="formatRef"/> was confirmed: the reviewer's alias decisions apply, and every other
    /// candidate the format introduced is accepted with it (confirming a format confirms the mappings it learned).
    /// </summary>
    public static async Task ConfirmAsync(LossRunRuntime runtime, string uploadId, string? documentId, string formatRef, IReadOnlyList<AliasDecision>? decisions,
        string user, string role, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var formatId = formatRef.Split('@')[0];
        var changes = await runtime.Registry.UpdateLearnedGlossaryAsync(g =>
        {
            var explicitDecisions = decisions ?? [];
            var implied = g.IntroducedBy(formatId).Where(a => a.Status == AliasStatus.Candidate
                    && !explicitDecisions.Any(d => LearnedMapping.KeyOf(d.Header, d.CanonicalField) == a.Key))
                .Select(a => new AliasDecision(a.Header, a.CanonicalField, AliasDecision.Accept));
            return AliasLifecycle.Review(g, [.. explicitDecisions, .. implied], user, at);
        }, cancellationToken).ConfigureAwait(false);
        await AuditAsync(runtime, uploadId, documentId, changes, user, role, at, $"confirm-format review of {formatRef}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Overrides on a document read with a learned format: each corrected field read through a learned alias retires it.</summary>
    public static async Task RetireOnCorrectionsAsync(LossRunRuntime runtime, string uploadId, DocumentResult after, IEnumerable<FieldChange> changes, string user, string role,
        DateTimeOffset at, CancellationToken cancellationToken)
    {
        var corrected = changes.Where(c => c.Kind == "override").Select(c => c.FieldPath).Distinct(StringComparer.Ordinal).ToList();
        if (corrected.Count == 0 || after.FormatRef is not { } formatRef || !int.TryParse(formatRef.Split('@').ElementAtOrDefault(1), out var version)
            || await runtime.Registry.GetAsync(formatRef.Split('@')[0], version, cancellationToken).ConfigureAwait(false) is not { } spec)
        {
            return;
        }

        var retired = await runtime.Registry.UpdateLearnedGlossaryAsync(g => corrected
            .SelectMany(path => AliasLifecycle.RetireOnCorrection(g, spec, path, user, at, $"override:{after.DocumentId}@{after.Revision}:{path}"))
            .ToList(), cancellationToken).ConfigureAwait(false);
        await AuditAsync(runtime, uploadId, after.DocumentId, retired, user, role, at, "a correction of a value read through the alias", cancellationToken).ConfigureAwait(false);
    }

    private static async Task AuditAsync(LossRunRuntime runtime, string uploadId, string? documentId, IReadOnlyList<AliasChange> changes, string user, string role,
        DateTimeOffset at, string context, CancellationToken cancellationToken)
    {
        foreach (var change in changes)
        {
            await runtime.RecordStore.AuditAsync(new AuditEvent(at, user, role, change.Kind, uploadId, documentId, $"glossary/aliases/{change.Alias.Id}",
                    Rationale: $"\"{change.Alias.Header}\" → {change.Alias.CanonicalField}: {change.Detail} ({context})"),
                new Actor(user, role, "api"), cancellationToken).ConfigureAwait(false);
        }
    }
}
