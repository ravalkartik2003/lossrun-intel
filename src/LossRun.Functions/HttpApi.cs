using System.Diagnostics;
using System.Text.Json;
using Azure.Storage.Blobs;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Ingestion;
using LossRun.Orchestration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Configuration;

namespace LossRun.Functions;

/// <summary>HTTP API v1 (route prefix api/v1 in host.json; contract in docs/api/openapi.yaml). Every route authenticates.</summary>
public sealed class HttpApi(LossRunRuntime runtime, IAuthenticator authenticator, ApiOptions options, IConfiguration config)
{
    /// <summary>Largest upload request: the Functions host's request body limit (FUNCTIONS_REQUEST_BODY_SIZE_LIMIT default).</summary>
    internal const long MaxUploadBytes = 100L * 1024 * 1024;

    private UploadApi Api(DurableTaskClient client) => new(runtime, new DurableOrchestrationClient(client), options);

    [Function("CreateUpload")]
    public async Task<IActionResult> CreateUpload([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "uploads")] HttpRequest req,
        [DurableClient] DurableTaskClient client)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        List<UploadFile> files;
        string? quoteId, carrierHint, workspaceId = null;
        DateOnly? valuationDate = null, lastDayWorked = null, targetEffective = null;
        List<int>? requestedYears = null;
        List<ContextPeriod>? policyPeriods = null;
        List<PeriodExposure>? payroll = null, fte = null;
        if (req.HasFormContentType)
        {
            // Brief 13 follow-up: the worker's Kestrel caps request bodies at 30,000,000 bytes, so a 50 MB batch failed with an
            // unlogged 500. Uploads get the Functions host's own limit (100 MB); every other route keeps the default.
            if (req.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
            {
                bodyLimit.MaxRequestBodySize = MaxUploadBytes;
            }

            IFormCollection form;
            try
            {
                form = await req.ReadFormAsync(req.HttpContext.RequestAborted);
            }
            catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return Json(ApiResult.Error(413, "upload-too-large", $"An upload request is limited to {MaxUploadBytes / 1024 / 1024} MB; split the batch."));
            }

            files = [];
            foreach (var file in form.Files)
            {
                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, req.HttpContext.RequestAborted);
                files.Add(new UploadFile(Path.GetFileName(file.FileName), buffer.ToArray()));
            }

            (quoteId, carrierHint) = (form["quoteId"].FirstOrDefault(), form["carrierHint"].FirstOrDefault());
            workspaceId = form["workspaceId"].FirstOrDefault() is { Length: > 0 } ws ? ws : null;
            if (form["valuationDate"].FirstOrDefault() is { Length: > 0 } v)
            {
                if (!DateOnly.TryParseExact(v, "yyyy-MM-dd", out var parsed))
                {
                    return Json(ApiResult.Error(400, "bad-valuation-date", "valuationDate must be yyyy-MM-dd."));
                }

                valuationDate = parsed;
            }

            if (form["policyPeriods"].FirstOrDefault() is { Length: > 0 } periods)
            {
                try
                {
                    policyPeriods = JsonSerializer.Deserialize<List<ContextPeriod>>(periods, CanonicalJson.Options);
                }
                catch (JsonException)
                {
                    return Json(ApiResult.Error(400, "bad-policy-periods", "policyPeriods must be a JSON array of { policyNumber, effectiveDate, expirationDate }."));
                }
            }

            try
            {
                payroll = form["payrollByPeriod"].FirstOrDefault() is { Length: > 0 } p ? JsonSerializer.Deserialize<List<PeriodExposure>>(p, CanonicalJson.Options) : null;
                fte = form["fteByPeriod"].FirstOrDefault() is { Length: > 0 } f ? JsonSerializer.Deserialize<List<PeriodExposure>>(f, CanonicalJson.Options) : null;
            }
            catch (JsonException)
            {
                return Json(ApiResult.Error(400, "bad-exposure", "payrollByPeriod / fteByPeriod must be JSON arrays of { effectiveDate, amount, policyNumber? }."));
            }

            if (form["targetPolicyEffectiveDate"].FirstOrDefault() is { Length: > 0 } tpe)
            {
                if (!DateOnly.TryParseExact(tpe, "yyyy-MM-dd", out var parsed))
                {
                    return Json(ApiResult.Error(400, "bad-target-effective-date", "targetPolicyEffectiveDate must be yyyy-MM-dd."));
                }

                targetEffective = parsed;
            }

            if (form["requestedPolicyYears"].FirstOrDefault() is { Length: > 0 } rpy)
            {
                var parts = rpy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Any(y => !int.TryParse(y, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)))
                {
                    return Json(ApiResult.Error(400, "bad-requested-years", "requestedPolicyYears is a comma-separated list of years, e.g. 2021,2022,2023."));
                }

                requestedYears = [.. parts.Select(y => int.Parse(y, System.Globalization.CultureInfo.InvariantCulture))];
            }

            if (form["lastDayWorked"].FirstOrDefault() is { Length: > 0 } ldw)
            {
                if (!DateOnly.TryParseExact(ldw, "yyyy-MM-dd", out var parsed))
                {
                    return Json(ApiResult.Error(400, "bad-last-day-worked", "lastDayWorked must be yyyy-MM-dd."));
                }

                lastDayWorked = parsed;
            }
        }
        else
        {
            // Blob reference: { files: [{ fileName, blobUrl }], quoteId, carrierHint }; only blobs in this app's storage account (read with its identity).
            var body = await JsonSerializer.DeserializeAsync<BlobUploadRequest>(req.Body, CanonicalJson.Options, req.HttpContext.RequestAborted);
            if (body?.Files is not { Count: > 0 })
            {
                return Json(ApiResult.Error(400, "no-files", "Send multipart/form-data with file fields, or JSON { files: [{ fileName, blobUrl }] }."));
            }

            var account = new Uri(config["Storage:BlobEndpoint"]!).Host;
            files = [];
            foreach (var f in body.Files)
            {
                // Brief 30 item 2: the uploads container and the caller's own prefix only (never ledger digests, extractions or another caller's files).
                var (path, refused) = await Api(client).ResolveBlobReferenceAsync(caller, f.BlobUrl, account, req.HttpContext.RequestAborted);
                if (refused is not null)
                {
                    return Json(refused);
                }

                var content = await runtime.Store.GetAsync(Containers.Uploads, path!, req.HttpContext.RequestAborted);
                if (content is null)
                {
                    return Json(ApiResult.Error(400, "blob-not-found", $"No blob at {Containers.Uploads}/{path}."));
                }

                files.Add(new UploadFile(Path.GetFileName(f.FileName), content.ToArray()));
            }

            (quoteId, carrierHint, valuationDate, policyPeriods) = (body.QuoteId, body.CarrierHint, body.ValuationDate, body.PolicyPeriods);
            (payroll, fte, lastDayWorked) = (body.PayrollByPeriod, body.FteByPeriod, body.LastDayWorked);
            (targetEffective, requestedYears) = (body.TargetPolicyEffectiveDate, body.RequestedPolicyYears);
        }

        var result = await Api(client).CreateUploadAsync(caller, files, quoteId, carrierHint, req.Headers["Idempotency-Key"].FirstOrDefault(), req.HttpContext.RequestAborted,
            valuationDate, policyPeriods, payroll, fte, lastDayWorked, workspaceId, targetEffective, requestedYears);
        if (result.Body is UploadAccepted accepted)
        {
            Activity.Current?.SetTag("uploadId", accepted.UploadId);
            req.HttpContext.Response.Headers.Location = accepted.StatusUrl;
        }

        return Json(result);
    }

    [Function("GetUpload")]
    public async Task<IActionResult> GetUpload([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "uploads/{uploadId}")] HttpRequest req,
        [DurableClient] DurableTaskClient client, string uploadId) =>
        await AuthAsync(req) is { } caller ? Json(await Api(client).GetUploadAsync(caller, uploadId, req.HttpContext.RequestAborted)) : Unauthorized();

    [Function("GetUploadStatus")]
    public async Task<IActionResult> GetUploadStatus([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "uploads/{uploadId}/status")] HttpRequest req,
        [DurableClient] DurableTaskClient client, string uploadId) =>
        await AuthAsync(req) is { } caller ? Json(await Api(client).GetStatusAsync(caller, uploadId, req.HttpContext.RequestAborted)) : Unauthorized();

    /// <summary>Server-Sent Events of status changes (500 ms poll of the orchestration custom statuses); closes at completion or after ~200 s (front-end limit 230 s; clients reconnect).</summary>
    [Function("UploadEvents")]
    public async Task<IActionResult> UploadEvents([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "uploads/{uploadId}/events")] HttpRequest req,
        [DurableClient] DurableTaskClient client, string uploadId)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        var api = Api(client);
        var response = req.HttpContext.Response;
        var cancel = req.HttpContext.RequestAborted;
        if (await api.ManifestFor(caller, uploadId, cancel) is null)
        {
            return Json(ApiResult.Error(404, "not-found", $"No upload '{uploadId}' visible to the caller."));
        }

        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        string? last = null;
        var stopwatch = Stopwatch.StartNew();
        var sequence = 0;
        while (!cancel.IsCancellationRequested && stopwatch.Elapsed < TimeSpan.FromSeconds(200))
        {
            var manifest = await api.ManifestFor(caller, uploadId, cancel);
            var view = await api.StatusViewAsync(manifest!, cancel);
            var json = JsonSerializer.Serialize(view, CanonicalJson.Options);
            if (json != last)
            {
                await response.WriteAsync($"id: {++sequence}\nevent: progress\ndata: {json}\n\n", cancel);
                await response.Body.FlushAsync(cancel);
                last = json;
            }

            if (view.RuntimeStatus is "Completed" or "Failed" or "Terminated" || manifest!.Status is UploadStatuses.Completed or UploadStatuses.AwaitingReview)
            {
                await response.WriteAsync($"event: end\ndata: {{\"status\":\"{manifest!.Status}\"}}\n\n", cancel);
                await response.Body.FlushAsync(cancel);
                break;
            }

            await Task.Delay(500, cancel);
        }

        return new EmptyResult();
    }

    [Function("GetDocument")]
    public async Task<IActionResult> GetDocument([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "documents/{documentId}")] HttpRequest req,
        [DurableClient] DurableTaskClient client, string documentId) =>
        await AuthAsync(req) is { } caller ? Json(await Api(client).GetDocumentAsync(caller, documentId, req.HttpContext.RequestAborted)) : Unauthorized();

    [Function("GetPageImage")]
    public async Task<IActionResult> GetPageImage([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "documents/{documentId}/source/pages/{page:int}")] HttpRequest req,
        [DurableClient] DurableTaskClient client, string documentId, int page) =>
        await AuthAsync(req) is { } caller ? Json(await Api(client).GetPageImageAsync(caller, documentId, page, req.HttpContext.RequestAborted)) : Unauthorized();

    [Function("GetPageLayout")]
    public async Task<IActionResult> GetPageLayout([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "documents/{documentId}/source/pages/{page:int}/layout")] HttpRequest req,
        [DurableClient] DurableTaskClient client, string documentId, int page) =>
        await AuthAsync(req) is { } caller ? Json(await Api(client).GetLayoutAsync(caller, documentId, page, req.HttpContext.RequestAborted)) : Unauthorized();

    [Function("GetQuotePayload")]
    public async Task<IActionResult> GetQuotePayload([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "documents/{documentId}/quote-payload")] HttpRequest req,
        [DurableClient] DurableTaskClient client, string documentId) =>
        await AuthAsync(req) is { } caller ? Json(await Api(client).GetQuotePayloadAsync(caller, documentId, req.HttpContext.RequestAborted)) : Unauthorized();

    // ---------------------------------------------------------------- Use Case 2 analytics (brief 10)

    [Function("GetQuoteFeatures")]
    public async Task<IActionResult> GetQuoteFeatures([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "quotes/{quoteId}/features")] HttpRequest req,
        string quoteId)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        var result = await new QuoteAnalytics(runtime).FeaturesAsync(caller, quoteId, req.Query["view"].FirstOrDefault(), req.HttpContext.RequestAborted);
        foreach (var (name, value) in result.Headers ?? new Dictionary<string, string>())
        {
            req.HttpContext.Response.Headers[name] = value;
        }

        return Json(result);
    }

    [Function("QuoteWhatIf")]
    public async Task<IActionResult> QuoteWhatIf([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "quotes/{quoteId}/features/what-if")] HttpRequest req,
        string quoteId)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        JsonElement body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, CanonicalJson.Options, req.HttpContext.RequestAborted);
        }
        catch (JsonException e)
        {
            return Json(ApiResult.Error(400, "bad-json", $"The what-if body is not JSON: {e.Message}")); // 12b: was a 500
        }

        return Json(await new QuoteAnalytics(runtime).WhatIfAsync(caller, quoteId, body, req.HttpContext.RequestAborted));
    }

    [Function("GetQuoteClaims")]
    public async Task<IActionResult> GetQuoteClaims([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "quotes/{quoteId}/claims")] HttpRequest req,
        string quoteId) =>
        await AuthAsync(req) is { } caller
            ? Json(await new QuoteAnalytics(runtime).ClaimsAsync(caller, quoteId, req.Query["view"].FirstOrDefault(), req.HttpContext.RequestAborted))
            : Unauthorized();

    [Function("GetQuotePayloadV2")]
    public async Task<IActionResult> GetQuotePayloadV2([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "quotes/{quoteId}/quote-payload")] HttpRequest req,
        string quoteId)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        // Brief 24 step 3: ?revision= (a command's pinned URL) and If-None-Match against ETag "q-<n>".
        int? revision = int.TryParse(req.Query["revision"].FirstOrDefault(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : null;
        var result = await new QuoteAnalytics(runtime).QuotePayloadAsync(caller, quoteId, req.HttpContext.RequestAborted, revision, req.Headers.IfNoneMatch.FirstOrDefault());
        foreach (var (name, value) in result.Headers ?? new Dictionary<string, string>())
        {
            req.HttpContext.Response.Headers[name] = value;
        }

        return result.Status == 304 ? new StatusCodeResult(304) : Json(result);
    }

    [Function("GetCatalog")]
    public async Task<IActionResult> GetCatalog([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "catalog")] HttpRequest req) =>
        await AuthAsync(req) is not null ? Json(QuoteAnalytics.CatalogJson()) : Unauthorized();

    [Function("ListReviews")]
    public async Task<IActionResult> ListReviews([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reviews")] HttpRequest req,
        [DurableClient] DurableTaskClient client) =>
        await AuthAsync(req) is { } caller
            ? Json(await Api(client).ListReviewsAsync(caller, req.Query["state"].FirstOrDefault() ?? "pending", req.HttpContext.RequestAborted))
            : Unauthorized();

    [Function("DecideReview")]
    public async Task<IActionResult> DecideReview([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "reviews/{documentId}/decision")] HttpRequest req,
        [DurableClient] DurableTaskClient client, string documentId)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        var body = await JsonSerializer.DeserializeAsync<ReviewDecisionRequest>(req.Body, CanonicalJson.Options, req.HttpContext.RequestAborted);
        return body is null
            ? Json(ApiResult.Error(400, "no-body", "Send { action, overrides?, promoteToFormat?, rationale? }."))
            : Json(await Api(client).DecideAsync(caller, documentId, body, req.HttpContext.RequestAborted));
    }

    [Function("ListGlossaryAliases")]
    public async Task<IActionResult> ListGlossaryAliases([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "glossary/aliases")] HttpRequest req,
        [DurableClient] DurableTaskClient client) =>
        await AuthAsync(req) is { } caller
            ? Json(await Api(client).ListAliasesAsync(caller, req.Query["formatId"].FirstOrDefault(), req.Query["status"].FirstOrDefault(), req.HttpContext.RequestAborted))
            : Unauthorized();

    [Function("ListFormats")]
    public async Task<IActionResult> ListFormats([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "formats")] HttpRequest req,
        [DurableClient] DurableTaskClient client) =>
        await AuthAsync(req) is { } caller ? Json(await Api(client).ListFormatsAsync(caller, req.HttpContext.RequestAborted)) : Unauthorized();

    [Function("GetFormat")]
    public async Task<IActionResult> GetFormat([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "formats/{formatId}")] HttpRequest req,
        [DurableClient] DurableTaskClient client, string formatId) =>
        await AuthAsync(req) is { } caller
            ? Json(await Api(client).GetFormatAsync(caller, formatId, int.TryParse(req.Query["version"].FirstOrDefault(), out var v) ? v : null, req.HttpContext.RequestAborted))
            : Unauthorized();

    /// <summary>Pre-bake-off reset of the learned format set (Admin; body { reason, dryRun }).</summary>
    [Function("ResetFormats")]
    public async Task<IActionResult> ResetFormats([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "formats/reset")] HttpRequest req,
        [DurableClient] DurableTaskClient client)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, CanonicalJson.Options, req.HttpContext.RequestAborted);
        var reason = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("reason", out var r) ? r.GetString() : null;
        var dryRun = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("dryRun", out var d) && d.ValueKind == JsonValueKind.True;
        return Json(await Api(client).ResetLearnedFormatsAsync(caller, reason, dryRun, req.HttpContext.RequestAborted));
    }

    [Function("RetireFormat")]
    public async Task<IActionResult> RetireFormat([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "formats/{formatId}/versions/{version:int}/retire")] HttpRequest req,
        [DurableClient] DurableTaskClient client, string formatId, int version)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, CanonicalJson.Options, req.HttpContext.RequestAborted);
        var reason = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("reason", out var r) ? r.GetString() : null;
        return Json(await Api(client).RetireFormatAsync(caller, formatId, version, reason, req.HttpContext.RequestAborted));
    }

    [Function("UnretireFormat")]
    public async Task<IActionResult> UnretireFormat([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "formats/{formatId}/versions/{version:int}/unretire")] HttpRequest req,
        [DurableClient] DurableTaskClient client, string formatId, int version)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, CanonicalJson.Options, req.HttpContext.RequestAborted);
        var reason = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("reason", out var r) ? r.GetString() : null;
        return Json(await Api(client).UnretireFormatAsync(caller, formatId, version, reason, req.HttpContext.RequestAborted));
    }

    // ---------------------------------------------------------------- UI workbench (brief 11)

    [Function("GetMe")]
    public async Task<IActionResult> GetMe([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "me")] HttpRequest req) =>
        await AuthAsync(req) is { } caller ? Json(WorkbenchApi.Me(caller)) : Unauthorized();

    [Function("ListQuotes")]
    public async Task<IActionResult> ListQuotes([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "quotes")] HttpRequest req) =>
        await AuthAsync(req) is { } caller
            ? Json(await Workbench.QuotesAsync(caller, req.HttpContext.RequestAborted, string.Equals(req.Query["includeRetired"], "true", StringComparison.OrdinalIgnoreCase)))
            : Unauthorized();

    [Function("PatchQuote")]
    public async Task<IActionResult> PatchQuote([HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "quotes/{quoteId}")] HttpRequest req, string quoteId)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        QuoteLabelRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<QuoteLabelRequest>(req.Body, CanonicalJson.Options, req.HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            body = null;
        }

        return body is null
            ? Json(ApiResult.Error(400, "bad-label", "Send { rationale, displayName?, retired? }."))
            : Json(await Workbench.PatchQuoteAsync(caller, quoteId, body, req.HttpContext.RequestAborted));
    }

    [Function("QuoteOwner")]
    public async Task<IActionResult> QuoteOwner([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "quotes/{quoteId}/owner")] HttpRequest req, string quoteId)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        QuoteOwnerRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<QuoteOwnerRequest>(req.Body, CanonicalJson.Options, req.HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            body = null;
        }

        return body is null
            ? Json(ApiResult.Error(400, "bad-owner", "Send { rationale, applicantUserId? }."))
            : Json(await Workbench.SetQuoteOwnerAsync(caller, quoteId, body, req.HttpContext.RequestAborted)); // brief 32 item 4
    }

    [Function("QuoteContext")]
    public async Task<IActionResult> QuoteContext([HttpTrigger(AuthorizationLevel.Anonymous, "get", "put", Route = "quotes/{quoteId}/context")] HttpRequest req, string quoteId)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        if (HttpMethods.IsGet(req.Method))
        {
            return Json(await Workbench.GetContextAsync(caller, quoteId, req.HttpContext.RequestAborted));
        }

        QuoteContext? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<QuoteContext>(req.Body, CanonicalJson.Options, req.HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            body = null;
        }

        return body is null
            ? Json(ApiResult.Error(400, "bad-context", "Send { payrollByPeriod?: [{ effectiveDate, amount }], fteByPeriod?: [...], lastDayWorked? }."))
            : Json(await Workbench.PutContextAsync(caller, quoteId, body with { QuoteId = quoteId }, req.HttpContext.RequestAborted));
    }

    [Function("GetDocumentAudit")]
    public async Task<IActionResult> GetDocumentAudit([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "documents/{documentId}/audit")] HttpRequest req, string documentId) =>
        await AuthAsync(req) is { } caller ? Json(await Workbench.DocumentAuditAsync(caller, documentId, req.HttpContext.RequestAborted)) : Unauthorized();

    [Function("GetQuoteAudit")]
    public async Task<IActionResult> GetQuoteAudit([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "quotes/{quoteId}/audit")] HttpRequest req, string quoteId) =>
        await AuthAsync(req) is { } caller ? Json(await Workbench.QuoteAuditAsync(caller, quoteId, req.HttpContext.RequestAborted)) : Unauthorized();

    [Function("GetCosts")]
    public async Task<IActionResult> GetCosts([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "costs")] HttpRequest req) =>
        await AuthAsync(req) is { } caller ? Json(await Workbench.CostsAsync(caller, req.HttpContext.RequestAborted)) : Unauthorized();

    [Function("GetQuoteMemo")]
    public async Task<IActionResult> GetQuoteMemo([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "quotes/{quoteId}/memo")] HttpRequest req, string quoteId) =>
        await AuthAsync(req) is { } caller
            ? Json(await Workbench.MemoAsync(caller, quoteId, req.Query["format"].FirstOrDefault(), req.HttpContext.RequestAborted))
            : Unauthorized();

    [Function("PushQuoteFlags")]
    public async Task<IActionResult> PushQuoteFlags([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "quotes/{quoteId}/flags")] HttpRequest req, string quoteId)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        var body = req.ContentLength is > 0 ? await JsonSerializer.DeserializeAsync<FlagPushRequest>(req.Body, CanonicalJson.Options, req.HttpContext.RequestAborted) : null;
        return Json(await Workbench.PushFlagsAsync(caller, quoteId, body ?? new FlagPushRequest(), req.HttpContext.RequestAborted));
    }

    [Function("QuoteFollowUps")]
    public async Task<IActionResult> QuoteFollowUps([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "quotes/{quoteId}/follow-ups")] HttpRequest req, string quoteId)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        if (HttpMethods.IsGet(req.Method))
        {
            return Json(await Workbench.FollowUpsAsync(caller, quoteId, req.HttpContext.RequestAborted));
        }

        var body = await JsonSerializer.DeserializeAsync<FollowUpRequest>(req.Body, CanonicalJson.Options, req.HttpContext.RequestAborted);
        return body is null
            ? Json(ApiResult.Error(400, "bad-follow-up", "Send { assignee, action, note?, claimIds?, due? }."))
            : Json(await Workbench.AssignFollowUpAsync(caller, quoteId, body, req.HttpContext.RequestAborted));
    }

    private WorkbenchApi Workbench => new(runtime);

    // ------------------------------------------------------------------ brief 12b

    [Function("ReopenReview")]
    public async Task<IActionResult> ReopenReview([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "reviews/{documentId}/reopen")] HttpRequest req, string documentId)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        var body = req.ContentLength is > 0 ? await JsonSerializer.DeserializeAsync<ReopenRequest>(req.Body, CanonicalJson.Options, req.HttpContext.RequestAborted) : null;
        return Json(await new DocumentCommands(runtime).ReopenAsync(caller, documentId, body, req.HttpContext.RequestAborted));
    }

    [Function("DocumentOverrides")]
    public async Task<IActionResult> DocumentOverrides([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "documents/{documentId}/overrides")] HttpRequest req,
        string documentId)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        OverrideRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<OverrideRequest>(req.Body, CanonicalJson.Options, req.HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            body = null;
        }

        return Json(await new DocumentCommands(runtime).OverrideAsync(caller, documentId, body, req.HttpContext.RequestAborted));
    }

    [Function("QuoteAnnotations")]
    public async Task<IActionResult> QuoteAnnotations([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "quotes/{quoteId}/annotations")] HttpRequest req, string quoteId)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        var body = await JsonSerializer.DeserializeAsync<AnnotationRequest>(req.Body, CanonicalJson.Options, req.HttpContext.RequestAborted);
        return body is null
            ? Json(ApiResult.Error(400, "bad-annotation", "Send { text, claimIds?, category? }."))
            : Json(await Workbench.AnnotateAsync(caller, quoteId, body, req.HttpContext.RequestAborted));
    }

    [Function("QuoteIntegration")]
    public async Task<IActionResult> QuoteIntegration([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "quotes/{quoteId}/integration")] HttpRequest req, string quoteId) =>
        await AuthAsync(req) is { } caller ? Json(await Workbench.IntegrationAsync(caller, quoteId, req.HttpContext.RequestAborted)) : Unauthorized();

    [Function("IntegrationOutbox")]
    public async Task<IActionResult> IntegrationOutbox([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "integration/outbox")] HttpRequest req) =>
        await AuthAsync(req) is { } caller ? Json(await Workbench.IntegrationAsync(caller, null, req.HttpContext.RequestAborted)) : Unauthorized();

    /// <summary>Test sink for the outbound webhook; authenticated by the HMAC signature instead of a bearer token.</summary>
    [Function("WebhookTestSink")]
    public async Task<IActionResult> WebhookTestSink([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "webhooks/test")] HttpRequest req,
        [DurableClient] DurableTaskClient client)
    {
        using var buffer = new MemoryStream();
        await req.Body.CopyToAsync(buffer, req.HttpContext.RequestAborted);
        return Json(await Api(client).ReceiveWebhookAsync(buffer.ToArray(), req.Headers[HmacWebhookSender.SignatureHeader].FirstOrDefault(), config["Webhook:Secret"],
            req.HttpContext.RequestAborted));
    }

    private Task<Caller?> AuthAsync(HttpRequest req) => authenticator.AuthenticateAsync(req.Headers.Authorization.FirstOrDefault(), req.HttpContext.RequestAborted);

    private static IActionResult Unauthorized() =>
        new JsonResult(new ApiError("unauthorized", "Send an Entra ID bearer token (or Basic credentials on the demo host)."), CanonicalJson.Options) { StatusCode = 401 };

    private static IActionResult Json(ApiResult result) => result.Bytes is { } bytes
        ? new FileContentResult(bytes, result.ContentType ?? "application/octet-stream")
        : new JsonResult(result.Body, CanonicalJson.Options) { StatusCode = result.Status };

    private sealed record BlobUploadRequest(List<BlobFile>? Files, string? QuoteId, string? CarrierHint, DateOnly? ValuationDate, List<ContextPeriod>? PolicyPeriods,
        List<PeriodExposure>? PayrollByPeriod = null, List<PeriodExposure>? FteByPeriod = null, DateOnly? LastDayWorked = null, DateOnly? TargetPolicyEffectiveDate = null,
        List<int>? RequestedPolicyYears = null);

    private sealed record BlobFile(string FileName, string BlobUrl);
}

