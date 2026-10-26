using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LossRun.Analytics;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;
using LossRun.Formats;
using LossRun.Rules;
using Microsoft.Extensions.AI;

namespace LossRun.Chat;

/// <summary>Claim filters the model may pass to <c>query_claims</c> and <c>compute_metric</c>. None of them names a scope.</summary>
public sealed record ClaimFilter
{
    [Description("open (includes reopened: a reopened claim is open for every underwriting purpose), closed, or reopened (only the reopened ones).")]
    public string? Status { get; init; }

    [Description("indemnity (lost time) or medicalOnly.")]
    public string? ClaimType { get; init; }

    [Description("Policy year (year of the policy period's effective date).")]
    public int? PolicyYear { get; init; }

    public string? PolicyNumber { get; init; }

    [Description("Carrier name or part of it, case-insensitive.")]
    public string? Carrier { get; init; }

    public string? ClaimNumber { get; init; }

    [Description("Minimum total incurred in dollars (inclusive).")]
    public decimal? MinIncurred { get; init; }

    [Description("Maximum total incurred in dollars (inclusive).")]
    public decimal? MaxIncurred { get; init; }

    public bool? Litigated { get; init; }

    [Description("Cumulative-trauma claims only (true) or none (false), by the catalog's CT rule.")]
    public bool? CumulativeTrauma { get; init; }

    [Description("Two-letter state, e.g. CA.")]
    public string? Jurisdiction { get; init; }
}

/// <summary>What-if parameters for <c>compute_metric</c> (catalog.yaml scenario).</summary>
public sealed record MetricScenario
{
    [Description("Adjust open reserves by this percent, e.g. 10 or -20.")]
    public decimal? ReservePercent { get; init; }

    [Description("Cap each claim's incurred at this many dollars.")]
    public decimal? CapPerClaim { get; init; }

    [Description("Exclude claims whose incurred is above this many dollars.")]
    public decimal? ExcludeAbove { get; init; }

    public bool? IncludeNonCA { get; init; }
    public bool? IncludeCT { get; init; }
}

/// <summary>
/// Per-turn bookkeeping: citation refs handed to the model (S1, C1, M1 …), the tool trace as executed, metered usage.
/// Only refs registered here can become citations (<see cref="ChatService"/> validates the model's list against it).
/// </summary>
public sealed class TurnRecorder(Func<string, object, Task>? onEvent = null)
{
    private readonly Lock _gate = new();
    private int _s, _c, _m;

    public Dictionary<string, ChatCitation> Refs { get; } = new(StringComparer.Ordinal);
    public List<ChatToolCall> Trace { get; } = [];
    public List<UsageEvent> Usage { get; } = [];
    public int SearchQueries;
    public int Embeddings;
    public int ToolCalls;
    public int Chunks;

    /// <summary>Set when a per-turn cap stopped a tool (12 Answers: shown in "How this was answered").</summary>
    public string? CapNote;

    /// <summary>The cap that stopped the latest call (the trace note of that call).</summary>
    public string? LastCap;

    /// <summary>Counts a tool call; false (with <see cref="CapNote"/> set) once the turn has used its <paramref name="max"/> calls.</summary>
    public bool TryCall(int max)
    {
        lock (_gate)
        {
            if (ToolCalls >= max)
            {
                LastCap = $"tool-call cap reached ({max} per turn); answered from the results so far";
                CapNote ??= LastCap;
                return false;
            }

            ToolCalls++;
            return true;
        }
    }

    /// <summary>Reserves up to <paramref name="wanted"/> chunks of the turn's <paramref name="max"/>; the cap note is set when it cuts.</summary>
    public int TakeChunks(int wanted, int max)
    {
        lock (_gate)
        {
            var granted = Math.Max(0, Math.Min(wanted, max - Chunks));
            if (granted < wanted)
            {
                LastCap = $"chunk cap reached ({max} document chunks per turn)";
                CapNote ??= LastCap;
            }

            Chunks += granted;
            return granted;
        }
    }

    /// <summary>Every document id any tool returned this turn (the leakage check reads it).</summary>
    public HashSet<string> DocumentsReturned { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Brief 24 step 7 Answers 2: the last claims/metric result of the turn and the filter that produced it, kept in the ledger
    /// row's trace ("last-result") so the next turn can resolve "those", "of those" and "its" against it.
    /// </summary>
    public object? LastResult { get; set; }

    /// <summary>Brief 24 step 7: the reviewers' rationales explain_value returned this turn (the answer must quote them exactly).</summary>
    public HashSet<string> Rationales { get; } = new(StringComparer.Ordinal);

    /// <summary>Brief 24 step 5: every string and number the tools returned this turn (the fact guard checks names, dates and quotes against it).</summary>
    public List<string> ToolStrings { get; } = [];

    /// <summary>Notes an override's rationale as one the answer must quote exactly; returns the override.</summary>
    public StoredOverride Rationale(StoredOverride o)
    {
        if (!string.IsNullOrWhiteSpace(o.Rationale))
        {
            lock (_gate)
            {
                Rationales.Add(o.Rationale);
            }
        }

        return o;
    }

    /// <summary>Records what a tool returned to the model, then hands it back.</summary>
    public object Output(object result)
    {
        var strings = new List<string>();
        Collect(JsonSerializer.SerializeToNode(result), strings);
        lock (_gate)
        {
            ToolStrings.AddRange(strings);
        }

        return result;
    }

    private static void Collect(JsonNode? node, List<string> into)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (_, v) in o)
                {
                    Collect(v, into);
                }

                break;
            case JsonArray a:
                foreach (var v in a)
                {
                    Collect(v, into);
                }

