using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;
using LossRun.Formats;
using LossRun.Formats.Replay;
using LossRun.Ingestion;
using LossRun.Ingestion.ContentUnderstanding;
using LossRun.Ingestion.Metering;
using LossRun.Learning;
using LossRun.Rules;

namespace LossRun.Orchestration;

/// <summary>Everything the activities need; built once per host (Functions DI) or per test.</summary>
/// <param name="Embeddings">Per-upload embedding client (metered on the upload's meter); null disables the semantic tier.</param>
/// <param name="LearnModel">Per-upload learn/delta model client (metered on the upload's meter).</param>
/// <param name="Records">System of record (brief 09): SQL when configured, else the Blob layout of <paramref name="Store"/>.</param>
public sealed record LossRunRuntime(
    IObjectStore Store,
    IFormatRegistry Registry,
    IContentUnderstandingService ContentUnderstanding,
    IngestionOptions Ingestion,
    LearnOptions Learn,
    Func<ICostMeter, IEmbeddingService?> Embeddings,
    Func<ICostMeter, ILearnModel> LearnModel,
    IWebhookSender Webhooks,
    TimeProvider Clock,
    PriceTable? Prices = null,
    Glossary? Glossary = null,
    IRecordStore? Records = null,
    IChatIndexQueue? ChatIndex = null,
    IOutboxRelay? Outbox = null,
    EscalationConfig? Escalation = null,
    IEscalationTelemetry? EscalationTelemetry = null)
{
    private IRecordStore? _blobRecords;

    public IRecordStore RecordStore => Records ?? (_blobRecords ??= new BlobRecordStore(Store));

    public PriceTable PriceTable => Prices ?? PriceTable.Embedded;

    /// <summary>Brief 26: escalation thresholds (config/escalation.json, embedded unless the host passes others).</summary>
    public EscalationConfig EscalationOptions => Escalation ?? EscalationConfig.Embedded;
    public Glossary GlossaryOrSeed => Glossary ?? CanonicalData.Glossary;

    /// <summary>
    /// Records a command or notification through the transactional outbox (brief 12b): the audit row and the outbox rows
    /// in one transaction, then a best-effort publish (the relay's timer sweep publishes anything this misses).
    /// </summary>
    /// <param name="bump">Brief 24 step 3: bump the quote's revision in the same unit (the rows carry <see cref="QuoteRevisions.Token"/>).</param>
    public async Task SendAsync(AuditEvent audit, Actor actor, IReadOnlyList<OutboxMessage> messages, CancellationToken cancellationToken = default, QuoteRevisionBump? bump = null)
    {
        await RecordStore.AuditWithOutboxAsync(audit, actor, messages, cancellationToken, bump).ConfigureAwait(false);
        PublishSoon(messages);
    }

    /// <summary>
    /// Publishes committed outbox rows without making the caller wait (brief 12b load test): if this is lost with the process,
    /// the 30 s sweep publishes them. Never throws (OutboxRelay).
    /// </summary>
    public void PublishSoon(IReadOnlyList<OutboxMessage> messages)
    {
        if (Outbox is { } relay && messages.Count > 0)
        {
            IReadOnlyList<string> ids = [.. messages.Select(m => m.Id)];
            _ = Task.Run(() => relay.RelayAsync(ids, CancellationToken.None), CancellationToken.None);
        }
    }

    /// <summary>The <c>review.decided</c> CloudEvent of a decision (committed with it by <see cref="IRecordStore.TryApplyDecisionAsync"/>).</summary>
    /// <remarks>Brief 24 step 3: on a quote it carries the quote revision the decision moved to.</remarks>
    public static OutboxMessage DecidedEvent(string? quoteId, string uploadId, DocumentResult after, ReviewDecision decision) =>
        OutboxMessage.Event(OutboxKinds.ReviewDecided, quoteId, $"documents/{decision.DocumentId}", new
        {
            decision.DocumentId, uploadId, quoteId, decision.Action, overrides = decision.Overrides.Count, revision = after.Revision, decision.User, decision.Role,
            decidedAt = decision.At, quoteRevision = quoteId is null ? null : QuoteRevisions.Token,
        }, decision.User, decision.At);

    /// <summary>
    /// Brief 24 step 3: the rows a decision on a quote's document commits with (ADR 0024): <c>review.decided</c>,
    /// <c>quote.revised</c>, and <c>quote.payload</c> unless another document of the upload still waits for its review
    /// (the upload's first push goes out when its last document review is decided).
    /// </summary>
    public async Task<IReadOnlyList<OutboxMessage>> DecisionOutboxAsync(string? quoteId, string uploadId, DocumentResult before, DocumentResult after, ReviewDecision decision,
        string cause, bool push, CancellationToken cancellationToken)
    {
        // Brief 30 item 6: every row keyed on (decision, kind), so a retried decision writes the same ids and the store recognises it.
        var rows = new List<OutboxMessage> { DecidedEvent(quoteId, uploadId, after, decision).KeyedBy(decision.Key) };
        if (quoteId is null)
        {
            return rows;
        }

        rows.Add(QuotePushes.Revised(quoteId, cause, decision.DocumentId, decision.User, decision.At).KeyedBy(decision.Key));
        if (push)
        {
            var readiness = await QuotePushes.ReadinessAsync(RecordStore, quoteId, after, cancellationToken).ConfigureAwait(false);
            rows.Add(QuotePushes.Payload(quoteId, cause, readiness,
                [new QuotePushes.ChangedDocument(after.DocumentId, after.Revision, after.ReviewState, QuotePushes.FieldStatusChanges(before, after))],
                new System.Text.Json.Nodes.JsonObject
                {
                    ["uploadId"] = uploadId, ["documentId"] = decision.DocumentId, ["action"] = decision.Action, ["decidedBy"] = decision.User, ["decidedAt"] = decision.At.ToString("O"),
                }, decision.User, decision.At, $"documents/{decision.DocumentId}").KeyedBy(decision.Key));
        }

        return rows;
    }

    /// <summary>A notification with no audit row of its own (its cause is audited where it happened).</summary>
    public Task NotifyAsync(OutboxMessage message, CancellationToken cancellationToken = default) =>
        SendAsync(new AuditEvent(message.CreatedAt, Actor.System.UserId, Actor.System.Role, "", "", null), Actor.System, [message], cancellationToken);
}

/// <summary>
/// Brief 33: the fingerprint embedding of a document, kept next to it (<c>fingerprint.json</c>: vector, model, text normalization, text hash).
/// A call for the same text, model and normalization returns the stored vector with no usage event; anything else calls the service and stores
/// the result. Only a real call is metered.
/// </summary>
public sealed class CachedEmbeddingService(LossRun.Formats.IEmbeddingService inner, IObjectStore store, string uploadId, string? model) : LossRun.Formats.IEmbeddingService
{
    public sealed record StoredEmbedding(float[] Vector, string Model, string Normalization, string TextSha256);