/// <summary>DurableTaskClient adapter for the API.</summary>
public sealed class DurableOrchestrationClient(DurableTaskClient client) : IOrchestrationClient
{
    public async Task<bool> StartAsync(UploadInput input, CancellationToken cancellationToken = default)
    {
        if (await client.GetInstanceAsync(input.UploadId, false, cancellationToken) is not null)
        {
            return false;
        }

        try
        {
            await client.ScheduleNewOrchestrationInstanceAsync(OrchestratorNames.IngestUpload, input, new Microsoft.DurableTask.StartOrchestrationOptions(input.UploadId),
                cancellationToken);
            return true;
        }
        catch (Exception)
        {
            if (await client.GetInstanceAsync(input.UploadId, false, cancellationToken) is not null)
            {
                return false; // a concurrent identical POST started it first
            }

            throw;
        }
    }

    public async Task<(string RuntimeStatus, string? CustomStatus)?> GetAsync(string instanceId, CancellationToken cancellationToken = default) =>
        await client.GetInstanceAsync(instanceId, getInputsAndOutputs: true, cancellationToken) is { } instance
            ? (instance.RuntimeStatus.ToString(), instance.SerializedCustomStatus)
            : null;

    public Task RaiseAsync(string instanceId, string eventName, object payload, CancellationToken cancellationToken = default) =>
        client.RaiseEventAsync(instanceId, eventName, payload, cancellationToken);
}
