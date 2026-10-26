using System.Text.Json;
using LossRun.Analytics;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;

namespace LossRun.Orchestration;

/// <summary>
/// The reads and actions the UI workbench needs (brief 11) beyond briefs 07–10: the caller, the quote list, the quote's
/// submission context, audit trails, the cost summary, the pricing-memo export, and the quote actions (push flags,
/// assign a follow-up, annotate). Quote-level actions are audit events (<see cref="QuoteAudit.FieldPath"/>) written with
/// their outbox row in one transaction, then delivered to the quote system over Service Bus (brief 12b).
/// </summary>
public sealed class WorkbenchApi(LossRunRuntime runtime)
{
    public const int AuditMax = 500;

    private IRecordStore Records => runtime.RecordStore;

    private static ApiResult Forbidden() => ApiResult.Error(403, "forbidden", "This is for underwriting roles.");

    /// <summary><c>GET /me</c>.</summary>
    public static ApiResult Me(Caller caller) => ApiResult.Ok(new MeView(caller.UserId, caller.Role, caller.IsReviewer, caller.IsReader));

    /// <summary><c>GET /quotes[?includeRetired=true]</c>.</summary>
    public async Task<ApiResult> QuotesAsync(Caller caller, CancellationToken cancellationToken = default, bool includeRetired = false) =>
        caller.IsReader ? ApiResult.Ok(await Records.ListQuotesAsync(cancellationToken, includeRetired).ConfigureAwait(false)) : Forbidden();

    /// <summary>
    /// <c>PATCH /quotes/{quoteId}</c> (brief 13 demo hygiene): sets the display name and/or retires or restores the quote.
    /// UWManager and Admin only; the rationale is required and every change is a ledger audit row. Nothing is deleted or
    /// re-keyed: a retired quote's documents, claims and audit trail stay readable by id.
    /// </summary>
    /// <summary>
    /// Brief 32 item 4: <c>POST /quotes/{quoteId}/owner</c>. UWManager and Admin transfer a quote to an Applicant, or clear its owner (underwriter roles only),
    /// with a rationale; audited as <c>quote.owner</c> (old → new).
    /// </summary>
    public async Task<ApiResult> SetQuoteOwnerAsync(Caller caller, string quoteId, QuoteOwnerRequest request, CancellationToken cancellationToken = default)
    {
        if (caller.Role is not (Roles.UWManager or Roles.Admin))
        {
            return ApiResult.Error(403, "forbidden", "Transferring or clearing a quote's owner is for UWManager and Admin roles.");
        }

        if (string.IsNullOrWhiteSpace(request.Rationale) || request.ApplicantUserId is { Length: 0 or > 256 })
        {
            return ApiResult.Error(400, "bad-owner", "Send { rationale, applicantUserId? (null clears the owner: underwriter roles only) }.");
        }

        var owner = new QuoteOwner(quoteId, request.ApplicantUserId?.Trim(), runtime.Clock.GetUtcNow(), caller.UserId);
        await Records.SetQuoteOwnerAsync(owner, request.Rationale.Trim(), new Actor(caller.UserId, caller.Role, "api"), cancellationToken).ConfigureAwait(false);
        return ApiResult.Ok(await Records.GetQuoteOwnerAsync(quoteId, cancellationToken).ConfigureAwait(false) ?? owner);
    }

