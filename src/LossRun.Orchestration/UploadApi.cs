using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;
using LossRun.Formats;
using LossRun.Rules;

namespace LossRun.Orchestration;

/// <summary>What the API needs from Durable Functions (adapted from DurableTaskClient in LossRun.Functions; faked in tests).</summary>
public interface IOrchestrationClient
{
    /// <summary>Starts the upload orchestration with the upload id as instance id; false when it already exists.</summary>
    Task<bool> StartAsync(UploadInput input, CancellationToken cancellationToken = default);

    /// <summary>(runtime status, custom status JSON) or null when the instance does not exist.</summary>
    Task<(string RuntimeStatus, string? CustomStatus)?> GetAsync(string instanceId, CancellationToken cancellationToken = default);

    Task RaiseAsync(string instanceId, string eventName, object payload, CancellationToken cancellationToken = default);
}

/// <summary>An API outcome the HTTP layer turns into a response (JSON body, or bytes with a content type).</summary>
/// <param name="Headers">Extra response headers (e.g. <c>X-Feature-Snapshot: hit|miss</c>, which the load test splits on).</param>
public sealed record ApiResult(int Status, object? Body = null, byte[]? Bytes = null, string? ContentType = null, IReadOnlyDictionary<string, string>? Headers = null)
{
    public static ApiResult Ok(object body) => new(200, body);
    public static ApiResult Error(int status, string code, string message) => new(status, new ApiError(code, message));
}

public sealed record UploadFile(string FileName, byte[] Bytes);

/// <param name="ApplicantMayCreateQuotes">
/// Brief 32 Answers 4 (<c>Quotes__ApplicantMayCreate</c>): may an Applicant's upload create a quote nothing names yet? True in the POC (a demo
/// convenience). False in an organisation environment, where the quote is the quote-management system's key and exists before any upload; there
/// an Applicant upload to an unknown quote is a 404, and only underwriter roles create one.
/// </param>
/// <param name="Retry">Brief 33: config/retry-policy.json, handed to every upload orchestration in its input; null uses the embedded copy.</param>
public sealed record ApiOptions(string BaseUrl = "/api/v1", TimeSpan? ReviewTimeout = null, bool ApplicantMayCreateQuotes = true, RetryConfig? Retry = null)
{
    public TimeSpan ReviewTimeoutOrDefault => ReviewTimeout ?? TimeSpan.FromDays(7);
}

/// <summary>The HTTP API v1 behaviour (brief 07 §2), independent of the Functions host.</summary>
public sealed class UploadApi(LossRunRuntime runtime, IOrchestrationClient orchestrations, ApiOptions? options = null)
{
    private readonly ApiOptions _options = options ?? new ApiOptions();

    /// <summary>Custom status as the Durable data converter wrote it (property casing is the converter's, so match case-insensitively).</summary>
    private static readonly JsonSerializerOptions StatusJson = new(CanonicalJson.Options) { PropertyNameCaseInsensitive = true };
    private IObjectStore Store => runtime.Store;

    private IRecordStore Records => runtime.RecordStore;

    // ------------------------------------------------------------------ uploads

