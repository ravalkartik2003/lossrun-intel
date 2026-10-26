using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace LossRun.Integration;

/// <summary>
/// The mock quote management system (brief 12b), shaped like Guidewire Cloud API: a quote resource with annotations,
/// flags and follow-ups as sub-collections, responses wrapped as <c>{ data: { attributes } }</c>, and every write keyed
/// by <c>Idempotency-Key</c>: a repeated key returns the first response (200, <c>replayed: true</c>) and changes nothing.
/// It also receives the Event Grid CloudEvents. The same logic runs in the deployed mock (Blob storage) and in tests (in memory).
/// Brief 24 step 3 (ADR 0024), the receiver contract: a <c>PUT /quotes/{id}</c> whose body carries <c>quoteRevision</c> lower
/// than the highest one stored is refused with 409 <c>stale-revision</c> and changes nothing.
/// </summary>
/// <remarks>
/// Brief 25: with an <see cref="ILossRunApi"/>, the mock also fetches the full payload on <c>quote.revised</c> (and on a
/// <c>quote.payload</c> command), stores it by revision — a lower revision never replaces a higher one — and serves the
/// "Loss Information" grid built from it (<see cref="LossInformation"/>).
/// </remarks>
public sealed class QuoteSystemService(IQuoteSystemStorage storage, TimeProvider clock, ILossRunApi? api = null)
{
    public static readonly string[] Collections = ["annotations", "flags", "followups"];

    public sealed record WriteResult(int Status, JsonObject Body, bool Replayed);

    /// <summary><c>POST /quotes/{id}/{collection}</c> and <c>PUT /quotes/{id}</c> (collection "payload").</summary>
    public async Task<WriteResult> WriteAsync(string quoteId, string collection, string? idempotencyKey, string? correlation, JsonNode? body,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return new WriteResult(400, Error("idempotency-key-required", "Send an Idempotency-Key header."), false);
        }

        if (body is not JsonObject attributes)
        {
            return new WriteResult(400, Error("bad-body", "Send a JSON object."), false);
        }

        if (await storage.GetReplayAsync(idempotencyKey, cancellationToken).ConfigureAwait(false) is { } replay)
        {
            replay["data"]!["attributes"]!["replayed"] = true;
            return new WriteResult(200, replay, true);
        }

        var now = clock.GetUtcNow();
        var incoming = collection == "payload" ? Revision(attributes) : null;
        var id = $"{collection[..Math.Min(3, collection.Length)]}-{Guid.NewGuid():N}"[..16];
        var record = new JsonObject
        {
            ["id"] = id, ["idempotencyKey"] = idempotencyKey, ["lossrunOutboxId"] = correlation, ["receivedAt"] = now.ToString("O"),
            ["attributes"] = attributes.DeepClone(),
        };
        int? held = null;
        await storage.UpdateQuoteAsync(quoteId, quote =>
        {
            // The check and the write in one read-modify-write: a concurrent older command cannot slip past a newer one.
            held = quote["lossRunAnalysis"]?["attributes"] is JsonObject stored ? Revision(stored) : null;
            if (incoming is { } n && held is { } h && n < h)
            {
                quote["staleRefused"] = (quote["staleRefused"]?.GetValue<int>() ?? 0) + 1; // brief 25: the integration strip shows it
                return;
            }

            held = null;
            quote["id"] ??= quoteId;
            quote["updatedAt"] = now.ToString("O");
            if (collection == "payload")
            {
                quote["lossRunAnalysis"] = record.DeepClone();
            }
            else
            {
                (quote[collection] as JsonArray ?? (JsonArray)(quote[collection] = new JsonArray())).Add(record.DeepClone());
            }
        }, cancellationToken).ConfigureAwait(false);
        if (held is { } current)
        {
            return new WriteResult(409, Error("stale-revision", $"Quote {quoteId} holds revision {current}; revision {incoming} is older."), false);
        }

