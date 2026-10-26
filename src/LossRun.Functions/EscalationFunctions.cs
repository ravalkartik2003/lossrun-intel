using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;
using LossRun.Orchestration;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LossRun.Functions;

/// <summary>
/// Brief 26 (ADR 0025): the investigation queue's routes, the hourly pattern sweep, and the Application Insights side of
/// an escalation (the <c>lossrun.escalation</c> event the first-occurrence alert reads, the <c>lossrun.escalations.open</c>
/// metric the daily digest reads).
/// </summary>
public sealed class EscalationFunctions(LossRunRuntime runtime, IAuthenticator authenticator, ILogger<EscalationFunctions> logger)
{
    private EscalationApi Api => new(runtime);

    [Function("ListEscalations")]
    public async Task<IActionResult> ListEscalations([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "escalations")] HttpRequest req) =>
        await AuthAsync(req) is { } caller
            ? Json(await Api.ListAsync(caller, req.Query["status"].FirstOrDefault(), req.Query["reason"].FirstOrDefault(), req.Query["carrier"].FirstOrDefault(),
                req.HttpContext.RequestAborted))
            : Unauthorized();

    [Function("Escalation")]
    public async Task<IActionResult> Escalation([HttpTrigger(AuthorizationLevel.Anonymous, "get", "patch", Route = "escalations/{id}")] HttpRequest req, string id)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        return HttpMethods.IsGet(req.Method)
            ? Json(await Api.GetAsync(caller, id, req.HttpContext.RequestAborted))
            : Json(await Api.SetStatusAsync(caller, id, await BodyAsync<EscalationStatusRequest>(req), req.HttpContext.RequestAborted));
    }

    [Function("EscalationTuningItem")]
    public async Task<IActionResult> EscalationTuningItem([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "escalations/{id}/tuning-items")] HttpRequest req, string id) =>
        await AuthAsync(req) is { } caller
            ? Json(await Api.CreateTuningItemAsync(caller, id, await BodyAsync<TuningItemRequest>(req), req.HttpContext.RequestAborted))
            : Unauthorized();

    [Function("ListTuningItems")]
    public async Task<IActionResult> ListTuningItems([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "tuning-items")] HttpRequest req) =>
        await AuthAsync(req) is { } caller ? Json(await Api.TuningItemsAsync(caller, req.HttpContext.RequestAborted)) : Unauthorized();

    [Function("FlagDocument")]
    public async Task<IActionResult> FlagDocument([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "documents/{documentId}/flag")] HttpRequest req, string documentId) =>
        await AuthAsync(req) is { } caller
            ? Json(await Api.FlagAsync(caller, documentId, await BodyAsync<FlagRequest>(req), req.HttpContext.RequestAborted))
            : Unauthorized();

    /// <summary>Hourly (minute 17): the pattern detector over the last 37 days, then the open-case gauge.</summary>
    [Function("EscalationSweep")]
    public async Task EscalationSweep([TimerTrigger("0 17 * * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        var sweep = await new EscalationService(runtime).SweepAsync(cancellationToken);
        logger.LogInformation("escalation sweep: {Documents} documents, {Signals} patterns, {Changed} changed, {Open} open", sweep.Documents, sweep.Signals, sweep.Changed, sweep.Open);
    }

    private static async Task<T?> BodyAsync<T>(HttpRequest req) where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(req.Body, CanonicalJson.Options, req.HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task<Caller?> AuthAsync(HttpRequest req) => authenticator.AuthenticateAsync(req.Headers.Authorization.FirstOrDefault(), req.HttpContext.RequestAborted);

    private static IActionResult Json(ApiResult result) => new JsonResult(result.Body, CanonicalJson.Options) { StatusCode = result.Status };

    private static IActionResult Unauthorized() =>
        new JsonResult(new ApiError("unauthorized", "Send an Entra ID bearer token (or Basic credentials on the demo host)."), CanonicalJson.Options) { StatusCode = 401 };
}

/// <summary>Application Insights: a custom event per change (identifiers only) and the open-case metric.</summary>
public sealed class AppInsightsEscalationTelemetry(TelemetryClient telemetry) : IEscalationTelemetry
{
    public void Changed(Escalation escalation, string change) =>
        telemetry.TrackEvent("lossrun.escalation", new Dictionary<string, string>
        {
            ["escalationId"] = escalation.Id, ["reasonCode"] = escalation.ReasonCode, ["formatFingerprint"] = escalation.FormatFingerprint, ["status"] = escalation.Status,
            ["change"] = change, ["count"] = escalation.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

    public void Open(int count) => telemetry.GetMetric("lossrun.escalations.open").TrackValue(count);
}