    public async Task<ApiResult> PatchQuoteAsync(Caller caller, string quoteId, QuoteLabelRequest request, CancellationToken cancellationToken = default)
    {
        if (caller.Role is not (Roles.UWManager or Roles.Admin))
        {
            return ApiResult.Error(403, "forbidden", "Renaming or retiring a quote is for UWManager and Admin roles.");
        }

        if (string.IsNullOrWhiteSpace(request.Rationale) || request.DisplayName is { Length: > 256 })
        {
            return ApiResult.Error(400, "bad-label", "Send { rationale, displayName? (≤ 256 chars, empty clears it), retired? }.");
        }

        // Brief 23 follow-up: a quote whose only uploads failed has no documents but can still be retired (audited).
        if ((await Records.DocumentsOfQuoteAsync(quoteId, cancellationToken).ConfigureAwait(false)).Count == 0
            && (await Records.UploadsOfQuoteAsync(quoteId, cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            return ApiResult.Error(404, "not-found", $"Quote {quoteId} has no documents or uploads.");
        }

        var now = runtime.Clock.GetUtcNow();
        var old = await Records.GetQuoteLabelAsync(quoteId, cancellationToken).ConfigureAwait(false);
        var name = request.DisplayName is null ? old?.DisplayName : request.DisplayName.Trim() is { Length: > 0 } trimmed ? trimmed : null;
        var (retiredAt, retiredBy) = request.Retired switch
        {
            true => (old?.RetiredAt ?? now, old?.RetiredBy ?? caller.UserId),
            false => ((DateTimeOffset?)null, (string?)null),
            null => (old?.RetiredAt, old?.RetiredBy),
        };
        var label = new QuoteLabel(quoteId, name, retiredAt, retiredBy, now, caller.UserId);
        await Records.SaveQuoteLabelAsync(label, request.Rationale.Trim(), new Actor(caller.UserId, caller.Role, "api"), cancellationToken).ConfigureAwait(false);
        return ApiResult.Ok(await Records.GetQuoteLabelAsync(quoteId, cancellationToken).ConfigureAwait(false) ?? label);
    }

    // ------------------------------------------------------------------ context (report 10 answer 1)

    /// <summary><c>GET /quotes/{quoteId}/context</c>: the stored context, or an empty one.</summary>
    public async Task<ApiResult> GetContextAsync(Caller caller, string quoteId, CancellationToken cancellationToken = default) =>
        caller.IsReader
            ? ApiResult.Ok(await Records.GetQuoteContextAsync(quoteId, cancellationToken).ConfigureAwait(false) ?? new QuoteContext(quoteId))
            : Forbidden();

    /// <summary><c>PUT /quotes/{quoteId}/context</c>: replaces the context (audited). Feature snapshots re-key on it.</summary>
    public async Task<ApiResult> PutContextAsync(Caller caller, string quoteId, QuoteContext body, CancellationToken cancellationToken = default)
    {
        if (!caller.IsReviewer)
        {
            return Forbidden();
        }

        if ((Invalid(body.PayrollByPeriod) ?? Invalid(body.FteByPeriod)
                ?? (body.RequestedPolicyYears?.Any(y => y is < 1900 or > 2200) == true ? "requestedPolicyYears are four-digit policy years" : null)) is { } error)
        {
            return ApiResult.Error(400, "bad-context", error);
        }

        var context = body with { QuoteId = quoteId, UpdatedAt = runtime.Clock.GetUtcNow(), UpdatedBy = caller.UserId };
        await Records.SaveQuoteContextAsync(context, new Actor(caller.UserId, caller.Role, "api"), cancellationToken).ConfigureAwait(false);
        return ApiResult.Ok(await Records.GetQuoteContextAsync(quoteId, cancellationToken).ConfigureAwait(false) ?? context);
    }

    private static string? Invalid(IReadOnlyList<PeriodExposure>? periods) =>
        periods?.FirstOrDefault(p => p.Amount < 0) is { } p ? $"exposure for {p.EffectiveDate:yyyy-MM-dd} is negative" : null;

    /// <summary>
    /// Merges exposures stated on an upload into the quote context (one entry per effective date; the upload's value wins),
    /// so a submission that carries payroll makes frequency measurable without a separate call.
    /// </summary>
    public static async Task MergeSubmissionAsync(IRecordStore records, string quoteId, SubmissionInfo submission, Actor actor, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        if (submission.PayrollByPeriod is not { Count: > 0 } && submission.FteByPeriod is not { Count: > 0 } && submission.LastDayWorked is null
            && submission.TargetPolicyEffectiveDate is null && submission.RequestedPolicyYears is not { Count: > 0 })
        {
            return;
        }

        static IReadOnlyList<PeriodExposure>? Merge(IReadOnlyList<PeriodExposure>? old, IReadOnlyList<PeriodExposure>? add) =>
            add is not { Count: > 0 } ? old
                : [.. (old ?? []).Where(o => add.All(a => a.EffectiveDate != o.EffectiveDate)).Concat(add).OrderBy(p => p.EffectiveDate)];
        var current = await records.GetQuoteContextAsync(quoteId, cancellationToken).ConfigureAwait(false) ?? new QuoteContext(quoteId);
        await records.SaveQuoteContextAsync(current with
        {
            PayrollByPeriod = Merge(current.PayrollByPeriod, submission.PayrollByPeriod),
            FteByPeriod = Merge(current.FteByPeriod, submission.FteByPeriod),
            LastDayWorked = submission.LastDayWorked ?? current.LastDayWorked,
            TargetPolicyEffectiveDate = submission.TargetPolicyEffectiveDate ?? current.TargetPolicyEffectiveDate,
            RequestedPolicyYears = submission.RequestedPolicyYears is { Count: > 0 } years ? years : current.RequestedPolicyYears,
            UpdatedAt = at, UpdatedBy = actor.UserId,
        }, actor, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ audit

    /// <summary><c>GET /documents/{documentId}/audit</c>.</summary>
    public async Task<ApiResult> DocumentAuditAsync(Caller caller, string documentId, CancellationToken cancellationToken = default) =>
        caller.IsReader ? ApiResult.Ok(await Records.AuditTrailAsync(documentId, null, AuditMax, cancellationToken).ConfigureAwait(false)) : Forbidden();

    /// <summary><c>GET /quotes/{quoteId}/audit</c>: the quote's documents' events plus quote-level events.</summary>
    public async Task<ApiResult> QuoteAuditAsync(Caller caller, string quoteId, CancellationToken cancellationToken = default) =>
        caller.IsReader ? ApiResult.Ok(await Records.AuditTrailAsync(null, quoteId, AuditMax, cancellationToken).ConfigureAwait(false)) : Forbidden();

    // ------------------------------------------------------------------ costs

    /// <summary>Where `lossrun eval reconcile --publish` writes the latest reconciliation (extractions container).</summary>
    public const string CostReconciliationPath = "reconciliation/latest.json";

    /// <summary><c>GET /costs</c>: POC-to-date metered cost by path and carrier, and the latest reconciliation to the Azure bill.</summary>
    public async Task<ApiResult> CostsAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsReader)
        {
            return Forbidden();
        }

        var lines = await Records.DocumentCostsAsync(cancellationToken).ConfigureAwait(false);
        // Brief 13: the latest reconciliation to the Azure bill (`lossrun eval reconcile --publish`), when one has been published.
        var reconciliation = await runtime.Store.GetJsonAsync<CostReconciliation>(Containers.Extractions, CostReconciliationPath, cancellationToken).ConfigureAwait(false);
        return ApiResult.Ok(Summarize(lines) with { Reconciliation = reconciliation });
    }

    public static CostSummary Summarize(IReadOnlyList<DocumentCostLine> lines)
    {
        lines = [.. lines.Select(l => l with { Path = CostPath(l.Path) })];
        var total = lines.Sum(l => l.CostUsd);
        var byPath = lines.GroupBy(l => l.Path).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CostByPath(g.Key, g.Count(), g.Sum(l => l.CostUsd), Math.Round(g.Average(l => l.CostUsd), 6), g.Sum(l => l.ModelCalls))).ToList();

        // Learn once vs replay (cost-tracking.md §3): a learn costs its document; each replay saves (learn − replay mean).
        var byCarrier = new List<CostByCarrier>();
        foreach (var g in lines.Where(l => l.Carrier is not null).GroupBy(l => l.Carrier!).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var learns = g.Where(l => l.Path is "learn" or "delta").ToList();
            var replays = g.Where(l => l.Path == "replay").ToList();
            var learn = learns.Count == 0 ? 0m : Math.Round(learns.Average(l => l.CostUsd), 6);
            var replayMean = replays.Count == 0 ? 0m : Math.Round(replays.Average(l => l.CostUsd), 6);
            var saving = learn - replayMean;
            int? breakEven = learns.Count == 0 ? null : saving <= 0 ? null : 1 + (int)Math.Ceiling(learn / saving);
            byCarrier.Add(new CostByCarrier(g.Key, learn, replays.Count, replayMean, Math.Round(Math.Max(saving, 0) * replays.Count, 6), breakEven));
        }

        return new CostSummary(total, lines.Count, lines.Count == 0 ? 0 : Math.Round(total / lines.Count, 6),
            lines.Count == 0 ? 0 : Math.Round((double)lines.Count(l => l.ModelCalls == 0) / lines.Count, 4), byPath, byCarrier,
            [.. lines.OrderByDescending(l => l.ComputedAt).Take(500)], null);
    }

    /// <summary>cost-tracking.md path names from the routing tier dbo.DocumentCost stores ("ReplayAccepted" → replay).</summary>
    public static string CostPath(string tier) => tier.ToLowerInvariant() switch
    {
        "replayaccepted" or "replay" => "replay",
        "learned" or "learn" => "learn",
        var other => other,
    };

    // ------------------------------------------------------------------ quote actions

    /// <summary><c>GET /quotes/{quoteId}/memo?format=docx|json</c>: the pricing memo (Word; PDF via the pipeline step, docs/analytics/memo-pdf.md).</summary>
    public async Task<ApiResult> MemoAsync(Caller caller, string quoteId, string? format, CancellationToken cancellationToken = default)
    {
        if (!caller.IsReviewer)
        {
            return Forbidden(); // the export also pushes the memo's link to the quote record (a write)
        }

        if (format is not (null or "" or "docx" or "json"))
        {
            return ApiResult.Error(400, "bad-format", "format is 'docx' or 'json'; PDF is produced by the export pipeline (LibreOffice headless).");
        }

        var analytics = new QuoteAnalytics(runtime);
        var (account, error) = await analytics.LoadAsync(caller, quoteId, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        // Re-parsed so every number is a JSON number node (the built payload holds CLR doubles the memo reads as decimals).
        var payload = System.Text.Json.Nodes.JsonNode.Parse((await analytics.PayloadAsync(account!, cancellationToken).ConfigureAwait(false)).ToJsonString())!.AsObject();
        var path = Path.Combine(Path.GetTempPath(), $"memo-{Guid.NewGuid():N}.docx");
        try
        {
            var memo = Memo.Write(payload, path);
            // Brief 12b: the export also pushes the memo's link to the quote record (an annotation of category pricing-memo).
            var at = runtime.Clock.GetUtcNow();
            var link = OutboxMessage.Command(OutboxKinds.MemoLink, quoteId, new
            {
                quoteId, category = "pricing-memo", text = $"Pricing memo exported by {caller.UserId} (catalog {Catalog.Embedded.Version})",
                url = $"/api/v1/quotes/{Uri.EscapeDataString(quoteId)}/memo?format=docx", format = format ?? "docx", generatedAt = at,
            }, caller.UserId, at, $"quotes/{quoteId}/memo");
            await runtime.SendAsync(new AuditEvent(at, caller.UserId, caller.Role, "quote.memo.exported", "", null, QuoteAudit.FieldPath(quoteId),
                Rationale: $"pricing memo ({format ?? "docx"}), catalog {Catalog.Embedded.Version}"), new Actor(caller.UserId, caller.Role, "api"), [link], cancellationToken)
                .ConfigureAwait(false);
            return format == "json"
                ? new ApiResult(200, null, System.Text.Encoding.UTF8.GetBytes(memo.ToJsonString()), "application/json")
                : new ApiResult(200, null, await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary><c>POST /quotes/{quoteId}/flags</c>: records the flag push (counts per flag) as an audit event.</summary>
    public async Task<ApiResult> PushFlagsAsync(Caller caller, string quoteId, FlagPushRequest body, CancellationToken cancellationToken = default)
    {
        if (!caller.IsReviewer)
        {
            return Forbidden();
        }

        var analytics = new QuoteAnalytics(runtime);
        var (account, error) = await analytics.LoadAsync(caller, quoteId, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var (json, _) = await analytics.BaselineAsync(account!, AccountBuilder.AsAdjusted, cancellationToken).ConfigureAwait(false);
        var wanted = body.Flags is { Count: > 0 } f ? f.ToHashSet(StringComparer.Ordinal) : null;
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var perClaim = new List<object>();
        foreach (var claim in JsonDocument.Parse(json).RootElement.GetProperty("perClaimFlags").EnumerateArray())
        {
            var flags = claim.GetProperty("flags").EnumerateArray().Select(x => x.GetString()!).Where(x => wanted is null || wanted.Contains(x)).ToList();
            if (flags.Count > 0)
            {
                perClaim.Add(new { claimId = claim.GetProperty("claimId").GetString(), flags });
            }

            foreach (var x in flags)
            {
                counts[x] = counts.GetValueOrDefault(x) + 1;
            }
        }

        var at = runtime.Clock.GetUtcNow();
        var command = OutboxMessage.Command(OutboxKinds.Flags, quoteId, new { quoteId, flags = perClaim, counts, catalogVersion = Catalog.Embedded.Version, rationale = body.Rationale },
            caller.UserId, at, $"quotes/{quoteId}/flags");
        var result = new FlagPushResult(quoteId, perClaim.Count, counts, $"queued (outbox {command.Id})", at);
        await runtime.SendAsync(new AuditEvent(at, caller.UserId, caller.Role, "quote.flags.pushed", "", null, QuoteAudit.FieldPath(quoteId), null,
            JsonSerializer.SerializeToElement(result, CanonicalJson.Options), body.Rationale), new Actor(caller.UserId, caller.Role, "api"), [command], cancellationToken)
            .ConfigureAwait(false);
        return ApiResult.Ok(result);
    }

    /// <summary><c>POST /quotes/{quoteId}/follow-ups</c>: assigns a follow-up (audited; listed by <c>GET</c>).</summary>
    public async Task<ApiResult> AssignFollowUpAsync(Caller caller, string quoteId, FollowUpRequest body, CancellationToken cancellationToken = default)
    {
        if (!caller.IsReviewer)
        {
            return Forbidden();
        }

        if (string.IsNullOrWhiteSpace(body.Assignee) || string.IsNullOrWhiteSpace(body.Action))
        {
            return ApiResult.Error(400, "bad-follow-up", "assignee and action are required.");
        }

        var at = runtime.Clock.GetUtcNow();
        var followUp = new FollowUp($"fu-{Guid.NewGuid():N}"[..15], quoteId, body.Assignee.Trim(), body.Action.Trim(), body.Note, body.ClaimIds ?? [], body.Due, caller.UserId, at);
        await runtime.SendAsync(new AuditEvent(at, caller.UserId, caller.Role, "quote.followup", "", null, QuoteAudit.FieldPath(quoteId), null,
            JsonSerializer.SerializeToElement(followUp, CanonicalJson.Options), $"{followUp.Action} → {followUp.Assignee}"), new Actor(caller.UserId, caller.Role, "api"),
            [OutboxMessage.Command(OutboxKinds.FollowUp, quoteId, followUp, caller.UserId, at, $"quotes/{quoteId}/followups/{followUp.Id}")], cancellationToken)
            .ConfigureAwait(false);
        return new ApiResult(201, followUp);
    }

    /// <summary><c>POST /quotes/{quoteId}/annotations</c> (brief 12b): a note pushed to the quote record through the outbox.</summary>
    public async Task<ApiResult> AnnotateAsync(Caller caller, string quoteId, AnnotationRequest body, CancellationToken cancellationToken = default)
    {
        if (!caller.IsReviewer)
        {
            return Forbidden();
        }

        if (string.IsNullOrWhiteSpace(body.Text) || body.Text.Length > 4000)
        {
            return ApiResult.Error(400, "bad-annotation", "text is required (≤ 4,000 characters).");
        }

        var at = runtime.Clock.GetUtcNow();
        var command = OutboxMessage.Command(OutboxKinds.Annotation, quoteId, new { quoteId, text = body.Text.Trim(), claimIds = body.ClaimIds ?? [], category = body.Category ?? "underwriting-note" },
            caller.UserId, at);
        var annotation = new Annotation($"an-{Guid.NewGuid():N}"[..15], quoteId, body.Text.Trim(), body.ClaimIds ?? [], body.Category, caller.UserId, at, command.Id);
        await runtime.SendAsync(new AuditEvent(at, caller.UserId, caller.Role, "quote.annotation", "", null, QuoteAudit.FieldPath(quoteId), null,
            JsonSerializer.SerializeToElement(annotation, CanonicalJson.Options), body.Category), new Actor(caller.UserId, caller.Role, "api"), [command with { Subject = $"quotes/{quoteId}/annotations/{annotation.Id}" }],
            cancellationToken).ConfigureAwait(false);
        return new ApiResult(201, annotation);
    }

    /// <summary><c>GET /quotes/{quoteId}/integration</c>: what was sent to the quote system for this quote and what it answered.</summary>
    public async Task<ApiResult> IntegrationAsync(Caller caller, string? quoteId, CancellationToken cancellationToken = default) =>
        caller.IsReader || caller.Role == Roles.QuoteSystem
            ? ApiResult.Ok((await Records.OutboxAsync(quoteId, AuditMax, cancellationToken).ConfigureAwait(false)).Select(o => o.ToEntry()).ToList())
            : Forbidden();

    /// <summary><c>GET /quotes/{quoteId}/follow-ups</c>, newest first.</summary>
    public async Task<ApiResult> FollowUpsAsync(Caller caller, string quoteId, CancellationToken cancellationToken = default)
    {
        if (!caller.IsReader)
        {
            return Forbidden();
        }

        var trail = await Records.AuditTrailAsync(null, quoteId, AuditMax, cancellationToken).ConfigureAwait(false);
        return ApiResult.Ok(trail.Entries.Where(e => e.Action == "quote.followup" && e.NewValue is not null)
            .Select(e => JsonSerializer.Deserialize<FollowUp>(e.NewValue!, CanonicalJson.Options)!).ToList());
    }
}
