using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;
using LossRun.Formats;
using LossRun.Rules;

namespace LossRun.Orchestration;

/// <summary>Brief 26: where escalations go besides the record and the outbox — the host's Application Insights (metric and event).</summary>
public interface IEscalationTelemetry
{
    /// <summary>A case was raised, counted or reopened (the first-occurrence alert reads these events).</summary>
    void Changed(Escalation escalation, string change);

    /// <summary>The open-case gauge <c>lossrun.escalations.open</c> (the daily digest alert reads it).</summary>
    void Open(int count);
}

/// <summary>
/// Brief 26 (ADR 0025): the engineering escalation queue. Single-event triggers are raised by the pipeline; the hourly
/// sweep runs <see cref="EscalationPatterns"/>; reviewers flag documents by hand. Every change is one unit of record,
/// audit row and CloudEvent (except a won't-fix case counting quietly), then the telemetry. Raising never fails the pipeline.
/// </summary>
public sealed class EscalationService(LossRunRuntime runtime)
{
    private const int MaxAttempts = 5;

    private IRecordStore Records => runtime.RecordStore;
    private EscalationConfig Config => runtime.EscalationOptions;

    public async Task<Escalation?> RaiseAsync(EscalationSignal signal, Actor actor, CancellationToken cancellationToken = default)
    {
        var id = Escalation.IdFor(signal.ReasonCode, signal.FormatFingerprint);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var existing = await Records.GetEscalationAsync(id, cancellationToken).ConfigureAwait(false);
            var (record, outcome) = EscalationLedger.Merge(existing, signal, actor.UserId);
            if (outcome == EscalationLedger.Outcome.Unchanged)
            {
                return existing;
            }

            var change = outcome switch { EscalationLedger.Outcome.Created => "raised", EscalationLedger.Outcome.Reopened => "reopened", _ => "counted" };
            IReadOnlyList<OutboxMessage> events = record.Status == EscalationStatuses.WontFix
                ? []
                : [EscalationLedger.Event(record, outcome == EscalationLedger.Outcome.Created, change, actor.UserId, signal.At)];
            var audit = Audit(record, outcome == EscalationLedger.Outcome.Created ? "escalation.raised" : "escalation.updated", actor, signal.At,
                existing is null ? null : new { existing.Count, existing.Status }, new { record.Count, record.Status, change }, signal.Note ?? change,
                signal.UploadIds.FirstOrDefault(), signal.DocumentIds.FirstOrDefault());
            if (await Records.SaveEscalationAsync(record, existing?.Version ?? 0, audit, actor, events, cancellationToken).ConfigureAwait(false))
            {
                runtime.PublishSoon(events);
                runtime.EscalationTelemetry?.Changed(record, change);
                return record with { Version = (existing?.Version ?? 0) + 1 };
            }
        }

