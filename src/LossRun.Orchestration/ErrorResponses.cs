using System.Text.Json;
using LossRun.Api.Contracts;

namespace LossRun.Orchestration;

/// <summary>
/// Brief 30 items 27–28: what the API answers for an exception no endpoint handled. A body that is not JSON for the endpoint is the
/// caller's error (400 <c>bad-json</c>); anything else is a generic 500 that names a correlation id and never the exception text (which
/// can carry paths, SQL or document values). The detail goes to the log under the same id.
/// </summary>
public static class ErrorResponses
{
    /// <summary>Rationales and reasons are stored in 2,000-character columns; longer is refused, never clipped silently.</summary>
    public const int MaxRationale = 2000;

    public static ApiResult For(Exception exception, string correlationId)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is JsonException or NotSupportedException { Source: "System.Text.Json" })
            {
                return ApiResult.Error(400, "bad-json", $"The request body is not valid JSON for this endpoint (correlation id {correlationId}).");
            }
        }

        return new ApiResult(500, new ApiError("internal", $"An unexpected error occurred. Quote correlation id {correlationId} when reporting it."));
    }

    /// <summary>The first rationale-like text over <see cref="MaxRationale"/>, as a 400; null when all fit.</summary>
    public static ApiResult? TooLong(params (string Name, string? Text)[] texts) =>
        texts.FirstOrDefault(t => t.Text is { Length: > MaxRationale }) is { Name: { } name } t
            ? ApiResult.Error(400, "too-long", $"{name} is {t.Text!.Length} characters; at most {MaxRationale}.")
            : null;
}
