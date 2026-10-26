using System.Text.Json.Nodes;
using Azure.Identity;
using Azure.Storage.Blobs;
using LossRun.Integration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Mock quote management system (brief 12b): Guidewire-Cloud-API-shaped REST over a Blob-backed record (the deployed app
// scales out, so state cannot live in memory). Function-key auth; identity-only storage.
var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();
builder.Services.AddApplicationInsightsTelemetryWorkerService().ConfigureFunctionsApplicationInsights();
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var blobs = new BlobServiceClient(new Uri(config["Storage:BlobEndpoint"]!), new DefaultAzureCredential());
    // Brief 25: with the loss-run API's address and the quote system's service credential (Key Vault reference), the mock
    // fetches the full payload on quote.revised and serves the loss-information page.
    ILossRunApi? api = config["LossRunApi:BaseUrl"] is { Length: > 0 } baseUrl && config["LossRunApi:Credential"] is { Length: > 0 } credential
        ? new HttpLossRunApi(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, new Uri(baseUrl.TrimEnd('/') + "/"), credential)
        : null;
    return new QuoteSystemService(new BlobQuoteSystemStorage(blobs.GetBlobContainerClient(config["QuoteSystem:Container"] ?? "quote-system-mock")), TimeProvider.System, api);
});
builder.Build().Run();

namespace LossRun.QuoteSystemMock
{
    public sealed class QuoteSystemFunctions(QuoteSystemService service)
    {
        [Function("ListQuotes")]
        public async Task<IActionResult> ListQuotes([HttpTrigger(AuthorizationLevel.Function, "get", Route = "quotes")] HttpRequest req) =>
            new JsonResult(new JsonObject { ["data"] = new JsonArray([.. (await service.QuoteIdsAsync(req.HttpContext.RequestAborted)).Select(id => (JsonNode)new JsonObject { ["id"] = id })]) });

        [Function("Quote")]
        public async Task<IActionResult> Quote([HttpTrigger(AuthorizationLevel.Function, "get", "put", Route = "quotes/{quoteId}")] HttpRequest req, string quoteId)
        {
            if (HttpMethods.IsGet(req.Method))
            {
                return await service.GetQuoteAsync(quoteId, req.HttpContext.RequestAborted) is { } q ? new JsonResult(q) : new NotFoundResult();
            }

            return await WriteAsync(req, quoteId, "payload");
        }

        [Function("QuoteCollection")]
        public Task<IActionResult> QuoteCollection([HttpTrigger(AuthorizationLevel.Function, "post", Route = "quotes/{quoteId}/{collection:regex(^(annotations|flags|followups)$)}")] HttpRequest req,
            string quoteId, string collection) => WriteAsync(req, quoteId, collection);

        /// <summary>Event Grid CloudEvents 1.0 webhook: the OPTIONS validation handshake, then one event per POST (or a batch).</summary>
        [Function("Events")]
        public async Task<IActionResult> Events([HttpTrigger(AuthorizationLevel.Function, "options", "post", Route = "events")] HttpRequest req)
        {
            if (HttpMethods.IsOptions(req.Method))
            {
                req.HttpContext.Response.Headers["WebHook-Allowed-Origin"] = req.Headers["WebHook-Request-Origin"].FirstOrDefault() ?? "*";
                req.HttpContext.Response.Headers["WebHook-Allowed-Rate"] = "120";
                return new OkResult();
            }

            var body = await JsonNode.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
            var events = body is JsonArray batch ? batch.OfType<JsonObject>().ToList() : body is JsonObject one ? [one] : [];
            foreach (var e in events)
            {
                await service.ReceiveEventAsync(e, req.HttpContext.RequestAborted);
            }

            return new OkResult();
        }

        // ------------------------------------------------------------------ brief 25: the loss-information page

        /// <summary>
        /// The page itself and its recorded replay: static files, anonymous. Brief 30 item 24: the page's data (the grid) and its stream are
        /// function-key routes like the rest of the mock; the page is opened with <c>?code=&lt;function key&gt;</c> and passes it on. A real quote
        /// system has its own UI and auth.
        /// </summary>
        [Function("LossInformationPage")]
        public IActionResult LossInformationPage([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ui/{file:regex(^(loss-information|loss-information\\.html|loss-information\\.js|replay\\.json)$)}")] HttpRequest req,
            string file) => file switch
            {
                "replay.json" => Asset("replay.json", "application/json"),
                "loss-information.js" => Asset("loss-information.js", "text/javascript; charset=utf-8"),
                _ => Asset("loss-information.html", "text/html; charset=utf-8"),
            };

        [Function("LossInformation")]
        public async Task<IActionResult> LossInformation([HttpTrigger(AuthorizationLevel.Function, "get", Route = "quotes/{quoteId}/loss-information")] HttpRequest req, string quoteId) =>
            await service.GridAsync(quoteId, req.HttpContext.RequestAborted) is { } grid ? new JsonResult(grid, Json) : new NotFoundResult();

        /// <summary>
        /// Server-sent events: <c>revision</c> whenever the stored payload or the last event changes. The mock scales out and the
        /// event may land on another instance, so the stream watches storage (every 2 s) rather than an in-memory signal; it ends
        /// after 4 minutes and EventSource reconnects.
        /// </summary>
        [Function("LossInformationStream")]
        public async Task LossInformationStream([HttpTrigger(AuthorizationLevel.Function, "get", Route = "quotes/{quoteId}/loss-information/stream")] HttpRequest req, string quoteId)
        {
            var response = req.HttpContext.Response;
            response.Headers.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
            var cancel = req.HttpContext.RequestAborted;
            (int?, string?, string?) last = default;
            var until = DateTime.UtcNow.AddMinutes(4);
            try
            {
                while (!cancel.IsCancellationRequested && DateTime.UtcNow < until)
                {
                    var now = await service.VersionAsync(quoteId, cancel);
                    if (now != last)
                    {
                        last = now;
                        await response.WriteAsync($"event: revision\ndata: {System.Text.Json.JsonSerializer.Serialize(new { quoteRevision = now.Revision, at = now.At })}\n\n", cancel);
                        await response.Body.FlushAsync(cancel);
                    }

                    await Task.Delay(2000, cancel);
                }
            }
            catch (OperationCanceledException)
            {
                // the page went away
            }
        }

        private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

        private static IActionResult Asset(string name, string contentType)
        {
            using var stream = typeof(QuoteSystemFunctions).Assembly.GetManifestResourceStream($"LossRun.QuoteSystemMock.wwwroot.{name}")
                ?? throw new FileNotFoundException(name);
            using var reader = new StreamReader(stream);
            return new ContentResult { Content = reader.ReadToEnd(), ContentType = contentType, StatusCode = 200 };
        }

        private async Task<IActionResult> WriteAsync(HttpRequest req, string quoteId, string collection)
        {
            JsonNode? body;
            try
            {
                body = await JsonNode.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
            }
            catch (System.Text.Json.JsonException)
            {
                body = null;
            }

            var result = await service.WriteAsync(quoteId, collection, req.Headers["Idempotency-Key"].FirstOrDefault(), req.Headers["X-LossRun-Outbox-Id"].FirstOrDefault(), body,
                req.HttpContext.RequestAborted);
            if (result.Replayed)
            {
                req.HttpContext.Response.Headers["Idempotent-Replayed"] = "true";
            }

            return new JsonResult(result.Body) { StatusCode = result.Status };
        }
    }
}
