using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Azure.Core;
using LossRun.Api.Contracts;

namespace LossRun.Chat;

/// <summary>
/// One index chunk: a page (or sheet) of a processed or kept document (brief 12 §1). Field names match
/// infra/search/lossrun-docs.index.json.
/// </summary>
public sealed record ChatChunk
{
    public required string ChunkId { get; init; }
    public required string DocumentId { get; init; }
    public required string UploadId { get; init; }
    public string? QuoteId { get; init; }
    public string? WorkspaceId { get; init; }

    /// <summary>org | quote | workspace (an upload with neither a quote nor a workspace is org).</summary>
    public required string Scope { get; init; }

    public string? Carrier { get; init; }

    /// <summary>LossRun, or the classifier category of a kept item (Payroll, Correspondence, …).</summary>
    public required string Classification { get; init; }

    public required string FileName { get; init; }
    public int? Page { get; init; }
    public string? Sheet { get; init; }
    public required string Markdown { get; init; }

    [JsonPropertyName("vector")]
    public float[]? Vector { get; init; }

    public DateTimeOffset? ValuationDate { get; init; }
    public string? FormatRef { get; init; }
    public IReadOnlyList<string> PolicyNumbers { get; init; } = [];

    /// <summary>Workspace chunks only: when the cleanup timer removes them.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Brief 30 item 8: <see cref="ChatReviewStatus.Rejected"/> or <see cref="ChatReviewStatus.Superseded"/> for a loss run the
    /// reviewer set aside; null for every other chunk. Out of the search scope unless history is asked for.</summary>
    public string? ReviewStatus { get; init; }
}

/// <summary>Brief 30 item 8: the review states that take a document's pages out of chat answers.</summary>
public static class ChatReviewStatus
{
    public const string Rejected = "rejected";

    /// <summary>Brief 32 item 7: a reopened document; its values are under review again. Out of the default scope until it is decided.</summary>
    public const string Superseded = "superseded";

    /// <summary>Brief 32 item 7: a decided (accepted or overridden) document; written back when a reopened one is decided again. In scope.</summary>
    public const string Approved = "approved";

    /// <summary>The status a document's chunks carry, from its manifest item and its result's review state.</summary>
    public static string? Of(string itemStatus, string? reviewState) => itemStatus == ItemStatuses.Rejected || reviewState == ReviewActions.Reject ? Rejected
        : reviewState == ReviewStates.Reopened ? Superseded
        : reviewState is ReviewActions.Accept or ReviewActions.Override ? Approved
        : null;
}

public sealed record SearchHit(ChatChunk Chunk, double Score);

/// <summary>The chat index. Every query carries the scope; implementations push it down as a filter.</summary>
public interface IChatSearch
{
    Task UpsertAsync(IReadOnlyList<ChatChunk> chunks, CancellationToken cancellationToken = default);

    Task DeleteAsync(IReadOnlyList<string> chunkIds, CancellationToken cancellationToken = default);

    /// <summary>Ids of every chunk in a workspace (to drop it on delete or expiry).</summary>
    Task<IReadOnlyList<string>> WorkspaceChunkIdsAsync(string workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Hybrid search inside the scope: BM25 on <paramref name="text"/>, k-NN on <paramref name="vector"/>, semantic re-rank.</summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(ChatScope scope, string text, float[]? vector, int top, CancellationToken cancellationToken = default, bool includeRejected = false);
}

/// <summary>
/// Azure AI Search over REST with an Entra token (the service has local auth disabled). Hybrid query: text + vector +
/// semantic ranker, with the scope's OData filter on every request. The index definition is embedded from
/// infra/search/lossrun-docs.index.json and created by <see cref="EnsureIndexAsync"/> (`lossrun chat init-index`).
/// </summary>
/// <param name="reviewStatusFilter">Brief 31 item 5 (<c>Chat__ReviewStatusFilter</c>, default true): push the review-status filter into the index and select the
/// field back. Off only for a deploy that ran before the index gained <c>reviewStatus</c>; the tools' store-side post-filter still leaves out rejected documents.</param>
public sealed class AzureSearchChatSearch(Uri endpoint, string index, TokenCredential credential, HttpClient? http = null, bool reviewStatusFilter = true) : IChatSearch
{
    public const string ApiVersion = "2025-09-01";
    private static readonly string[] Scopes = ["https://search.azure.com/.default"];
    private static readonly string[] Fields =
    [
        "chunkId", "documentId", "uploadId", "quoteId", "workspaceId", "scope", "carrier", "classification", "fileName", "page", "sheet", "markdown",
        "valuationDate", "formatRef", "policyNumbers", "expiresAt",
    ];

    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The checked-in index definition with the name replaced.</summary>
    public static JsonObject IndexDefinition(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LossRun.Chat.lossrun-docs.index.json")!;
        var node = JsonNode.Parse(stream)!.AsObject();
        node["name"] = name;
        return node;
    }