    public async Task<LossRun.Formats.EmbeddingResult> EmbedAsync(string text, string documentId, string runId, CancellationToken cancellationToken)
    {
        var path = StorePaths.Document(uploadId, documentId, "fingerprint.json");
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        if (await store.GetJsonAsync<StoredEmbedding>(Containers.Extractions, path, cancellationToken).ConfigureAwait(false) is { } cached
            && cached.TextSha256 == hash && cached.Normalization == LossRun.Formats.FingerprintText.Normalization && (model is null || cached.Model == model))
        {
            return new LossRun.Formats.EmbeddingResult(cached.Vector, cached.Model, TimeSpan.Zero, []);
        }

        var result = await inner.EmbedAsync(text, documentId, runId, cancellationToken).ConfigureAwait(false);
        await store.PutJsonAsync(Containers.Extractions, path, new StoredEmbedding(result.Vector, result.Model, LossRun.Formats.FingerprintText.Normalization, hash), cancellationToken)
            .ConfigureAwait(false);
        return result;
    }
}

/// <summary>Raw CU results go next to the upload's documents.</summary>
public sealed class StoreRawResultStore(IObjectStore store, string uploadId) : IRawResultStore
{
    public async Task<string> SaveAsync(string documentId, string name, string json, CancellationToken cancellationToken)
    {
        var path = StorePaths.Document(uploadId, documentId, name);
        await store.PutAsync(Containers.Extractions, path, BinaryData.FromString(json), true, cancellationToken).ConfigureAwait(false);
        return $"{Containers.Extractions}/{path}";
    }

    public async Task<RawResult?> TryGetAsync(string documentId, string name, CancellationToken cancellationToken)
    {
        var path = StorePaths.Document(uploadId, documentId, name);
        return await store.GetAsync(Containers.Extractions, path, cancellationToken).ConfigureAwait(false) is { } data
            ? new RawResult(data.ToString(), $"{Containers.Extractions}/{path}") : null;
    }
}

/// <summary>The activity bodies (brief 07 §1). Each is idempotent: a retried activity rewrites the same blobs.</summary>
public sealed class UploadActivities(LossRunRuntime runtime)
{
    private IObjectStore Store => runtime.Store;

    private IRecordStore Records => runtime.RecordStore;

    // ------------------------------------------------------------------ intake

