using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Chat;
using LossRun.Contracts;
using LossRun.Orchestration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LossRun.Functions;

/// <summary>The chat service's pieces, built once per host (null members when Search is not configured).</summary>
public sealed record ChatRuntime(ChatService? Service, ChatWorkspaces Workspaces, ChatIndexer? Indexer);

/// <summary>
/// Chat (brief 12, ADR 0002): <c>POST /chat/turns</c> (JSON, or Server-Sent Events with <c>Accept: text/event-stream</c>),
/// <c>GET /chat/conversations/{id}</c>, ad-hoc workspaces, the chat-index queue trigger and the hourly workspace sweep.
/// The scope comes from the request body and the authenticated caller; the service enforces it in every tool.
/// </summary>
public sealed class ChatFunctions(ChatRuntime chat, IAuthenticator authenticator, ILogger<ChatFunctions> logger)
{
    [Function("ChatTurn")]
    public async Task<IActionResult> ChatTurn([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "chat/turns")] HttpRequest req)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        if (chat.Service is null)
        {
            return Error(503, "chat-unavailable", "Chat is not configured on this host (Search:Endpoint).");
        }

        ChatTurnRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<ChatTurnRequest>(req.Body, CanonicalJson.Options, req.HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            body = null;
        }

        if (body is null)
        {
            return Error(400, "no-body", "Send { scope, question, quoteId?, workspaceId?, conversationId? }.");
        }

        var cancel = req.HttpContext.RequestAborted;
        if (!req.Headers.Accept.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var outcome = await chat.Service.RunTurnAsync(caller.UserId, caller.Role, body, null, cancel);
            return outcome.Result is { } r ? new JsonResult(r, CanonicalJson.Options) : Error(outcome.Status, outcome.Code!, outcome.Message!);
        }

        // Streaming: tool calls as they run, answer text as it is generated, then the final turn (or an error event).
        var response = req.HttpContext.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        var gate = new SemaphoreSlim(1, 1);
        async Task Send(string name, object data)
        {
            await gate.WaitAsync(cancel);
            try
            {
                await response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, CanonicalJson.Options)}\n\n", cancel);
                await response.Body.FlushAsync(cancel);
            }
            finally
            {
                gate.Release();
            }
        }

        try
        {
            var outcome = await chat.Service.RunTurnAsync(caller.UserId, caller.Role, body, Send, cancel);
            await Send(outcome.Result is null ? "error" : "final", outcome.Result is { } r ? r : new ApiError(outcome.Code!, outcome.Message!));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "chat turn failed");
            await Send("error", new ApiError("chat-failed", "The assistant could not answer; try again."));
        }

        return new EmptyResult();
    }

    [Function("ChatConversation")]
    public async Task<IActionResult> ChatConversation([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "chat/conversations/{conversationId}")] HttpRequest req,
        string conversationId)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        var turns = chat.Service is null ? [] : await ConversationOf(caller.UserId, conversationId, req.HttpContext.RequestAborted);
        return turns.Count == 0 ? Error(404, "not-found", $"Conversation '{conversationId}' not found.")
            : new JsonResult(new ChatConversation(conversationId, turns), CanonicalJson.Options);
    }

    private async Task<IReadOnlyList<ChatTurnResult>> ConversationOf(string userId, string conversationId, CancellationToken cancellationToken)
    {
        var turns = await chat.Workspaces.Records.ConversationAsync(conversationId, cancellationToken);
        return turns.All(t => t.UserId == userId) ? turns : []; // someone else's conversation is "not found"
    }

    [Function("CreateWorkspace")]
    public async Task<IActionResult> CreateWorkspace([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "workspaces")] HttpRequest req)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        WorkspaceRequest? body = null;
        if (req.ContentLength is > 0)
        {
            body = await JsonSerializer.DeserializeAsync<WorkspaceRequest>(req.Body, CanonicalJson.Options, req.HttpContext.RequestAborted);
        }

        var workspace = await chat.Workspaces.CreateAsync(caller.UserId, caller.Role, body?.Name, req.HttpContext.RequestAborted);
        return new JsonResult(workspace, CanonicalJson.Options) { StatusCode = 201 };
    }

    [Function("Workspace")]
    public async Task<IActionResult> Workspace([HttpTrigger(AuthorizationLevel.Anonymous, "get", "delete", Route = "workspaces/{workspaceId}")] HttpRequest req,
        string workspaceId)
    {
        if (await AuthAsync(req) is not { } caller)
        {
            return Unauthorized();
        }

        if (HttpMethods.IsDelete(req.Method))
        {
            var (status, message) = await chat.Workspaces.DeleteAsync(caller.UserId, caller.Role, workspaceId, req.HttpContext.RequestAborted);
            return status == 204 ? new NoContentResult() : Error(status, status == 404 ? "not-found" : "gone", message!);
        }

        var (workspace, code, text) = await chat.Workspaces.GetOwnedAsync(caller.UserId, workspaceId, req.HttpContext.RequestAborted);
        return workspace is null ? Error(code, "not-found", text!) : new JsonResult(workspace, CanonicalJson.Options);
    }

    /// <summary>Indexes an upload's chunks (message = upload id). Idempotent; poison messages land in chat-index-poison after 5 tries.</summary>
    [Function("ChatIndex")]
    public async Task ChatIndex([QueueTrigger("%Chat:IndexQueue%", Connection = "ChatIndexQueue")] string uploadId, CancellationToken cancellationToken)
    {
        if (chat.Indexer is null)
        {
            logger.LogWarning("chat-index message for {UploadId} ignored: Search is not configured", uploadId);
            return;
        }

        var chunks = await chat.Indexer.IndexUploadAsync(uploadId.Trim(), cancellationToken);
        logger.LogInformation("chat-index {UploadId}: {Chunks} chunks", uploadId, chunks);
    }

    /// <summary>Hourly: expired workspaces leave the index and are marked deleted.</summary>
    [Function("ChatWorkspaceSweep")]
    public async Task ChatWorkspaceSweep([TimerTrigger("0 7 * * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        var swept = await chat.Workspaces.SweepAsync(cancellationToken);
        if (swept > 0)
        {
            logger.LogInformation("workspace sweep: {Count} expired", swept);
        }
    }

    /// <remarks>Brief 25 Answers 2: the quote system's identity has no chat or workspace access (treated as unauthenticated here).</remarks>
    private async Task<Caller?> AuthAsync(HttpRequest req) =>
        await authenticator.AuthenticateAsync(req.Headers.Authorization.FirstOrDefault(), req.HttpContext.RequestAborted) is { Role: not Roles.QuoteSystem } caller ? caller : null;

    private static IActionResult Unauthorized() =>
        new JsonResult(new ApiError("unauthorized", "Send an Entra ID bearer token (or Basic credentials on the demo host)."), CanonicalJson.Options) { StatusCode = 401 };

    private static IActionResult Error(int status, string code, string message) => new JsonResult(new ApiError(code, message), CanonicalJson.Options) { StatusCode = status };
}