    public async Task EnsureIndexAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Put, $"indexes('{index}')?api-version={ApiVersion}&allowIndexDowntime=false", IndexDefinition(index),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertAsync(IReadOnlyList<ChatChunk> chunks, CancellationToken cancellationToken = default)
    {
        foreach (var batch in chunks.Chunk(100))
        {
            var value = new JsonArray();
            foreach (var c in batch)
            {
                var doc = JsonSerializer.SerializeToNode(c, Json)!.AsObject();
                doc["@search.action"] = "mergeOrUpload";
                value.Add(doc);
            }

            using var response = await SendAsync(HttpMethod.Post, $"indexes('{index}')/docs/search.index?api-version={ApiVersion}", new JsonObject { ["value"] = value },
                cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteAsync(IReadOnlyList<string> chunkIds, CancellationToken cancellationToken = default)
    {
        foreach (var batch in chunkIds.Chunk(500))
        {
            var value = new JsonArray([.. batch.Select(id => (JsonNode)new JsonObject { ["@search.action"] = "delete", ["chunkId"] = id })]);
            using var response = await SendAsync(HttpMethod.Post, $"indexes('{index}')/docs/search.index?api-version={ApiVersion}", new JsonObject { ["value"] = value },
                cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<string>> WorkspaceChunkIdsAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["search"] = "*", ["filter"] = ChatScope.ForWorkspace(workspaceId).ODataFilter(), ["select"] = "chunkId", ["top"] = 1000,
        };
        using var response = await SendAsync(HttpMethod.Post, $"indexes('{index}')/docs/search.post.search?api-version={ApiVersion}", body, cancellationToken)
            .ConfigureAwait(false);
        var text = await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return [.. JsonNode.Parse(text)!["value"]!.AsArray().Select(v => v!["chunkId"]!.GetValue<string>())];
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(ChatScope scope, string text, float[]? vector, int top, CancellationToken cancellationToken = default,
        bool includeRejected = false)
    {
        var body = new JsonObject
        {
            ["search"] = string.IsNullOrWhiteSpace(text) ? "*" : text,
            ["filter"] = scope.ODataFilter(includeRejected || !reviewStatusFilter),
            ["select"] = string.Join(",", reviewStatusFilter ? [.. Fields, "reviewStatus"] : Fields),
            ["top"] = top,
        };
        if (!string.IsNullOrWhiteSpace(text))
        {
            // The semantic ranker needs query text; a bare "*" (listing a scope) is a plain filtered query.
            body["queryType"] = "semantic";
            body["semanticConfiguration"] = "default";
        }

        if (vector is not null)
        {
            body["vectorQueries"] = new JsonArray(new JsonObject
            {
                ["kind"] = "vector", ["vector"] = new JsonArray([.. vector.Select(v => (JsonNode)v)]), ["fields"] = "vector", ["k"] = Math.Max(top, 20),
            });
        }

        using var response = await SendAsync(HttpMethod.Post, $"indexes('{index}')/docs/search.post.search?api-version={ApiVersion}", body, cancellationToken)
            .ConfigureAwait(false);
        var json = await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var hits = new List<SearchHit>();
        foreach (var v in JsonNode.Parse(json)!["value"]!.AsArray())
        {
            var score = v!["@search.rerankerScore"]?.GetValue<double>() ?? v["@search.score"]?.GetValue<double>() ?? 0;
            hits.Add(new SearchHit(v.Deserialize<ChatChunk>(Json)!, score));
        }

        return hits;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonObject body, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, new Uri(endpoint, path)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new("Bearer", token.Token);
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? text : throw new HttpRequestException($"AI Search {(int)response.StatusCode}: {text[..Math.Min(text.Length, 600)]}");
    }
}

/// <summary>
/// In-process index for local runs and tests: the scope filter is applied the way the service applies the OData filter
/// (<see cref="ChatScope.Admits(ChatChunk)"/>), then a term-overlap score blended with cosine similarity ranks.
/// </summary>
public sealed class InMemoryChatSearch : IChatSearch
{
    private readonly Dictionary<string, ChatChunk> _chunks = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public IReadOnlyCollection<ChatChunk> Chunks
    {
        get
        {
            lock (_gate)
            {
                return [.. _chunks.Values];
            }
        }
    }

    public Task UpsertAsync(IReadOnlyList<ChatChunk> chunks, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var c in chunks)
            {
                _chunks[c.ChunkId] = c;
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(IReadOnlyList<string> chunkIds, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var id in chunkIds)
            {
                _chunks.Remove(id);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> WorkspaceChunkIdsAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        var scope = ChatScope.ForWorkspace(workspaceId);
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<string>>([.. _chunks.Values.Where(scope.Admits).Select(c => c.ChunkId)]);
        }
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(ChatScope scope, string text, float[]? vector, int top, CancellationToken cancellationToken = default,
        bool includeRejected = false) =>
        Task.FromResult(Rank(Chunks.Where(c => scope.Admits(c, includeRejected)), text, vector, top));

    /// <summary>Ranking shared with test doubles that skip the filter (to prove the tools' post-filter).</summary>
    public static IReadOnlyList<SearchHit> Rank(IEnumerable<ChatChunk> chunks, string text, float[]? vector, int top)
    {
        var terms = Terms(text);
        return [.. chunks
            .Select(c =>
            {
                var words = Terms(c.Markdown + " " + c.FileName + " " + c.Classification + " " + c.Carrier);
                var overlap = terms.Count == 0 ? 0 : terms.Count(words.Contains) / (double)terms.Count;
                var cosine = vector is not null && c.Vector is not null ? Cosine(vector, c.Vector) : 0;
                return new SearchHit(c, overlap + cosine);
            })
            .Where(h => h.Score > 0 || terms.Count == 0)
            .OrderByDescending(h => h.Score).ThenBy(h => h.Chunk.ChunkId, StringComparer.Ordinal)
            .Take(top)];
    }

    private static HashSet<string> Terms(string? text) =>
        [.. (text ?? "").ToLowerInvariant().Split([' ', '\n', '\r', '\t', '|', ',', '.', ':', ';', '(', ')', '?', '!', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2)];

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }
}
