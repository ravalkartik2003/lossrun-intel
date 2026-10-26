using LossRun.Contracts;

namespace LossRun.Api.Contracts;

// Brief 12 (ADR 0002): scoped chat over documents, claims and catalog metrics. Shapes are in docs/api/openapi.yaml.

/// <summary>What a turn may see. The server resolves and enforces it; the model never chooses it.</summary>
public static class ChatScopes
{
    /// <summary>Every quote's documents (underwriting roles); ad-hoc workspaces are excluded.</summary>
    public const string Org = "org";

    /// <summary>One quote's documents.</summary>
    public const string Quote = "quote";

    /// <summary>One ad-hoc workspace of just-uploaded files (its owner only).</summary>
    public const string Workspace = "workspace";
}

/// <summary><c>POST /chat/turns</c>.</summary>
public sealed record ChatTurnRequest(string Scope, string Question, string? QuoteId = null, string? WorkspaceId = null, string? ConversationId = null);

/// <summary>A source the answer cites: a page (or sheet) chunk, a claim row, or a catalog metric.</summary>
/// <param name="Ref">The id the model used in the text, e.g. <c>S2</c>, <c>C5</c>, <c>M1</c> (unique within the turn).</param>
/// <param name="Kind">page | claim | metric.</param>
/// <param name="Label">Chip text: "lossrun-2024.pdf · p.4", "WC-22-0001 · doc …", "Total incurred (catalog 1.1.0)".</param>
public sealed record ChatCitation(string Ref, string Kind, string Label, string? DocumentId = null, int? Page = null, string? Sheet = null,
    string? FieldPath = null, string? ClaimId = null, string? Metric = null, string? FileName = null, string? QuoteId = null);

/// <summary>One server-side tool call of the turn, as executed (not as the model described it).</summary>
/// <param name="Arguments">The arguments the model passed (the scope is not among them; it is injected).</param>
/// <param name="Results">Rows or chunks returned after the scope check.</param>
/// <param name="Dropped">Results removed by the post-filter scope check (always 0 unless the index or store is wrong).</param>
public sealed record ChatToolCall(string Tool, string Arguments, int Results, long Ms, int Dropped = 0, string? Note = null);

public sealed record ChatTurnUsage(int ModelCalls, long InputTokens, long CachedInputTokens, long OutputTokens, int Embeddings, int SearchQueries,
    decimal CostUsd, long FirstTokenMs, long TotalMs, IReadOnlyList<CostLine> Lines);

/// <summary>A finished turn: the answer, its citations (validated against this turn's tool results), the tool trace, usage.</summary>
/// <param name="NotFound">The model said the documents do not contain the answer.</param>
/// <param name="InvalidCitations">Refs the model wrote that no tool returned in this turn (removed from the answer's citations).</param>
/// <param name="CapNote">Set when the per-turn cap (6 tool calls, 12 document chunks) stopped a tool.</param>
public sealed record ChatTurnResult(string TurnId, string ConversationId, string UserId, string Role, string Scope, string? QuoteId, string? WorkspaceId,
    string Question, string Text, bool NotFound, IReadOnlyList<ChatCitation> Citations, IReadOnlyList<ChatToolCall> ToolTrace, ChatTurnUsage Usage,
    DateTimeOffset At, IReadOnlyList<string>? InvalidCitations = null, string? CapNote = null, IReadOnlyList<string>? Unverified = null);

/// <summary><c>GET /chat/conversations/{id}</c>: the caller's own turns, oldest first.</summary>
public sealed record ChatConversation(string ConversationId, IReadOnlyList<ChatTurnResult> Turns);

/// <summary><c>POST /workspaces</c> body.</summary>
public sealed record WorkspaceRequest(string? Name = null);

/// <summary>An ad-hoc workspace: uploads with its id are processed normally but belong to no quote and expire.</summary>
public sealed record ChatWorkspace(string WorkspaceId, string OwnerUserId, string? Name, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? DeletedAt = null);
