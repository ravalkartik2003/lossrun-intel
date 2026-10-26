using LossRun.Api.Contracts;
using LossRun.Data;

namespace LossRun.Orchestration;

/// <summary>
/// Brief 26: the "Needs investigation" queue over HTTP. Reading and working the queue is for UWManager and Admin (the
/// people who triage it with engineering); any reviewing role can flag a document for engineering.
/// </summary>
public sealed class EscalationApi(LossRunRuntime runtime)
{
    public const int MaxNote = 2000;

    private readonly EscalationService _service = new(runtime);

    private static bool CanTriage(Caller caller) => caller.Role is Roles.UWManager or Roles.Admin or Roles.Engineer;

    private static ApiResult Forbidden() => ApiResult.Error(403, "forbidden", "The investigation queue is for Engineer, UWManager and Admin roles.");

    /// <summary><c>GET /escalations?status=&amp;reason=&amp;carrier=</c>: most recently seen first; <c>status=open</c> means new or investigating.</summary>
    public async Task<ApiResult> ListAsync(Caller caller, string? status, string? reason, string? carrier, CancellationToken cancellationToken = default)
    {
        if (!CanTriage(caller))
        {
            return Forbidden();
        }

        var all = await runtime.RecordStore.ListEscalationsAsync(cancellationToken).ConfigureAwait(false);
        return ApiResult.Ok(all.Where(e => status is null or "" || (status == "open" ? EscalationStatuses.IsOpen(e.Status) : e.Status == status))
            .Where(e => reason is null or "" || e.ReasonCode == reason)
            .Where(e => carrier is null or "" || string.Equals(e.Carrier, carrier, StringComparison.OrdinalIgnoreCase))
            .ToList());
    }

    public async Task<ApiResult> GetAsync(Caller caller, string id, CancellationToken cancellationToken = default) =>
        !CanTriage(caller) ? Forbidden()
        : await runtime.RecordStore.GetEscalationAsync(id, cancellationToken).ConfigureAwait(false) is { } e ? ApiResult.Ok(e)
        : ApiResult.Error(404, "not-found", $"No escalation {id}.");

    /// <summary><c>PATCH /escalations/{id}</c> <c>{ status, note }</c>.</summary>
    public async Task<ApiResult> SetStatusAsync(Caller caller, string id, EscalationStatusRequest? body, CancellationToken cancellationToken = default)
    {
        if (!CanTriage(caller))
        {
            return Forbidden();
        }

        if (body is null || !EscalationStatuses.All.Contains(body.Status) || string.IsNullOrWhiteSpace(body.Note) || body.Note.Length > MaxNote)
        {
            return ApiResult.Error(400, "bad-status", $"Send {{ status: {string.Join(" | ", EscalationStatuses.All)}, note (1–{MaxNote} chars) }}.");
        }

        return await _service.SetStatusAsync(id, body.Status, body.Note.Trim(), new Actor(caller.UserId, caller.Role, "api"), cancellationToken).ConfigureAwait(false) is { } e
            ? ApiResult.Ok(e)
            : ApiResult.Error(404, "not-found", $"No escalation {id}.");
    }

    /// <summary><c>POST /escalations/{id}/tuning-items</c> <c>{ category: a–g, note }</c>: the case goes into the next tuning batch.</summary>
    public async Task<ApiResult> CreateTuningItemAsync(Caller caller, string id, TuningItemRequest? body, CancellationToken cancellationToken = default)
    {
        if (!CanTriage(caller))
        {
            return Forbidden();
        }

        if (body is null || !TuningItem.Categories.ContainsKey(body.Category) || string.IsNullOrWhiteSpace(body.Note) || body.Note.Length > MaxNote)
        {
            return ApiResult.Error(400, "bad-tuning-item",
                $"Send {{ category: {string.Join(" | ", TuningItem.Categories.Select(c => $"{c.Key} ({c.Value})"))}, note (1–{MaxNote} chars) }}.");
        }

        return await _service.CreateTuningItemAsync(id, body.Category, body.Note.Trim(), new Actor(caller.UserId, caller.Role, "api"), cancellationToken).ConfigureAwait(false) is { } r
            ? new ApiResult(201, new { escalation = r.Escalation, tuningItem = r.Item })
            : ApiResult.Error(404, "not-found", $"No escalation {id}.");
    }

    /// <summary><c>GET /tuning-items</c>: what the next <c>lossrun tune</c> batch reads.</summary>
    public async Task<ApiResult> TuningItemsAsync(Caller caller, CancellationToken cancellationToken = default) =>
        CanTriage(caller) ? ApiResult.Ok(await _service.TuningItemsAsync(cancellationToken).ConfigureAwait(false)) : Forbidden();

    /// <summary><c>POST /documents/{id}/flag</c> <c>{ note }</c>: "Flag for engineering" from the review screen.</summary>
    public async Task<ApiResult> FlagAsync(Caller caller, string documentId, FlagRequest? body, CancellationToken cancellationToken = default)
    {
        if (!caller.IsReader)
        {
            return ApiResult.Error(403, "forbidden", "Flagging is for underwriting and engineering roles.");
        }

        if (body is null || string.IsNullOrWhiteSpace(body.Note) || body.Note.Length > MaxNote)
        {
            return ApiResult.Error(400, "bad-flag", $"Send {{ note (1–{MaxNote} chars) }}: what looks wrong, for engineering.");
        }

        return await _service.FlagAsync(documentId, body.Note.Trim(), new Actor(caller.UserId, caller.Role, "api"), cancellationToken).ConfigureAwait(false) is { } e
            ? new ApiResult(201, e)
            : ApiResult.Error(404, "not-found", $"No document {documentId}.");
    }
}
