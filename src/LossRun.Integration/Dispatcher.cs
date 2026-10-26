using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LossRun.Data;

namespace LossRun.Integration;

/// <summary>One call to the quote system: status, body.</summary>
public sealed record QuoteSystemResponse(int Status, string Body);

/// <summary>Where commands land: HTTP to the deployed mock, or the in-process service in tests.</summary>
public interface IQuoteSystemEndpoint
{
    Task<QuoteSystemResponse> SendAsync(string method, string path, string idempotencyKey, string body, CancellationToken cancellationToken = default);
}

/// <summary>HTTP to the quote system with its function key (Key Vault reference in app settings).</summary>
public sealed class HttpQuoteSystemEndpoint(HttpClient http, Uri baseUrl, string? key) : IQuoteSystemEndpoint
{
    public async Task<QuoteSystemResponse> SendAsync(string method, string path, string idempotencyKey, string body, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(baseUrl, path.TrimStart('/')))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(CommandHeaders.IdempotencyKey, idempotencyKey);
        request.Headers.Add(CommandHeaders.Correlation, idempotencyKey);
        if (key is { Length: > 0 })
        {
            request.Headers.Add("x-functions-key", key);
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return new QuoteSystemResponse((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>In-process endpoint over <see cref="QuoteSystemService"/> (tests; same idempotency logic as the deployed mock).</summary>
public sealed class InProcessQuoteSystemEndpoint(QuoteSystemService service) : IQuoteSystemEndpoint
{
    public async Task<QuoteSystemResponse> SendAsync(string method, string path, string idempotencyKey, string body, CancellationToken cancellationToken = default)
    {
        var parts = path.Trim('/').Split('/');
        var quoteId = Uri.UnescapeDataString(parts[1]);
        var collection = parts.Length > 2 ? parts[2] : "payload";
        var result = await service.WriteAsync(quoteId, collection, idempotencyKey, idempotencyKey, JsonNode.Parse(body), cancellationToken).ConfigureAwait(false);
        return new QuoteSystemResponse(result.Status, result.Body.ToJsonString());
    }
}

public enum DispatchResult
{
    /// <summary>The quote system applied it (201/200).</summary>
    Delivered,

    /// <summary>The quote system had already applied this idempotency key (a redelivery or re-publish).</summary>
    Replayed,

    /// <summary>4xx: retrying will not help; the message goes to the dead-letter queue with the reason.</summary>
    Rejected,

    /// <summary>
    /// Brief 24 step 3: 409 stale-revision, the quote system already holds a newer revision of the quote. Delivered-superseded:
    /// recorded on the outbox row, completed (not dead-lettered, not retried).
    /// </summary>
    Superseded,
}

/// <summary>
/// The Service Bus consumer's logic (brief 12b): one command → one quote-system call with Idempotency-Key = outbox id;
/// the answer is recorded on the outbox row. 5xx and transport errors throw (Service Bus redelivers, up to 10 times,
/// then dead-letters); 4xx is <see cref="DispatchResult.Rejected"/>.
/// </summary>
public sealed class CommandDispatcher(IQuoteSystemEndpoint endpoint, IRecordStore records, TimeProvider clock)
{
    public static (string Method, string Path) Route(string kind, string quoteId)
    {
        var q = Uri.EscapeDataString(quoteId);
        return kind switch
        {
            OutboxKinds.Flags => ("POST", $"quotes/{q}/flags"),
            OutboxKinds.Annotation or OutboxKinds.MemoLink => ("POST", $"quotes/{q}/annotations"),
            OutboxKinds.FollowUp => ("POST", $"quotes/{q}/followups"),
            OutboxKinds.QuotePayload => ("PUT", $"quotes/{q}"),
            _ => throw new ArgumentException($"No quote-system route for command kind '{kind}'."),
        };
    }

    public async Task<(DispatchResult Result, string Detail)> DispatchAsync(string messageId, string kind, string? quoteId, string body, CancellationToken cancellationToken = default)
    {
        if (quoteId is null)
        {
            await records.RecordOutboxDeliveryAsync(messageId, "rejected", "{\"reason\":\"no quote id\"}", clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            return (DispatchResult.Rejected, "command without a quote id");
        }

        var (method, path) = Route(kind, quoteId);
        var response = await endpoint.SendAsync(method, path, messageId, body, cancellationToken).ConfigureAwait(false);
        if (response.Status >= 500 || response.Status is (int)HttpStatusCode.RequestTimeout or (int)HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException($"quote system {response.Status} for {kind} {messageId}; Service Bus will redeliver");
        }

        var replayed = response.Status == 200 && response.Body.Contains("\"replayed\":true", StringComparison.Ordinal);
        var stale = response.Status == (int)HttpStatusCode.Conflict && response.Body.Contains("\"stale-revision\"", StringComparison.Ordinal);
        var (result, status) = response.Status switch
        {
            >= 200 and < 300 => replayed ? (DispatchResult.Replayed, "replayed") : (DispatchResult.Delivered, "delivered"),
            _ when stale => (DispatchResult.Superseded, "superseded"),
            _ => (DispatchResult.Rejected, "rejected"),
        };
        await records.RecordOutboxDeliveryAsync(messageId, status, Json(response), clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return (result, $"{response.Status} {method} {path}");
    }

    private static string Json(QuoteSystemResponse r)
    {
        try
        {
            return new JsonObject { ["status"] = r.Status, ["body"] = JsonNode.Parse(r.Body) }.ToJsonString();
        }
        catch (System.Text.Json.JsonException)
        {
            return new JsonObject { ["status"] = r.Status, ["body"] = r.Body.Length > 1000 ? r.Body[..1000] : r.Body }.ToJsonString();
        }
    }
}
