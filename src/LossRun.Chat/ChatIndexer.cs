using System.Security.Cryptography;
using System.Text;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;
using LossRun.Formats;
using LossRun.Ingestion.Metering;

namespace LossRun.Chat;

/// <summary>
/// Builds and pushes the chat chunks of an upload (brief 12 §1): every processed or under-review loss run, and every kept
/// (not processed) segment (ADR 0005: non-loss-run documents are indexed for chat). One chunk per page or sheet; the
/// scope fields come from the upload's submission (quote, workspace or neither = org). Idempotent: chunk ids derive from
/// document and page, so re-indexing replaces in place.
/// </summary>
public sealed class ChatIndexer(IRecordStore records, IChatSearch search, Func<string, string, CancellationToken, Task<DocumentGraph?>> loadGraph,
    Func<ICostMeter, IEmbeddingService> embeddings)
{
    /// <summary>Markdown kept per chunk: a page of a loss run fits easily; longer text is split into parts.</summary>
    public const int MaxChunkChars = 8000;

    public async Task<int> IndexUploadAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        var manifest = await records.GetManifestAsync(uploadId, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            return 0;
        }

        var submission = manifest.Submission;
        var workspace = submission.WorkspaceId is { } w ? await records.GetWorkspaceAsync(w, cancellationToken).ConfigureAwait(false) : null;
        var meter = new ListMeter();
        var embedder = embeddings(new ForwardingMeter(meter, records, uploadId));
        var chunks = new List<ChatChunk>();
        foreach (var item in manifest.Items)
        {
            chunks.AddRange(await ChunksOfAsync(manifest, item, workspace, cancellationToken).ConfigureAwait(false));
        }

        var embedded = new List<ChatChunk>(chunks.Count);
        foreach (var c in chunks)
        {
            var vector = (await embedder.EmbedAsync(EmbeddingText(c), c.DocumentId, uploadId, cancellationToken).ConfigureAwait(false)).Vector;
            embedded.Add(c with { Vector = vector });
        }

        await search.UpsertAsync(embedded, cancellationToken).ConfigureAwait(false);
        return embedded.Count;
    }

    /// <summary>Removes a workspace's chunks (delete or expiry).</summary>
    public async Task<int> DropWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        var ids = await search.WorkspaceChunkIdsAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        await search.DeleteAsync(ids, cancellationToken).ConfigureAwait(false);
        return ids.Count;
    }

    private async Task<IReadOnlyList<ChatChunk>> ChunksOfAsync(UploadManifest manifest, ManifestItem item, ChatWorkspace? workspace, CancellationToken cancellationToken)
    {
        DocumentGraph? graph;
        IEnumerable<GraphPage> pages;
        DocumentResult? result = null;
        if (item.Status is ItemStatuses.Processed or ItemStatuses.AwaitingReview or ItemStatuses.Processing or ItemStatuses.Rejected && item.Classification == "LossRun")
        {
            // A loss run is indexed once its result exists (ValidateAndPersist enqueues before the upload completes).
            result = await records.GetResultAsync(item.DocumentId, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                return [];
            }

            graph = await loadGraph(manifest.UploadId, item.DocumentId, cancellationToken).ConfigureAwait(false);
            pages = graph?.Pages ?? [];
        }
        else if (item.Status == ItemStatuses.KeptNotProcessed && item.ParentDocumentId is { } parent && Range(item.PageRange) is var (first, last))
        {
            graph = await loadGraph(manifest.UploadId, parent, cancellationToken).ConfigureAwait(false);
            pages = graph?.Pages.Where(p => p.Number >= first && p.Number <= last) ?? [];
        }
        else
        {
            return [];
        }

        return Build(manifest, item, pages, result, workspace);
    }

    /// <summary>The chunks of one manifest item from its pages (pure; tested without storage).</summary>
    public static IReadOnlyList<ChatChunk> Build(UploadManifest manifest, ManifestItem item, IEnumerable<GraphPage> pages, DocumentResult? result, ChatWorkspace? workspace)
    {
        var s = manifest.Submission;
        var scope = s.WorkspaceId is not null ? ChatScopes.Workspace : s.QuoteId is not null ? ChatScopes.Quote : ChatScopes.Org;
        var policies = result?.Document.PolicyPeriods.Select(p => p.PolicyNumber).OfType<string>().Distinct().ToList() ?? [];
        var chunks = new List<ChatChunk>();
        foreach (var page in pages.Where(p => !p.Hidden && !string.IsNullOrWhiteSpace(p.Markdown)))
        {
            var text = page.Markdown!;
            var parts = (int)Math.Ceiling(text.Length / (double)MaxChunkChars);
            for (var part = 0; part < parts; part++)
            {
                var markdown = text.Substring(part * MaxChunkChars, Math.Min(MaxChunkChars, text.Length - part * MaxChunkChars));
                chunks.Add(new ChatChunk
                {
                    ChunkId = ChunkId(item.DocumentId, page.SheetName ?? page.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), part),
                    DocumentId = item.DocumentId,
                    UploadId = manifest.UploadId,
                    QuoteId = scope == ChatScopes.Quote ? s.QuoteId : null,
                    WorkspaceId = scope == ChatScopes.Workspace ? s.WorkspaceId : null,
                    Scope = scope,
                    Carrier = result?.Document.CarrierName ?? s.CarrierHint,
                    Classification = item.Classification,
                    FileName = item.FileName,
                    Page = page.SheetName is null ? page.Number : null,
                    Sheet = page.SheetName,
                    Markdown = markdown,
                    ValuationDate = result?.Document.ValuationDate is { } v ? new DateTimeOffset(v.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
                    FormatRef = result?.FormatRef,
                    PolicyNumbers = policies,
                    ExpiresAt = workspace?.ExpiresAt,
                    // Brief 30 item 8: a rejected loss run stays indexed (history on request) but out of the default scope. Brief 32 item 7: so does a
                    // reopened one (superseded) until it is decided again (approved).
                    ReviewStatus = ChatReviewStatus.Of(item.Status, result?.ReviewState),
                });
            }
        }

        return chunks;
    }

    /// <summary>Key-safe (letters, digits, dash) and stable: SHA-256 of document, page and part.</summary>
    public static string ChunkId(string documentId, string page, int part) =>
        "c-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{documentId}|{page}|{part}")))[..40];

    private static string EmbeddingText(ChatChunk c) =>
        $"{c.FileName} · {c.Classification}{(c.Carrier is null ? "" : " · " + c.Carrier)}{(c.Page is { } p ? $" · page {p}" : c.Sheet is { } s ? $" · sheet {s}" : "")}\n{c.Markdown}";

    private static (int, int)? Range(string? pageRange)
    {
        var parts = pageRange?.Split('-');
        return parts is [var a, var b] && int.TryParse(a, out var first) && int.TryParse(b, out var last) ? (first, last)
            : parts is [var one] && int.TryParse(one, out var single) ? (single, single) : null;
    }

    private sealed class ListMeter : ICostMeter
    {
        public List<UsageEvent> Events { get; } = [];

        public ValueTask RecordAsync(UsageEvent usage, CancellationToken cancellationToken = default)
        {
            Events.Add(usage);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Indexing embeddings are part of the upload's cost (stage chat-index), recorded in the system of record.</summary>
    private sealed class ForwardingMeter(ListMeter inner, IRecordStore records, string uploadId) : ICostMeter
    {
        public async ValueTask RecordAsync(UsageEvent usage, CancellationToken cancellationToken = default)
        {
            var e = usage with { Stage = CostCalculator.ChatIndexStage };
            await inner.RecordAsync(e, cancellationToken).ConfigureAwait(false);
            await records.RecordUsageAsync(uploadId, e, cancellationToken).ConfigureAwait(false);
        }
    }
}
