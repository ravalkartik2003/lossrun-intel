using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LossRun.Analytics;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;
using LossRun.Rules;

namespace LossRun.Orchestration;

/// <summary>
/// Use Case 2 analytics over a quote (brief 10 §4): cached baseline feature snapshots, server what-if, the per-account claims
/// payload the client recomputes from, the catalog, and quote payload v2. Underwriting roles only (an applicant sees the
/// upload, not the analytics). Baselines cache in two levels: an in-process L1 keyed by the quote's document revisions (an
/// override bumps a revision, so a stale entry can never match) and dbo.FeatureSnapshot / Blob as L2, which the command
/// handler invalidates on any claim change.
/// </summary>
public sealed class QuoteAnalytics(LossRunRuntime runtime)
{
    /// <summary>The claims payload the client holds (underwriter-analytics.md KQ2: 1–5K claims per account).</summary>
    public const int MaxClaims = 5000;

    private const int L1Capacity = 256;
    private static readonly ConcurrentDictionary<FeatureSnapshotKey, string> L1 = new();

    /// <summary>
    /// Parsed document results by (id, revision), brief 12b: deserializing a 1,000-claim account's result JSON took 1.4–2.8 s
    /// per request; an override bumps one revision, so only that document reloads. A revision is immutable, so an entry is never stale.
    /// </summary>
    private const int DocumentCacheCapacity = 4000;
    private static readonly ConcurrentDictionary<(string Id, int Revision, string? ReviewState), QuoteResult> DocumentCache = new(); // brief 30 item 16: review state too

    private IRecordStore Records => runtime.RecordStore;

    /// <param name="Documents">The quote's documents that count: every one but the rejected (brief 24 step 2).</param>
    /// <param name="Excluded">Rejected documents: out of claims, features and periods, listed in the payload's <c>excluded[]</c>.</param>
    /// <param name="Pending">Verified-only accounts: the documents left out because they wait for review (listed in the payload's <c>excluded[]</c>).</param>
    public sealed record Account(string QuoteId, IReadOnlyList<DocumentResult> Documents, IReadOnlyList<AccountDocument> AccountDocuments, string RevisionKey,
        AnalyticsContext? Context = null, IReadOnlyList<DocumentResult>? Excluded = null, IReadOnlyList<DocumentResult>? Pending = null);

    /// <summary>
    /// Brief 24 Answers 2: <c>view=verified-only</c>, the view the quote payload and the pricing memo always use — they must never
    /// carry an unverified value. Documents waiting for review are left out, and a field that is unresolved (a hard rule still
    /// failing on it, or R-33 required and missing) is null. The Analytics screen keeps the whole picture (adjusted), labelled
    /// by the readiness banner.
    /// </summary>
    public const string VerifiedOnly = "verified-only";

    /// <summary>The verified-only account: approved and straight-through documents, unresolved fields null. Its cache key is its own.</summary>
    public static Account Verified(Account account)
    {
        var pending = account.Documents.Where(d => d.ReviewState is "pending" or "reopened").ToList();
        var kept = account.Documents.Except(pending).ToList();
        var documents = kept.Select(d =>
        {
            var overrides = account.AccountDocuments.FirstOrDefault(a => a.DocumentId == d.DocumentId)?.Overrides ?? [];
            var unresolved = FieldStatusEvaluator.Evaluate(d.Document, d.ReviewState, d.RuleOutcomes).Where(s => s.Value.Status != FieldStates.Approved).Select(s => s.Key).ToList();
            if (unresolved.Count == 0)
            {
                return new AccountDocument(d.DocumentId, d.Document, overrides);
            }

            var node = JsonSerializer.SerializeToNode(d.Document, CanonicalJson.Options)!.AsObject();
            var lineage = d.Document.Lineage;
            node.Remove("lineage");
            foreach (var path in unresolved)
            {
                if (JsonPath.Get(node, path) is not null && !NotNullable.Contains(path[(path.LastIndexOf('.') + 1)..]))
                {
                    JsonPath.Set(node, path, null);
                }
            }

            var verified = CanonicalJson.Deserialize(node.ToJsonString(CanonicalJson.Options));
            verified.Lineage = lineage;
            return new AccountDocument(d.DocumentId, verified, overrides);
        }).ToList();
        return account with { Documents = kept, AccountDocuments = documents, RevisionKey = account.RevisionKey + "-verified", Pending = pending };
    }

    /// <summary>Canonical fields that cannot be null (flags and enums): an unresolved one keeps its value, its status says so.</summary>
    private static readonly HashSet<string> NotNullable = new(StringComparer.Ordinal)
        { "detailLevel", "noLossesStatement", "unattributedNoLossesStatement", "expenseTreatment", "schemaVersion", "documentId" };

    /// <summary>The account a view computes over, and the engine view (verified-only computes as adjusted).</summary>
    private static (Account Account, string EngineView) Resolve(Account account, string view) =>
        view == VerifiedOnly ? (Verified(account), AccountBuilder.AsAdjusted) : (account, view);

