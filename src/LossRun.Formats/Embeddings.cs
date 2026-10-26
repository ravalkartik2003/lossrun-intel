using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using LossRun.Contracts;

namespace LossRun.Formats;

/// <param name="Vector">Embedding vector.</param>
/// <param name="Model">Deployment that produced it (centroids only compare within one model).</param>
/// <param name="Latency">Round-trip time of the request.</param>
/// <param name="Usage">Metered usage (one inputTokens event; stage "fingerprint", or "chat-embedding" / "chat-index" for chat).</param>
public sealed record EmbeddingResult(float[] Vector, string Model, TimeSpan Latency, IReadOnlyList<UsageEvent> Usage);

public interface IEmbeddingService
{
    Task<EmbeddingResult> EmbedAsync(string text, string documentId, string runId, CancellationToken cancellationToken);
}

/// <summary>
/// Azure OpenAI embeddings over REST with a bearer token (keyless). The deployment name is configuration
/// (Terraform output <c>embedding_model</c>). Records one UsageEvent per call on the meter.
/// </summary>
public sealed class AzureOpenAiEmbeddingService(
    Uri endpoint, TokenCredential credential, string deployment, ICostMeter meter, string region, string deploymentType, HttpClient? http = null,
    string stage = "fingerprint")
    : IEmbeddingService
{
    private const string ApiVersion = "2024-10-21";
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];
    private readonly HttpClient _http = http ?? new HttpClient();

    /// <summary>Tries per embedding when the deployment answers 429 (rate limit), honouring Retry-After.</summary>
    public const int MaxAttempts = 5;

    public async Task<EmbeddingResult> EmbedAsync(string text, string documentId, string runId, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        string body;
        for (var attempt = 1; ; attempt++)
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, $"openai/deployments/{deployment}/embeddings?api-version={ApiVersion}"))
            {
                Content = JsonContent.Create(new { input = text }),
            };
            request.Headers.Authorization = new("Bearer", token.Token);
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            // Brief 12b: a burst (35 spreadsheets fingerprinted and chat-indexed at once) exceeded the deployment's request rate;
            // wait as long as the service says (Retry-After), up to 5 tries, instead of failing the document or the index message.
            if ((int)response.StatusCode != 429 || attempt == MaxAttempts)
            {
                break;
            }

            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(30, 2 << attempt));
            response.Dispose();
            await Task.Delay(wait + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500)), cancellationToken).ConfigureAwait(false);
        }

        using var _ = response;
        stopwatch.Stop();
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Embedding request failed ({(int)response.StatusCode}): {body}");
        }

        using var json = JsonDocument.Parse(body);
        var vector = json.RootElement.GetProperty("data")[0].GetProperty("embedding").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        var tokens = json.RootElement.TryGetProperty("usage", out var usage) && usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
        var requestId = response.Headers.TryGetValues("apim-request-id", out var ids) ? ids.FirstOrDefault() : null;
        var usageEvent = new UsageEvent(documentId, runId, stage, "AzureOpenAI", "inputTokens", tokens, "token", deployment, deploymentType, region,
            DateTimeOffset.UtcNow, requestId ?? Guid.NewGuid().ToString("N"));
        await meter.RecordAsync(usageEvent, cancellationToken).ConfigureAwait(false);
        return new EmbeddingResult(vector, deployment, stopwatch.Elapsed, [usageEvent]);
    }
}
