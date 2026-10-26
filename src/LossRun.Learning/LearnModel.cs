using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using LossRun.Contracts;

namespace LossRun.Learning;

/// <summary>One turn of a learn/delta conversation (the static prefix is sent separately so it caches).</summary>
/// <param name="Role">user | assistant.</param>
public sealed record ModelTurn(string Role, string Text);

/// <param name="Stage">learn | delta (the meter stage).</param>
/// <param name="Deployment">Deployment name (configuration).</param>
/// <param name="ModelVersion">Model version behind the deployment (configuration).</param>
/// <param name="Instructions">Static prefix: identical bytes across calls so the service caches it (≥ 1,024 tokens).</param>
/// <param name="Turns">Per-document part, then previous answers and verification feedback.</param>
/// <param name="SchemaName">Name of the strict response schema.</param>
/// <param name="Schema">Strict JSON Schema of the answer.</param>
/// <param name="PageImagePng">Optional page-1 rendering attached to the first user turn.</param>
/// <param name="CacheKey">prompt_cache_key: routes calls sharing a prefix to the same cache.</param>
public sealed record ModelRequest(
    string Stage,
    string Deployment,
    string? ModelVersion,
    string Instructions,
    IReadOnlyList<ModelTurn> Turns,
    string SchemaName,
    JsonObject Schema,
    byte[]? PageImagePng,
    string CacheKey,
    string DocumentId,
    string RunId);

/// <param name="Json">The structured answer (conforms to the request schema).</param>
/// <param name="InputTokens">All prompt tokens (cached ones included).</param>
/// <param name="CachedInputTokens">Prompt tokens served from the prompt cache (a subset of input).</param>
public sealed record ModelResponse(
    string Json,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningTokens,
    TimeSpan Latency,
    string CorrelationId,
    IReadOnlyList<UsageEvent> Usage);

/// <summary>The only model surface of the learn path. Fakes implement it offline; Azure OpenAI implements it in production.</summary>
public interface ILearnModel
{
    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default);
}

public static class ModelUsage
{
    /// <summary>One event per meter (input, cached input, output); cached input is billed as a subset of input (CostCalculator).</summary>
    public static IReadOnlyList<UsageEvent> Events(ModelRequest request, long input, long cached, long output, string correlationId, string deploymentType,
        string region, DateTimeOffset at)
    {
        UsageEvent Event(string meter, long quantity) => new(request.DocumentId, request.RunId, request.Stage, "AzureOpenAI", meter, quantity, "token",
            request.Deployment, deploymentType, region, at, correlationId, request.ModelVersion);
        return [Event("inputTokens", input), Event("cachedInputTokens", cached), Event("outputTokens", output)];
    }
}

