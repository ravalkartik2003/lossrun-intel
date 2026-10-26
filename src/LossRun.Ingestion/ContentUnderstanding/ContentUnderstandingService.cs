using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Azure;
using Azure.AI.ContentUnderstanding;
using Azure.Core;

namespace LossRun.Ingestion.ContentUnderstanding;

/// <summary>One completed analyze call: the final operation JSON (<c>{id, status, result, usage}</c>) and timing.</summary>
/// <param name="AnalyzerId">Analyzer that ran.</param>
/// <param name="OperationId">CU operation (result) id, recorded as the UsageEvent correlation id.</param>
/// <param name="RawJson">Final operation body as returned by the service (stored to Blob unchanged).</param>
/// <param name="Latency">Wall-clock time from submit to completed result, including polling.</param>
/// <param name="Mode"><c>async</c> (GA long-running operation, polled) or <c>inline</c> (2026-06-01-preview synchronous call).</param>
public sealed record CuCallResult(string AnalyzerId, string OperationId, string RawJson, TimeSpan Latency, string Mode = "async")
{
    public JsonElement Root => JsonDocument.Parse(RawJson).RootElement;
}

/// <summary>Content Understanding calls the pipeline needs; faked in unit tests.</summary>
public interface IContentUnderstandingService
{
    Task<CuCallResult> AnalyzeAsync(string analyzerId, BinaryData content, string contentType, CancellationToken cancellationToken);

    /// <summary>Creates the analyzer from its checked-in definition if it does not exist (ids are versioned, never replaced).</summary>
    Task EnsureAnalyzerAsync(string analyzerId, CancellationToken cancellationToken);
}

/// <summary>
/// Synchronous ("inline") analyze of the 2026-06-01-preview API for small documents (brief 04 Answers 4: measure it for
/// ≤ 5 pages; do not build on the preview for anything else). Billed as <c>documentPagesStandardInline</c> ($7.50/1K vs
/// $5.00/1K async). Any non-success falls back to the GA asynchronous call.
/// </summary>
/// <param name="MaxPages">Use inline only for PDFs/images of at most this many pages; 0 disables it.</param>
public sealed record InlineAnalyzeOptions(Uri Endpoint, TokenCredential Credential, int MaxPages = 5, string ApiVersion = "2026-06-01-preview",
    IReadOnlyList<string>? Analyzers = null)
{
    public IReadOnlyList<string> SupportedAnalyzers => Analyzers ?? ["prebuilt-layout", "prebuilt-read"];
}