        var response = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["attributes"] = new JsonObject { ["id"] = id, ["quoteId"] = quoteId, ["collection"] = collection, ["idempotencyKey"] = idempotencyKey, ["receivedAt"] = now.ToString("O"), ["replayed"] = false },
                ["links"] = new JsonObject { ["self"] = new JsonObject { ["href"] = $"/quotes/{quoteId}" } },
            },
        };
        // First writer of a key wins; a concurrent duplicate (same key, two instances) is reported as the replay it is.
        if (!await storage.TrySaveReplayAsync(idempotencyKey, response, cancellationToken).ConfigureAwait(false))
        {
            await storage.UpdateQuoteAsync(quoteId, quote => RemoveById(quote, collection, id), cancellationToken).ConfigureAwait(false);
            var first = (await storage.GetReplayAsync(idempotencyKey, cancellationToken).ConfigureAwait(false))!;
            first["data"]!["attributes"]!["replayed"] = true;
            return new WriteResult(200, first, true);
        }

        if (collection == "payload" && incoming is { } applied)
        {
            await TryRefreshAsync(quoteId, applied, cancellationToken).ConfigureAwait(false);
        }

        return new WriteResult(collection == "payload" ? 200 : 201, response, false);
    }

    // ------------------------------------------------------------------ brief 25: the full payload and the grid

    /// <summary>
    /// Fetches the payload at a revision (the latest when that one is superseded) and the uploads' manifests, and stores them
    /// unless the quote already holds a newer revision. Never throws: a failure is recorded on the quote and retried by the next event.
    /// </summary>
    public async Task TryRefreshAsync(string quoteId, int revision, CancellationToken cancellationToken = default)
    {
        if (api is null)
        {
            return;
        }

        try
        {
            var q = Uri.EscapeDataString(quoteId);
            var (status, payload) = await api.GetAsync($"quotes/{q}/quote-payload?revision={revision}", cancellationToken).ConfigureAwait(false);
            if (status == 409)
            {
                (status, payload) = await api.GetAsync($"quotes/{q}/quote-payload", cancellationToken).ConfigureAwait(false);
            }

            if (status != 200 || payload is null)
            {
                await NoteAsync(quoteId, $"payload fetch answered {status}", cancellationToken).ConfigureAwait(false);
                return;
            }

            var manifests = new JsonArray();
            foreach (var upload in (payload["policyPeriods"] as JsonArray ?? []).Select(p => p?["uploadId"]?.ToString()).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                if (await api.GetAsync($"uploads/{Uri.EscapeDataString(upload)}", cancellationToken).ConfigureAwait(false) is (200, { } manifest))
                {
                    manifests.Add(new JsonObject { ["uploadId"] = upload, ["items"] = manifest["items"]?.DeepClone() });
                }
            }

            await AcceptPayloadAsync(quoteId, payload, manifests, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            await NoteAsync(quoteId, e.GetType().Name + ": " + e.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Stores a payload by its revision; false when the quote already holds a newer one (the 409 rule, applied to fetches too).</summary>
    public async Task<bool> AcceptPayloadAsync(string quoteId, JsonObject payload, JsonArray manifests, CancellationToken cancellationToken = default)
    {
        var revision = payload["quoteRevision"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
        var current = (await storage.GetQuoteAsync(quoteId, cancellationToken).ConfigureAwait(false))?["payloadRevision"]?.GetValue<int>();
        if (current is { } c && c > revision)
        {
            return false;
        }

        await storage.SavePayloadAsync(quoteId, revision, payload.ToJsonString(), cancellationToken).ConfigureAwait(false);
        var accepted = false;
        await storage.UpdateQuoteAsync(quoteId, quote =>
        {
            accepted = quote["payloadRevision"]?.GetValue<int>() is not { } held || held <= revision;
            if (accepted)
            {
                quote["id"] ??= quoteId;
                quote["payloadRevision"] = revision;
                quote["payloadReceivedAt"] = clock.GetUtcNow().ToString("O");
                quote["manifests"] = manifests.DeepClone();
                quote.Remove("lastFetchError");
            }
        }, cancellationToken).ConfigureAwait(false);
        return accepted;
    }

    /// <summary>The "Loss Information" grid of a quote from its newest stored payload (null when the quote is unknown).</summary>
    public async Task<LossInfoGrid?> GridAsync(string quoteId, CancellationToken cancellationToken = default)
    {
        if (await storage.GetQuoteAsync(quoteId, cancellationToken).ConfigureAwait(false) is not { } quote)
        {
            return null;
        }

        var revision = quote["payloadRevision"]?.GetValue<int>();
        var payload = revision is { } r && await storage.GetPayloadAsync(quoteId, r, cancellationToken).ConfigureAwait(false) is { } json ? JsonNode.Parse(json) as JsonObject : null;
        var manifests = (quote["manifests"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        return LossInformation.Build(quoteId, payload, quote, manifests);
    }

    /// <summary>The newest stored payload revision and when it arrived (what the page's live stream watches).</summary>
    public async Task<(int? Revision, string? At, string? LastEvent)> VersionAsync(string quoteId, CancellationToken cancellationToken = default)
    {
        var quote = await storage.GetQuoteAsync(quoteId, cancellationToken).ConfigureAwait(false);
        return (quote?["payloadRevision"]?.GetValue<int>(), quote?["payloadReceivedAt"]?.ToString(), (quote?["events"] as JsonArray)?.LastOrDefault()?["id"]?.ToString());
    }

    private Task NoteAsync(string quoteId, string error, CancellationToken cancellationToken) =>
        storage.UpdateQuoteAsync(quoteId, quote => quote["lastFetchError"] = error, cancellationToken);

    /// <summary>An Event Grid CloudEvent (structured mode); deduplicated by event id (Event Grid is at-least-once).</summary>
    public async Task<bool> ReceiveEventAsync(JsonObject cloudEvent, CancellationToken cancellationToken = default)
    {
        var id = cloudEvent["id"]?.GetValue<string>() ?? throw new ArgumentException("CloudEvent without id.");
        var replayKey = $"event:{id}";
        if (await storage.GetReplayAsync(replayKey, cancellationToken).ConfigureAwait(false) is not null)
        {
            return false;
        }

        if (cloudEvent["data"] is null && cloudEvent["data_base64"]?.GetValue<string>() is { } b64)
        {
            cloudEvent["data"] = JsonNode.Parse(Convert.FromBase64String(b64)); // binary-mode publishers
        }

        var quoteId = cloudEvent["data"]?["quoteId"]?.GetValue<string>() ?? "_events";
        var entry = new JsonObject
        {
            ["id"] = id, ["source"] = cloudEvent["source"]?.DeepClone(), ["type"] = cloudEvent["type"]?.DeepClone(), ["subject"] = cloudEvent["subject"]?.DeepClone(), ["time"] = cloudEvent["time"]?.DeepClone(),
            ["receivedAt"] = clock.GetUtcNow().ToString("O"), ["data"] = cloudEvent["data"]?.DeepClone(),
        };
        if (!await storage.TrySaveReplayAsync(replayKey, new JsonObject { ["data"] = new JsonObject { ["attributes"] = new JsonObject { ["eventId"] = id } } }, cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }

        await storage.UpdateQuoteAsync(quoteId, quote =>
        {
            quote["id"] ??= quoteId;
            (quote["events"] as JsonArray ?? (JsonArray)(quote["events"] = new JsonArray())).Add(entry);
        }, cancellationToken).ConfigureAwait(false);
        // Brief 25: a revised quote is fetched at its revision, so the loss-information page fills without a reload.
        if (entry["type"]?.ToString() == "lossrun.quote.revised" && entry["data"]?["quoteRevision"] is JsonValue rv && rv.TryGetValue<int>(out var revised))
        {
            await TryRefreshAsync(quoteId, revised, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public async Task<JsonObject?> GetQuoteAsync(string quoteId, CancellationToken cancellationToken = default) =>
        await storage.GetQuoteAsync(quoteId, cancellationToken).ConfigureAwait(false) is { } q ? new JsonObject { ["data"] = new JsonObject { ["attributes"] = q } } : null;

    public Task<IReadOnlyList<string>> QuoteIdsAsync(CancellationToken cancellationToken = default) => storage.QuoteIdsAsync(cancellationToken);

    private static void RemoveById(JsonObject quote, string collection, string id)
    {
        if (quote[collection] is JsonArray items && items.FirstOrDefault(i => i?["id"]?.GetValue<string>() == id) is { } mine)
        {
            items.Remove(mine);
        }
    }

    private static int? Revision(JsonObject attributes) =>
        attributes["quoteRevision"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;

    private static JsonObject Error(string code, string message) => new() { ["errors"] = new JsonArray(new JsonObject { ["code"] = code, ["message"] = message }) };
}

public interface IQuoteSystemStorage
{
    Task<JsonObject?> GetQuoteAsync(string quoteId, CancellationToken cancellationToken = default);

    /// <summary>Read-modify-write; <paramref name="change"/> may run more than once (optimistic concurrency).</summary>
    Task UpdateQuoteAsync(string quoteId, Action<JsonObject> change, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> QuoteIdsAsync(CancellationToken cancellationToken = default);

    Task<JsonObject?> GetReplayAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Stores the first response for a key; false when the key already has one.</summary>
    Task<bool> TrySaveReplayAsync(string key, JsonObject response, CancellationToken cancellationToken = default);

    /// <summary>Brief 25: a quote's full payload at a revision (overwrites the same revision only).</summary>
    Task SavePayloadAsync(string quoteId, int revision, string json, CancellationToken cancellationToken = default);

    Task<string?> GetPayloadAsync(string quoteId, int revision, CancellationToken cancellationToken = default);
}

/// <summary>Brief 25: the loss-run API as the quote system calls it (GET only), with its own credential.</summary>
public interface ILossRunApi
{
    /// <param name="path">Relative to the API base, e.g. <c>quotes/Q-1/quote-payload?revision=2</c>.</param>
    Task<(int Status, JsonObject? Body)> GetAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>HTTP to the loss-run API with HTTP Basic (the quote system's service user; Key Vault reference in app settings).</summary>
public sealed class HttpLossRunApi(HttpClient http, Uri baseUrl, string credential) : ILossRunApi
{
    public async Task<(int Status, JsonObject? Body)> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, path));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(credential)));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ((int)response.StatusCode, text.Length > 0 && text[0] == '{' ? JsonNode.Parse(text) as JsonObject : null);
    }
}

public sealed class InMemoryQuoteSystemStorage : IQuoteSystemStorage
{
    private readonly ConcurrentDictionary<string, JsonObject> _quotes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _replays = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<JsonObject?> GetQuoteAsync(string quoteId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_quotes.TryGetValue(quoteId, out var q) ? (JsonObject?)q.DeepClone() : null);
        }
    }

    public Task UpdateQuoteAsync(string quoteId, Action<JsonObject> change, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            change(_quotes.GetOrAdd(quoteId, _ => []));
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> QuoteIdsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. _quotes.Keys.Order(StringComparer.Ordinal)]);

    public Task<JsonObject?> GetReplayAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_replays.TryGetValue(key, out var r) ? JsonNode.Parse(r)!.AsObject() : null);

    public Task<bool> TrySaveReplayAsync(string key, JsonObject response, CancellationToken cancellationToken = default) =>
        Task.FromResult(_replays.TryAdd(key, response.ToJsonString()));

    private readonly ConcurrentDictionary<string, string> _payloads = new(StringComparer.Ordinal);

    public Task SavePayloadAsync(string quoteId, int revision, string json, CancellationToken cancellationToken = default)
    {
        _payloads[$"{quoteId}|{revision}"] = json;
        return Task.CompletedTask;
    }

    public Task<string?> GetPayloadAsync(string quoteId, int revision, CancellationToken cancellationToken = default) =>
        Task.FromResult(_payloads.TryGetValue($"{quoteId}|{revision}", out var json) ? json : null);
}

/// <summary>Blob layout: <c>quotes/{quoteId}.json</c> (ETag-guarded), <c>idempotency/{sha}.json</c> (create-if-absent).</summary>
public sealed class BlobQuoteSystemStorage(BlobContainerClient container) : IQuoteSystemStorage
{
    private static string QuotePath(string quoteId) => $"quotes/{Uri.EscapeDataString(quoteId)}.json";

    private static string ReplayPath(string key) =>
        $"idempotency/{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))}.json";

    public async Task<JsonObject?> GetQuoteAsync(string quoteId, CancellationToken cancellationToken = default) =>
        (await GetAsync(QuotePath(quoteId), cancellationToken).ConfigureAwait(false))?.Node;

    public async Task UpdateQuoteAsync(string quoteId, Action<JsonObject> change, CancellationToken cancellationToken = default)
    {
        var blob = container.GetBlobClient(QuotePath(quoteId));
        for (var attempt = 0; ; attempt++)
        {
            var current = await GetAsync(QuotePath(quoteId), cancellationToken).ConfigureAwait(false);
            var node = current?.Node ?? [];
            change(node);
            var conditions = current is null ? new BlobRequestConditions { IfNoneMatch = ETag.All } : new BlobRequestConditions { IfMatch = current.Value.ETag };
            try
            {
                await blob.UploadAsync(BinaryData.FromString(node.ToJsonString()), new BlobUploadOptions { Conditions = conditions }, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (RequestFailedException e) when (e.Status is 409 or 412 && attempt < 30)
            {
                await Task.Delay(Random.Shared.Next(10, 80), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<IReadOnlyList<string>> QuoteIdsAsync(CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        await foreach (var item in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "quotes/", cancellationToken).ConfigureAwait(false))
        {
            ids.Add(Uri.UnescapeDataString(item.Name["quotes/".Length..^".json".Length]));
        }

        return ids;
    }

    public async Task<JsonObject?> GetReplayAsync(string key, CancellationToken cancellationToken = default) =>
        (await GetAsync(ReplayPath(key), cancellationToken).ConfigureAwait(false))?.Node;

    public async Task<bool> TrySaveReplayAsync(string key, JsonObject response, CancellationToken cancellationToken = default)
    {
        try
        {
            await container.GetBlobClient(ReplayPath(key)).UploadAsync(BinaryData.FromString(response.ToJsonString()),
                new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException e) when (e.Status is 409 or 412)
        {
            return false;
        }
    }

    private static string PayloadPath(string quoteId, int revision) => $"payloads/{Uri.EscapeDataString(quoteId)}/{revision}.json";

    public Task SavePayloadAsync(string quoteId, int revision, string json, CancellationToken cancellationToken = default) =>
        container.GetBlobClient(PayloadPath(quoteId, revision)).UploadAsync(BinaryData.FromString(json), overwrite: true, cancellationToken);

    public async Task<string?> GetPayloadAsync(string quoteId, int revision, CancellationToken cancellationToken = default)
    {
        try
        {
            return (await container.GetBlobClient(PayloadPath(quoteId, revision)).DownloadContentAsync(cancellationToken).ConfigureAwait(false)).Value.Content.ToString();
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
    }

    private async Task<(JsonObject Node, ETag ETag)?> GetAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var download = await container.GetBlobClient(path).DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            return (JsonNode.Parse(download.Value.Content.ToString())!.AsObject(), download.Value.Details.ETag);
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
    }
}