    /// <summary>
    /// Stores the files, writes the manifest and starts the orchestration. Idempotent per caller: the upload id derives from
    /// the caller and the <c>Idempotency-Key</c> header when given, else from the caller and the content (file names +
    /// SHA-256), so a re-POST returns the existing upload (<c>existing: true</c>) and two callers never share one.
    /// </summary>
    public async Task<ApiResult> CreateUploadAsync(Caller caller, IReadOnlyList<UploadFile> files, string? quoteId, string? carrierHint, string? idempotencyKey,
        CancellationToken cancellationToken = default, DateOnly? valuationDate = null, IReadOnlyList<ContextPeriod>? policyPeriods = null,
        IReadOnlyList<PeriodExposure>? payrollByPeriod = null, IReadOnlyList<PeriodExposure>? fteByPeriod = null, DateOnly? lastDayWorked = null,
        string? workspaceId = null, DateOnly? targetPolicyEffectiveDate = null, IReadOnlyList<int>? requestedPolicyYears = null)
    {
        if (caller.Role == Roles.QuoteSystem)
        {
            return ApiResult.Error(403, "forbidden", "The quote system's identity reads; it does not upload.");
        }

        if (files.Count == 0)
        {
            return ApiResult.Error(400, "no-files", "Attach at least one file (multipart field 'file').");
        }

        if (workspaceId is not null)
        {
            // Brief 12: a workspace upload belongs to its owner's private chat scope, never to a quote.
            if (quoteId is { Length: > 0 })
            {
                return ApiResult.Error(400, "workspace-and-quote", "An upload goes to a quote or to a workspace, not both.");
            }

            var workspace = await Records.GetWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            if (workspace is null || workspace.OwnerUserId != caller.UserId)
            {
                return ApiResult.Error(404, "not-found", $"Workspace '{workspaceId}' not found.");
            }

            if (workspace.DeletedAt is not null || workspace.ExpiresAt <= runtime.Clock.GetUtcNow())
            {
                return ApiResult.Error(410, "expired", $"Workspace '{workspaceId}' has expired or was deleted.");
            }
        }

        var claimOwner = false;
        if (quoteId is { Length: > 0 })
        {
            (var denied, claimOwner) = await QuoteAccessAsync(caller, quoteId, cancellationToken).ConfigureAwait(false);
            if (denied is not null)
            {
                return denied;
            }
        }

        files = DistinctNames(files); // brief 30 item 16: two files of one name are both kept, listed and processed
        var hashes = files.Select(f => (f.FileName, Sha: Convert.ToHexStringLower(SHA256.HashData(f.Bytes)))).ToList();
        var key = idempotencyKey is { Length: > 0 } k ? $"key:{caller.UserId}:{k}" : $"content:{caller.UserId}:" + string.Join("|", hashes.Select(h => $"{h.FileName}:{h.Sha}").Order(StringComparer.Ordinal));
        var keyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var uploadId = "up-" + keyHash[..20];

        var existing = await Records.GetManifestAsync(uploadId, cancellationToken).ConfigureAwait(false);
        if (existing is null && claimOwner)
        {
            // Brief 32 item 4: a new quote is claimed only by a request that passed every check and is about to be stored, never by the check itself.
            // Two first uploads race on the insert-if-absent; an Applicant who lost it is refused before anything is written.
            var owner = await Records.ClaimQuoteOwnerAsync(new QuoteOwner(quoteId!, caller.Role == Roles.Applicant ? caller.UserId : null, runtime.Clock.GetUtcNow(), caller.UserId),
                cancellationToken).ConfigureAwait(false);
            if (caller.Role == Roles.Applicant && owner.ApplicantUserId != caller.UserId)
            {
                return NotQuoteOwner(quoteId!);
            }
        }

        if (existing is null)
        {
            foreach (var file in files)
            {
                await Store.PutAsync(Containers.Uploads, StorePaths.Source(uploadId, file.FileName), BinaryData.FromBytes(file.Bytes), overwrite: false, cancellationToken)
                    .ConfigureAwait(false);
            }

            var submission = new SubmissionInfo(quoteId, carrierHint, caller.Role, caller.UserId, valuationDate, policyPeriods, payrollByPeriod, fteByPeriod, lastDayWorked,
                workspaceId, targetPolicyEffectiveDate, requestedPolicyYears);
            await Records.UpdateManifestAsync(uploadId, m => m ?? new UploadManifest
            {
                UploadId = uploadId,
                CreatedAt = runtime.Clock.GetUtcNow(),
                Status = UploadStatuses.Received,
                Submission = submission,
                Files = [.. files.Zip(hashes, (f, h) => new ManifestFile(f.FileName, h.Sha, f.Bytes.LongLength, $"{Containers.Uploads}/{StorePaths.Source(uploadId, f.FileName)}"))],
            }, new Actor(caller.UserId, caller.Role, "api"), cancellationToken).ConfigureAwait(false);
            await Store.PutJsonAsync(Containers.Extractions, StorePaths.KeyIndex(keyHash), new DocumentIndexEntry(uploadId), cancellationToken).ConfigureAwait(false);
            if (quoteId is { Length: > 0 })
            {
                await WorkbenchApi.MergeSubmissionAsync(Records, quoteId, submission, new Actor(caller.UserId, caller.Role, "api"), runtime.Clock.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var started = await orchestrations.StartAsync(new UploadInput(uploadId, _options.ReviewTimeoutOrDefault, _options.Retry ?? RetryConfig.Embedded), cancellationToken)
            .ConfigureAwait(false);
        return new ApiResult(202, new UploadAccepted(uploadId, $"{_options.BaseUrl}/uploads/{uploadId}", $"{_options.BaseUrl}/uploads/{uploadId}/events",
            Existing: existing is not null || !started));
    }

    public async Task<ApiResult> GetUploadAsync(Caller caller, string uploadId, CancellationToken cancellationToken = default)
    {
        var manifest = await ManifestFor(caller, uploadId, cancellationToken).ConfigureAwait(false);
        return manifest is null ? NotFound("upload", uploadId) : ApiResult.Ok(manifest);
    }

    /// <summary>The upload's and each loss-run document's latest custom status (polling fallback and SSE payload).</summary>
    public async Task<ApiResult> GetStatusAsync(Caller caller, string uploadId, CancellationToken cancellationToken = default)
    {
        var manifest = await ManifestFor(caller, uploadId, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            return NotFound("upload", uploadId);
        }

        return ApiResult.Ok(await StatusViewAsync(manifest, cancellationToken).ConfigureAwait(false));
    }

    public async Task<UploadStatusView> StatusViewAsync(UploadManifest manifest, CancellationToken cancellationToken = default)
    {
        static UploadProgress? Parse(string? json) => json is null ? null : JsonSerializer.Deserialize<UploadProgress>(json, StatusJson);
        var parent = await orchestrations.GetAsync(manifest.UploadId, cancellationToken).ConfigureAwait(false);
        var documents = new List<UploadProgress>();
        foreach (var item in manifest.Items.Where(i => i.Classification == "LossRun"))
        {
            var child = await orchestrations.GetAsync(OrchestratorNames.DocumentInstance(manifest.UploadId, item.DocumentId), cancellationToken).ConfigureAwait(false);
            if (Parse(child?.CustomStatus) is { } progress)
            {
                documents.Add(progress);
            }
        }

        return new UploadStatusView(manifest.UploadId, parent?.RuntimeStatus ?? "Unknown", Parse(parent?.CustomStatus), documents);
    }

    // ------------------------------------------------------------------ documents

    public async Task<ApiResult> GetDocumentAsync(Caller caller, string documentId, CancellationToken cancellationToken = default)
    {
        var (result, error) = await ResultFor(caller, documentId, cancellationToken).ConfigureAwait(false);
        return error ?? ApiResult.Ok(result!.WithFieldStatus());
    }

    /// <summary>
    /// The graph slice of one page (paragraphs, tables, cells, words with polygons) for highlight overlays. A spreadsheet's
    /// "page" is a sheet: its cells carry A1 addresses, and <c>sheets</c> lists the workbook's sheets so the UI renders the
    /// grid and pages by sheet name (brief 13: spreadsheets asked for page images and got 409).
    /// </summary>
    public async Task<ApiResult> GetLayoutAsync(Caller caller, string documentId, int page, CancellationToken cancellationToken = default)
    {
        var (graph, error) = await GraphFor(caller, documentId, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var tableIds = graph!.Tables.Where(t => t.Page == page).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        return ApiResult.Ok(new
        {
            documentId, page, graph.Unit,
            pageInfo = graph.Pages.FirstOrDefault(p => p.Number == page),
            sheets = graph.SourceFile.Kind == FileKind.Pdf ? null : graph.Pages.Select(p => new { p.Number, p.SheetName }).ToList(),
            paragraphs = graph.Paragraphs.Where(p => p.Page == page),
            tables = graph.Tables.Where(t => t.Page == page),
            cells = graph.Cells.Where(c => c.Page == page && (c.TableId is null || tableIds.Contains(c.TableId))),
            words = graph.Words.Where(w => w.Page == page),
        });
    }

    /// <summary>Page image rendered server-side from the original PDF (1-based page number within the document).</summary>
    public async Task<ApiResult> GetPageImageAsync(Caller caller, string documentId, int page, CancellationToken cancellationToken = default)
    {
        var (graph, error) = await GraphFor(caller, documentId, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (graph!.SourceFile.Kind != FileKind.Pdf)
        {
            return ApiResult.Error(409, "not-a-pdf", "Page images exist for PDF documents only; spreadsheets carry sheet+cell lineage.");
        }

        var uploadId = (await Records.UploadOfDocumentAsync(documentId, cancellationToken).ConfigureAwait(false))!;
        var manifest = (await Records.GetManifestAsync(uploadId, cancellationToken).ConfigureAwait(false))!;
        var fileName = manifest.Items.First(i => i.DocumentId == documentId).FileName;
        var pdf = (await Store.GetAsync(Containers.Uploads, StorePaths.Source(uploadId, fileName), cancellationToken).ConfigureAwait(false))!.ToArray();
        var absolute = (graph.SourceFile.FirstPage ?? 1) + page - 1;
        try
        {
            using var bitmap = PDFtoImage.Conversion.ToImage(pdf, page: absolute - 1, options: new PDFtoImage.RenderOptions(Dpi: 110));
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            return new ApiResult(200, Bytes: png.ToArray(), ContentType: "image/png");
        }
        catch (ArgumentOutOfRangeException)
        {
            return ApiResult.Error(404, "no-such-page", $"Document {documentId} has no page {page}.");
        }
    }

    public async Task<ApiResult> GetQuotePayloadAsync(Caller caller, string documentId, CancellationToken cancellationToken = default)
    {
        var (result, error) = await ResultFor(caller, documentId, cancellationToken).ConfigureAwait(false);
        return error ?? ApiResult.Ok(QuotePayloads.From(result!));
    }

    // ------------------------------------------------------------------ reviews

    public async Task<ApiResult> ListReviewsAsync(Caller caller, string state, CancellationToken cancellationToken = default)
    {
        if (!caller.IsReader)
        {
            return ApiResult.Error(403, "forbidden", "Reviews are for Underwriter, UWManager and Admin roles.");
        }

        return ApiResult.Ok((await Records.ListReviewsAsync(state, cancellationToken).ConfigureAwait(false)).ToList());
    }

    /// <summary>Records the decision and raises <c>ReviewDecision</c> on the upload orchestration (which applies, audits and re-validates).</summary>
    public async Task<ApiResult> DecideAsync(Caller caller, string documentId, ReviewDecisionRequest request, CancellationToken cancellationToken = default)
    {
        if (!caller.IsReviewer)
        {
            return ApiResult.Error(403, "forbidden", "Only Underwriter, UWManager and Admin roles decide reviews.");
        }

        if (ErrorResponses.TooLong(("rationale", request.Rationale), ("an override's rationale", request.Overrides?.Select(o => o.Rationale).MaxBy(r => r?.Length ?? 0))) is { } tooLong)
        {
            return tooLong; // brief 30 item 28
        }

        if (request.Action is not (ReviewActions.Accept or ReviewActions.Override or ReviewActions.Reject))
        {
            return ApiResult.Error(400, "bad-action", "action must be accept, override or reject.");
        }

        // Brief 24 step 1: overrides belong to an override decision; accept or reject with overrides is refused, never
        // silently dropped (the review plan's §3 defect: typed corrections lost on "Accept extracted values").
        if (request.Action != ReviewActions.Override && request.Overrides is { Count: > 0 })
        {
            return ApiResult.Error(400, "overrides-not-allowed", $"overrides are sent only with action override, not {request.Action}.");
        }

        if (request.Action == ReviewActions.Override && (request.Overrides is not { Count: > 0 } || request.Overrides.Any(o => string.IsNullOrWhiteSpace(o.Rationale))))
        {
            return ApiResult.Error(400, "bad-overrides", "override needs at least one { fieldPath, newValue, rationale } with a rationale.");
        }

        // Brief 30 item 3: checked here, before the 202; a value the activity could not apply would otherwise be lost after the acknowledgement.
        request = request with { Overrides = request.Overrides is { } sent ? DocumentCommands.Normalized(sent) : null };
        if (DocumentCommands.OverrideValueError(request.Overrides) is { } typeError)
        {
            return typeError;
        }

        var item = await Records.GetReviewAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return NotFound("review", documentId);
        }

        if (item.State != "pending")
        {
            return ApiResult.Error(409, "not-pending", $"Review {documentId} is {item.State}.");
        }

        if (request.Overrides is { Count: > 0 } && await Records.GetResultAsync(documentId, cancellationToken).ConfigureAwait(false) is { } current
            && DocumentCommands.OverridePathError(request.Overrides, current.Document) is { } pathError)
        {
            return pathError; // brief 32 item 5: before the 202, like the type check
        }

        if (request.Aliases is { Count: > 0 } aliases)
        {
            if (item.Kind != ReviewKinds.ConfirmFormat)
            {
                return ApiResult.Error(400, "aliases-need-confirm-format", "Alias decisions belong to a confirm-format review.");
            }

            if (aliases.FirstOrDefault(a => a.Action is not (AliasDecision.Accept or AliasDecision.Edit or AliasDecision.Reject)
                    || a.Action == AliasDecision.Edit && (a.NewField is null || !CanonicalData.Glossary.Fields.ContainsKey(a.NewField))) is { } bad)
            {
                return ApiResult.Error(400, "bad-alias-decision", $"Alias \"{bad.Header}\": action must be accept, reject, or edit with a canonical newField.");
            }

            // Brief 30 item 17: refused here, before the 202 (the activity would refuse it after the decision commits).
            var learned = await runtime.Registry.GetLearnedGlossaryAsync(cancellationToken).ConfigureAwait(false);
            if (aliases.FirstOrDefault(a => a.Action is AliasDecision.Accept or AliasDecision.Edit
                    && learned.Find(a.Header, a.CanonicalField) is { Status: AliasStatus.Retired }) is { } retired)
            {
                return ApiResult.Error(409, "alias-retired", $"Alias \"{retired.Header}\" → {retired.CanonicalField} is retired; it cannot be re-activated in a review.");
            }
        }

        // Brief 34 A3: a format change is checked here, before the 202, like the overrides.
        if (request.FormatChanges is { Count: > 0 } && DocumentCommands.FormatChangesError(request.FormatChanges, request.Action, item,
                item.FormatRef ?? await ReplayedFormatAsync(documentId, cancellationToken).ConfigureAwait(false)) is { } formatError)
        {
            return formatError;
        }

        // Brief 24 step 1: the effective promotion, resolved where the review kind is known; reject never promotes.
        var promote = request.Action != ReviewActions.Reject && (request.PromoteToFormat ?? item.Kind == ReviewKinds.ConfirmFormat);
        var decision = new ReviewDecision(documentId, request.Action, request.Overrides ?? [], promote, request.Rationale, caller.UserId, caller.Role,
            runtime.Clock.GetUtcNow(), request.Aliases, ReviewDecision.NewId(), request.FormatChanges);
        if (item.Reopened)
        {
            return await new DocumentCommands(runtime).DecideReopenedAsync(decision, cancellationToken).ConfigureAwait(false); // 12b: applied now
        }

        await orchestrations.RaiseAsync(item.UploadId, OrchestratorNames.ReviewDecisionEvent, decision, cancellationToken).ConfigureAwait(false);
        return new ApiResult(202, decision);
    }

    // ------------------------------------------------------------------ learned glossary aliases (ADR 0019)

    /// <summary><c>GET /glossary/aliases[?formatId=&amp;status=]</c>: learned aliases with status and evidence (the Formats screen, the confirm-format review).</summary>
    public async Task<ApiResult> ListAliasesAsync(string? formatId = null, string? status = null, CancellationToken cancellationToken = default)
    {
        var glossary = await runtime.Registry.GetLearnedGlossaryAsync(cancellationToken).ConfigureAwait(false);
        return ApiResult.Ok(glossary.Mappings
            .Where(m => formatId is null || m.FormatId == formatId || m.Evidence.Any(e => e.FormatRef.Split('@')[0] == formatId))
            .Where(m => status is null || m.Status == status)
            .OrderBy(m => m.FormatId, StringComparer.Ordinal).ThenBy(m => m.CanonicalField, StringComparer.Ordinal).ThenBy(m => m.Header, StringComparer.Ordinal)
            .ToList());
    }

    // ------------------------------------------------------------------ formats

    /// <summary>Brief 30 item 29: carrier specs and learned aliases are for underwriting and engineering roles, not Applicants or the quote system.</summary>
    private static ApiResult? ReadersOnly(Caller caller) =>
        caller.IsReader ? null : ApiResult.Error(403, "forbidden", "Formats and learned aliases are for Underwriter, UWManager, Admin and Engineer roles.");

    public async Task<ApiResult> ListFormatsAsync(Caller caller, CancellationToken cancellationToken = default) =>
        ReadersOnly(caller) ?? await ListFormatsAsync(cancellationToken).ConfigureAwait(false);

    public async Task<ApiResult> GetFormatAsync(Caller caller, string formatId, int? version, CancellationToken cancellationToken = default) =>
        ReadersOnly(caller) ?? await GetFormatAsync(formatId, version, cancellationToken).ConfigureAwait(false);

    public async Task<ApiResult> ListAliasesAsync(Caller caller, string? formatId = null, string? status = null, CancellationToken cancellationToken = default) =>
        ReadersOnly(caller) ?? await ListAliasesAsync(formatId, status, cancellationToken).ConfigureAwait(false);

    public async Task<ApiResult> ListFormatsAsync(CancellationToken cancellationToken = default)
    {
        var summaries = new List<FormatSummary>();
        foreach (var entry in await runtime.Registry.GetIndexAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await runtime.Registry.GetAsync(entry.FormatId, entry.Version, cancellationToken).ConfigureAwait(false) is { } spec)
            {
                summaries.Add(await SummaryAsync(spec, cancellationToken).ConfigureAwait(false));
            }
        }

        return ApiResult.Ok(summaries.OrderBy(s => s.FormatId, StringComparer.Ordinal).ThenBy(s => s.Version).ToList());
    }

    public async Task<ApiResult> GetFormatAsync(string formatId, int? version, CancellationToken cancellationToken = default)
    {
        var spec = await runtime.Registry.GetAsync(formatId, version, cancellationToken).ConfigureAwait(false);
        return spec is null ? NotFound("format", formatId)
            : ApiResult.Ok(new { summary = await SummaryAsync(spec, cancellationToken).ConfigureAwait(false), spec = JsonDocument.Parse(FormatJson.Write(spec)).RootElement });
    }

    /// <summary>
    /// Pre-bake-off registry reset (08c Answers 1, brief 08d): retires every learned version that is not already retired, so
    /// matching starts from the hand specs and an empty learned set. Admin only; reason required; one <c>formats.reset</c>
    /// audit event naming every retired ref, plus each version's lifecycle record. Retired specs stay for lineage.
    /// <paramref name="dryRun"/> lists what would be retired and changes nothing.
    /// </summary>
    public async Task<ApiResult> ResetLearnedFormatsAsync(Caller caller, string? reason, bool dryRun, CancellationToken cancellationToken = default)
    {
        if (caller.Role is not Roles.Admin)
        {
            return ApiResult.Error(403, "forbidden", "Only Admin resets the format registry.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return ApiResult.Error(400, "no-reason", "Send { reason } — the reset is audited.");
        }

        var retired = new List<(string FormatId, int Version)>();
        var kept = 0;
        foreach (var entry in await runtime.Registry.GetIndexAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await runtime.Registry.GetAsync(entry.FormatId, entry.Version, cancellationToken).ConfigureAwait(false) is not { } spec)
            {
                continue;
            }

            if (spec.CreatedBy == "hand" || await FormatLifecycle.StatusAsync(runtime.Registry, spec, cancellationToken).ConfigureAwait(false) == FormatLifecycle.Retired)
            {
                kept++;
                continue;
            }

            retired.Add((spec.FormatId, spec.Version));
        }

        var at = runtime.Clock.GetUtcNow();
        var refs = retired.Select(r => $"{r.FormatId}@{r.Version}").ToList();
        if (!dryRun)
        {
            foreach (var (formatId, version) in retired)
            {
                await FormatLifecycle.RetireAsync(runtime.Registry, formatId, version, caller.UserId, $"reset: {reason}", at, cancellationToken).ConfigureAwait(false);
            }

            await Records.AuditAsync(new AuditEvent(at, caller.UserId, caller.Role, "formats.reset", "-", null,
                    Rationale: $"{reason} · retired {refs.Count} learned version(s): {string.Join(", ", refs)}"),
                new Actor(caller.UserId, caller.Role, "api"), cancellationToken).ConfigureAwait(false);
        }

        return ApiResult.Ok(new FormatResetResult(dryRun, refs, kept, at));
    }

    /// <summary>Retires a format version (Admin, UWManager): hidden from matching, kept for lineage; audited.</summary>
    public async Task<ApiResult> RetireFormatAsync(Caller caller, string formatId, int version, string? reason, CancellationToken cancellationToken = default)
    {
        if (caller.Role is not (Roles.Admin or Roles.UWManager))
        {
            return ApiResult.Error(403, "forbidden", "Only Admin and UWManager retire formats.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return ApiResult.Error(400, "no-reason", "Send { reason } — retirement is audited.");
        }

        var spec = await runtime.Registry.GetAsync(formatId, version, cancellationToken).ConfigureAwait(false);
        if (spec is null)
        {
            return NotFound("format", $"{formatId}@{version}");
        }

        var at = runtime.Clock.GetUtcNow();
        await FormatLifecycle.RetireAsync(runtime.Registry, formatId, version, caller.UserId, reason, at, cancellationToken).ConfigureAwait(false);
        await Records.AuditAsync(new AuditEvent(at, caller.UserId, caller.Role, "format.retire", "-", null, Rationale: $"{spec.Ref}: {reason}"),
            new Actor(caller.UserId, caller.Role, "api"), cancellationToken).ConfigureAwait(false);
        return ApiResult.Ok(await SummaryAsync(spec, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Brief 30 item 17: puts a retired version back in matching (Engineer, Admin): the explicit, audited way back.</summary>
    public async Task<ApiResult> UnretireFormatAsync(Caller caller, string formatId, int version, string? reason, CancellationToken cancellationToken = default)
    {
        if (caller.Role is not (Roles.Admin or Roles.Engineer))
        {
            return ApiResult.Error(403, "forbidden", "Only Engineer and Admin un-retire formats.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return ApiResult.Error(400, "no-reason", "Send { reason } — un-retirement is audited.");
        }

        var spec = await runtime.Registry.GetAsync(formatId, version, cancellationToken).ConfigureAwait(false);
        if (spec is null)
        {
            return NotFound("format", $"{formatId}@{version}");
        }

        if (await FormatLifecycle.StatusAsync(runtime.Registry, spec, cancellationToken).ConfigureAwait(false) != FormatLifecycle.Retired)
        {
            return ApiResult.Error(409, "not-retired", $"{spec.Ref} is not retired.");
        }

        var at = runtime.Clock.GetUtcNow();
        await FormatLifecycle.UnretireAsync(runtime.Registry, formatId, version, caller.UserId, reason, at, cancellationToken).ConfigureAwait(false);
        await Records.AuditAsync(new AuditEvent(at, caller.UserId, caller.Role, "format.unretire", "-", null, Rationale: $"{spec.Ref}: {reason}"),
            new Actor(caller.UserId, caller.Role, "api"), cancellationToken).ConfigureAwait(false);
        return ApiResult.Ok(await SummaryAsync(spec, cancellationToken).ConfigureAwait(false));
    }

    private async Task<FormatSummary> SummaryAsync(CarrierFormat spec, CancellationToken cancellationToken) =>
        new(spec.FormatId, spec.Version, spec.CarrierName, spec.FileKind, spec.CreatedBy, spec.CreatedAt,
            await FormatLifecycle.StatusAsync(runtime.Registry, spec, cancellationToken).ConfigureAwait(false), spec.Supersedes, spec.Learning?.VerificationGate);

    // ------------------------------------------------------------------ webhook test sink

    /// <summary>Brief 30 item 30: how far a delivery's sentAt may be from the receiver's clock.</summary>
    public static readonly TimeSpan WebhookMaxSkew = TimeSpan.FromMinutes(5);

    private sealed record WebhookNonce(DateTimeOffset SentAt, DateTimeOffset ReceivedAt);

    public async Task<ApiResult> ReceiveWebhookAsync(byte[] body, string? signature, string? secret, CancellationToken cancellationToken = default)
    {
        var valid = secret is { Length: > 0 } && signature is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(HmacWebhookSender.Sign(secret, body)), Encoding.UTF8.GetBytes(signature));
        var at = runtime.Clock.GetUtcNow();
        await Store.PutAsync(Containers.Extractions, $"webhooks/received/{at:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json",
            BinaryData.FromString(JsonSerializer.Serialize(new { at, signatureValid = valid, body = Encoding.UTF8.GetString(body) })), true, cancellationToken)
            .ConfigureAwait(false);
        if (!valid)
        {
            return ApiResult.Error(401, "bad-signature", "X-LossRun-Signature does not match the body.");
        }

        // Brief 30 item 30: replay protection. The signed body carries when it was sent and a nonce: older than 5 minutes (either way) or a
        // nonce already received is refused, so a captured delivery cannot be replayed.
        var envelope = JsonSerializer.Deserialize<WebhookEnvelope>(body, CanonicalJson.Options);
        if (envelope?.SentAt is not { } sent || envelope.Nonce is not { Length: > 0 } nonce)
        {
            return ApiResult.Error(401, "unsigned-freshness", "The signed body must carry sentAt and nonce.");
        }

        if ((at - sent).Duration() > WebhookMaxSkew)
        {
            return ApiResult.Error(401, "stale", $"Sent at {sent:O}: more than {WebhookMaxSkew.TotalMinutes} minutes from now.");
        }

        var first = false;
        await Store.UpdateAsync<WebhookNonce>(Containers.Extractions, $"webhooks/nonces/{StorePaths.Safe(nonce)}", seen =>
        {
            first = seen is null;
            return seen ?? new WebhookNonce(sent, at);
        }, cancellationToken).ConfigureAwait(false);
        return first ? ApiResult.Ok(new { received = true }) : ApiResult.Error(409, "replayed", "This delivery was already received.");
    }

    /// <summary>"run.csv", "run.csv" → "run.csv", "run (2).csv" (the store path is the file name, so a repeat would collide).</summary>
    internal static IReadOnlyList<UploadFile> DistinctNames(IReadOnlyList<UploadFile> files)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var named = new List<UploadFile>(files.Count);
        foreach (var file in files)
        {
            var name = file.FileName;
            for (var n = 2; !used.Add(name); n++)
            {
                name = $"{Path.GetFileNameWithoutExtension(file.FileName)} ({n}){Path.GetExtension(file.FileName)}";
            }

            named.Add(name == file.FileName ? file : file with { FileName = name });
        }

        return named;
    }

    // ------------------------------------------------------------------ access

    /// <summary>
    /// Brief 30 item 2: a blob-reference upload names a blob the app reads with its own identity, so the reference is confined:
    /// https on this account, the <c>uploads</c> container only, no dot segments (raw or percent-encoded), and a prefix the caller
    /// may read: underwriter roles any upload; an Applicant <c>incoming/{their user id}/…</c> or an upload of their own.
    /// Returns the path inside <c>uploads</c>, or the error (400 malformed or outside the container, 403 another caller's prefix).
    /// </summary>
    public async Task<(string? Path, ApiResult? Error)> ResolveBlobReferenceAsync(Caller caller, string? blobUrl, string accountHost, CancellationToken cancellationToken = default)
    {
        static (string?, ApiResult?) Bad(string message) => (null, ApiResult.Error(400, "bad-blob-reference", message));
        if (!Uri.TryCreate(blobUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || !string.Equals(uri.Host, accountHost, StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length > 0)
        {
            return Bad($"blobUrl must be an https URL on {accountHost}.");
        }

        var raw = blobUrl!;
        if (raw.Contains("..", StringComparison.Ordinal) || raw.Contains("%2e", StringComparison.OrdinalIgnoreCase) || raw.Contains('\\')
            || raw.Contains("%2f", StringComparison.OrdinalIgnoreCase) || raw.Contains("%5c", StringComparison.OrdinalIgnoreCase))
        {
            return Bad("blobUrl must not contain dot segments or encoded separators.");
        }

        var segments = uri.AbsolutePath.TrimStart('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        if (segments.Length < 3 || segments[0] != Containers.Uploads || segments.Any(s => s.Length == 0 || s == "."))
        {
            return Bad($"blobUrl must name a blob in the '{Containers.Uploads}' container: {Containers.Uploads}/incoming/<your user id>/<file> or {Containers.Uploads}/<upload id>/<file>.");
        }

        var path = string.Join('/', segments[1..]);
        var readable = caller.SeesAllUploads && caller.Role != Roles.QuoteSystem
            || segments is [_, "incoming", var owner, _, ..] && owner == caller.UserId
            || await ManifestFor(caller, segments[1], cancellationToken).ConfigureAwait(false) is not null;
        return readable ? (path, null) : (null, ApiResult.Error(403, "forbidden-blob", "blobUrl names a blob outside your uploads."));
    }

    /// <summary>
    /// Brief 30 item 1: may the caller attach an upload (and its submission context) to the quote? Underwriter roles: any quote.
    /// An Applicant: a quote they own, or a new one. A quote an underwriter role created, or one that predates the owner record, is underwriter-only.
    /// Brief 32 item 4: the check writes nothing. A quote is new only when nothing names it (no owner record, documents, uploads, context,
    /// revision or label); then <c>Claim</c> is true and the upload claims it once it is accepted.
    /// </summary>
    public async Task<(ApiResult? Denied, bool Claim)> QuoteAccessAsync(Caller caller, string quoteId, CancellationToken cancellationToken)
    {
        var owner = await Records.GetQuoteOwnerAsync(quoteId, cancellationToken).ConfigureAwait(false);
        var isNew = owner is null && !await QuoteExistsAsync(quoteId, cancellationToken).ConfigureAwait(false);
        if (isNew && caller.Role == Roles.Applicant && !_options.ApplicantMayCreateQuotes)
        {
            return (ApiResult.Error(404, "quote-not-found", $"Quote '{quoteId}' does not exist; quotes are created by the quote system or an underwriter."), false);
        }

        if (caller.Role != Roles.Applicant || isNew || owner?.ApplicantUserId is { } id && id == caller.UserId)
        {
            return (null, isNew);
        }

        return (NotQuoteOwner(quoteId), false);
    }

    /// <summary>The format a document was replayed with: its result's, or for a document in Review (no format on the result) its routing decision's.</summary>
    private async Task<string?> ReplayedFormatAsync(string documentId, CancellationToken cancellationToken) =>
        await Records.GetResultAsync(documentId, cancellationToken).ConfigureAwait(false) is { } result ? result.FormatRef ?? result.Routing?.FormatRef : null;

    private async Task<bool> QuoteExistsAsync(string quoteId, CancellationToken cancellationToken) =>
        (await Records.DocumentsOfQuoteAsync(quoteId, cancellationToken).ConfigureAwait(false)).Count > 0
        || (await Records.UploadsOfQuoteAsync(quoteId, cancellationToken).ConfigureAwait(false)).Count > 0
        || await Records.GetQuoteContextAsync(quoteId, cancellationToken).ConfigureAwait(false) is not null
        || await Records.QuoteRevisionAsync(quoteId, cancellationToken).ConfigureAwait(false) > 0
        || await Records.GetQuoteLabelAsync(quoteId, cancellationToken).ConfigureAwait(false) is not null;

    private static ApiResult NotQuoteOwner(string quoteId) =>
        ApiResult.Error(403, "not-quote-owner", $"Quote '{quoteId}' belongs to another submitter; an Applicant uploads only to quotes they created.");

    /// <summary>Applicants see only their own uploads; other roles see all.</summary>
    public async Task<UploadManifest?> ManifestFor(Caller caller, string uploadId, CancellationToken cancellationToken)
    {
        var manifest = await Records.GetManifestAsync(uploadId, cancellationToken).ConfigureAwait(false);
        return manifest is not null && (caller.SeesAllUploads || manifest.Submission.UploaderId == caller.UserId) ? manifest : null;
    }

    private async Task<(DocumentResult? Result, ApiResult? Error)> ResultFor(Caller caller, string documentId, CancellationToken cancellationToken)
    {
        if (caller.Role == Roles.QuoteSystem)
        {
            return (null, ApiResult.Error(403, "forbidden", "The quote system reads the quote payload and manifests, not documents.")); // brief 25 Answers 2
        }

        var uploadId = await Records.UploadOfDocumentAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (uploadId is null || await ManifestFor(caller, uploadId, cancellationToken).ConfigureAwait(false) is null)
        {
            return (null, NotFound("document", documentId));
        }

        var result = await Records.GetResultAsync(documentId, cancellationToken).ConfigureAwait(false);
        return result is null ? (null, ApiResult.Error(409, "not-ready", $"Document {documentId} is still processing.")) : (result, null);
    }

    private async Task<(DocumentGraph? Graph, ApiResult? Error)> GraphFor(Caller caller, string documentId, CancellationToken cancellationToken)
    {
        if (caller.Role == Roles.QuoteSystem)
        {
            return (null, ApiResult.Error(403, "forbidden", "The quote system reads the quote payload and manifests, not documents."));
        }

        var uploadId = await Records.UploadOfDocumentAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (uploadId is null || await ManifestFor(caller, uploadId, cancellationToken).ConfigureAwait(false) is not { } manifest)
        {
            return (null, NotFound("document", documentId));
        }

        // A kept (not processed) segment has no graph of its own: its pages are the parent's (chat cites them, brief 12).
        var graphId = manifest.Items.FirstOrDefault(i => i.DocumentId == documentId) is { Status: ItemStatuses.KeptNotProcessed, ParentDocumentId: { } parent }
            ? parent : documentId;
        var graph = await Store.GetJsonAsync<DocumentGraph>(Containers.Extractions, StorePaths.Document(uploadId, graphId, "graph.json"), cancellationToken)
            .ConfigureAwait(false);
        return graph is null ? (null, NotFound("document", documentId)) : (graph, null);
    }

    private static ApiResult NotFound(string what, string id) => ApiResult.Error(404, "not-found", $"No {what} '{id}' visible to the caller.");
}

/// <summary>Quote-system payload: per-period sums and the raw claim list (feature math is brief 10's catalog).</summary>
public static class QuotePayloads
{
    public static QuotePayload From(DocumentResult result)
    {
        var d = result.Document;
        static MoneySums Sum(IEnumerable<MoneyTriplet?> triplets)
        {
            var list = triplets.Where(t => t is not null).ToList();
            return new MoneySums(list.Sum(t => t!.Paid ?? 0), list.Sum(t => t!.Reserve ?? 0), list.Sum(t => t!.Incurred ?? (t.Paid ?? 0) + (t.Reserve ?? 0)));
        }

        var periods = d.PolicyPeriods.Select(p => new QuotePeriod(p.PolicyNumber, p.EffectiveDate, p.ExpirationDate, p.Claims.Count,
            p.Claims.Count(c => c.Status is ClaimStatus.Open or ClaimStatus.Reopened),
            Sum(p.Claims.Select(c => c.Indemnity)), Sum(p.Claims.Select(c => c.Medical)), Sum(p.Claims.Select(c => c.Expense)), Sum(p.Claims.Select(c => c.Total)))).ToList();
        return new QuotePayload(result.DocumentId, d.CarrierName, d.InsuredName, d.ValuationDate, periods, d.PolicyPeriods.SelectMany(p => p.Claims).ToList(), d.ValuationDateSource);
    }
}