/// <summary>
/// Azure OpenAI Responses API (<c>/openai/v1/responses</c>) over REST with a bearer token (keyless). Strict structured
/// outputs; <c>store: false</c> (nothing retained server-side); <c>prompt_cache_key</c> + 24 h cache retention so the
/// static prefix is billed at the cached rate after the first call (azure-extraction-stack.md, prompt caching).
/// </summary>
public sealed class AzureOpenAiLearnModel(Uri endpoint, TokenCredential credential, ICostMeter meter, string deploymentType, string region,
    HttpClient? http = null) : ILearnModel
{
    /// <summary>Attempts per request (first try + retries on 429/5xx).</summary>
    public const int MaxAttempts = 8;

    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        var input = new JsonArray { new JsonObject { ["role"] = "developer", ["content"] = request.Instructions } };
        for (var i = 0; i < request.Turns.Count; i++)
        {
            var turn = request.Turns[i];
            if (turn.Role == "assistant")
            {
                input.Add(new JsonObject { ["role"] = "assistant", ["content"] = turn.Text });
                continue;
            }

            var content = new JsonArray { new JsonObject { ["type"] = "input_text", ["text"] = turn.Text } };
            if (i == 0 && request.PageImagePng is { } png)
            {
                content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = $"data:image/png;base64,{Convert.ToBase64String(png)}", ["detail"] = "high" });
            }

            input.Add(new JsonObject { ["role"] = "user", ["content"] = content });
        }

        var body = new JsonObject
        {
            ["model"] = request.Deployment,
            ["input"] = input,
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject { ["type"] = "json_schema", ["name"] = request.SchemaName, ["strict"] = true, ["schema"] = request.Schema.DeepClone() },
            },
            ["prompt_cache_key"] = request.CacheKey,
            ["prompt_cache_retention"] = "24h",
            ["store"] = false,
        };

        var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "openai/v1/responses")) { Content = JsonContent.Create(body) };
            message.Headers.Authorization = new("Bearer", token.Token);
            var stopwatch = Stopwatch.StartNew();
            using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            if ((int)response.StatusCode is 429 or 500 or 502 or 503 && attempt < MaxAttempts - 1)
            {
                // Token-rate throttling (08b learn-reliability study: 27 of 40 concurrent learns hit 429 at 100K TPM) waits as
                // long as the service asks (retry-after-ms, then Retry-After), else backs off exponentially, capped at 60 s.
                var wait = response.Headers.TryGetValues("retry-after-ms", out var ms) && double.TryParse(ms.FirstOrDefault(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var millis)
                    ? TimeSpan.FromMilliseconds(millis)
                    : response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(60, 2 << attempt));
                await Task.Delay(wait + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500)), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Responses API request failed ({(int)response.StatusCode}): {text}");
            }

            return await ParseAsync(request, text, stopwatch.Elapsed, response.Headers.TryGetValues("apim-request-id", out var ids) ? ids.FirstOrDefault() : null,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ModelResponse> ParseAsync(ModelRequest request, string text, TimeSpan latency, string? requestId, CancellationToken cancellationToken)
    {
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        if (root.TryGetProperty("status", out var status) && status.GetString() is { } s && s != "completed")
        {
            throw new InvalidOperationException($"Responses API returned status '{s}': {text[..Math.Min(text.Length, 500)]}");
        }

        var answer = root.GetProperty("output").EnumerateArray()
            .Where(o => o.GetProperty("type").GetString() == "message")
            .SelectMany(o => o.GetProperty("content").EnumerateArray())
            .FirstOrDefault(c => c.GetProperty("type").GetString() == "output_text");
        if (answer.ValueKind == JsonValueKind.Undefined)
        {
            var refusal = root.GetProperty("output").EnumerateArray().SelectMany(o => o.TryGetProperty("content", out var c) ? c.EnumerateArray() : [])
                .FirstOrDefault(c => c.GetProperty("type").GetString() == "refusal");
            throw new InvalidOperationException(refusal.ValueKind == JsonValueKind.Undefined ? "Responses API returned no output_text." : $"Model refused: {refusal}");
        }

        var usage = root.GetProperty("usage");
        long Get(JsonElement e, string a, string? b = null) =>
            b is null ? (e.TryGetProperty(a, out var v) ? v.GetInt64() : 0)
                : e.TryGetProperty(a, out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty(b, out var w) ? w.GetInt64() : 0;
        var (input, cached, output, reasoning) = (Get(usage, "input_tokens"), Get(usage, "input_tokens_details", "cached_tokens"), Get(usage, "output_tokens"),
            Get(usage, "output_tokens_details", "reasoning_tokens"));
        var correlation = requestId ?? (root.TryGetProperty("id", out var id) ? id.GetString()! : Guid.NewGuid().ToString("N"));
        var events = ModelUsage.Events(request, input, cached, output, correlation, deploymentType, region, DateTimeOffset.UtcNow);
        foreach (var e in events)
        {
            await meter.RecordAsync(e, cancellationToken).ConfigureAwait(false);
        }

        return new ModelResponse(answer.GetProperty("text").GetString()!, input, cached, output, reasoning, latency, correlation, events);
    }
}