    public async Task<IReadOnlyList<FileWork>> ExpandUploadAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        var manifest = await UpdateManifestAsync(uploadId, m => m.Status = UploadStatuses.Processing, cancellationToken).ConfigureAwait(false);
        return manifest.Files.Select(f => new FileWork(uploadId, f.FileName)).ToList();
    }

    /// <summary>Sniff, unlock, expand archives, classify bundles, CU layout or native spreadsheet read (brief 04 pipeline) for one file.</summary>
    public async Task<ParseResult> ParseFileAsync(FileWork file, CancellationToken cancellationToken = default)
    {
        var bytes = (await Store.GetAsync(Containers.Uploads, StorePaths.Source(file.UploadId, file.FileName), cancellationToken).ConfigureAwait(false))?.ToArray()
            ?? throw new InvalidOperationException($"Source {file.FileName} of {file.UploadId} is missing.");
        var pipeline = new IngestionPipeline(runtime.ContentUnderstanding, new UploadCostMeter(Records, file.UploadId), new StoreRawResultStore(Store, file.UploadId),
            runtime.PriceTable, await runtime.Registry.GlossaryAsync(runtime.GlossaryOrSeed, cancellationToken).ConfigureAwait(false), runtime.Ingestion with { DocumentIdScope = file.UploadId });
        var result = await pipeline.IngestAsync(file.FileName, bytes, cancellationToken).ConfigureAwait(false);

        var items = new List<ManifestItem>();
        var lossRuns = new List<DocumentWork>();
        foreach (var graph in result.Documents)
        {
            await Store.PutJsonAsync(Containers.Extractions, StorePaths.Document(file.UploadId, graph.DocumentId, "graph.json"), graph, cancellationToken).ConfigureAwait(false);
            await Records.IndexDocumentAsync(graph.DocumentId, file.UploadId, cancellationToken).ConfigureAwait(false);
            var range = graph.SourceFile.FirstPage is { } first ? $"{first}-{graph.SourceFile.LastPage}" : null;
            if (graph.Segments.Count == 0 || graph.SourceFile.FirstPage is not null)
            {
                items.Add(new ManifestItem
                {
                    DocumentId = graph.DocumentId, FileName = file.FileName, ParentDocumentId = graph.SourceFile.ParentDocumentId, PageRange = range,
                    Classification = "LossRun", Status = ItemStatuses.Processing, BlobPath = StorePaths.Document(file.UploadId, graph.DocumentId, "graph.json"),
                });
                lossRuns.Add(new DocumentWork(file.UploadId, graph.DocumentId, file.FileName, graph.SourceFile.Kind == FileKind.Pdf));
                continue;
            }

            // Brief 26: a bundle split into more segments than the limit goes to engineering (keyed by the file's fingerprint).
            if (EscalationTriggers.Segments(file.UploadId, graph.DocumentId, FingerprintText.Compute(graph).ShortKey, graph.Segments.Count, graph.Segments.Max(s => s.EndPage),
                    runtime.EscalationOptions, runtime.Clock.GetUtcNow()) is { } segmented)
            {
                await new EscalationService(runtime).TryRaiseAsync([segmented], cancellationToken).ConfigureAwait(false);
            }

            // A classified bundle: its other segments are kept, not processed (ADR 0005).
            foreach (var segment in graph.Segments.Where(s => s.ChildDocumentId is null))
            {
                // Indexed too: chat cites kept segments, and their pages resolve through this upload (brief 12).
                await Records.IndexDocumentAsync($"{graph.DocumentId}-p{segment.StartPage}-{segment.EndPage}", file.UploadId, cancellationToken).ConfigureAwait(false);
                items.Add(new ManifestItem
                {
                    DocumentId = $"{graph.DocumentId}-p{segment.StartPage}-{segment.EndPage}", FileName = file.FileName, ParentDocumentId = graph.DocumentId,
                    PageRange = $"{segment.StartPage}-{segment.EndPage}", Classification = segment.Category, Status = ItemStatuses.KeptNotProcessed,
                    Reason = segment.Reason ?? segment.DerivedDecision ?? $"classified as {segment.Category} (confidence {segment.Confidence?.ToString("0.00", CultureInfo.InvariantCulture) ?? "n/a"}); kept, not extracted",
                    BlobPath = $"{Containers.Uploads}/{StorePaths.Source(file.UploadId, file.FileName)}",
                });
            }
        }

        items.AddRange(result.Unsupported.Select(u => new ManifestItem
        {
            DocumentId = "unsupported-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{file.UploadId}/{u.FileName}")))[..12],
            FileName = u.FileName, Classification = u.Kind.ToString(), Status = ItemStatuses.Unsupported, Reason = u.Reason,
            BlobPath = $"{Containers.Uploads}/{StorePaths.Source(file.UploadId, file.FileName)}",
        }));
        if (items.Any(i => i.Status == ItemStatuses.KeptNotProcessed) && runtime.ChatIndex is { } index)
        {
            await index.EnqueueAsync(file.UploadId, cancellationToken).ConfigureAwait(false); // kept items are searchable at once
        }

        return new ParseResult(items, lossRuns, result.CacheMisses.Count > 0 ? result.CacheMisses : null);
    }

    /// <summary>Brief 23 follow-up: the manifest of an upload whose orchestration failed says so (never "processing" for good).</summary>
    /// <remarks>Brief 26: the failure is also an engineering escalation, keyed by the failed step and the exception type (never its message).</remarks>
    public async Task FailUploadAsync(FailInput input, CancellationToken cancellationToken = default)
    {
        await UpdateManifestAsync(input.UploadId, m =>
        {
            if (m.Status is not (UploadStatuses.Completed or UploadStatuses.AwaitingReview))
            {
                m.Status = UploadStatuses.Failed;
                m.CompletedAt ??= runtime.Clock.GetUtcNow();
            }
        }, cancellationToken).ConfigureAwait(false);
        await new EscalationService(runtime).TryRaiseAsync([EscalationTriggers.Exception(input.UploadId, input.Step, input.ErrorType, runtime.Clock.GetUtcNow(), input.Classification)],
            cancellationToken).ConfigureAwait(false);
    }

    public Task RecordItemsAsync(ItemsInput input, CancellationToken cancellationToken = default) =>
        UpdateManifestAsync(input.UploadId, m =>
        {
            foreach (var item in input.Items)
            {
                m.Items.RemoveAll(i => i.DocumentId == item.DocumentId);
                m.Items.Add(item);
            }
        }, cancellationToken);

    // ------------------------------------------------------------------ format memory

    public async Task<RouteResult> RouteAsync(DocumentWork work, CancellationToken cancellationToken = default)
    {
        var (graph, submission, meter) = await LoadAsync(work, cancellationToken).ConfigureAwait(false);
        // Brief 33: the fingerprint embedding is kept per document, so a retried Route and the deliberate re-route after a learn slot do not pay again.
        var embeddings = runtime.Embeddings(meter) is { } service ? new CachedEmbeddingService(service, Store, work.UploadId, runtime.Learn.EmbeddingModel) : null;
        var memory = new FormatMemory(runtime.Registry, embeddings, aliasPolicy: runtime.Learn.AliasPolicy);
        var context = await RuleContexts.ForUploadAsync(Records, work.UploadId, cancellationToken).ConfigureAwait(false); // brief 29: R-15, R-25
        var (match, replay, decision) = await memory.ProcessAsync(graph, work.UploadId, runtime.Clock.GetUtcNow(), context, submission, cancellationToken).ConfigureAwait(false);
        decision = decision with { FingerprintKey = FingerprintText.Compute(graph).ShortKey }; // brief 26: the escalation key when no format matched
        await Store.PutJsonAsync(Containers.Extractions, StorePaths.Document(work.UploadId, work.DocumentId, "routing.json"), decision, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            await SaveReplayAsync(work, replay, cancellationToken).ConfigureAwait(false);
        }

        return new RouteResult(work.DocumentId, decision.Tier.ToString(), match.Format?.Ref, match.Drift, replay?.UnmappedHeaders ?? [], decision.Reasons, decision.Badge,
            replay is not null, await CostSoFarAsync(work, cancellationToken).ConfigureAwait(false));
    }

    public async Task<LearnResult> DeltaAsync(LearnInput input, CancellationToken cancellationToken = default)
    {
        var (graph, submission, meter) = await LoadAsync(input.Work, cancellationToken).ConfigureAwait(false);
        var spec = await GetSpecAsync(input.Route.FormatRef!, cancellationToken).ConfigureAwait(false);
        var (learner, repairer) = Learners(meter);
        var outcome = await repairer.RepairAsync(graph, spec, input.Route.Unmapped, input.Work.UploadId, submission, null, cancellationToken,
            await ExemplarsAsync(spec.FormatId, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        _ = learner;
        return await LearnResultAsync(input.Work, outcome, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LearnResult> LearnAsync(LearnInput input, CancellationToken cancellationToken = default)
    {
        var (graph, submission, meter) = await LoadAsync(input.Work, cancellationToken).ConfigureAwait(false);
        var prior = input.Route.FormatRef is { } formatRef && input.Route.Drift ? await GetSpecAsync(formatRef, cancellationToken).ConfigureAwait(false) : null;
        var pdf = runtime.Learn.IncludePageImage && input.Work.IsPdf
            ? (await Store.GetAsync(Containers.Uploads, StorePaths.Source(input.Work.UploadId, input.Work.FileName), cancellationToken).ConfigureAwait(false))?.ToArray()
            : null;
        var (learner, _) = Learners(meter);
        var outcome = await learner.LearnAsync(new LearnRequest(graph, input.Work.UploadId, submission, prior, string.Join("; ", input.Route.Reasons), pdf),
            cancellationToken).ConfigureAwait(false);
        return await LearnResultAsync(input.Work, outcome, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LearnResult> LearnResultAsync(DocumentWork work, LearnOutcome outcome, CancellationToken cancellationToken)
    {
        // The published spec's replay becomes the document's result; an unpublished best attempt is kept for the reviewer.
        if (outcome.Verification?.Replay is { } replay)
        {
            var name = outcome.Published ? "replay.json" : "best-attempt.replay.json";
            await Store.PutAsync(Containers.Extractions, StorePaths.Document(work.UploadId, work.DocumentId, name), BinaryData.FromString(replay.ToJson()), true,
                cancellationToken).ConfigureAwait(false);
        }

        return new LearnResult(work.DocumentId, outcome.Published, outcome.Published ? outcome.Spec!.Ref : null, outcome.Published ? outcome.Spec!.Status : null,
            outcome.Reasons, outcome.ModelCalls, await CostSoFarAsync(work, cancellationToken).ConfigureAwait(false));
    }

    // ------------------------------------------------------------------ validate, persist, review

    public async Task<DocumentOutcome> ValidateAndPersistAsync(PersistInput input, CancellationToken cancellationToken = default)
    {
        var work = input.Work;
        var replay = await GetReplayAsync(work, "replay.json", cancellationToken).ConfigureAwait(false)
            ?? await GetReplayAsync(work, "best-attempt.replay.json", cancellationToken).ConfigureAwait(false);
        var routing = await Store.GetJsonAsync<RoutingDecision>(Containers.Extractions, StorePaths.Document(work.UploadId, work.DocumentId, "routing.json"), cancellationToken)
            .ConfigureAwait(false);

        var learned = input.Learn;
        var finalTier = learned is null ? input.Route.Tier
            : learned.Published ? (input.Route.Tier == "ReplayWithDelta" ? "Delta" : "Learned")
            : "Review";
        var reviewKind = finalTier == "Review" ? ReviewKinds.DocumentReview
            : routing?.ConfirmNewFormat == true || learned?.FormatStatus == FormatLifecycle.Shadow ? ReviewKinds.ConfirmFormat
            : null;

        // Validators run on every document, replay hits included (CLAUDE.md).
        var document = replay?.Document ?? new LossRunDocument { DocumentId = work.DocumentId };
        var outcomes = RuleEvaluator.Default.Evaluate(document, await RuleContexts.ForUploadAsync(Records, work.UploadId, cancellationToken).ConfigureAwait(false)).Outcomes.ToList();
        var events = (await Records.ReadUsageAsync(work.UploadId, cancellationToken).ConfigureAwait(false)).Where(e => e.DocumentId == work.DocumentId).ToList();
        var cost = CostCalculator.Compute(work.DocumentId, work.UploadId, finalTier, events, runtime.PriceTable);
        var formatRef = learned?.FormatRef ?? (finalTier == "Review" ? null : input.Route.FormatRef);
        if (formatRef is not null && finalTier is "ReplayAccepted" or "Learned" or "Delta")
        {
            await RecordExemplarAsync(SplitRef(formatRef).Item1, work, cancellationToken).ConfigureAwait(false);
        }

        var result = new DocumentResult
        {
            DocumentId = work.DocumentId, UploadId = work.UploadId, Document = document, RuleOutcomes = outcomes, Routing = routing, FormatRef = formatRef, Cost = cost,
            ReviewState = reviewKind is null ? null : "pending",
        };
        await Records.SaveResultAsync(result, Actor.System, cancellationToken).ConfigureAwait(false);

        var now = runtime.Clock.GetUtcNow();
        await new EscalationService(runtime).TryRaiseAsync(EscalationTriggers.AfterPersist(result, input.Route, learned, finalTier, runtime.EscalationOptions, now), cancellationToken)
            .ConfigureAwait(false);
        if (learned is { Published: true, FormatRef: { } learnedRef })
        {
            var quote = (await GetManifestAsync(work.UploadId, cancellationToken).ConfigureAwait(false)).Submission.QuoteId;
            // Brief 33: keyed on the format and document, so the activity's retry (the manifest update below fails more often than anything
            // here) finds the row and sends nothing; a re-sent row would also lose its PublishedAt and be published again.
            var learnedEvent = OutboxMessage.Event(OutboxKinds.FormatLearned, quote, $"formats/{learnedRef}",
                new { formatRef = learnedRef, status = learned.FormatStatus, documentId = work.DocumentId, work.UploadId, quoteId = quote, tier = finalTier }, Actor.System.UserId, now)
                .KeyedBy($"format-learned:{learnedRef}:{work.DocumentId}");
            if (await Records.GetOutboxAsync(learnedEvent.Id, cancellationToken).ConfigureAwait(false) is null)
            {
                await runtime.NotifyAsync(learnedEvent, cancellationToken).ConfigureAwait(false);
            }
        }
        if (reviewKind is not null)
        {
            var reasons = learned is { Published: false } ? [.. learned.Reasons] : routing?.Reasons.ToList() ?? [];
            await Records.OpenReviewAsync(new ReviewItem
            {
                DocumentId = work.DocumentId, UploadId = work.UploadId, Kind = reviewKind, State = "pending", Reasons = reasons, Badge = routing?.Badge, FormatRef = formatRef,
                OpenedAt = now,
            }, cancellationToken).ConfigureAwait(false);
        }

        var claims = document.PolicyPeriods.Sum(p => p.Claims.Count);
        await UpdateManifestAsync(work.UploadId, m =>
        {
            var item = m.Items.FirstOrDefault(i => i.DocumentId == work.DocumentId);
            if (item is null)
            {
                return;
            }

            item.Status = reviewKind == ReviewKinds.DocumentReview ? ItemStatuses.AwaitingReview : ItemStatuses.Processed;
            item.RoutingTier = finalTier;
            item.Badge = routing?.Badge;
            item.FormatRef = formatRef;
            item.FormatStatus = learned?.FormatStatus ?? routing?.FormatStatus;
            item.ClaimCount = claims;
            item.KeptRows = KeptRows(document);
            item.UnnormalizedValues = routing is { UnnormalizedValues: > 0 } r ? r.UnnormalizedValues : null; // brief 30 item 4
            item.CostUsd = cost.CostUsd;
            item.ReviewKind = reviewKind;
            item.Reason = finalTier == "Review" ? string.Join("; ", learned?.Reasons ?? routing?.Reasons ?? []) : item.Reason ?? KeptRowsFlag(document);
            if (claims > 0 && reviewKind != ReviewKinds.DocumentReview && m.FirstClaimMs is null)
            {
                m.FirstClaimMs = (long)(now - m.CreatedAt).TotalMilliseconds;
            }
        }, cancellationToken).ConfigureAwait(false);

        if (runtime.ChatIndex is { } index)
        {
            await index.EnqueueAsync(work.UploadId, cancellationToken).ConfigureAwait(false);
        }

        return new DocumentOutcome(work.DocumentId, finalTier, reviewKind, claims, cost.CostUsd);
    }

    public async Task<UploadCompletion> CompleteUploadAsync(CompleteInput input, CancellationToken cancellationToken = default)
    {
        var events = await Records.ReadUsageAsync(input.UploadId, cancellationToken).ConfigureAwait(false);
        var byDocument = events.GroupBy(e => e.DocumentId).ToDictionary(g => g.Key, g => CostCalculator.Compute(g.Key, input.UploadId, "upload", g, runtime.PriceTable));
        var cost = new UploadCost(byDocument.Values.Sum(c => c.CostUsd), byDocument.Values.Sum(c => c.LlmCalls), byDocument.Values.Sum(c => c.EmbeddingCalls),
            byDocument.Values.Sum(c => c.Pages), byDocument.ToDictionary(p => p.Key, p => p.Value.CostUsd));
        var awaiting = input.Outcomes.Count(o => o.ReviewKind == ReviewKinds.DocumentReview);
        var now = runtime.Clock.GetUtcNow();
        var manifest = await UpdateManifestAsync(input.UploadId, m =>
        {
            m.Cost = cost;
            m.Status = awaiting > 0 ? UploadStatuses.AwaitingReview : UploadStatuses.Completed;
            m.CompletedAt = now;
            m.CompletedMs = (long)(now - m.CreatedAt).TotalMilliseconds;
            foreach (var item in m.Items.Where(i => i.Status == ItemStatuses.Processing))
            {
                item.Status = ItemStatuses.Processed;
            }
        }, cancellationToken).ConfigureAwait(false);

        // Brief 30 item 6: the completion's outbox rows are keyed on the upload; a retried activity finds them and does not bump the quote again.
        var completionKey = $"complete:{input.UploadId}";
        var alreadyCompleted = await Records.GetOutboxAsync(OutboxMessage.KeyedId(completionKey, OutboxChannels.Event, OutboxKinds.UploadCompleted), cancellationToken)
            .ConfigureAwait(false) is not null;

        // Brief 12b: the CloudEvent for subscribers, and (for a quote) the analysis summary pushed to the quote record.
        // The full quote payload v2 stays behind its URL (claim check: a 1,000-claim payload exceeds a 256 KB message).
        // Brief 24 step 3 (ADR 0024): the completion bumps the quote's revision; the quote.payload command goes out now only when
        // no document waits for its review (otherwise the last review decision sends it), and is pinned to the revision.
        var quoteId = manifest.Submission.QuoteId;
        var summary = new System.Text.Json.Nodes.JsonObject
        {
            ["quoteId"] = quoteId, ["uploadId"] = input.UploadId, ["status"] = manifest.Status, ["documents"] = input.Outcomes.Count, ["awaitingReview"] = awaiting,
            ["claims"] = input.Outcomes.Sum(o => o.ClaimCount), ["costUsd"] = cost.TotalUsd, ["completedAt"] = now.ToString("O"),
            ["quotePayloadUrl"] = quoteId is null ? null : QuotePushes.PayloadUrl(quoteId), ["quoteRevision"] = quoteId is null ? null : QuoteRevisions.Token,
        };
        var messages = new List<OutboxMessage> { OutboxMessage.Event(OutboxKinds.UploadCompleted, quoteId, $"uploads/{input.UploadId}", summary, Actor.System.UserId, now) };
        var pushed = quoteId is not null && awaiting == 0 && !alreadyCompleted;
        if (quoteId is not null)
        {
            messages.Add(QuotePushes.Revised(quoteId, QuoteRevisions.UploadCompleted, null, Actor.System.UserId, now));
        }

        if (pushed)
        {
            var changed = new List<QuotePushes.ChangedDocument>();
            foreach (var o in input.Outcomes)
            {
                if (await Records.GetResultAsync(o.DocumentId, cancellationToken).ConfigureAwait(false) is { } r)
                {
                    changed.Add(new QuotePushes.ChangedDocument(r.DocumentId, r.Revision, r.ReviewState, QuotePushes.FieldStatusChanges(null, r)));
                }
            }

            var readiness = await QuotePushes.ReadinessAsync(Records, quoteId!, null, cancellationToken).ConfigureAwait(false);
            messages.Add(QuotePushes.Payload(quoteId!, QuoteRevisions.UploadCompleted, readiness, changed, summary, Actor.System.UserId, now, $"uploads/{input.UploadId}"));
        }

        if (!alreadyCompleted)
        {
            await runtime.SendAsync(new AuditEvent(now, Actor.System.UserId, Actor.System.Role, pushed ? "quote.payload.pushed" : "", input.UploadId, null,
                pushed ? QuoteAudit.FieldPath(quoteId!) : null, Rationale: $"upload {input.UploadId} {manifest.Status}: analysis summary to the quote system"),
                Actor.System, [.. messages.Select(m => m.KeyedBy(completionKey))], cancellationToken,
                quoteId is null ? null : new QuoteRevisionBump(quoteId, QuoteRevisions.UploadCompleted)).ConfigureAwait(false);
        }

        // Brief 30 item 6: the signed webhook is best effort after the durable record; the upload.completed CloudEvent in the outbox is the retried delivery.
        // A timeout or an unreachable sink never fails the upload.
        try
        {
            await runtime.Webhooks.SendAsync(new WebhookEnvelope("upload.completed", input.UploadId, now, manifest.Status, input.Outcomes.Count, awaiting, cost.TotalUsd,
                runtime.Clock.GetUtcNow(), Guid.NewGuid().ToString("N")),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"webhook upload.completed {input.UploadId} not delivered ({e.GetType().Name}); the outbox CloudEvent carries it");
        }
        if (runtime.ChatIndex is { } index)
        {
            await index.EnqueueAsync(input.UploadId, cancellationToken).ConfigureAwait(false);
        }
        return new UploadCompletion(input.UploadId, manifest.Status, cost.TotalUsd, awaiting);
    }

    /// <summary>Applies a reviewer decision: overrides (lineage extractor <c>manual</c>), re-derived values, re-validation, format approval,
    /// computed by <see cref="DocumentEdits"/> and persisted in one unit by the record store (SQL: <see cref="ClaimCommandService"/>).</summary>
    public async Task<DocumentOutcome> ApplyDecisionAsync(ReviewDecision decision, CancellationToken cancellationToken = default)
    {
        // Brief 30 item 6: Durable re-executes activities, and the API has already answered 202. The decision is applied once (its outbox rows are
        // keyed on it), and a failure puts the document back in Review with an escalation instead of ending the orchestration.
        string uploadId;
        DocumentResult after;
        ReviewItem? pending;
        bool approve;
        IReadOnlyList<FieldChange> changes = [];
        var replayed = await Records.GetOutboxAsync(OutboxMessage.KeyedId(decision.Key, OutboxChannels.Event, OutboxKinds.ReviewDecided), cancellationToken)
            .ConfigureAwait(false) is not null;
        try
        {
            uploadId = await Records.UploadOfDocumentAsync(decision.DocumentId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Document {decision.DocumentId} is unknown.");
            var before = await Records.GetResultAsync(decision.DocumentId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Result of {decision.DocumentId} is missing.");
            pending = await Records.GetReviewAsync(decision.DocumentId, cancellationToken).ConfigureAwait(false);
            if (replayed)
            {
                after = before;
            }
            else
            {
                // Brief 34 A3: a confirmed format change publishes the changed version (shadow) and replays the document with it; the decision's
                // edits and re-validation then apply to that replay, and the change itself is one more audited change of the decision.
                var (basis, formatChanges) = decision.FormatChanges is { Count: > 0 }
                    ? await FormatChangeAsync(uploadId, before, decision, cancellationToken).ConfigureAwait(false)
                    : (before, []);
                (after, changes) = DocumentEdits.Apply(basis, decision, await RuleContexts.ForUploadAsync(Records, uploadId, cancellationToken).ConfigureAwait(false));
                if (formatChanges.Count > 0)
                {
                    changes = [.. formatChanges, .. changes];
                }
            }

            // Brief 24 step 1: a reject never approves a format (any path, reopened documents with a FormatRef included), and
            // promoteToFormat is the decision's own (DecideAsync resolves the default: true on confirm-format, false otherwise).
            // Brief 30 item 17: a retired format is never approved by a decision (un-retire is an explicit, audited engineer action).
            approve = decision.Action != ReviewActions.Reject && decision.PromoteToFormat && after.FormatRef is not null
                && await FormatRetiredAsync(after.FormatRef, cancellationToken).ConfigureAwait(false) is false;

            if (!replayed)
            {
                // Brief 24 step 3: the decision, its review.decided and quote.revised events, the quote revision bump and (when the upload's
                // reviews are all decided) the quote.payload command commit together.
                var manifestBefore = await Records.GetManifestAsync(uploadId, cancellationToken).ConfigureAwait(false);
                var quoteId = manifestBefore?.Submission.QuoteId;
                var cause = pending?.Reopened == true ? QuoteRevisions.ReopenDecided : QuoteRevisions.ReviewDecided;
                var push = manifestBefore?.Items.Any(i => i.DocumentId != decision.DocumentId && i.Status == ItemStatuses.AwaitingReview) != true;
                var outbox = await runtime.DecisionOutboxAsync(quoteId, uploadId, before, after, decision, cause, push, cancellationToken).ConfigureAwait(false);
                var result = await Records.ApplyDecisionAsync(new DecisionRecord(uploadId, before, after, decision, changes, approve,
                    quoteId is null ? null : new QuoteRevisionBump(quoteId, cause)), cancellationToken, outbox).ConfigureAwait(false);
                if (result == DecisionApplyResult.Conflict)
                {
                    // Brief 32 item 1: the revision was taken by a concurrent decision on this review (a double click, two reviewers). It is decided:
                    // the manifest item is not reopened and nothing is escalated; the loser is told the review is not pending.
                    if (await Records.GetReviewAsync(decision.DocumentId, cancellationToken).ConfigureAwait(false) is { } now && now.State != "pending")
                    {
                        return new DocumentOutcome(decision.DocumentId, DecisionOutcomes.NotPending, null, before.Document.PolicyPeriods.Sum(p => p.Claims.Count), 0);
                    }

                    // Brief 30 item 7: another decision took the revision; nothing was written, nothing is published, the reviewer decides again.
                    return await DecisionFailedAsync(decision, "conflict: the document changed under the decision", cancellationToken).ConfigureAwait(false);
                }

                if (result == DecisionApplyResult.Applied)
                {
                    runtime.PublishSoon(outbox);
                }
                else
                {
                    (after, changes) = (before, []);
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException && FailureClassifier.Classify(e, RetryConfig.Embedded) != FailureClass.Transient)
        {
            // Brief 33: a transient failure (a SQL resume, a 503) is not caught: the activity fails and the retry handler runs it again, which the
            // decision key makes safe. Only a permanent or unknown one comes here: back to Review, escalated.
            return await DecisionFailedAsync(decision, $"{FailureClassifier.Reason(e, RetryConfig.Embedded)} while applying the {decision.Action} decision", cancellationToken)
                .ConfigureAwait(false);
        }

        // Brief 30 item 34: the format is approved after the decision commits, and again on a retry (approval is idempotent), so a crash between the
        // two never leaves an approved format without its decision; at worst the retry completes the approval.
        // Brief 32 item 3: from here the decision is committed. Each later step is best-effort (a few tries, then an escalation naming the step),
        // so a persistent failure never faults the activity, the orchestration's decision loop, or the sibling decisions after it.
        if (approve)
        {
            var (formatId, version) = SplitRef(after.FormatRef!);
            await PostCommitAsync(decision, "format-approve", () => FormatLifecycle.ApproveAsync(runtime.Registry, formatId, version, decision.User, decision.At, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        // ADR 0019: a confirmed format confirms (or edits / rejects) the aliases it introduced; a correction retires the alias it read through.
        if (pending?.Kind == ReviewKinds.ConfirmFormat && approve)
        {
            await PostCommitAsync(decision, "alias-confirm", () => LearnedAliasReview.ConfirmAsync(runtime, uploadId, decision.DocumentId, after.FormatRef!, decision.Aliases,
                decision.User, decision.Role, decision.At, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        await PostCommitAsync(decision, "alias-retire", () => LearnedAliasReview.RetireOnCorrectionsAsync(runtime, uploadId, after, changes, decision.User, decision.Role, decision.At,
            cancellationToken), cancellationToken).ConfigureAwait(false);
        // Brief 30 item 8: a reject marks the chunks rejected. Brief 32 item 7: a decision on a reopened review marks them approved (or rejected) again.
        if ((decision.Action == ReviewActions.Reject || pending?.Reopened == true) && runtime.ChatIndex is { } chatIndex)
        {
            await PostCommitAsync(decision, "chat-reindex", () => chatIndex.EnqueueAsync(uploadId, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        var hardFailures = after.RuleOutcomes.Count(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard);
        await PostCommitAsync(decision, "manifest", () => UpdateManifestAsync(uploadId, m =>
        {
            if (m.Items.FirstOrDefault(i => i.DocumentId == decision.DocumentId) is { } item)
            {
                item.Status = decision.Action == ReviewActions.Reject ? ItemStatuses.Rejected : ItemStatuses.Processed;
                item.Reason = $"{decision.Action} by {decision.User}: {decision.Rationale}; {hardFailures} hard failure(s) after re-validation";
            }

            if (!m.Items.Any(i => i.Status == ItemStatuses.AwaitingReview))
            {
                m.Status = UploadStatuses.Completed;
            }
        }, cancellationToken), cancellationToken).ConfigureAwait(false);

        return new DocumentOutcome(decision.DocumentId, decision.Action, null, after.Document.PolicyPeriods.Sum(p => p.Claims.Count), after.Cost?.CostUsd ?? 0);
    }

    /// <summary>Tries a post-commit step up to <see cref="PostCommitTries"/> times; after the last failure, an escalation names the step (type only, never values).</summary>
    private async Task PostCommitAsync(ReviewDecision decision, string step, Func<Task> work, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await work().ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (attempt < PostCommitTries)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), runtime.Clock, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var reasons = new[] { $"decision {decision.Key} by {decision.Role}: {step} failed after the decision committed ({e.GetType().Name}, {attempt} tries); the decision stands" };
                await new EscalationService(runtime).TryRaiseAsync([new EscalationSignal(EscalationReasons.DecisionPostCommitFailed, $"document:{decision.DocumentId}:{step}",
                    runtime.Clock.GetUtcNow(), new EscalationTrace { Reasons = EscalationTraceText.Sanitize(reasons), Counts = new Dictionary<string, decimal>(), Step = step })
                    { DocumentIds = [decision.DocumentId] }], cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    private const int PostCommitTries = 3;

    /// <summary>
    /// Brief 34 A3, brief 35 Answers 2: the reviewer answered a question the review asked about the document's format. Publishes the format again
    /// with the answer as a new version and replays the document with it; the decision's edits then apply to that replay.
    /// <list type="bullet">
    /// <item><c>periods.fillDown: true</c> changes how the layout is read, so the version is <c>shadow</c> (approval or
    /// <see cref="FormatLifecycle.ShadowReplays"/> clean replays make it active, ADR 0018 amendment 2).</item>
    /// <item><c>carrierNames.add</c> only names the carrier again; the layout is unchanged, so the version keeps its parent's status and the next
    /// document that prints that name replays clean.</item>
    /// </list>
    /// A retried decision finds the version it published (its learning record names the decision) instead of publishing another.
    /// </summary>
    private async Task<(DocumentResult Basis, IReadOnlyList<FieldChange> Changes)> FormatChangeAsync(string uploadId, DocumentResult before, ReviewDecision decision,
        CancellationToken cancellationToken)
    {
        // A document in Review has no format on its result; the one it was replayed with is on its routing decision.
        var spec = await GetSpecAsync(before.FormatRef ?? before.Routing?.FormatRef
            ?? throw new InvalidOperationException($"Document {decision.DocumentId} has no format to change."), cancellationToken).ConfigureAwait(false);
        var fillDown = decision.FormatChanges!.ContainsKey(DocumentCommands.FillDownChange) && !spec.Periods.FillDown;
        var addName = decision.FormatChanges.TryGetValue(DocumentCommands.CarrierNamesAddChange, out var n) && n.GetString()!.Trim() is { Length: > 0 } name
            && !spec.KnownCarrierNames.Any(k => CanonicalComparer.NormalizeText(k) == CanonicalComparer.NormalizeText(name)) ? name : null;
        if (!fillDown && addName is null)
        {
            return (before, []); // already a fill-down format, already a known name: nothing to publish
        }

        var marker = $"decision {decision.Key}: {string.Join(", ", new[] { fillDown ? $"{DocumentCommands.FillDownChange} true" : null, addName is null ? null : $"{DocumentCommands.CarrierNamesAddChange} \"{addName}\"" }.OfType<string>())} (from {spec.Ref})";
        var index = await runtime.Registry.GetIndexAsync(cancellationToken).ConfigureAwait(false);
        CarrierFormat? changed = null;
        foreach (var entry in index.Where(e => e.FormatId == spec.FormatId && e.Version > spec.Version))
        {
            if (await runtime.Registry.GetAsync(entry.FormatId, entry.Version, cancellationToken).ConfigureAwait(false) is { Learning.Verification: { } v } existing && v == marker)
            {
                changed = existing;
                break;
            }
        }

        if (changed is null)
        {
            changed = FormatJson.Parse(FormatJson.Write(spec));
            changed.Periods.FillDown |= fillDown;
            if (addName is not null)
            {
                changed.CarrierNames.Add(addName);
            }

            changed.Status = fillDown ? FormatLifecycle.Shadow : await FormatLifecycle.StatusAsync(runtime.Registry, spec, cancellationToken).ConfigureAwait(false);
            changed.CreatedAt = decision.At;
            changed.Learning = new LearningRecord
            {
                Stage = "format-change", Model = "reviewer", PromptVersion = spec.Learning?.PromptVersion ?? "", SourceDocumentId = decision.DocumentId, Verification = marker,
            };
            for (var attempt = 0; ; attempt++)
            {
                changed.Version = index.Where(e => e.FormatId == spec.FormatId).Select(e => e.Version).DefaultIfEmpty(spec.Version).Max() + 1;
                try
                {
                    await runtime.Registry.PublishAsync(changed, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (FormatAlreadyPublishedException) when (attempt < 3)
                {
                    index = await runtime.Registry.GetIndexAsync(cancellationToken).ConfigureAwait(false); // a concurrent publish took the version
                }
            }

            await FormatLifecycle.InitializeAsync(runtime.Registry, changed, $"review:{decision.User}", decision.At, cancellationToken).ConfigureAwait(false);
        }

        var graph = await Store.GetJsonAsync<DocumentGraph>(Containers.Extractions, StorePaths.Document(uploadId, decision.DocumentId, "graph.json"), cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException($"Graph {decision.DocumentId} is missing.");
        var replay = ReplayEngine.Execute(graph, changed);
        var basis = JsonSerializer.Deserialize<DocumentResult>(JsonSerializer.Serialize(before, CanonicalJson.Options), CanonicalJson.Options)!;
        (basis.Document, basis.FormatRef) = (replay.Document, changed.Ref);
        var why = string.IsNullOrWhiteSpace(decision.Rationale) ? "" : $" ({decision.Rationale})";
        var changes = new List<FieldChange>();
        if (fillDown)
        {
            changes.Add(new FieldChange("format.change", DocumentCommands.FillDownChange, "false", "true", $"{spec.Ref} → {changed.Ref}: the carrier's export fills the policy down{why}"));
        }

        if (addName is not null)
        {
            changes.Add(new FieldChange("format.change", "carrierNames", string.Join("; ", spec.KnownCarrierNames), string.Join("; ", changed.KnownCarrierNames),
                $"{spec.Ref} → {changed.Ref}: \"{addName}\" is another name of {spec.CarrierName}{why}"));
        }

        return (basis, changes);
    }

    /// <summary>Brief 30 item 19: the format's last accepted documents, kept by reference (their graphs already live under their upload).</summary>
    public sealed record FormatExemplarList(List<string> Documents);

    private const int ExemplarsKept = 5;

    private Task RecordExemplarAsync(string formatId, DocumentWork work, CancellationToken cancellationToken) =>
        Store.UpdateAsync<FormatExemplarList>(Containers.Extractions, StorePaths.FormatExemplars(formatId), list =>
        {
            var key = $"{work.UploadId}/{work.DocumentId}";
            var documents = new List<string> { key };
            documents.AddRange((list?.Documents ?? []).Where(d => d != key).Take(ExemplarsKept - 1));
            return new FormatExemplarList(documents);
        }, cancellationToken);

    /// <summary>The graphs of the format's last accepted documents, at most <see cref="LossRun.Learning.LearnGate.Exemplars"/>, for a drift repair to verify on.</summary>
    public async Task<IReadOnlyList<DocumentGraph>> ExemplarsAsync(string formatId, CancellationToken cancellationToken = default)
    {
        var list = await Store.GetJsonAsync<FormatExemplarList>(Containers.Extractions, StorePaths.FormatExemplars(formatId), cancellationToken).ConfigureAwait(false);
        var graphs = new List<DocumentGraph>();
        foreach (var key in list?.Documents ?? [])
        {
            var slash = key.IndexOf('/', StringComparison.Ordinal);
            if (await Store.GetJsonAsync<DocumentGraph>(Containers.Extractions, StorePaths.Document(key[..slash], key[(slash + 1)..], "graph.json"), cancellationToken)
                    .ConfigureAwait(false) is { } graph)
            {
                graphs.Add(graph);
            }

            if (graphs.Count == LossRun.Learning.LearnGate.Default.Exemplars)
            {
                break;
            }
        }

        return graphs;
    }

    private async Task<bool> FormatRetiredAsync(string formatRef, CancellationToken cancellationToken)
    {
        var (formatId, version) = SplitRef(formatRef);
        var stats = await runtime.Registry.GetStatsAsync(formatId, cancellationToken).ConfigureAwait(false);
        return stats.Lifecycle.TryGetValue(version.ToString(System.Globalization.CultureInfo.InvariantCulture), out var live) && live.Status == FormatLifecycle.Retired;
    }

    /// <summary>
    /// Brief 30 item 6: the decision was acknowledged but not applied. The review item stays pending (nothing was written), the manifest says so,
    /// an engineering escalation is raised (reason and type only, never values), and the orchestration waits for the next decision.
    /// </summary>
    private async Task<DocumentOutcome> DecisionFailedAsync(ReviewDecision decision, string what, CancellationToken cancellationToken)
    {
        var now = runtime.Clock.GetUtcNow();
        var result = await Records.GetResultAsync(decision.DocumentId, cancellationToken).ConfigureAwait(false);
        var reasons = new[] { $"decision {decision.Key} by {decision.Role}: {what}; the document is back in Review" };
        await new EscalationService(runtime).TryRaiseAsync([result is null
            ? new EscalationSignal(EscalationReasons.DecisionApplyFailed, $"document:{decision.DocumentId}", now, new EscalationTrace
            {
                Reasons = EscalationTraceText.Sanitize(reasons), Counts = new Dictionary<string, decimal>(), Step = "decision",
            }) { DocumentIds = [decision.DocumentId] }
            : EscalationTriggers.ForDocument(EscalationReasons.DecisionApplyFailed, result, now, reasons, "decision")], cancellationToken).ConfigureAwait(false);
        if (await Records.UploadOfDocumentAsync(decision.DocumentId, cancellationToken).ConfigureAwait(false) is { } uploadId)
        {
            await UpdateManifestAsync(uploadId, m =>
            {
                if (m.Items.FirstOrDefault(i => i.DocumentId == decision.DocumentId) is { } item)
                {
                    item.Status = ItemStatuses.AwaitingReview;
                    item.Reason = $"the {decision.Action} decision by {decision.User} could not be applied ({what}); decide again";
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        return new DocumentOutcome(decision.DocumentId, EscalationReasons.DecisionApplyFailed, ReviewKinds.DocumentReview,
            result?.Document.PolicyPeriods.Sum(p => p.Claims.Count) ?? 0, 0);
    }

    public async Task ExpireReviewsAsync(ExpireInput input, CancellationToken cancellationToken = default)
    {
        var now = runtime.Clock.GetUtcNow();
        foreach (var documentId in input.DocumentIds)
        {
            await Records.ExpireReviewAsync(input.UploadId, documentId, now, cancellationToken).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ helpers

    private (FormatLearner Learner, FormatRepairer Repairer) Learners(ICostMeter meter)
    {
        var model = runtime.LearnModel(meter);
        var learner = new FormatLearner(model, runtime.Registry, runtime.Learn, runtime.Embeddings(meter), runtime.Clock);
        return (learner, new FormatRepairer(model, learner, runtime.Learn));
    }

    private async Task<(DocumentGraph Graph, SubmissionContext Submission, ICostMeter Meter)> LoadAsync(DocumentWork work, CancellationToken cancellationToken)
    {
        var graph = await Store.GetJsonAsync<DocumentGraph>(Containers.Extractions, StorePaths.Document(work.UploadId, work.DocumentId, "graph.json"), cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException($"Graph {work.DocumentId} is missing.");
        var manifest = await GetManifestAsync(work.UploadId, cancellationToken).ConfigureAwait(false);
        return (graph, manifest.Submission.ToContext(), new UploadCostMeter(Records, work.UploadId));
    }

    private async Task<UploadManifest> GetManifestAsync(string uploadId, CancellationToken cancellationToken) =>
        await Records.GetManifestAsync(uploadId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Manifest of {uploadId} is missing.");

    private Task<UploadManifest> UpdateManifestAsync(string uploadId, Action<UploadManifest> change, CancellationToken cancellationToken) =>
        Records.UpdateManifestAsync(uploadId, m =>
        {
            var manifest = m ?? throw new InvalidOperationException($"Manifest of {uploadId} is missing.");
            change(manifest);
            return manifest;
        }, Actor.System, cancellationToken);

    private Task SaveReplayAsync(DocumentWork work, ReplayResult replay, CancellationToken cancellationToken) =>
        Store.PutAsync(Containers.Extractions, StorePaths.Document(work.UploadId, work.DocumentId, "replay.json"), BinaryData.FromString(replay.ToJson()), true, cancellationToken);

    private Task<ReplayResult?> GetReplayAsync(DocumentWork work, string name, CancellationToken cancellationToken) =>
        Store.GetJsonAsync<ReplayResult>(Containers.Extractions, StorePaths.Document(work.UploadId, work.DocumentId, name), cancellationToken);

    private async Task<decimal> CostSoFarAsync(DocumentWork work, CancellationToken cancellationToken)
    {
        var events = (await Records.ReadUsageAsync(work.UploadId, cancellationToken).ConfigureAwait(false)).Where(e => e.DocumentId == work.DocumentId).ToList();
        return events.Count == 0 ? 0 : CostCalculator.Compute(work.DocumentId, work.UploadId, "so-far", events, runtime.PriceTable).CostUsd;
    }

    private async Task<CarrierFormat> GetSpecAsync(string formatRef, CancellationToken cancellationToken)
    {
        var (formatId, version) = SplitRef(formatRef);
        return await runtime.Registry.GetAsync(formatId, version, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Format {formatRef} is missing from the registry.");
    }

    /// <summary>
    /// Brief 23 Answers 1: R-31 on the manifest. Kept rows and no printed total to reconcile the workers' comp rows against is a
    /// flag the reviewer sees, not a review trigger.
    /// </summary>
    internal static string? KeptRowsFlag(LossRunDocument document) =>
        document.PolicyPeriods.Any(p => p.KeptRows is { Count: > 0 }) && document.ReportTotals is null && document.PolicyPeriods.All(p => p.Subtotals is null)
            ? "R-31: rows of another line were kept, not extracted, and the run prints no total to reconcile the workers' comp rows against"
            : null;

    /// <summary>Brief 23 §2: the document's kept rows (other lines of business) for the manifest; null when it has none.</summary>
    internal static List<KeptRowItem>? KeptRows(LossRunDocument document)
    {
        var rows = document.PolicyPeriods.SelectMany((p, i) => (p.KeptRows ?? []).Select((c, j) => new KeptRowItem(c.ClaimNumber, p.PolicyNumber, c.KeptReason ?? "kept",
            document.Lineage is { } lineage && lineage.TryGetValue($"policyPeriods[{i}].keptRows[{j}].keptReason", out var l) ? l.Page : null, c.Total?.Incurred))).ToList();
        return rows.Count == 0 ? null : rows;
    }

    internal static (string FormatId, int Version) SplitRef(string formatRef)
    {
        var at = formatRef.LastIndexOf('@');
        return (formatRef[..at], int.Parse(formatRef[(at + 1)..], CultureInfo.InvariantCulture));
    }

}

public interface IWebhookSender
{
    Task SendAsync(WebhookEnvelope envelope, CancellationToken cancellationToken = default);
}

/// <summary>Outbound <c>upload.completed</c> with an HMAC-SHA256 signature over the raw body (Service Bus / Event Grid is brief 11).</summary>
public sealed class HmacWebhookSender(Uri? url, string? secret, HttpClient? http = null) : IWebhookSender
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    public const string SignatureHeader = "X-LossRun-Signature";
    public const string EventHeader = "X-LossRun-Event";

    public static string Sign(string secret, byte[] body) => "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    public async Task SendAsync(WebhookEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (url is null || string.IsNullOrEmpty(secret))
        {
            return;
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(envelope, CanonicalJson.Options);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add(SignatureHeader, Sign(secret, body));
        request.Headers.Add(EventHeader, envelope.Event);
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // best effort in the POC; delivery guarantees (outbox, retries) are brief 11
        }
    }
}