/// <summary>Real implementation over the <c>Azure.AI.ContentUnderstanding</c> SDK (protocol methods, keyless credential).</summary>
public sealed class ContentUnderstandingService(ContentUnderstandingClient client, InlineAnalyzeOptions? inline = null, HttpClient? http = null) : IContentUnderstandingService
{
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];
    private readonly HashSet<string> _ensured = [];
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(100) };

    /// <param name="maxAttempts">
    /// Brief 33: attempts per HTTP request, set on the client's pipeline (Azure.Core: 429, 408 and 5xx, Retry-After honoured, exponential to a 60 s
    /// cap; the captured defaults are 3 retries from 0.8 s). The pipeline retries each request, so a failed poll polls the same operation again and
    /// never re-submits the analyze. A 429 is not billed; a completed analyze is.
    /// </param>
    public ContentUnderstandingService(Uri endpoint, TokenCredential credential, InlineAnalyzeOptions? inline = null, int maxAttempts = IngestionOptions.DefaultCuMaxAttempts)
        : this(new ContentUnderstandingClient(endpoint, credential, ClientOptions(maxAttempts)), inline)
    {
    }

    public static ContentUnderstandingClientOptions ClientOptions(int maxAttempts, Azure.Core.Pipeline.HttpPipelineTransport? transport = null)
    {
        var options = new ContentUnderstandingClientOptions();
        options.Retry.MaxRetries = Math.Max(0, maxAttempts - 1);
        options.Retry.MaxDelay = TimeSpan.FromSeconds(60);
        options.Retry.Mode = RetryMode.Exponential;
        if (transport is not null)
        {
            options.Transport = transport;
        }

        return options;
    }

    public async Task<CuCallResult> AnalyzeAsync(string analyzerId, BinaryData content, string contentType, CancellationToken cancellationToken)
    {
        if (inline is { MaxPages: > 0 } && inline.SupportedAnalyzers.Contains(analyzerId) && PageCount(content, contentType) is { } pages && pages <= inline.MaxPages
            && await TryInlineAsync(analyzerId, content, contentType, cancellationToken).ConfigureAwait(false) is { } result)
        {
            return result;
        }

        var stopwatch = Stopwatch.StartNew();
        var operation = await client.AnalyzeBinaryAsync(
            WaitUntil.Completed, analyzerId, content, contentRange: null, contentType: contentType, processingLocation: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        // The final poll response carries the whole operation (id, status, result, usage); keep it verbatim.
        var body = operation.GetRawResponse().Content.ToString();
        var id = JsonDocument.Parse(body).RootElement.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? operation.Id : operation.Id;
        return new CuCallResult(analyzerId, id, body, stopwatch.Elapsed);
    }

    internal async Task<CuCallResult?> TryInlineAsync(string analyzerId, BinaryData content, string contentType, CancellationToken cancellationToken)
    {
        var token = await inline!.Credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(inline.Endpoint, $"contentunderstanding/analyzers/{analyzerId}:analyzeBinaryInline?api-version={inline.ApiVersion}"))
        {
            Content = new ByteArrayContent(content.ToArray()),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        request.Headers.Authorization = new("Bearer", token.Token);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            if (!response.IsSuccessStatusCode || !Succeeded(body))
            {
                return null; // fall back to the GA asynchronous call
            }

            var id = response.Headers.TryGetValues("apim-request-id", out var ids) ? ids.First() : Guid.NewGuid().ToString("N");
            return new CuCallResult(analyzerId, id, body, stopwatch.Elapsed, "inline");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Brief 30 item 39: a body that is not JSON (a gateway's HTML error page with a 200) is a failed inline call, not an exception.</summary>
    private static bool Succeeded(string body)
    {
        try
        {
            return JsonDocument.Parse(body).RootElement is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String && status.GetString() == "Succeeded";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int? PageCount(BinaryData content, string contentType)
    {
        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (contentType != "application/pdf")
        {
            return null;
        }

        try
        {
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(content.ToArray());
            return pdf.NumberOfPages;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task EnsureAnalyzerAsync(string analyzerId, CancellationToken cancellationToken)
    {
        if (_ensured.Contains(analyzerId))
        {
            return;
        }

        var existing = await client.GetAnalyzerAsync(analyzerId, new RequestContext { ErrorOptions = ErrorOptions.NoThrow, CancellationToken = cancellationToken })
            .ConfigureAwait(false);
        if (existing.Status == 404)
        {
            await client.CreateAnalyzerAsync(WaitUntil.Completed, analyzerId, RequestContent.Create(BinaryData.FromString(AnalyzerDefinitions.Get(analyzerId))),
                allowReplace: false, context: new RequestContext { CancellationToken = cancellationToken }).ConfigureAwait(false);
        }
        else if (existing.IsError)
        {
            throw new RequestFailedException(existing);
        }

        _ensured.Add(analyzerId);
    }
}

/// <summary>Analyzer definitions checked in under Analyzers/{analyzerId}.json.</summary>
public static class AnalyzerDefinitions
{
    public static string Get(string analyzerId)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"LossRun.Analyzers.{analyzerId}.json")
            ?? throw new InvalidOperationException($"No checked-in definition for analyzer '{analyzerId}' (expected Analyzers/{analyzerId}.json).");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
