using LossRun.Contracts;
using LossRun.Orchestration;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace LossRun.Functions;

/// <summary>Brief 30 items 27–28: an HTTP function's unhandled exception is logged with the invocation id and answered by <see cref="ErrorResponses"/>.</summary>
public sealed class ErrorMiddleware(ILogger<ErrorMiddleware> logger) : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception e) when (e is not OperationCanceledException && context.GetHttpContext() is { Response.HasStarted: false } http)
        {
            var result = ErrorResponses.For(e, context.InvocationId);
            logger.LogError(e, "{Function} failed (correlation id {CorrelationId}, answered {Status})", context.FunctionDefinition.Name, context.InvocationId, result.Status);
            http.Response.StatusCode = result.Status;
            http.Response.ContentType = "application/json; charset=utf-8";
            await http.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(result.Body, CanonicalJson.Options));
        }
    }
}