        throw new InvalidOperationException($"escalation {id}: {MaxAttempts} concurrent writers in a row");
    }

    /// <summary>For the pipeline: raises and swallows any failure (an escalation must never fail the document it reports).</summary>
    public async Task TryRaiseAsync(IEnumerable<EscalationSignal> signals, CancellationToken cancellationToken = default)
    {
        foreach (var signal in signals)
        {
            try
            {
                await RaiseAsync(signal, Actor.System, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Console.Error.WriteLine($"escalation {signal.ReasonCode} not recorded: {e.GetType().Name}");
            }
        }
    }

    /// <summary>Status change with a note (the note is required: it is the audit rationale and the resolution note).</summary>
    public async Task<Escalation?> SetStatusAsync(string id, string status, string note, Actor actor, CancellationToken cancellationToken = default)
    {
        var at = runtime.Clock.GetUtcNow();
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (await Records.GetEscalationAsync(id, cancellationToken).ConfigureAwait(false) is not { } existing)
            {
                return null;
            }

            var record = existing with { Status = status, ResolutionNote = note, UpdatedAt = at, UpdatedBy = actor.UserId };
            IReadOnlyList<OutboxMessage> events = [EscalationLedger.Event(record, false, $"status:{status}", actor.UserId, at)];
            var audit = Audit(record, "escalation.status", actor, at, existing.Status, status, note);
            if (await Records.SaveEscalationAsync(record, existing.Version, audit, actor, events, cancellationToken).ConfigureAwait(false))
            {
                runtime.PublishSoon(events);
                runtime.EscalationTelemetry?.Changed(record, $"status:{status}");
                return record with { Version = existing.Version + 1 };
            }
        }

        throw new InvalidOperationException($"escalation {id}: {MaxAttempts} concurrent writers in a row");
    }

    /// <summary>
    /// "Create tuning item": the case goes into the tuning loop under a cause category (a)–(g), as a file the next
    /// <c>lossrun tune</c> batch reads (<c>tuning/items/{id}.json</c>); a new case moves to investigating.
    /// </summary>
    public async Task<(Escalation Escalation, TuningItem Item)?> CreateTuningItemAsync(string id, string category, string note, Actor actor,
        CancellationToken cancellationToken = default)
    {
        if (await Records.GetEscalationAsync(id, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return null;
        }

        var at = runtime.Clock.GetUtcNow();
        var item = new TuningItem($"ti-{Guid.NewGuid():N}"[..15], category, current.Id, current.ReasonCode, current.FormatFingerprint, current.FormatRef, current.DocumentIds, note,
            at, actor.UserId);
        await runtime.Store.PutJsonAsync(Containers.Extractions, TuningItemPath(item.Id), item, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var existing = await Records.GetEscalationAsync(id, cancellationToken).ConfigureAwait(false) ?? current;
            var record = existing with
            {
                TuningItemRef = item.Id, Status = existing.Status == EscalationStatuses.New ? EscalationStatuses.Investigating : existing.Status, UpdatedAt = at, UpdatedBy = actor.UserId,
            };
            IReadOnlyList<OutboxMessage> events = [EscalationLedger.Event(record, false, "tuning-item", actor.UserId, at)];
            var audit = Audit(record, "escalation.tuning-item", actor, at, existing.TuningItemRef, $"{item.Id} ({category}: {TuningItem.Categories[category]})", note);
            if (await Records.SaveEscalationAsync(record, existing.Version, audit, actor, events, cancellationToken).ConfigureAwait(false))
            {
                runtime.PublishSoon(events);
                return (record with { Version = existing.Version + 1 }, item);
            }
        }

        throw new InvalidOperationException($"escalation {id}: {MaxAttempts} concurrent writers in a row");
    }

    public static string TuningItemPath(string id) => $"tuning/items/{StorePaths.Safe(id)}.json";

    public async Task<IReadOnlyList<TuningItem>> TuningItemsAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<TuningItem>();
        foreach (var path in await runtime.Store.ListAsync(Containers.Extractions, "tuning/items/", cancellationToken).ConfigureAwait(false))
        {
            if (await runtime.Store.GetJsonAsync<TuningItem>(Containers.Extractions, path, cancellationToken).ConfigureAwait(false) is { } item)
            {
                items.Add(item);
            }
        }

        return [.. items.OrderBy(i => i.CreatedAt)];
    }

    /// <summary>"Flag for engineering" on a document: the reviewer's note is the rationale; the trace is built from the stored result.</summary>
    public async Task<Escalation?> FlagAsync(string documentId, string note, Actor actor, CancellationToken cancellationToken = default)
    {
        if (await Records.GetResultAsync(documentId, cancellationToken).ConfigureAwait(false) is not { } result)
        {
            return null;
        }

        var review = await Records.GetReviewAsync(documentId, cancellationToken).ConfigureAwait(false);
        var signal = EscalationTriggers.ForDocument(EscalationReasons.ManualFlag, result, runtime.Clock.GetUtcNow(), review?.Reasons ?? result.Routing?.Reasons ?? [], "review") with
        {
            Note = note,
        };
        return await RaiseAsync(signal, actor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The hourly sweep: detect patterns over the observation window, raise what is new, report the open count.</summary>
    public async Task<EscalationSweep> SweepAsync(CancellationToken cancellationToken = default)
    {
        var now = runtime.Clock.GetUtcNow();
        var since = now.AddDays(-(Config.ReviewRateBaselineDays + Config.FingerprintReviewWindowDays));
        var observations = await Records.EscalationObservationsAsync(since, cancellationToken).ConfigureAwait(false);
        var signals = EscalationPatterns.Detect(observations, Config, now);
        var changed = 0;
        foreach (var signal in signals)
        {
            var before = await Records.GetEscalationAsync(Escalation.IdFor(signal.ReasonCode, signal.FormatFingerprint), cancellationToken).ConfigureAwait(false);
            var after = await RaiseAsync(signal, Actor.System, cancellationToken).ConfigureAwait(false);
            changed += after?.Version != before?.Version ? 1 : 0;
        }

        var open = (await Records.ListEscalationsAsync(cancellationToken).ConfigureAwait(false)).Count(e => EscalationStatuses.IsOpen(e.Status));
        runtime.EscalationTelemetry?.Open(open);
        return new EscalationSweep(observations.Count, signals.Count, changed, open, now);
    }

    private static AuditEvent Audit(Escalation e, string action, Actor actor, DateTimeOffset at, object? oldValue, object? newValue, string rationale,
        string? uploadId = null, string? documentId = null) =>
        new(at, actor.UserId, actor.Role, action, uploadId ?? "", documentId, $"escalations/{e.Id}",
            oldValue is null ? null : JsonSerializer.SerializeToElement(oldValue, CanonicalJson.Options),
            newValue is null ? null : JsonSerializer.SerializeToElement(newValue, CanonicalJson.Options), rationale);
}

public sealed record EscalationSweep(int Documents, int Signals, int Changed, int Open, DateTimeOffset At);

/// <summary>The single-event triggers, as pure functions of what the pipeline already has.</summary>
public static class EscalationTriggers
{
    /// <summary>A signal about one document: its fingerprint (or format, or id) as the key, failed rules, scrubbed reasons, counts.</summary>
    public static EscalationSignal ForDocument(string reason, DocumentResult result, DateTimeOffset at, IEnumerable<string> reasons, string step,
        IReadOnlyDictionary<string, decimal>? extraCounts = null)
    {
        var routing = result.Routing;
        var counts = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["claims"] = result.Document.PolicyPeriods.Sum(p => p.Claims.Count),
            ["periods"] = result.Document.PolicyPeriods.Count,
            ["hardFailures"] = result.RuleOutcomes.Count(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard),
            ["softFailures"] = result.RuleOutcomes.Count(o => o.Status == RuleStatus.Fail && o.Severity != RuleSeverity.Hard),
            ["unmappedHeaders"] = routing?.UnmappedHeaders ?? 0,
            ["modelCalls"] = routing?.ModelCalls ?? 0,
            ["costUsd"] = result.Cost?.CostUsd ?? 0,
        };
        foreach (var (k, v) in extraCounts ?? new Dictionary<string, decimal>())
        {
            counts[k] = v;
        }

        return new EscalationSignal(reason, routing?.FingerprintKey ?? result.FormatRef ?? routing?.FormatRef ?? $"document:{result.DocumentId}", at, new EscalationTrace
        {
            RuleIds = [.. result.RuleOutcomes.Where(o => o.Status == RuleStatus.Fail).Select(o => o.RuleId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            Reasons = EscalationTraceText.Sanitize(reasons), Counts = counts, Step = step,
        })
        {
            FormatRef = result.FormatRef ?? routing?.FormatRef, Carrier = result.Document.CarrierName, DocumentIds = [result.DocumentId], UploadIds = [result.UploadId],
        };
    }

    /// <summary>After validate-and-persist: learn exhausted, replay failure on an active format, cost over the limit.</summary>
    public static IReadOnlyList<EscalationSignal> AfterPersist(DocumentResult result, RouteResult route, LearnResult? learned, string finalTier, EscalationConfig config,
        DateTimeOffset at)
    {
        var signals = new List<EscalationSignal>();
        if (learned is { Published: false })
        {
            var delta = route.Tier == "ReplayWithDelta";
            signals.Add(ForDocument(delta ? EscalationReasons.DeltaExhausted : EscalationReasons.LearnExhausted, result, at, learned.Reasons, delta ? "delta" : "learn",
                new Dictionary<string, decimal> { ["learnModelCalls"] = learned.ModelCalls }));
        }

        if (result.Routing is { FormatRef: not null, FormatStatus: FormatLifecycle.Active, HardFailures: > 0 } routing && finalTier == "Review")
        {
            signals.Add(ForDocument(EscalationReasons.ReplayFailedActiveFormat, result, at, routing.Reasons, "replay"));
        }

        // Brief 30 item 22: a metered call whose model has no price: the document's cost is unknown, not $0 (keyed by the meters, never values).
        if (result.Cost is { UnpricedLines: > 0 } unpriced)
        {
            signals.Add(ForDocument(EscalationReasons.MissingPrice, result, at, [$"no price in config/azure-prices.json for {string.Join(", ", unpriced.Unpriced)}"], "cost",
                new Dictionary<string, decimal> { ["unpricedLines"] = unpriced.UnpricedLines }) with { FormatFingerprint = $"price:{string.Join("|", unpriced.Unpriced.Order(StringComparer.Ordinal))}" });
        }

        if (result.Cost is { } cost && cost.CostUsd > config.MaxCostUsd)
        {
            signals.Add(ForDocument(EscalationReasons.CostOverLimit, result, at, [$"cost above the {config.MaxCostUsd} USD limit"], finalTier.ToLowerInvariant(),
                new Dictionary<string, decimal> { ["limitUsd"] = config.MaxCostUsd }));
        }

        return signals;
    }

    /// <summary>The classifier split one file into more segments than <see cref="EscalationConfig.MaxSegments"/>.</summary>
    public static EscalationSignal? Segments(string uploadId, string documentId, string fingerprint, int segments, int pages, EscalationConfig config, DateTimeOffset at) =>
        segments <= config.MaxSegments ? null
            : new EscalationSignal(EscalationReasons.TooManySegments, fingerprint, at, new EscalationTrace
            {
                Counts = new Dictionary<string, decimal> { ["segments"] = segments, ["pages"] = pages, ["limit"] = config.MaxSegments }, Step = "classify",
            }) { DocumentIds = [documentId], UploadIds = [uploadId] }; // the file's id: a retried parse is not a second occurrence

    /// <summary>An orchestration step failed with an exception nothing handled: keyed by the step and the exception type, never its message.</summary>
    /// <param name="classification">Brief 33: "permanent: InvalidOperationException" or "unknown: …" (type only, never the message).</param>
    public static EscalationSignal Exception(string uploadId, string? step, string? exceptionType, DateTimeOffset at, string? classification = null) =>
        new(EscalationReasons.UnhandledException, $"exception:{step ?? "unknown"}:{exceptionType ?? "unknown"}", at, new EscalationTrace
        {
            Step = step, ExceptionType = exceptionType, Counts = new Dictionary<string, decimal>(),
            Reasons = classification is null ? [] : [classification],
        }) { UploadIds = [uploadId] };
}

/// <summary>
/// The hourly pattern detector (pure): repeated review of one fingerprint, one rule failing on most of a carrier's
/// documents, a carrier's review rate above its trailing baseline, and documents reopened too often.
/// </summary>
public static class EscalationPatterns
{
    public static IReadOnlyList<EscalationSignal> Detect(IReadOnlyList<EscalationObservation> observations, EscalationConfig config, DateTimeOffset now)
    {
        var signals = new List<EscalationSignal>();
        var recentFrom = now.AddDays(-config.FingerprintReviewWindowDays);
        var baselineFrom = recentFrom.AddDays(-config.ReviewRateBaselineDays);
        var recent = observations.Where(o => o.ProcessedAt >= recentFrom && o.ProcessedAt <= now).ToList();

        // Same fingerprint to review ≥ N times in the window.
        foreach (var group in recent.Where(o => o.Reviewed && o.Fingerprint is not null).GroupBy(o => o.Fingerprint!, StringComparer.Ordinal))
        {
            var docs = group.OrderBy(o => o.ProcessedAt).ToList();
            if (docs.Count >= config.FingerprintReviewCount)
            {
                signals.Add(Signal(EscalationReasons.RepeatedReview, group.Key, now, docs, "review",
                    new Dictionary<string, decimal> { ["reviews"] = docs.Count, ["windowDays"] = config.FingerprintReviewWindowDays }));
            }
        }

        // Same rule on ≥ share of a carrier's documents (minimum count), over the baseline window.
        foreach (var carrier in observations.Where(o => o.ProcessedAt >= baselineFrom && o.Carrier is not null).GroupBy(o => o.Carrier!, StringComparer.OrdinalIgnoreCase))
        {
            var docs = carrier.ToList();
            if (docs.Count < config.RuleShareMinDocuments)
            {
                continue;
            }

            foreach (var rule in docs.SelectMany(d => d.FailedRuleIds.Distinct(StringComparer.Ordinal)).GroupBy(r => r, StringComparer.Ordinal))
            {
                var share = (double)rule.Count() / docs.Count;
                if (share >= config.RuleShareOfCarrierDocuments)
                {
                    var failing = docs.Where(d => d.FailedRuleIds.Contains(rule.Key, StringComparer.Ordinal)).OrderBy(d => d.ProcessedAt).ToList();
                    signals.Add(Signal(EscalationReasons.RuleDominant, $"{EscalationTraceText.CarrierKey(carrier.Key)}/{rule.Key}", now, failing, "validate",
                        new Dictionary<string, decimal> { ["documents"] = docs.Count, ["failing"] = failing.Count, ["share"] = Math.Round((decimal)share, 3) }, [rule.Key]) with
                    {
                        Carrier = carrier.Key,
                    });
                }
            }
        }

        // A carrier's review rate in the window above its trailing baseline by the configured delta.
        foreach (var carrier in observations.Where(o => o.Carrier is not null && o.ProcessedAt >= baselineFrom).GroupBy(o => o.Carrier!, StringComparer.OrdinalIgnoreCase))
        {
            var now7 = carrier.Where(o => o.ProcessedAt >= recentFrom).ToList();
            var before = carrier.Where(o => o.ProcessedAt < recentFrom).ToList();
            if (now7.Count < config.ReviewRateMinDocuments || before.Count < config.ReviewRateMinDocuments)
            {
                continue;
            }

            var (rate, baseline) = ((double)now7.Count(o => o.Reviewed) / now7.Count, (double)before.Count(o => o.Reviewed) / before.Count);
            if (rate - baseline >= config.ReviewRateDelta)
            {
                signals.Add(Signal(EscalationReasons.ReviewRateSpike, EscalationTraceText.CarrierKey(carrier.Key), now, [.. now7.Where(o => o.Reviewed).OrderBy(o => o.ProcessedAt)], "review",
                    new Dictionary<string, decimal>
                    {
                        ["reviewRate"] = Math.Round((decimal)rate, 3), ["baselineRate"] = Math.Round((decimal)baseline, 3), ["documents"] = now7.Count, ["baselineDocuments"] = before.Count,
                    }) with { Carrier = carrier.Key });
            }
        }

        // A document reopened more than the limit.
        foreach (var o in observations.Where(o => o.Reopens > config.MaxReopens))
        {
            signals.Add(Signal(EscalationReasons.RepeatedReopen, o.Fingerprint ?? o.FormatRef ?? $"document:{o.DocumentId}", now, [o], "review",
                new Dictionary<string, decimal> { ["reopens"] = o.Reopens, ["limit"] = config.MaxReopens }));
        }

        return signals;
    }

    private static EscalationSignal Signal(string reason, string fingerprint, DateTimeOffset at, IReadOnlyList<EscalationObservation> docs, string step,
        Dictionary<string, decimal> counts, IReadOnlyList<string>? ruleIds = null) =>
        new(reason, fingerprint, at, new EscalationTrace
        {
            RuleIds = ruleIds ?? [.. docs.SelectMany(d => d.FailedRuleIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], Counts = counts, Step = step,
        })
        {
            FormatRef = docs.Select(d => d.FormatRef).LastOrDefault(f => f is not null), Carrier = docs.Select(d => d.Carrier).LastOrDefault(c => c is not null),
            DocumentIds = [.. docs.Select(d => d.DocumentId)], UploadIds = [.. docs.Select(d => d.UploadId).Distinct(StringComparer.Ordinal)],
        };
}