    /// <summary>The quote's cache identity from its document ids and revisions alone (one small query; no result is loaded).</summary>
    public async Task<(string? RevisionKey, ApiResult? Error)> KeyAsync(Caller caller, string quoteId, CancellationToken cancellationToken)
    {
        if (!Roles.CanRead(caller.Role))
        {
            return (null, ApiResult.Error(403, "forbidden", "Quote analytics are for underwriting roles."));
        }

        var documents = await Records.DocumentsOfQuoteAsync(quoteId, cancellationToken).ConfigureAwait(false);
        if (documents.Count == 0)
        {
            return (null, ApiResult.Error(404, "not-found", $"No loss-run documents for quote '{quoteId}'."));
        }

        var context = ToAnalytics(await Records.GetQuoteContextAsync(quoteId, cancellationToken).ConfigureAwait(false));
        var quoteRevision = await Records.QuoteRevisionAsync(quoteId, cancellationToken).ConfigureAwait(false);
        return (RevisionKey(documents, context, quoteRevision), null);
    }

    /// <summary>Hash of the document ids and revisions and of the submission context (report 10 answer 1: snapshots key on context too).
    /// Brief 30 item 16: and of the quote revision, which also moves when a review state changes without a new document revision (reopen),
    /// so the verified view's snapshot is never served from before it.</summary>
    private static string RevisionKey(IEnumerable<QuoteDocument> documents, AnalyticsContext? context, int quoteRevision)
    {
        var text = $"q{quoteRevision};" + string.Join(";", documents.OrderBy(d => d.DocumentId, StringComparer.Ordinal).Select(d => $"{d.DocumentId}:{d.Revision}"));
        if (context is not null)
        {
            text += "|" + AnalyticsJson.WriteInput(new AnalyticsInput([], context, []));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    /// <summary>The engine's context from a quote context: payroll in cents and FTE (whole, rounded) summed by policy year.</summary>
    public static AnalyticsContext? ToAnalytics(QuoteContext? q)
    {
        if (q is null)
        {
            return null;
        }

        static Dictionary<int, long>? ByYear(IReadOnlyList<PeriodExposure>? periods, decimal scale) => periods is { Count: > 0 }
            ? periods.GroupBy(p => p.EffectiveDate.Year).ToDictionary(g => g.Key, g => (long)Math.Round(g.Sum(p => p.Amount) * scale, MidpointRounding.AwayFromZero))
            : null;
        return new AnalyticsContext(q.QuoteId, null, ByYear(q.PayrollByPeriod, 100), ByYear(q.FteByPeriod, 1), q.LastDayWorked?.ToString("yyyy-MM-dd"));
    }

    public async Task<(Account? Account, ApiResult? Error)> LoadAsync(Caller caller, string quoteId, CancellationToken cancellationToken) =>
        Roles.CanRead(caller.Role) ? await LoadCoreAsync(quoteId, cancellationToken).ConfigureAwait(false)
            : (null, ApiResult.Error(403, "forbidden", "Quote analytics are for underwriting roles."));

    /// <summary>The account without a role check: the callers above (and the quote payload, brief 25 Answers 2) check first.</summary>
    private async Task<(Account? Account, ApiResult? Error)> LoadCoreAsync(string quoteId, CancellationToken cancellationToken)
    {
        var current = await Records.DocumentsOfQuoteAsync(quoteId, cancellationToken).ConfigureAwait(false);
        var missing = current.Where(d => !DocumentCache.ContainsKey((d.DocumentId, d.Revision, d.ReviewState))).Select(d => d.DocumentId).ToList();
        if (missing.Count > 0)
        {
            if (DocumentCache.Count + missing.Count > DocumentCacheCapacity)
            {
                DocumentCache.Clear();
                missing = [.. current.Select(d => d.DocumentId)];
            }

            foreach (var loaded in await Records.QuoteResultsAsync(quoteId, cancellationToken, missing.Count == current.Count ? null : missing).ConfigureAwait(false))
            {
                DocumentCache[(loaded.Result.DocumentId, loaded.Result.Revision, loaded.Result.ReviewState)] = loaded;
            }
        }

        var results = new List<DocumentResult>();
        var excluded = new List<DocumentResult>();
        var documents = new List<AccountDocument>();
        foreach (var (result, overrides) in current.Select(d => DocumentCache.GetValueOrDefault((d.DocumentId, d.Revision, d.ReviewState))).OfType<QuoteResult>())
        {
            if (result.ReviewState == ReviewActions.Reject)
            {
                excluded.Add(result);
                continue;
            }

            results.Add(result);
            documents.Add(new AccountDocument(result.DocumentId, result.Document,
                [.. overrides.Select(o => new OverrideRecord(o.Id, o.DocumentId, o.FieldPath, o.OldValue, o.NewValue, o.Rationale, o.UserId, o.AtUtc))]));
        }

        if (results.Count + excluded.Count == 0)
        {
            return (null, ApiResult.Error(404, "not-found", $"No loss-run documents for quote '{quoteId}'."));
        }

        var claims = results.Sum(r => r.Document.PolicyPeriods.Sum(p => p.Claims.Count));
        if (claims > MaxClaims)
        {
            return (null, ApiResult.Error(413, "too-many-claims", $"{claims} claims exceed the {MaxClaims}-claim account payload."));
        }

        var context = ToAnalytics(await Records.GetQuoteContextAsync(quoteId, cancellationToken).ConfigureAwait(false));
        var quoteRevision = await Records.QuoteRevisionAsync(quoteId, cancellationToken).ConfigureAwait(false);
        return (new Account(quoteId, results, documents, RevisionKey(results.Concat(excluded).Select(r => new QuoteDocument(r.DocumentId, r.Revision)), context, quoteRevision), context,
            excluded), null);
    }

    /// <summary>The engine input for the quote under a view.</summary>
    /// <remarks>What-if <paramref name="extras"/> override the stored quote context field by field.</remarks>
    public static AnalyticsInput Input(Account account, string view, AnalyticsContext? extras = null) =>
        AccountBuilder.Build(account.QuoteId, account.AccountDocuments, view, Merge(account.Context, extras));

    private static AnalyticsContext? Merge(AnalyticsContext? stored, AnalyticsContext? extras) =>
        stored is null ? extras : extras is null ? stored : new AnalyticsContext(stored.QuoteId, extras.ValuationDate ?? stored.ValuationDate,
            extras.PayrollCentsByYear ?? stored.PayrollCentsByYear, extras.FteByYear ?? stored.FteByYear, extras.LastDayWorked ?? stored.LastDayWorked);

    /// <summary>A cached baseline (L1, then L2) for a quote's revision key, without loading its documents; null on a miss.</summary>
    public async Task<(string Json, string Source)?> CachedBaselineAsync(string quoteId, string revisionKey, string view, CancellationToken cancellationToken)
    {
        var key = new FeatureSnapshotKey(quoteId, revisionKey, Catalog.Embedded.Version, view, "baseline");
        if (L1.TryGetValue(key, out var hit))
        {
            return (hit, "memory");
        }

        if (await Records.GetFeatureSnapshotAsync(key, cancellationToken).ConfigureAwait(false) is { } stored)
        {
            Remember(key, stored.Json);
            return (stored.Json, "store");
        }

        return null;
    }

    /// <summary>The baseline snapshot JSON: L1, then L2 (SQL / Blob), then computed and stored.</summary>
    public async Task<(string Json, string Source)> BaselineAsync(Account account, string view, CancellationToken cancellationToken)
    {
        var key = new FeatureSnapshotKey(account.QuoteId, account.RevisionKey, Catalog.Embedded.Version, view, "baseline");
        if (await CachedBaselineAsync(account.QuoteId, account.RevisionKey, view, cancellationToken).ConfigureAwait(false) is { } cached)
        {
            return cached;
        }

        var input = Input(account, view == VerifiedOnly ? AccountBuilder.AsAdjusted : view);
        if (input.Overrides.Where(o => o.Unrevertable == true).Select(o => o.ClaimId.Split('/')[0]).Distinct(StringComparer.Ordinal).ToList() is { Count: > 0 } documents)
        {
            // Brief 32 item 5: once per revision (the baseline is cached by it); the trace names the fields, never their values.
            var fields = input.Overrides.Where(o => o.Unrevertable == true).Select(o => o.Field).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            await new EscalationService(runtime).TryRaiseAsync([new EscalationSignal(EscalationReasons.OverrideUnrevertable, $"quote:{account.QuoteId}", runtime.Clock.GetUtcNow(),
                new EscalationTrace
                {
                    Reasons = EscalationTraceText.Sanitize([$"as-reported view of {account.QuoteId}: the stored old value of {string.Join(", ", fields)} is not of the field's type; the current value is shown"]),
                    Counts = new Dictionary<string, decimal>(), Step = "as-reported",
                }) { DocumentIds = documents }], cancellationToken).ConfigureAwait(false);
        }

        var snapshot = FeatureEngine.Compute(input, null, view);
        var json = snapshot.ToJson();
        await Records.SaveFeatureSnapshotAsync(new StoredFeatureSnapshot(key, snapshot.InputsHash, input.Claims.Count, json, runtime.Clock.GetUtcNow()), cancellationToken)
            .ConfigureAwait(false);
        Remember(key, json);
        return (json, "computed");
    }

    private static void Remember(FeatureSnapshotKey key, string json)
    {
        if (L1.Count >= L1Capacity)
        {
            L1.Clear();
        }

        L1[key] = json;
    }

    // ------------------------------------------------------------------ endpoints

    /// <summary><c>GET /quotes/{quoteId}/features?view=reported|adjusted</c>.</summary>
    public async Task<ApiResult> FeaturesAsync(Caller caller, string quoteId, string? view, CancellationToken cancellationToken = default)
    {
        if (ViewOf(view) is not { } v)
        {
            return ApiResult.Error(400, "bad-view", "view is 'reported', 'adjusted' or 'verified-only'.");
        }

        // Cached path: one (id, revision) query, then L1 / L2 — documents are loaded only on a miss.
        var (revisionKey, keyError) = await KeyAsync(caller, quoteId, cancellationToken).ConfigureAwait(false);
        if (keyError is not null)
        {
            return keyError;
        }

        if (await CachedBaselineAsync(quoteId, v == VerifiedOnly ? revisionKey + "-verified" : revisionKey!, v, cancellationToken).ConfigureAwait(false) is { } cached)
        {
            return Raw(cached.Json) with { Headers = SnapshotHit };
        }

        var (account, error) = await LoadAsync(caller, quoteId, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var (json, _) = await BaselineAsync(v == VerifiedOnly ? Verified(account!) : account!, v, cancellationToken).ConfigureAwait(false);
        return Raw(json) with { Headers = SnapshotMiss };
    }

    /// <summary>Whether a features read was served from a stored snapshot (12b: the load test reports p95 for each).</summary>
    public const string SnapshotHeader = "X-Feature-Snapshot";
    private static readonly IReadOnlyDictionary<string, string> SnapshotHit = new Dictionary<string, string> { [SnapshotHeader] = "hit" };
    private static readonly IReadOnlyDictionary<string, string> SnapshotMiss = new Dictionary<string, string> { [SnapshotHeader] = "miss" };

    /// <summary>
    /// <c>POST /quotes/{quoteId}/features/what-if</c>: body <c>{ "view", "scenario": {…}, "context": {…} }</c>. Returns the
    /// baseline, the scenario snapshot and the per-metric delta (server recompute, for parity and for the pricing memo).
    /// </summary>
    public async Task<ApiResult> WhatIfAsync(Caller caller, string quoteId, JsonElement body, CancellationToken cancellationToken = default)
    {
        var view = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("view", out var vv) ? ViewOf(vv.GetString()) : AccountBuilder.AsAdjusted;
        if (view is null)
        {
            return ApiResult.Error(400, "bad-view", "view is 'reported', 'adjusted' or 'verified-only'.");
        }

        var (account, error) = await LoadAsync(caller, quoteId, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var scenario = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("scenario", out var s) && s.ValueKind == JsonValueKind.Object
            ? AnalyticsJson.ReadScenario(s) : new Scenario();
        var extras = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("context", out var c) && c.ValueKind == JsonValueKind.Object ? Context(c) : null;
        var (scoped, engineView) = Resolve(account!, view);
        var input = Input(scoped, engineView, extras);
        var baseline = FeatureEngine.Compute(input, null, view);
        var next = FeatureEngine.Compute(input, scenario, view);

        var w = new CanonicalWriter();
        w.StartObject().Name("quoteId").String(quoteId).Name("view").String(view).Name("scenarioHash").String(AnalyticsJson.ScenarioHash(scenario))
            .Name("baseline").Raw(baseline.ToJson()).Name("scenario").Raw(next.ToJson()).Name("deltas").StartArray();
        for (var i = 0; i < baseline.Metrics.Count; i++)
        {
            var (b, n) = (baseline.Metrics[i], next.Metrics[i]);
            w.StartObject().Name("name").String(b.Name).Name("unit").String(b.Unit)
                .Name("baseline").Scaled(b.Value, b.Scale).Name("scenario").Scaled(n.Value, n.Scale)
                .Name("delta").Scaled(b.Value is { } bv && n.Value is { } nv ? nv - bv : null, b.Scale).EndObject();
        }

        w.EndArray().EndObject();
        return Raw(w.ToString());
    }

    /// <summary><c>GET /quotes/{quoteId}/claims?view=…</c>: the engine input the client recomputes from, plus the baseline per-claim flags.</summary>
    public async Task<ApiResult> ClaimsAsync(Caller caller, string quoteId, string? view, CancellationToken cancellationToken = default)
    {
        if (ViewOf(view) is not { } v)
        {
            return ApiResult.Error(400, "bad-view", "view is 'reported', 'adjusted' or 'verified-only'.");
        }

        var (account, error) = await LoadAsync(caller, quoteId, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var (scoped, engineView) = Resolve(account!, v);
        var (baseline, _) = await BaselineAsync(scoped, v, cancellationToken).ConfigureAwait(false);
        var flags = JsonNode.Parse(baseline)!["perClaimFlags"]!.ToJsonString();
        var w = new CanonicalWriter();
        w.StartObject().Name("catalogVersion").String(Catalog.Embedded.Version).Name("view").String(v)
            .Name("input").Raw(AnalyticsJson.WriteInput(Input(scoped, engineView))).Name("perClaimFlags").Raw(flags)
            .Name("readiness").Raw(Readiness(account!).ToJsonString()).EndObject();
        return Raw(w.ToString());
    }

    /// <summary><c>GET /catalog</c>.</summary>
    public static ApiResult CatalogJson() => Raw(Catalog.Embedded.ToJson());

    /// <summary>
    /// <c>GET /quotes/{quoteId}/quote-payload</c> (v2, supersedes the brief-07 per-document payload): both feature
    /// snapshots, per-period aggregates, canonical claims with flags, and a lineage summary (documents, valuation dates,
    /// format versions, extractors, overrides with rationale, review states).
    /// </summary>
    /// <param name="revision">Brief 24 step 3: <c>?revision=</c> from a command's pinned URL; 409 revision-superseded once a newer revision exists.</param>
    /// <param name="ifNoneMatch">The client's <c>If-None-Match</c>; 304 when it names the current revision's ETag.</param>
    public async Task<ApiResult> QuotePayloadAsync(Caller caller, string quoteId, CancellationToken cancellationToken = default, int? revision = null,
        string? ifNoneMatch = null)
    {
        if (!Roles.CanReadQuotePayload(caller.Role))
        {
            return ApiResult.Error(403, "forbidden", "Quote analytics are for underwriting roles.");
        }

        var current = await Records.QuoteRevisionAsync(quoteId, cancellationToken).ConfigureAwait(false);
        if (revision is { } asked && asked != current)
        {
            return asked < current
                ? ApiResult.Error(409, "revision-superseded", $"Quote {quoteId} is at revision {current}; revision {asked} is superseded. Fetch without ?revision= or with {current}.")
                : ApiResult.Error(404, "no-such-revision", $"Quote {quoteId} is at revision {current}; there is no revision {asked} yet.");
        }

        var etag = new Dictionary<string, string> { ["ETag"] = QuoteRevisions.ETag(current) };
        if (ifNoneMatch is { Length: > 0 } && ifNoneMatch.Split(',').Select(t => t.Trim()).Contains(QuoteRevisions.ETag(current)))
        {
            return new ApiResult(304, Headers: etag);
        }

        var (account, error) = await LoadCoreAsync(quoteId, cancellationToken).ConfigureAwait(false);
        return error ?? Raw((await PayloadAsync(account!, cancellationToken, current).ConfigureAwait(false)).ToJsonString()) with { Headers = etag };
    }

    /// <param name="quoteRevision">The revision read before the account was loaded (null reads it now).</param>
    public async Task<JsonObject> PayloadAsync(Account account, CancellationToken cancellationToken, int? quoteRevision = null)
    {
        quoteRevision ??= await Records.QuoteRevisionAsync(account.QuoteId, cancellationToken).ConfigureAwait(false);
        // Brief 24 Answers 2: the payload never carries an unverified value — its features, periods and claims are verified-only.
        var verified = Verified(account);
        var (adjusted, _) = await BaselineAsync(verified, VerifiedOnly, cancellationToken).ConfigureAwait(false);
        var (reported, _) = await BaselineAsync(verified, AccountBuilder.AsReported, cancellationToken).ConfigureAwait(false);
        var adjustedNode = JsonNode.Parse(adjusted)!.AsObject();
        var flags = adjustedNode["perClaimFlags"]!.AsArray().ToDictionary(f => f!["claimId"]!.GetValue<string>(), f => f!["flags"]!.DeepClone());

        var claims = new JsonArray();
        var extractors = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var statusCounts = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var periodRows = new JsonArray();
        foreach (var r in (account.Excluded ?? []).OrderBy(d => d.DocumentId, StringComparer.Ordinal))
        {
            AddPeriods(periodRows, r, FieldStatusEvaluator.Evaluate(r.Document, r.ReviewState, r.RuleOutcomes), "rejected");
        }

        foreach (var r in account.Documents.OrderBy(d => d.DocumentId, StringComparer.Ordinal))
        {
            var doc = r.Document;
            var statuses = FieldStatusEvaluator.Evaluate(doc, r.ReviewState, r.RuleOutcomes);
            statusCounts[r.DocumentId] = new JsonObject(statuses.Values.GroupBy(s => s.Status).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count())));
            AddPeriods(periodRows, r, statuses, verified.Pending!.Contains(r) ? "pending-review" : null);
            if (verified.Pending!.Contains(r))
            {
                continue; // waiting for review: in documents[] and excluded[], never in claims
            }

            for (var p = 0; p < doc.PolicyPeriods.Count; p++)
            {
                for (var c = 0; c < doc.PolicyPeriods[p].Claims.Count; c++)
                {
                    var id = $"{r.DocumentId}/{p}/{c}";
                    var node = JsonSerializer.SerializeToNode(doc.PolicyPeriods[p].Claims[c], CanonicalJson.Options)!.AsObject();
                    node.Insert(0, "id", id);
                    node.Insert(1, "policyNumber", doc.PolicyPeriods[p].PolicyNumber);
                    node["flags"] = flags.TryGetValue(id, out var f) ? f : new JsonArray();
                    // Brief 24 step 2: an unresolved value travels as null with its status, never as the unverified extraction.
                    var prefix = $"policyPeriods[{p}].claims[{c}].";
                    var open = new JsonObject();
                    foreach (var (path, status) in statuses.Where(s => s.Key.StartsWith(prefix, StringComparison.Ordinal) && s.Value.Status != FieldStates.Approved))
                    {
                        var field = path[prefix.Length..];
                        SetNull(node, field);
                        open[field] = new JsonObject { ["status"] = status.Status, ["reason"] = status.Reason };
                    }

                    node["fieldStatus"] = open;
                    claims.Add(node);
                }
            }

            foreach (var l in doc.Lineage is { } lineage ? lineage.Values : Enumerable.Empty<LineageRecord>())
            {
                var name = l.Extractor ?? "unknown";
                extractors[name] = extractors.GetValueOrDefault(name) + 1;
            }
        }

        var overrides = new JsonArray();
        foreach (var o in account.AccountDocuments.SelectMany(d => d.Overrides).OrderBy(o => o.AtUtc).ThenBy(o => o.Id, StringComparer.Ordinal))
        {
            overrides.Add(new JsonObject
            {
                ["id"] = o.Id, ["documentId"] = o.DocumentId, ["fieldPath"] = o.FieldPath, ["oldValue"] = o.OldValue, ["newValue"] = o.NewValue,
                ["rationale"] = o.Rationale, ["userId"] = o.UserId, ["atUtc"] = o.AtUtc?.ToString("O"),
            });
        }

        var pending = account.Documents.Count(d => d.ReviewState is "pending" or "reopened"); // brief 30 item 13: a reopened document is pending too
        var excluded = account.Excluded ?? [];
        // ADR 0017: summary-only periods carry no claims; they travel as period rows with granularity "period".
        var summaryRows = PeriodSummaries.Rows(verified.AccountDocuments);
        return new JsonObject
        {
            ["payloadVersion"] = "2",
            ["quoteId"] = account.QuoteId,
            // Brief 24 step 3 (ADR 0024): the revision this payload reflects; also the ETag "q-<n>".
            ["quoteRevision"] = quoteRevision,
            ["catalogVersion"] = Catalog.Embedded.Version,
            ["valuationDate"] = adjustedNode["valuationDate"]?.DeepClone(),
            ["generatedUtc"] = runtime.Clock.GetUtcNow().ToString("O"),
            ["documents"] = new JsonArray([.. account.Documents.OrderBy(d => d.DocumentId, StringComparer.Ordinal).Select(d => (JsonNode)new JsonObject
            {
                ["documentId"] = d.DocumentId, ["uploadId"] = d.UploadId, ["revision"] = d.Revision, ["carrierName"] = d.Document.CarrierName,
                ["insuredName"] = d.Document.InsuredName, ["valuationDate"] = d.Document.ValuationDate?.ToString("yyyy-MM-dd"), ["valuationDateSource"] = d.Document.ValuationDateSource, ["formatRef"] = d.FormatRef,
                ["routingTier"] = d.Routing?.Tier.ToString(), ["reviewState"] = d.ReviewState, ["claimCount"] = d.Document.PolicyPeriods.Sum(p => p.Claims.Count),
                ["granularity"] = Granularity(d.Document),
                ["absentComponents"] = new JsonArray([.. Absent(d.Document).Select(c => (JsonNode)c)]),
                ["fieldStatusCounts"] = statusCounts[d.DocumentId],
            })]),
            // Brief 25: one row per policy period of every document of the quote (the quote system's loss-information columns).
            ["policyPeriods"] = periodRows,
            ["readiness"] = Readiness(account),
            ["excluded"] = new JsonArray([.. excluded.Select(d => (d, "rejected")).Concat(verified.Pending!.Select(d => (d, "pending-review")))
                .OrderBy(x => x.d.DocumentId, StringComparer.Ordinal).Select(x => (JsonNode)new JsonObject
            {
                ["documentId"] = x.d.DocumentId, ["uploadId"] = x.d.UploadId, ["revision"] = x.d.Revision, ["reason"] = x.Item2,
                ["claimCount"] = x.d.Document.PolicyPeriods.Sum(p => p.Claims.Count),
            })]),
            // "adjusted" keeps its name for consumers; with featuresView it is the verified-only snapshot.
            ["featuresView"] = VerifiedOnly,
            ["features"] = new JsonObject { ["adjusted"] = adjustedNode, ["reported"] = JsonNode.Parse(reported) },
            ["periods"] = adjustedNode["perPeriod"]!.DeepClone(),
            ["claims"] = claims,
            ["sourceAvailability"] = SourceAvailability(account.Documents.Select(d => (d.DocumentId, d.Document)).ToList()),
            ["periodSummaries"] = new JsonObject
            {
                ["granularity"] = "period",
                ["rows"] = PeriodSummaries.ToJson(summaryRows),
                ["metrics"] = summaryRows.Count == 0 ? null : PeriodSummaries.Metrics(summaryRows),
                // Brief 30 item 13: the rows cover verified documents only; say when documents (and their claims) are left out.
                ["complete"] = verified.Pending!.Count == 0,
                ["excludedDocuments"] = verified.Pending.Count,
            },
            ["lineage"] = new JsonObject
            {
                ["documents"] = account.Documents.Count,
                ["pendingReview"] = pending,
                ["reviewRate"] = account.Documents.Count == 0 ? 0 : Math.Round((double)account.Documents.Count(d => d.Routing?.Tier.ToString() == "Review") / account.Documents.Count, 4),
                ["valuationDates"] = new JsonArray([.. account.Documents.Select(d => d.Document.ValuationDate?.ToString("yyyy-MM-dd")).OfType<string>().Distinct().Order().Select(v => (JsonNode)v)]),
                // Brief 23 Answers 2: valuation dates the runs do not print, as stated by the submitter (R-32).
                ["valuationDatesStatedBySubmitter"] = new JsonArray([.. account.Documents.Where(d => d.Document.ValuationDateSource == "submission")
                    .Select(d => d.Document.ValuationDate?.ToString("yyyy-MM-dd")).OfType<string>().Distinct().Order().Select(v => (JsonNode)v)]),
                ["formatVersions"] = new JsonArray([.. account.Documents.Select(d => d.FormatRef).OfType<string>().Distinct().Order().Select(v => (JsonNode)v)]),
                ["extractors"] = new JsonObject(extractors.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                ["overrides"] = overrides,
            },
        };
    }

    /// <summary>
    /// Brief 25: a document's policy periods for the payload — identity (policy number, dates, carrier), the document's valuation
    /// date and its source, and the status of each identity field. Verified-only: a value that is not approved is null, with its
    /// status; an excluded document (rejected, or pending review) carries its reason.
    /// </summary>
    private static void AddPeriods(JsonArray rows, DocumentResult r, IReadOnlyDictionary<string, FieldStatus> statuses, string? excludedReason)
    {
        var doc = r.Document;
        for (var p = 0; p < doc.PolicyPeriods.Count; p++)
        {
            var period = doc.PolicyPeriods[p];
            var status = new JsonObject();
            JsonNode? Value(string path, object? value)
            {
                var s = statuses.GetValueOrDefault(path);
                if (s is not null && s.Status != FieldStates.Approved)
                {
                    status[path[(path.LastIndexOf('.') + 1)..]] = new JsonObject { ["status"] = s.Status, ["reason"] = s.Reason };
                    return null;
                }

                return value is null ? null : JsonValue.Create(value.ToString());
            }

            var prefix = $"policyPeriods[{p}]";
            rows.Add(new JsonObject
            {
                ["documentId"] = r.DocumentId, ["uploadId"] = r.UploadId, ["periodIndex"] = p, ["revision"] = r.Revision,
                ["policyNumber"] = Value($"{prefix}.policyNumber", period.PolicyNumber),
                ["effectiveDate"] = Value($"{prefix}.effectiveDate", period.EffectiveDate?.ToString("yyyy-MM-dd")),
                ["expirationDate"] = Value($"{prefix}.expirationDate", period.ExpirationDate?.ToString("yyyy-MM-dd")),
                ["periodLabel"] = period.PeriodLabel,
                ["carrierName"] = Value("carrierName", period.CarrierName ?? doc.CarrierName),
                ["valuationDate"] = Value("valuationDate", doc.ValuationDate?.ToString("yyyy-MM-dd")),
                ["valuationDateSource"] = doc.ValuationDateSource,
                ["detailLevel"] = period.DetailLevel.ToString(),
                ["noLossesStatement"] = period.NoLossesStatement,
                ["excludedReason"] = excludedReason,
                ["fieldStatus"] = status,
            });
        }
    }

    /// <summary>Most blocking fields listed in <c>readiness</c>; <c>blockingFieldCount</c> counts them all (a sum mismatch marks a column of every claim).</summary>
    public const int MaxBlockingFields = 200;

    /// <summary>
    /// Brief 24 step 2: <c>{ ready, blockingFields[], blockingFieldCount, rejectedDocuments[], pendingDocuments[] }</c>. Ready when
    /// at least one document counts, none waits for review, and no field of a counted document is unresolved (a hard rule still
    /// failing on it, or R-33 required and null). Rejected documents never block: they are excluded.
    /// </summary>
    public static JsonObject Readiness(Account account)
    {
        var blocking = new List<JsonNode>();
        var pending = new JsonArray();
        foreach (var d in account.Documents.OrderBy(d => d.DocumentId, StringComparer.Ordinal))
        {
            if (d.ReviewState is "pending" or "reopened")
            {
                pending.Add(new JsonObject { ["documentId"] = d.DocumentId, ["revision"] = d.Revision, ["reviewState"] = d.ReviewState });
                continue;
            }

            blocking.AddRange(FieldStatusEvaluator.Evaluate(d.Document, d.ReviewState, d.RuleOutcomes).Where(s => s.Value.Status == FieldStates.Unresolved)
                .Select(s => (JsonNode)new JsonObject { ["documentId"] = d.DocumentId, ["fieldPath"] = s.Key, ["status"] = s.Value.Status, ["reason"] = s.Value.Reason }));
        }

        return new JsonObject
        {
            ["ready"] = account.Documents.Count > 0 && pending.Count == 0 && blocking.Count == 0,
            ["blockingFields"] = new JsonArray([.. blocking.Take(MaxBlockingFields)]),
            ["blockingFieldCount"] = blocking.Count,
            ["rejectedDocuments"] = new JsonArray([.. (account.Excluded ?? []).OrderBy(d => d.DocumentId, StringComparer.Ordinal)
                .Select(d => (JsonNode)new JsonObject { ["documentId"] = d.DocumentId, ["revision"] = d.Revision, ["reason"] = "rejected" })]),
            ["pendingDocuments"] = pending,
        };
    }

    /// <summary>Sets a dotted path under a claim node to null, when its parent exists.</summary>
    private static void SetNull(JsonObject node, string path)
    {
        var parts = path.Split('.');
        var parent = node;
        foreach (var part in parts[..^1])
        {
            if (parent[part] is not JsonObject next)
            {
                return;
            }

            parent = next;
        }

        if (parent.ContainsKey(parts[^1]))
        {
            parent[parts[^1]] = null;
        }
    }

    private static IEnumerable<string> Absent(LossRunDocument document) =>
        new[] { ("indemnity", document.ComponentPresence?.Indemnity), ("medical", document.ComponentPresence?.Medical), ("expense", document.ComponentPresence?.Expense) }
            .Where(c => c.Item2 is { Present: false }).Select(c => c.Item1);

    /// <summary>
    /// Brief 19 Answers 1: per component a document does not print, the documents and the catalog metrics that use it
    /// ("computed without medical: not available from source"). Empty when every component is printed.
    /// </summary>
    private static JsonArray SourceAvailability(IReadOnlyList<(string DocumentId, LossRunDocument Document)> documents) =>
        [.. documents.SelectMany(d => Absent(d.Document).Select(c => (Component: c, d.DocumentId))).GroupBy(x => x.Component).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (JsonNode)new JsonObject
            {
                ["component"] = g.Key,
                ["documents"] = new JsonArray([.. g.Select(x => (JsonNode)x.DocumentId).Distinct()]),
                ["metrics"] = new JsonArray([.. Catalog.Embedded.Metrics.Where(m => m.InputFields.Any(f => f.StartsWith($"claim.{g.Key}.", StringComparison.Ordinal))).Select(m => (JsonNode)m.Name)]),
                ["note"] = $"{g.Key} not available from source: these metrics are computed without it",
            })];

    /// <summary>claim (every period lists claims), period (every period is summary-only, ADR 0017) or mixed.</summary>
    private static string Granularity(LossRunDocument document) =>
        document.PolicyPeriods.Count > 0 && document.PolicyPeriods.All(p => p.DetailLevel == DetailLevel.SummaryOnly) ? "period"
        : document.PolicyPeriods.Any(p => p.DetailLevel == DetailLevel.SummaryOnly) ? "mixed"
        : "claim";

    private static string? ViewOf(string? view) => view switch
    {
        null or "" or AccountBuilder.AsAdjusted => AccountBuilder.AsAdjusted,
        AccountBuilder.AsReported => AccountBuilder.AsReported,
        VerifiedOnly => VerifiedOnly,
        _ => null,
    };

    /// <summary>Exposures the documents do not carry, in dollars: <c>{ "payrollByYear": {"2023": 1200000}, "fteByYear": {...}, "lastDayWorked": "…" }</c>.</summary>
    private static AnalyticsContext Context(JsonElement c)
    {
        Dictionary<int, long>? Map(string name, long scale) => c.TryGetProperty(name, out var m) && m.ValueKind == JsonValueKind.Object
            ? m.EnumerateObject().ToDictionary(p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture), p => (long)Math.Round(p.Value.GetDecimal() * scale, MidpointRounding.AwayFromZero))
            : null;
        return new AnalyticsContext(null, c.TryGetProperty("valuationDate", out var v) ? v.GetString() : null, Map("payrollByYear", 100), Map("fteByYear", 1),
            c.TryGetProperty("lastDayWorked", out var l) ? l.GetString() : null);
    }

    private static ApiResult Raw(string json) => new(200, null, Encoding.UTF8.GetBytes(json), "application/json");
}