                break;
            case JsonValue v:
                into.Add(v.ToJsonString().Trim('"') is var raw && v.TryGetValue<string>(out var s) ? s : raw);
                break;
        }
    }

    public string Add(char kind, ChatCitation citation)
    {
        lock (_gate)
        {
            var id = kind switch { 'S' => $"S{++_s}", 'C' => $"C{++_c}", _ => $"M{++_m}" };
            Refs[id] = citation with { Ref = id };
            if (citation.DocumentId is { } d)
            {
                DocumentsReturned.Add(d);
            }

            return id;
        }
    }

    public async Task TraceAsync(ChatToolCall call)
    {
        lock (_gate)
        {
            Trace.Add(call);
        }

        if (onEvent is not null)
        {
            await onEvent("tool", call).ConfigureAwait(false);
        }
    }

    public ICostMeter Meter => new RecorderMeter(this);

    private sealed class RecorderMeter(TurnRecorder recorder) : ICostMeter
    {
        public ValueTask RecordAsync(UsageEvent usage, CancellationToken cancellationToken = default)
        {
            lock (recorder._gate)
            {
                recorder.Usage.Add(usage);
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Brief 24 step 5: which tools a role may use. Applicants get no <c>explain_value</c> (reviewer names and rationales are
/// underwriting records) and no <c>reported</c> view; underwriting roles get everything. Enforced twice: the tool is not
/// offered to the model, and a call to it is refused and traced.
/// </summary>
public static class ToolPolicy
{
    public const string SearchDocuments = "search_documents";
    public const string QueryClaims = "query_claims";
    public const string ComputeMetric = "compute_metric";
    public const string ExplainValue = "explain_value";
    public const string GetReviewStatus = "get_review_status";

    public static readonly IReadOnlyList<string> All = [SearchDocuments, QueryClaims, ComputeMetric, ExplainValue, GetReviewStatus];

    /// <param name="role">Null (tests, tools built without a caller) is an underwriting role.</param>
    public static bool Allows(string? role, string tool) => role is null || Roles.CanRead(role) || tool != ExplainValue;

    public static bool AllowsReportedView(string? role) => role is null || Roles.CanRead(role);
}

/// <summary>
/// The server-side tools of a chat turn (ADR 0002; brief 24 step 5 adds explain_value and get_review_status), bound to one
/// scope and one role. The scope is a constructor argument the HTTP layer resolved from the authenticated request; it is
/// pushed into every query (the search filter, the document list the claims load from) and re-checked on every result
/// before it is returned to the model. The role picks the tools (<see cref="ToolPolicy"/>).
/// </summary>
public sealed class ChatTools(ChatScope scope, IReadOnlyList<ScopedDocument> documents, IChatSearch search, IEmbeddingService embeddings, IRecordStore records,
    TurnRecorder recorder, string turnId, string searchRegion, string? role = null)
{
    public const int MaxSearchResults = 6;

    /// <summary>Per-turn caps (12 Answers): tool calls and document chunks returned to the model.</summary>
    public const int MaxToolCalls = 6;
    public const int MaxChunksPerTurn = 12;

    /// <summary>A capped call is still traced (with the cap as its note) so "How this was answered" shows where the turn stopped.</summary>
    private async Task<object> CapReachedAsync(string tool, object arguments)
    {
        await recorder.TraceAsync(new ChatToolCall(tool, JsonSerializer.Serialize(arguments), 0, 0, Note: recorder.LastCap)).ConfigureAwait(false);
        return new { results = Array.Empty<object>(), note = recorder.LastCap + ". Answer with what you have." };
    }
    public const int SnippetChars = 1800;
    public const int MaxClaimRows = 25;

    private readonly HashSet<string> _allowed = documents.Where(scope.Admits).Select(d => d.DocumentId).ToHashSet(StringComparer.Ordinal);
    private Loaded? _loaded;

    /// <summary>The tools the caller's role may use (the others are not offered to the model).</summary>
    public IList<AITool> AsAITools()
    {
        IList<AITool> all =
        [
            AIFunctionFactory.Create(SearchDocumentsAsync, ToolPolicy.SearchDocuments,
                "Search the text of the documents in scope (loss-run pages, payroll reports, letters …). Returns page chunks with refs S1, S2 … to cite."),
            AIFunctionFactory.Create(QueryClaimsAsync, ToolPolicy.QueryClaims,
                "Query the extracted claims in scope with filters. Returns matching claim rows (refs C1, C2 … to cite) with each row's review state and the fields not yet approved, and totals over all matches: count, open (open includes reopened), incurred, paid, reserve, and per component (indemnity, medical, expense) paid, reserve and incurred."),
            AIFunctionFactory.Create(ComputeMetricAsync, ToolPolicy.ComputeMetric,
                "Compute one analytics-catalog metric over the claims in scope, optionally filtered or under a what-if scenario. Returns the value with ref M1, M2 … to cite. Metrics: "
                + string.Join("; ", Catalog.Embedded.Metrics.Select(m => $"{m.Name} ({m.Label})"))),
            AIFunctionFactory.Create(ExplainValueAsync, ToolPolicy.ExplainValue,
                "Explain corrections. With a claim number: the value as the loss run printed it (ref S… to the unmodified page), its current value and review status (ref C…), and every correction with the reviewer, role, time, revision and the reviewer's rationale verbatim. Without a claim number: every correction in scope, newest first (at most 50, with a count of the rest). Use it when asked why a value differs, who changed what, or what has been changed."),
            AIFunctionFactory.Create(GetReviewStatusAsync, ToolPolicy.GetReviewStatus,
                "Review status of the documents in scope: which are approved, pending review or straight through, their revisions, how many fields are unresolved, and whether the quote is ready."),
        ];
        return [.. all.Where(t => ToolPolicy.Allows(role, t.Name))];
    }

    /// <summary>A call the role may not make: refused, and the refusal is in the trace ("How this was answered").</summary>
    private async Task<object?> DeniedAsync(string tool, object arguments, string? view = null)
    {
        var reported = view == AccountBuilder.AsReported && !ToolPolicy.AllowsReportedView(role);
        if (ToolPolicy.Allows(role, tool) && !reported)
        {
            return null;
        }

        var note = reported ? $"the reported view is not available to {role}" : $"{tool} is not available to {role}";
        await recorder.TraceAsync(new ChatToolCall(tool, JsonSerializer.Serialize(arguments), 0, 0, Note: $"denied by tool policy: {note}")).ConfigureAwait(false);
        return new { error = $"Not allowed: {note}." };
    }

    // ------------------------------------------------------------------ search_documents

    [Description("Searches the documents in scope.")]
    public async Task<object> SearchDocumentsAsync(
        [Description("What to look for, in words (e.g. 'payroll report 2023', 'broker letter request').")] string query,
        [Description("How many chunks (1–6).")] int top = 4,
        [Description("Also search documents a reviewer rejected (only when the user asks about history or rejected documents).")] bool includeRejected = false,
        CancellationToken cancellationToken = default)
    {
        if (await DeniedAsync(ToolPolicy.SearchDocuments, new { query, top }).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        if (!recorder.TryCall(MaxToolCalls))
        {
            return await CapReachedAsync("search_documents", new { query, top }).ConfigureAwait(false);
        }

        var stopwatch = Stopwatch.StartNew();
        top = recorder.TakeChunks(Math.Clamp(top, 1, MaxSearchResults), MaxChunksPerTurn);
        if (top == 0)
        {
            return await CapReachedAsync("search_documents", new { query }).ConfigureAwait(false);
        }

        var vector = (await embeddings.EmbedAsync(query, turnId, turnId, cancellationToken).ConfigureAwait(false)).Vector;
        Interlocked.Increment(ref recorder.Embeddings);
        Interlocked.Increment(ref recorder.SearchQueries);
        await recorder.Meter.RecordAsync(new UsageEvent(turnId, turnId, Ingestion.Metering.CostCalculator.ChatStage, "AISearch", "semanticQueries", 1, "query", null, null, searchRegion,
            DateTimeOffset.UtcNow, $"{turnId}-q{recorder.SearchQueries}"), cancellationToken).ConfigureAwait(false);

        var hits = await search.SearchAsync(scope, query, vector, top, cancellationToken, includeRejected).ConfigureAwait(false);
        // Post-filter: the index is trusted, but not with scope. Brief 30 item 8: nor with review state — a loss run the record store no longer
        // lists in scope (rejected) is out unless asked for, whatever the chunk says (an index written before the decision).
        var live = documents.Select(d => d.DocumentId).ToHashSet(StringComparer.Ordinal);
        bool Rejected(ChatChunk c) => c.ReviewStatus is ChatReviewStatus.Rejected or ChatReviewStatus.Superseded || c.Classification == "LossRun" && !live.Contains(c.DocumentId);
        var admitted = hits.Where(h => scope.Admits(h.Chunk, includeRejected) && (includeRejected || !Rejected(h.Chunk))).ToList();
        var results = admitted.Select(h =>
        {
            var c = h.Chunk;
            var rejected = Rejected(c);
            var label = (c.Page is { } p ? $"{c.FileName} · p.{p}" : c.Sheet is { } s ? $"{c.FileName} · {s}" : c.FileName) + (rejected ? " (rejected document)" : "");
            var id = recorder.Add('S', new ChatCitation("", "page", label, c.DocumentId, c.Page, c.Sheet, FileName: c.FileName, QuoteId: c.QuoteId));
            return new
            {
                @ref = id, fileName = c.FileName, page = c.Page, sheet = c.Sheet, classification = c.Classification, carrier = c.Carrier,
                text = c.Markdown.Length > SnippetChars ? c.Markdown[..SnippetChars] + " …" : c.Markdown,
                fromRejectedDocument = rejected ? true : (bool?)null,
            };
        }).ToList();

        await recorder.TraceAsync(new ChatToolCall("search_documents", JsonSerializer.Serialize(new { query, top }), results.Count, stopwatch.ElapsedMilliseconds,
            hits.Count - admitted.Count, hits.Count == admitted.Count ? null : "results outside the scope were dropped")).ConfigureAwait(false);
        return recorder.Output(new { results, note = results.Count == 0 ? "No text in the documents in scope matches." : null });
    }

    // ------------------------------------------------------------------ query_claims

    [Description("Queries claims in scope.")]
    public async Task<object> QueryClaimsAsync(
        [Description("Filters; all optional.")] ClaimFilter? filter = null,
        [Description("incurred_desc (default), incurred_asc, date_of_injury, date_reported.")] string? orderBy = null,
        [Description("Rows to return (1–25); totals always cover every match.")] int limit = 10,
        [Description("adjusted (after underwriter overrides, default) or reported (as the carrier printed).")] string? view = null,
        CancellationToken cancellationToken = default)
    {
        if (await DeniedAsync(ToolPolicy.QueryClaims, new { filter, orderBy, limit, view }, view).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        if (!recorder.TryCall(MaxToolCalls))
        {
            return await CapReachedAsync("query_claims", new { filter, orderBy, limit, view }).ConfigureAwait(false);
        }

        var stopwatch = Stopwatch.StartNew();
        var loaded = await LoadAsync(view, cancellationToken).ConfigureAwait(false);
        var matches = Filter(loaded, filter).ToList();
        IEnumerable<AnalyticsClaim> ordered = orderBy switch
        {
            "incurred_asc" => matches.OrderBy(Incurred),
            "date_of_injury" => matches.OrderBy(c => c.DateOfInjury, StringComparer.Ordinal),
            "date_reported" => matches.OrderBy(c => c.DateReported, StringComparer.Ordinal),
            _ => matches.OrderByDescending(Incurred),
        };
        var rows = ordered.Take(Math.Clamp(limit, 1, MaxClaimRows)).Select(c =>
        {
            var doc = loaded.Documents[DocumentOf(c.Id)];
            var (p, i) = Position(c.Id);
            var path = $"policyPeriods[{p}].claims[{i}]";
            var page = doc.Document.Lineage?.GetValueOrDefault(path + ".claimNumber")?.Page;
            var file = loaded.FileNames.GetValueOrDefault(doc.DocumentId) ?? doc.Document.CarrierName ?? doc.DocumentId;
            var id = recorder.Add('C', new ChatCitation("", "claim", $"{c.ClaimNumber ?? "claim"} · {file}{(page is { } pg ? $" · p.{pg}" : "")}", doc.DocumentId, page,
                FieldPath: path + ".total.incurred", ClaimId: c.Id, FileName: file, QuoteId: loaded.Quotes.GetValueOrDefault(doc.DocumentId)));
            return new
            {
                @ref = id, claimNumber = c.ClaimNumber, carrier = doc.Document.CarrierName, policyNumber = c.PolicyNumber, policyYear = c.PolicyYear,
                dateOfInjury = c.DateOfInjury, dateReported = c.DateReported, dateClosed = c.DateClosed, status = c.Status, claimType = c.ClaimType,
                incurred = Dollars(Incurred(c)), paid = Dollars(Paid(c)), reserve = Dollars(Incurred(c) - Paid(c)),
                indemnityIncurred = Dollars(c.IndemnityPaidCents + c.IndemnityReserveCents), medicalIncurred = Dollars(c.MedicalPaidCents + c.MedicalReserveCents),
                expenseIncurred = Dollars(c.ExpensePaidCents + c.ExpenseReserveCents), litigated = c.Litigation, cumulativeTrauma = FeatureEngine.IsCumulativeTrauma(c),
                jurisdiction = c.Jurisdiction,
                // Brief 24 step 5: say "pending review" rather than assert a value nobody approved yet.
                reviewState = ReviewStateOf(doc), fieldStatus = OpenFields(loaded, doc, path + "."),
            };
        }).ToList();

        var totals = new
        {
            claims = matches.Count,
            open = matches.Count(c => c.Status is "open" or "reopened"),
            incurred = Dollars(matches.Sum(Incurred)),
            paid = Dollars(matches.Sum(Paid)),
            reserve = Dollars(matches.Sum(c => Incurred(c) - Paid(c))),
            // Brief 24 step 7 Answers 3: per-component sums, so "total indemnity paid" or "medical incurred" is read here, not guessed.
            indemnityPaid = Dollars(matches.Sum(c => c.IndemnityPaidCents)), indemnityReserve = Dollars(matches.Sum(c => c.IndemnityReserveCents)),
            indemnityIncurred = Dollars(matches.Sum(c => c.IndemnityPaidCents + c.IndemnityReserveCents)),
            medicalPaid = Dollars(matches.Sum(c => c.MedicalPaidCents)), medicalReserve = Dollars(matches.Sum(c => c.MedicalReserveCents)),
            medicalIncurred = Dollars(matches.Sum(c => c.MedicalPaidCents + c.MedicalReserveCents)),
            expensePaid = Dollars(matches.Sum(c => c.ExpensePaidCents)), expenseReserve = Dollars(matches.Sum(c => c.ExpenseReserveCents)),
            expenseIncurred = Dollars(matches.Sum(c => c.ExpensePaidCents + c.ExpenseReserveCents)),
            documents = matches.Select(c => DocumentOf(c.Id)).Distinct().Count(),
        };
        recorder.LastResult = new
        {
            tool = "query_claims", filter, orderBy, view = loaded.View, matches = matches.Count,
            rows = rows.Select(r => JsonSerializer.SerializeToNode(r)).Select(n => new
            {
                claimNumber = n?["claimNumber"]?.ToString(), carrier = n?["carrier"]?.ToString(), status = n?["status"]?.ToString(),
                policyYear = n?["policyYear"]?.ToString(), incurred = n?["incurred"]?.ToString(),
            }),
        };
        await recorder.TraceAsync(new ChatToolCall("query_claims", JsonSerializer.Serialize(new { filter, orderBy, limit, view }), rows.Count, stopwatch.ElapsedMilliseconds,
            loaded.Dropped)).ConfigureAwait(false);
        return recorder.Output(new { totals, rows, view = loaded.View, note = loaded.Note });
    }

    // ------------------------------------------------------------------ compute_metric

    [Description("Computes a catalog metric over claims in scope.")]
    public async Task<object> ComputeMetricAsync(
        [Description("Catalog metric name, e.g. incurred_total, open_claim_count, ct_share, severity_all.")] string metric,
        [Description("Restrict the claims first; optional.")] ClaimFilter? filter = null,
        [Description("What-if; optional.")] MetricScenario? scenario = null,
        [Description("adjusted (default) or reported.")] string? view = null,
        CancellationToken cancellationToken = default)
    {
        if (await DeniedAsync(ToolPolicy.ComputeMetric, new { metric, filter, scenario, view }, view).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        if (!recorder.TryCall(MaxToolCalls))
        {
            return await CapReachedAsync("compute_metric", new { metric, filter, scenario, view }).ConfigureAwait(false);
        }

        var stopwatch = Stopwatch.StartNew();
        var def = Catalog.Embedded.Metrics.FirstOrDefault(m => m.Name == metric);
        if (def is null)
        {
            await recorder.TraceAsync(new ChatToolCall("compute_metric", JsonSerializer.Serialize(new { metric }), 0, stopwatch.ElapsedMilliseconds, Note: "unknown metric"))
                .ConfigureAwait(false);
            return new { error = $"Unknown metric '{metric}'. Use one of: {string.Join(", ", Catalog.Embedded.Metrics.Select(m => m.Name))}." };
        }

        var loaded = await LoadAsync(view, cancellationToken).ConfigureAwait(false);
        var keep = Filter(loaded, filter).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var s = new Scenario
        {
            ExcludeClaimIds = [.. loaded.Input.Claims.Select(c => c.Id).Where(id => !keep.Contains(id))],
            ReserveFactorBps = (int)Math.Round((scenario?.ReservePercent ?? 0) * 100),
            CapPerClaimCents = scenario?.CapPerClaim is { } cap ? (long)Math.Round(cap * 100) : null,
            ExcludeCatastrophicAboveCents = scenario?.ExcludeAbove is { } ex ? (long)Math.Round(ex * 100) : null,
            IncludeNonCA = scenario?.IncludeNonCA ?? true,
            IncludeCT = scenario?.IncludeCT ?? true,
        };
        var snapshot = FeatureEngine.Compute(loaded.Input, s.IsBaseline ? null : s, loaded.View);
        var m = snapshot[metric];
        var value = m.Value is { } v ? v / (decimal)Math.Pow(10, m.Scale) : (decimal?)null;
        var id = recorder.Add('M', new ChatCitation("", "metric", $"{def.Label} · catalog {Catalog.Embedded.Version}", Metric: metric));
        recorder.LastResult = new { tool = "compute_metric", metric, filter, scenario, view = loaded.View, value };
        await recorder.TraceAsync(new ChatToolCall("compute_metric", JsonSerializer.Serialize(new { metric, filter, scenario, view }), 1, stopwatch.ElapsedMilliseconds,
            loaded.Dropped)).ConfigureAwait(false);
        return recorder.Output(new
        {
            @ref = id, metric, label = def.Label, unit = m.Unit, value, text = Text(value, m.Unit), notMeasurable = m.NotMeasurable,
            claimsIncluded = snapshot.Included, contributors = m.Contributors?.Count ?? snapshot.Included,
            breakdown = m.Breakdown?.Take(10).Select(b => new { key = b.Key, value = b.Value is { } bv ? bv / (decimal)Math.Pow(10, m.Scale) : (decimal?)null }),
            catalogVersion = Catalog.Embedded.Version, view = loaded.View,
        });
    }

    // ------------------------------------------------------------------ explain_value (brief 24 step 5)

    /// <summary>At most this many claims per explain_value call (a claim number printed on several runs).</summary>
    public const int MaxExplainedClaims = 3;

    [Description("Explains a claim value and its corrections.")]
    public async Task<object> ExplainValueAsync(
        [Description("The claim number, as printed. Omit to list every correction in scope.")] string? claimNumber = null,
        [Description("The claim's field, e.g. total.incurred, medical.paid, indemnity.reserve, claimantName, dateOfInjury. Omit for every corrected field.")] string? field = null,
        CancellationToken cancellationToken = default)
    {
        if (await DeniedAsync(ToolPolicy.ExplainValue, new { claimNumber, field }).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        if (!recorder.TryCall(MaxToolCalls))
        {
            return await CapReachedAsync("explain_value", new { claimNumber, field }).ConfigureAwait(false);
        }

        var stopwatch = Stopwatch.StartNew();
        var loaded = await LoadAsync(AccountBuilder.AsAdjusted, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(claimNumber))
        {
            return await ListCorrectionsAsync(loaded, field, stopwatch).ConfigureAwait(false);
        }

        recorder.LastResult = new { tool = "explain_value", claimNumber, field };
        var claims = loaded.Input.Claims.Where(c => string.Equals(c.ClaimNumber, claimNumber, StringComparison.OrdinalIgnoreCase)).Take(MaxExplainedClaims).ToList();
        var explained = new List<object>();
        foreach (var c in claims)
        {
            var doc = loaded.Documents[DocumentOf(c.Id)];
            var (p, i) = Position(c.Id);
            var prefix = $"policyPeriods[{p}].claims[{i}].";
            var overrides = loaded.Overrides.GetValueOrDefault(doc.DocumentId) ?? [];
            var fields = field is { Length: > 0 } f ? [prefix + f.TrimStart('.')]
                : overrides.Where(o => o.FieldPath.StartsWith(prefix, StringComparison.Ordinal)).Select(o => o.FieldPath).Distinct(StringComparer.Ordinal).ToList();
            if (fields.Count == 0)
            {
                fields = [prefix + "total.incurred"];
            }

            var node = JsonSerializer.SerializeToNode(doc.Document, CanonicalJson.Options)!.AsObject();
            var statuses = Statuses(loaded, doc);
            var file = loaded.FileNames.GetValueOrDefault(doc.DocumentId) ?? doc.Document.CarrierName ?? doc.DocumentId;
            foreach (var path in fields)
            {
                var chain = overrides.Where(o => o.FieldPath == path).OrderBy(o => o.AtUtc).ToList();
                var current = DocumentEdits.Text(JsonPath.Get(node, path));
                var lineage = doc.Document.Lineage?.GetValueOrDefault(path);
                // The printed value's evidence is the page or sheet chunk in the index, which no decision ever rewrites.
                var source = lineage is { Page: not null } or { Sheet: not null }
                    ? recorder.Add('S', new ChatCitation("", "page", lineage.Page is { } pg ? $"{file} · p.{pg}" : $"{file} · {lineage.Sheet}", doc.DocumentId, lineage.Page, lineage.Sheet,
                        FileName: file, QuoteId: loaded.Quotes.GetValueOrDefault(doc.DocumentId)))
                    : null;
                var currentRef = recorder.Add('C', new ChatCitation("", "claim", $"{c.ClaimNumber} · {path[prefix.Length..]} · {file}", doc.DocumentId, lineage?.Page, FieldPath: path,
                    ClaimId: c.Id, FileName: file, QuoteId: loaded.Quotes.GetValueOrDefault(doc.DocumentId)));
                var status = statuses.GetValueOrDefault(path);
                explained.Add(new
                {
                    claimNumber = c.ClaimNumber, field = path[prefix.Length..], fieldPath = path, documentId = doc.DocumentId, file,
                    original = new
                    {
                        // Brief 30 item 30/24: corrected from nothing → the loss run did not print it (never the current value as the original).
                        value = chain.Count == 0 ? current : chain[0].OldValue ?? "originally not present",
                        present = chain.Count == 0 ? current is not null : chain[0].OldValue is not null,
                        @ref = source, page = lineage?.Page, sheet = lineage?.Sheet, cell = lineage?.Cell,
                        readBy = chain.Count > 0 ? "the carrier format (before the corrections below)" : lineage?.Extractor,
                        confidence = chain.Count > 0 ? null : lineage?.Confidence,
                    },
                    current = new { value = current, status = status?.Status ?? FieldStates.Approved, reason = status?.Reason, revision = doc.Revision, @ref = currentRef },
                    corrections = chain.Select(o => recorder.Rationale(o)).Select(o => new
                    {
                        id = o.Id, from = o.OldValue, to = o.NewValue, reviewer = o.UserId, role = o.Role, at = o.AtUtc.ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture),
                        rationale = o.Rationale, revision = o.Revision, supersedes = o.SupersedesOverrideId,
                    }),
                });
            }
        }

        await recorder.TraceAsync(new ChatToolCall("explain_value", JsonSerializer.Serialize(new { claimNumber, field }), explained.Count, stopwatch.ElapsedMilliseconds,
            loaded.Dropped)).ConfigureAwait(false);
        return recorder.Output(new
        {
            values = explained,
            note = claims.Count == 0 ? $"No claim {claimNumber} in the documents in scope."
                : "Quote a rationale verbatim, with its reviewer, role and date. A value with no corrections is as the loss run printed it.",
        });
    }

    /// <summary>At most this many corrections when explain_value lists the scope's corrections.</summary>
    public const int MaxListedCorrections = 50;

    /// <summary>
    /// Brief 24 step 7 Answers 1: explain_value without a claim number — every correction in scope, newest first, capped at
    /// <see cref="MaxListedCorrections"/> with a count of the rest. Each with its claim, field, values, reviewer, role, time and verbatim rationale.
    /// </summary>
    private async Task<object> ListCorrectionsAsync(Loaded loaded, string? field, Stopwatch stopwatch)
    {
        var claimByPrefix = loaded.Input.Claims.ToDictionary(c => c.Id, c => c.ClaimNumber, StringComparer.Ordinal);
        var all = loaded.Overrides.SelectMany(kv => kv.Value.Select(o => (DocumentId: kv.Key, Override: o)))
            .Where(x => field is not { Length: > 0 } f || x.Override.FieldPath.EndsWith("." + f.TrimStart('.'), StringComparison.Ordinal))
            .OrderByDescending(x => x.Override.AtUtc).ThenByDescending(x => x.Override.Id, StringComparer.Ordinal).ToList();
        var listed = all.Take(MaxListedCorrections).Select(x =>
        {
            var o = recorder.Rationale(x.Override);
            var m = System.Text.RegularExpressions.Regex.Match(o.FieldPath, @"^policyPeriods\[(\d+)\]\.claims\[(\d+)\]\.(.+)$");
            var claimNumber = m.Success ? claimByPrefix.GetValueOrDefault($"{x.DocumentId}/{m.Groups[1].Value}/{m.Groups[2].Value}") : null;
            var file = loaded.FileNames.GetValueOrDefault(x.DocumentId) ?? x.DocumentId;
            var id = recorder.Add('C', new ChatCitation("", "claim", $"{claimNumber ?? "document"} · {(m.Success ? m.Groups[3].Value : o.FieldPath)} · {file}", x.DocumentId,
                FieldPath: o.FieldPath, ClaimId: m.Success ? $"{x.DocumentId}/{m.Groups[1].Value}/{m.Groups[2].Value}" : null, FileName: file,
                QuoteId: loaded.Quotes.GetValueOrDefault(x.DocumentId)));
            return new
            {
                @ref = id, claimNumber, field = m.Success ? m.Groups[3].Value : o.FieldPath, documentId = x.DocumentId, file, from = o.OldValue, to = o.NewValue,
                reviewer = o.UserId, role = o.Role, at = o.AtUtc.ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture), rationale = o.Rationale,
                revision = o.Revision, supersedes = o.SupersedesOverrideId,
            };
        }).ToList();
        recorder.LastResult = new { tool = "explain_value", listed = listed.Count, claims = listed.Select(l => l.claimNumber).Distinct() };
        await recorder.TraceAsync(new ChatToolCall("explain_value", JsonSerializer.Serialize(new { claimNumber = (string?)null, field }), listed.Count, stopwatch.ElapsedMilliseconds,
            loaded.Dropped)).ConfigureAwait(false);
        return recorder.Output(new
        {
            corrections = listed, more = Math.Max(0, all.Count - listed.Count),
            note = all.Count == 0 ? "No value in scope has been corrected: every value is as the loss runs printed it."
                : "Newest first. Quote a rationale verbatim, with its reviewer, role and date.",
        });
    }

    // ------------------------------------------------------------------ get_review_status (brief 24 step 5)

    [Description("Review status of the documents in scope.")]
    public async Task<object> GetReviewStatusAsync(CancellationToken cancellationToken = default)
    {
        if (await DeniedAsync(ToolPolicy.GetReviewStatus, new { }).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        if (!recorder.TryCall(MaxToolCalls))
        {
            return await CapReachedAsync("get_review_status", new { }).ConfigureAwait(false);
        }

        var stopwatch = Stopwatch.StartNew();
        var loaded = await LoadAsync(AccountBuilder.AsAdjusted, cancellationToken).ConfigureAwait(false);
        var rows = loaded.Documents.Values.OrderBy(d => d.DocumentId, StringComparer.Ordinal).Select(d =>
        {
            var statuses = Statuses(loaded, d);
            var file = loaded.FileNames.GetValueOrDefault(d.DocumentId) ?? d.Document.CarrierName ?? d.DocumentId;
            var id = recorder.Add('C', new ChatCitation("", "review", $"{file} · review status", d.DocumentId, FileName: file, QuoteId: loaded.Quotes.GetValueOrDefault(d.DocumentId)));
            return new
            {
                @ref = id, documentId = d.DocumentId, file, carrier = d.Document.CarrierName, reviewState = ReviewStateOf(d), revision = d.Revision,
                fields = statuses.Values.GroupBy(s => s.Status).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
                unresolved = statuses.Where(s => s.Value.Status == FieldStates.Unresolved).Take(5).Select(s => new { fieldPath = s.Key, reason = s.Value.Reason }),
            };
        }).ToList();
        var pending = rows.Count(r => r.reviewState is "pending" or "reopened");
        var unresolved = rows.Sum(r => r.fields.GetValueOrDefault(FieldStates.Unresolved));
        await recorder.TraceAsync(new ChatToolCall("get_review_status", "{}", rows.Count, stopwatch.ElapsedMilliseconds, loaded.Dropped)).ConfigureAwait(false);
        return recorder.Output(new
        {
            documents = rows, pendingDocuments = pending, unresolvedFields = unresolved,
            ready = rows.Count > 0 && pending == 0 && unresolved == 0,
            note = "Rejected documents are excluded from this conversation. Values of a document pending review are not approved yet.",
        });
    }

    private static string ReviewStateOf(DocumentResult d) => d.ReviewState ?? "straight-through";

    private readonly Dictionary<string, IReadOnlyDictionary<string, FieldStatus>> _statuses = new(StringComparer.Ordinal);

    private IReadOnlyDictionary<string, FieldStatus> Statuses(Loaded loaded, DocumentResult d)
    {
        if (!_statuses.TryGetValue(d.DocumentId, out var s))
        {
            _statuses[d.DocumentId] = s = FieldStatusEvaluator.Evaluate(d.Document, d.ReviewState, d.RuleOutcomes);
        }

        return s;
    }

    /// <summary>The fields of one claim that are not approved, by path relative to the claim (empty when all are).</summary>
    private Dictionary<string, string> OpenFields(Loaded loaded, DocumentResult d, string prefix) =>
        Statuses(loaded, d).Where(s => s.Key.StartsWith(prefix, StringComparison.Ordinal) && s.Value.Status != FieldStates.Approved)
            .ToDictionary(s => s.Key[prefix.Length..], s => s.Value.Reason is { } r ? $"{s.Value.Status} ({r})" : s.Value.Status, StringComparer.Ordinal);

    // ------------------------------------------------------------------ loading (scope-bound)

    private sealed record Loaded(string View, AnalyticsInput Input, Dictionary<string, DocumentResult> Documents, Dictionary<string, string> FileNames,
        Dictionary<string, string?> Quotes, int Dropped, string? Note, Dictionary<string, IReadOnlyList<StoredOverride>> Overrides);

    private async Task<Loaded> LoadAsync(string? view, CancellationToken cancellationToken)
    {
        var v = view == AccountBuilder.AsReported ? AccountBuilder.AsReported : AccountBuilder.AsAdjusted;
        if (_loaded?.View == v)
        {
            return _loaded;
        }

        var results = new Dictionary<string, DocumentResult>(StringComparer.Ordinal);
        var accounts = new List<AccountDocument>();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var quotes = new Dictionary<string, string?>(StringComparer.Ordinal);
        var overridesOf = new Dictionary<string, IReadOnlyList<StoredOverride>>(StringComparer.Ordinal);
        var claims = 0;
        string? note = null;
        // One cached batch (brief 13: per document it was result + overrides + manifest, ~4.6 s on a 34-document quote).
        var scoped = documents.Where(d => _allowed.Contains(d.DocumentId)).ToList();
        var loaded = (await ResultCache.GetAsync(records, [.. scoped.Select(d => (d.DocumentId, d.Revision))], cancellationToken).ConfigureAwait(false))
            .ToDictionary(r => r.Result.DocumentId, StringComparer.Ordinal);
        foreach (var d in scoped)
        {
            if (!loaded.TryGetValue(d.DocumentId, out var hit))
            {
                continue;
            }

            var result = hit.Result;

            claims += result.Document.PolicyPeriods.Sum(p => p.Claims.Count);
            if (claims > QuoteLimits.MaxClaims)
            {
                note = $"Only the first {results.Count} documents were read ({QuoteLimits.MaxClaims}-claim limit); narrow the scope for complete totals.";
                break;
            }

            var overrides = hit.Overrides;
            overridesOf[d.DocumentId] = overrides;
            results[d.DocumentId] = result;
            quotes[d.DocumentId] = d.QuoteId;
            accounts.Add(new AccountDocument(d.DocumentId, result.Document,
                [.. overrides.Select(o => new OverrideRecord(o.Id, o.DocumentId, o.FieldPath, o.OldValue, o.NewValue, o.Rationale, o.UserId, o.AtUtc))]));
            if (d.FileName is { } file)
            {
                files[d.DocumentId] = file;
            }
            else if (await records.GetManifestAsync(d.UploadId, cancellationToken).ConfigureAwait(false) is { } manifest
                && manifest.Items.FirstOrDefault(i => i.DocumentId == d.DocumentId) is { } item)
            {
                files[d.DocumentId] = item.FileName; // Blob mode: no file name in the scope row
            }
        }

        var input = AccountBuilder.Build(scope.QuoteId, accounts, v, scope.QuoteId is { } q ? Context(await records.GetQuoteContextAsync(q, cancellationToken).ConfigureAwait(false)) : null);
        // Post-filter: every claim id is {documentId}/{period}/{claim}; anything outside the scope's documents is dropped.
        var inScope = input.Claims.Where(c => _allowed.Contains(DocumentOf(c.Id))).ToList();
        var dropped = input.Claims.Count - inScope.Count;
        _loaded = new Loaded(v, input with { Claims = inScope }, results, files, quotes, dropped, note, overridesOf);
        return _loaded;
    }

    /// <summary>The quote's submission context as engine input (same rule as QuoteAnalytics.ToAnalytics).</summary>
    private static AnalyticsContext? Context(QuoteContext? q)
    {
        if (q is null)
        {
            return null;
        }

        static Dictionary<int, long>? ByYear(IReadOnlyList<PeriodExposure>? periods, decimal scale) => periods is { Count: > 0 }
            ? periods.GroupBy(p => p.EffectiveDate.Year).ToDictionary(g => g.Key, g => (long)Math.Round(g.Sum(p => p.Amount) * scale, MidpointRounding.AwayFromZero))
            : null;
        return new AnalyticsContext(q.QuoteId, null, ByYear(q.PayrollByPeriod, 100), ByYear(q.FteByPeriod, 1), q.LastDayWorked?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private static IEnumerable<AnalyticsClaim> Filter(Loaded loaded, ClaimFilter? f)
    {
        IEnumerable<AnalyticsClaim> q = loaded.Input.Claims.Where(c => c.LineOfBusiness is null || c.LineOfBusiness.Trim().Equals("WC", StringComparison.OrdinalIgnoreCase));
        if (f is null)
        {
            return q;
        }

        if (f.Status is { } s)
        {
            // Brief 24 step 7 Answers 2: "open" includes reopened, as open_claim_count does (catalog.yaml); "open-or-reopened" stays an alias.
            q = s.Equals("open", StringComparison.OrdinalIgnoreCase) || s == "open-or-reopened" ? q.Where(c => c.Status is "open" or "reopened")
                : q.Where(c => string.Equals(c.Status, s, StringComparison.OrdinalIgnoreCase));
        }

        if (f.ClaimType is { } t) q = q.Where(c => string.Equals(c.ClaimType, t, StringComparison.OrdinalIgnoreCase));
        if (f.PolicyYear is { } y) q = q.Where(c => c.PolicyYear == y);
        if (f.PolicyNumber is { } pn) q = q.Where(c => string.Equals(c.PolicyNumber, pn, StringComparison.OrdinalIgnoreCase));
        if (f.ClaimNumber is { } cn) q = q.Where(c => string.Equals(c.ClaimNumber, cn, StringComparison.OrdinalIgnoreCase));
        if (f.Carrier is { } carrier)
        {
            q = q.Where(c => loaded.Documents[DocumentOf(c.Id)].Document.CarrierName?.Contains(carrier, StringComparison.OrdinalIgnoreCase) == true);
        }

        if (f.MinIncurred is { } min) q = q.Where(c => Incurred(c) >= (long)Math.Round(min * 100));
        if (f.MaxIncurred is { } max) q = q.Where(c => Incurred(c) <= (long)Math.Round(max * 100));
        if (f.Litigated is { } lit) q = q.Where(c => (c.Litigation == true) == lit);
        if (f.CumulativeTrauma is { } ct) q = q.Where(c => FeatureEngine.IsCumulativeTrauma(c) == ct);
        if (f.Jurisdiction is { } j) q = q.Where(c => string.Equals(c.Jurisdiction?.Trim(), j, StringComparison.OrdinalIgnoreCase));
        return q;
    }

    private static string DocumentOf(string claimId) => claimId[..claimId.IndexOf('/', StringComparison.Ordinal)];

    private static (int Period, int Claim) Position(string claimId)
    {
        var parts = claimId.Split('/');
        return (int.Parse(parts[^2], CultureInfo.InvariantCulture), int.Parse(parts[^1], CultureInfo.InvariantCulture));
    }

    private static long Paid(AnalyticsClaim c) => c.IndemnityPaidCents + c.MedicalPaidCents + c.ExpensePaidCents;

    private static long Incurred(AnalyticsClaim c) => Paid(c) + c.IndemnityReserveCents + c.MedicalReserveCents + c.ExpenseReserveCents;

    private static decimal Dollars(long cents) => cents / 100m;

    private static string? Text(decimal? value, string unit) => value is not { } v ? null : unit switch
    {
        "money" => v.ToString("$#,##0.00", CultureInfo.InvariantCulture),
        "ratio" => (v * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %",
        "days" => v.ToString("0.0", CultureInfo.InvariantCulture) + " days",
        "rate" => v.ToString("0.######", CultureInfo.InvariantCulture),
        _ => v.ToString("#,##0", CultureInfo.InvariantCulture),
    };
}

/// <summary>The claims payload limit shared with QuoteAnalytics (underwriter-analytics.md KQ2).</summary>
public static class QuoteLimits
{
    public const int MaxClaims = 5000;
}
