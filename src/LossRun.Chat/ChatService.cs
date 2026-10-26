using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;
using LossRun.Formats;
using LossRun.Ingestion.Metering;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LossRun.Chat;

/// <param name="Deployment">Chat model deployment (configuration; Models:Chat).</param>
/// <param name="ModelVersion">Model version behind it (configuration), for the usage rows.</param>
/// <param name="HistoryTurns">Brief 24 step 6: the most prior turns replayed verbatim (a ceiling; Chat:HistoryTurns).</param>
/// <param name="HistoryTokenBudget">Tokens of prior turns replayed verbatim, newest first, at least the last two (Chat:HistoryTokenBudget).</param>
public sealed record ChatServiceOptions(string Deployment, string? ModelVersion, string DeploymentType, string Region, string SearchRegion,
    int WorkspaceTtlHours = 72, int MaxToolIterations = 6, int HistoryTurns = 12, int HistoryTokenBudget = 6000);

/// <summary>A turn's outcome: the result, or an HTTP-style error.</summary>
public sealed record ChatOutcome(int Status, ChatTurnResult? Result = null, string? Code = null, string? Message = null)
{
    public static ChatOutcome Error(int status, string code, string message) => new(status, null, code, message);
}

/// <summary>
/// One chat turn (brief 12): authorize and resolve the scope from the authenticated caller, run a ChatClientAgent with
/// the three scope-bound tools, stream the answer, validate its citations against this turn's tool results, price the
/// turn from metered usage, and append it to the ChatTurn ledger with an audit event.
/// </summary>
public sealed partial class ChatService(ChatServiceOptions options, IChatClient model, IChatSearch search, Func<ICostMeter, IEmbeddingService> embeddings,
    IRecordStore records, PriceTable prices, TimeProvider clock)
{
    public const int MaxQuestionChars = 2000;

    /// <param name="onEvent">SSE sink: <c>tool</c> (ChatToolCall), <c>delta</c> ({ text }), and the caller sends <c>final</c> itself.</param>
    public async Task<ChatOutcome> RunTurnAsync(string userId, string role, ChatTurnRequest request, Func<string, object, Task>? onEvent = null,
        CancellationToken cancellationToken = default)
    {
        var total = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > MaxQuestionChars)
        {
            return ChatOutcome.Error(400, "bad-question", $"question is 1–{MaxQuestionChars} characters.");
        }

        ChatScope scope;
        try
        {
            scope = ChatScope.Parse(request.Scope, request.QuoteId, request.WorkspaceId);
        }
        catch (ArgumentException e)
        {
            return ChatOutcome.Error(400, "bad-scope", e.Message);
        }

        if (await AuthorizeAsync(userId, role, scope, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        var conversationId = request.ConversationId ?? $"conv-{Guid.NewGuid():N}";
        var history = request.ConversationId is null ? [] : await records.ConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (history.Any(t => t.UserId != userId || t.Scope != scope.Kind || t.QuoteId != scope.QuoteId || t.WorkspaceId != scope.WorkspaceId))
        {
            return ChatOutcome.Error(409, "conversation-scope", "A conversation keeps the user and scope it started with; start a new one.");
        }

        var turnId = $"turn-{Guid.NewGuid():N}";
        var documents = await records.DocumentsInScopeAsync(scope.Kind, scope.QuoteId, scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var recorder = new TurnRecorder(onEvent);
        var tools = new ChatTools(scope, documents, search, embeddings(recorder.Meter), records, recorder, turnId, options.SearchRegion, role);

        var metered = new MeteredChatClient(model, recorder, options, turnId);
        var invoking = new FunctionInvokingChatClient(metered) { MaximumIterationsPerRequest = options.MaxToolIterations, MaximumConsecutiveErrorsPerRequest = 2 };
        var agent = new ChatClientAgent(invoking, new ChatClientAgentOptions
        {
            Name = "lossrun-chat",
            ChatOptions = new ChatOptions
            {
                Instructions = ChatPrompt.Instructions,
                Tools = tools.AsAITools(),
                ResponseFormat = ChatPrompt.AnswerFormat,
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
            },
            UseProvidedChatClientAsIs = true,
        });

        // Brief 24 step 6: the conversation facts of every prior turn, then the newest turns verbatim within the token budget.
        var messages = new List<ChatMessage>();
        var facts = ChatMemory.Facts(history, scope);
        if (facts is not null)
        {
            messages.Add(new ChatMessage(ChatRole.System, facts));
        }

        var verbatim = ChatMemory.Verbatim(history, options.HistoryTokenBudget, options.HistoryTurns);
        foreach (var (_, t) in verbatim)
        {
            messages.Add(new ChatMessage(ChatRole.User, t.Question));
            messages.Add(new ChatMessage(ChatRole.Assistant, t.Text));
        }

        if (history.Count > 0)
        {
            // What the model saw of the conversation, kept in the ledger row's tool trace.
            await recorder.TraceAsync(new ChatToolCall("history", JsonSerializer.Serialize(new
            {
                priorTurns = history.Count, verbatimTurns = verbatim.Select(v => v.Turn), factsTokens = ChatMemory.EstimateTokens(facts),
                historyTokenBudget = options.HistoryTokenBudget, historyTurns = options.HistoryTurns,
            }), verbatim.Count, 0, Note: verbatim.Count < history.Count ? $"turns 1–{history.Count - verbatim.Count} as conversation facts only" : null)).ConfigureAwait(false);
        }

        messages.Add(new ChatMessage(ChatRole.User, $"Scope: {scope} ({documents.Count} loss-run document(s)).\nQuestion: {request.Question.Trim()}"));

        var parser = new AnswerStreamParser();
        long firstToken = -1;
        try
        {
            await foreach (var update in agent.RunStreamingAsync(messages, null, null, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrEmpty(update.Text))
                {
                    continue;
                }

                var delta = parser.Push(update.Text);
                if (delta.Length > 0)
                {
                    if (firstToken < 0)
                    {
                        firstToken = total.ElapsedMilliseconds;
                    }

                    if (onEvent is not null)
                    {
                        await onEvent("delta", new { text = delta }).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception e) when (!IsContentFilter(e) && e is not OperationCanceledException)
        {
            // Brief 30 item 24: the turn fails, but what the model calls cost is recorded under the turn before the error propagates.
            foreach (var u in recorder.Usage)
            {
                await records.RecordUsageAsync(turnId, u, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception e) when (IsContentFilter(e))
        {
            // Prompt Shields / content filter on the deployment refused the request: answer safely, keep the audit trail.
            parser = new AnswerStreamParser();
            parser.Push(JsonSerializer.Serialize(new { text = "This request was blocked by the content safety filter.", citations = Array.Empty<string>(), notFound = true }));
            await (recorder.TraceAsync(new ChatToolCall("content-filter", "{}", 0, 0, Note: Shorten(e.Message)))).ConfigureAwait(false);
        }

        var (text, cited, notFound) = ParseAnswer(parser.Raw);

        // Brief 24 step 7: quotations must be exact and the reviewers' rationales quoted verbatim. One restate pass when not.
        var restateFlags = new List<string>();
        var quoteMisses = FactGuard.Unverified(text, recorder.ToolStrings).Where(u => u.StartsWith("quote ", StringComparison.Ordinal)).ToList();
        var rationaleMisses = FactGuard.MissingRationales(text, recorder.Rationales);
        if (!notFound && (quoteMisses.Count > 0 || rationaleMisses.Count > 0))
        {
            var restated = await RestateAsync(metered, request.Question, text, quoteMisses, recorder.Rationales, cancellationToken).ConfigureAwait(false);
            // Brief 25 step 0: a restate never turns an answer into "not found" and never loses an amount or date the first answer
            // stated; then the first answer is kept and its misquotes are flagged unverified instead.
            var lost = restated is { } candidate ? FactGuard.Facts(text).Except(FactGuard.Facts(candidate.Text)).ToList() : [];
            var keep = restated is null || restated.Value.NotFound || lost.Count > 0;
            await recorder.TraceAsync(new ChatToolCall("restate", JsonSerializer.Serialize(new { quoteMisses, rationaleMisses, lost }), keep ? 0 : 1, 0,
                Note: restated is null ? "restate failed; first answer kept, misquotes flagged"
                    : keep ? "restate dropped facts or answered not-found; first answer kept, misquotes flagged" : "answer restated once to quote exactly")).ConfigureAwait(false);
            if (!keep)
            {
                (text, cited, notFound) = restated!.Value;
            }
            else
            {
                restateFlags.AddRange(rationaleMisses.Select(m => $"rationale not quoted verbatim: \"{m}\""));
            }
        }
        if (recorder.LastResult is { } last)
        {
            // Brief 24 step 7 Answers 2: the turn's last result set and filter, for the next turn's follow-up resolution.
            await recorder.TraceAsync(new ChatToolCall(ChatMemory.LastResultTool, JsonSerializer.Serialize(last), 0, 0)).ConfigureAwait(false);
        }

        var citations = cited.Where(recorder.Refs.ContainsKey).Distinct(StringComparer.Ordinal).Select(r => recorder.Refs[r]).ToList();
        var invalid = cited.Where(r => !recorder.Refs.ContainsKey(r)).Distinct(StringComparer.Ordinal).ToList();
        // Inline refs the model wrote but did not list are still sources it used: add them when they are real.
        foreach (Match m in InlineRef().Matches(text))
        {
            var r = m.Groups[1].Value;
            if (recorder.Refs.TryGetValue(r, out var c) && citations.All(x => x.Ref != r))
            {
                citations.Add(c);
            }
        }

        // Brief 24 step 5: names, dates and quoted rationales must come from this turn's tool output; the rest is flagged
        // and the flag is kept in the ledger row's tool trace.
        var unverified = FactGuard.Unverified(text, recorder.ToolStrings).Concat(restateFlags).Distinct(StringComparer.Ordinal).ToList();
        if (unverified.Count > 0)
        {
            await recorder.TraceAsync(new ChatToolCall("fact-guard", JsonSerializer.Serialize(unverified), unverified.Count, 0,
                Note: "unverified: not in this turn's tool output")).ConfigureAwait(false);
        }

        var cost = CostCalculator.Compute(turnId, turnId, "chat", recorder.Usage, prices);
        var modelEvents = recorder.Usage.Where(e => e.Service == "AzureOpenAI" && !CostCalculator.IsEmbedding(e.Stage)).ToList();
        var usage = new ChatTurnUsage(
            ModelCalls: modelEvents.Select(e => e.CorrelationId).Distinct().Count(),
            InputTokens: (long)modelEvents.Where(e => e.Meter == "inputTokens").Sum(e => e.Quantity),
            CachedInputTokens: (long)modelEvents.Where(e => e.Meter == "cachedInputTokens").Sum(e => e.Quantity),
            OutputTokens: (long)modelEvents.Where(e => e.Meter == "outputTokens").Sum(e => e.Quantity),
            Embeddings: recorder.Embeddings,
            SearchQueries: recorder.SearchQueries,
            CostUsd: Math.Round(cost.CostUsd, 6),
            FirstTokenMs: Math.Max(firstToken, 0),
            TotalMs: total.ElapsedMilliseconds,
            Lines: cost.Breakdown);

        var result = new ChatTurnResult(turnId, conversationId, userId, role, scope.Kind, scope.QuoteId, scope.WorkspaceId, request.Question.Trim(), text, notFound,
            citations, [.. recorder.Trace], usage, clock.GetUtcNow(), invalid.Count > 0 ? invalid : null, recorder.CapNote, unverified.Count > 0 ? unverified : null);
        var actor = new Actor(userId, role, "api", turnId);
        await records.SaveChatTurnAsync(result, actor, cancellationToken).ConfigureAwait(false);
        foreach (var e in recorder.Usage)
        {
            await records.RecordUsageAsync(turnId, e, cancellationToken).ConfigureAwait(false);
        }

        return new ChatOutcome(200, result);
    }

    /// <summary>
    /// Who may ask in which scope (12 Answers): Applicants only in their own workspace; Underwriters in a quote or their
    /// workspace; org-wide only UWManager and Admin; workspaces only by their owner.
    /// </summary>
    public async Task<ChatOutcome?> AuthorizeAsync(string userId, string role, ChatScope scope, CancellationToken cancellationToken)
    {
        if (role == Roles.QuoteSystem)
        {
            return ChatOutcome.Error(403, "forbidden", "The quote system's identity does not chat.");
        }

        var reviewer = Roles.CanRead(role); // brief 26: Engineer reads what a reviewer reads
        if (scope.Kind != ChatScopes.Workspace && !reviewer)
        {
            return ChatOutcome.Error(403, "forbidden", "Applicants can ask about their own workspace only.");
        }

        if (scope.Kind == ChatScopes.Org && role is not (Roles.UWManager or Roles.Admin))
        {
            return ChatOutcome.Error(403, "forbidden", "Questions across all documents are for UWManager and Admin; ask within a quote or your workspace.");
        }

        if (scope.Kind == ChatScopes.Workspace)
        {
            var ws = await records.GetWorkspaceAsync(scope.WorkspaceId!, cancellationToken).ConfigureAwait(false);
            if (ws is null || ws.DeletedAt is not null || ws.OwnerUserId != userId)
            {
                return ChatOutcome.Error(404, "not-found", $"Workspace '{scope.WorkspaceId}' not found."); // not "forbidden": do not reveal it exists
            }

            if (ws.ExpiresAt <= clock.GetUtcNow())
            {
                return ChatOutcome.Error(410, "expired", $"Workspace '{scope.WorkspaceId}' expired at {ws.ExpiresAt:u}.");
            }
        }

        return null;
    }

    /// <summary>
    /// The restate pass: the same model, no tools, asked once to rewrite its answer quoting exactly. It is given the exact
    /// rationales this turn's explain_value returned and the quotations that did not match. Null when the call fails.
    /// </summary>
    private static async Task<(string Text, IReadOnlyList<string> Citations, bool NotFound)?> RestateAsync(IChatClient client, string question, string answer,
        IReadOnlyList<string> quoteMisses, IReadOnlyCollection<string> rationales, CancellationToken cancellationToken)
    {
        var correction = new StringBuilder()
            .AppendLine("Rewrite your answer. Keep its facts and refs; change only the quotations.")
            .AppendLine("Quote every reviewer rationale exactly as the tool returned it: same words, same order, same case, inside quotation marks. Never paraphrase a rationale.");
        if (rationales.Count > 0)
        {
            correction.AppendLine("The rationales, exactly:");
            foreach (var r in rationales.Take(20))
            {
                correction.AppendLine(CultureInfo.InvariantCulture, $"- \"{r}\"");
            }
        }

        if (quoteMisses.Count > 0)
        {
            correction.AppendLine("These quotations in your answer are not in the tool results; correct or remove them: " + string.Join("; ", quoteMisses));
        }

        correction.Append("Reply with JSON only: {\"text\": \"...\", \"citations\": [...], \"notFound\": false}");
        try
        {
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, question), new ChatMessage(ChatRole.Assistant, answer), new ChatMessage(ChatRole.User, correction.ToString())],
                new ChatOptions { Instructions = ChatPrompt.Instructions, ResponseFormat = ChatPrompt.AnswerFormat, Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low } },
                cancellationToken).ConfigureAwait(false);
            return ParseAnswer(response.Text);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    public static (string Text, IReadOnlyList<string> Citations, bool NotFound) ParseAnswer(string raw)
    {
        try
        {
            // Brief 24 step 7 Answers 3: a model that answers twice in one stream writes two JSON objects back to back; the last is its answer.
            using var doc = JsonDocument.Parse(LastJsonObject(raw));
            var root = doc.RootElement;
            var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            var citations = root.TryGetProperty("citations", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : [];
            var notFound = root.TryGetProperty("notFound", out var n) && n.ValueKind == JsonValueKind.True;
            return (text, citations, notFound);
        }
        catch (JsonException)
        {
            // Not JSON (should not happen with structured output): keep the text and take the inline refs.
            return (raw, [.. InlineRef().Matches(raw).Select(m => m.Groups[1].Value)], false);
        }
    }

    /// <summary>The last complete top-level JSON value of a stream of concatenated values (the whole text when it is one value).</summary>
    internal static string LastJsonObject(string raw)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(raw);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowMultipleValues = true });
        long start = -1, end = -1;
        try
        {
            while (reader.Read())
            {
                if (reader.CurrentDepth == 0 && reader.TokenType == JsonTokenType.StartObject)
                {
                    var from = reader.TokenStartIndex;
                    reader.Skip();
                    (start, end) = (from, reader.BytesConsumed);
                }
            }
        }
        catch (JsonException)
        {
            // A truncated tail: keep the last complete value found.
        }

        return start < 0 ? raw : System.Text.Encoding.UTF8.GetString(bytes, (int)start, (int)(end - start));
    }

    private static bool IsContentFilter(Exception e) =>
        e.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("ResponsibleAIPolicyViolation", StringComparison.OrdinalIgnoreCase)
        || e.Message.Contains("content management policy", StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string s) => s.Length > 300 ? s[..300] : s;

    [GeneratedRegex(@"\[([SCM]\d{1,3})\]")]
    private static partial Regex InlineRef();
}

/// <summary>Records token usage of every model call (each round of the tool loop) as UsageEvents, stage "chat".</summary>
public sealed class MeteredChatClient(IChatClient inner, TurnRecorder recorder, ChatServiceOptions options, string turnId) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await RecordAsync(null, null, CancellationToken.None).ConfigureAwait(false); // brief 30 item 24: the attempt counts as a model call
            throw;
        }

        await RecordAsync(response.Usage, response.ResponseId, cancellationToken).ConfigureAwait(false);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        UsageDetails? usage = null;
        string? responseId = null;
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false))
            {
                responseId ??= update.ResponseId;
                foreach (var u in update.Contents.OfType<UsageContent>())
                {
                    usage ??= new UsageDetails();
                    usage.Add(u.Details);
                }

                yield return update;
            }
        }
        finally
        {
            // Brief 30 item 24: the call is metered whether or not the stream completes (a failed turn is still billed).
            await RecordAsync(usage, responseId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RecordAsync(UsageDetails? usage, string? responseId, CancellationToken cancellationToken)
    {
        var correlation = responseId ?? $"{turnId}-{Guid.NewGuid():N}";
        var at = DateTimeOffset.UtcNow;
        UsageEvent Event(string meter, long quantity) => new(turnId, turnId, CostCalculator.ChatStage, "AzureOpenAI", meter, quantity, "token", options.Deployment,
            options.DeploymentType, options.Region, at, correlation, options.ModelVersion);
        var meter = recorder.Meter;
        await meter.RecordAsync(Event("inputTokens", usage?.InputTokenCount ?? 0), cancellationToken).ConfigureAwait(false);
        await meter.RecordAsync(Event("cachedInputTokens", usage?.CachedInputTokenCount ?? 0), cancellationToken).ConfigureAwait(false);
        await meter.RecordAsync(Event("outputTokens", usage?.OutputTokenCount ?? 0), cancellationToken).ConfigureAwait(false);
    }
}
