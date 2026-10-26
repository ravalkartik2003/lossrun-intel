using System.Text.RegularExpressions;
using LossRun.Api.Contracts;
using LossRun.Data;

namespace LossRun.Chat;

/// <summary>
/// The scope of one chat turn, built by the server from the authenticated request (ADR 0002). Every tool receives it;
/// none of the model-callable tool parameters can name a quote, workspace or document. It is enforced twice:
/// <list type="number">
/// <item>as a filter pushed into the source (the search filter; the document list the claim tools load from);</item>
/// <item>as a post-filter on every result before the model sees it (<see cref="Admits(ChatChunk)"/>), so a misconfigured
/// index or a wrong store row is dropped and counted, never shown.</item>
/// </list>
/// </summary>
public sealed partial record ChatScope
{
    private ChatScope(string kind, string? quoteId, string? workspaceId) => (Kind, QuoteId, WorkspaceId) = (kind, quoteId, workspaceId);

    public string Kind { get; }
    public string? QuoteId { get; }
    public string? WorkspaceId { get; }

    public static ChatScope Org() => new(ChatScopes.Org, null, null);

    public static ChatScope ForQuote(string quoteId) => new(ChatScopes.Quote, Id(quoteId, "quoteId"), null);

    public static ChatScope ForWorkspace(string workspaceId) => new(ChatScopes.Workspace, null, Id(workspaceId, "workspaceId"));

    /// <summary>Parses the request's scope fields; throws <see cref="ArgumentException"/> on anything malformed.</summary>
    public static ChatScope Parse(string? scope, string? quoteId, string? workspaceId) => scope switch
    {
        ChatScopes.Org when quoteId is null && workspaceId is null => Org(),
        ChatScopes.Quote when workspaceId is null => ForQuote(quoteId ?? throw new ArgumentException("scope 'quote' needs quoteId.")),
        ChatScopes.Workspace when quoteId is null => ForWorkspace(workspaceId ?? throw new ArgumentException("scope 'workspace' needs workspaceId.")),
        ChatScopes.Org or ChatScopes.Quote or ChatScopes.Workspace => throw new ArgumentException($"scope '{scope}' takes only its own id."),
        _ => throw new ArgumentException("scope is 'org', 'quote' or 'workspace'."),
    };

    /// <summary>
    /// The search filter (OData). Ids are validated against a strict pattern and quotes doubled, so a value can never
    /// close the string literal; org excludes workspace chunks.
    /// </summary>
    /// <param name="includeRejected">Brief 30 item 8: also rejected and superseded documents (asked for explicitly, labelled in the answer).</param>
    public string ODataFilter(bool includeRejected = false) => Kind switch
    {
        ChatScopes.Quote => $"scope eq 'quote' and quoteId eq '{Escape(QuoteId!)}'",
        ChatScopes.Workspace => $"scope eq 'workspace' and workspaceId eq '{Escape(WorkspaceId!)}'",
        _ => "scope ne 'workspace'",
    } + (includeRejected ? "" : $" and (reviewStatus eq null or (reviewStatus ne '{ChatReviewStatus.Rejected}' and reviewStatus ne '{ChatReviewStatus.Superseded}'))");

    /// <summary>The same predicate as <see cref="ODataFilter"/>, evaluated in process on every returned chunk.</summary>
    public bool Admits(ChatChunk chunk) => Admits(chunk, includeRejected: false);

    public bool Admits(ChatChunk chunk, bool includeRejected) => (includeRejected || chunk.ReviewStatus is not (ChatReviewStatus.Rejected or ChatReviewStatus.Superseded)) && Kind switch
    {
        ChatScopes.Quote => chunk.Scope == ChatScopes.Quote && chunk.QuoteId == QuoteId && chunk.WorkspaceId is null,
        ChatScopes.Workspace => chunk.Scope == ChatScopes.Workspace && chunk.WorkspaceId == WorkspaceId,
        _ => chunk.Scope != ChatScopes.Workspace && chunk.WorkspaceId is null,
    };

    /// <summary>The same predicate on a document the store resolved for the scope (claims and metrics).</summary>
    public bool Admits(ScopedDocument document) => Kind switch
    {
        ChatScopes.Quote => document.QuoteId == QuoteId && document.WorkspaceId is null,
        ChatScopes.Workspace => document.WorkspaceId == WorkspaceId,
        _ => document.WorkspaceId is null,
    };

    public override string ToString() => Kind switch
    {
        ChatScopes.Quote => $"quote {QuoteId}",
        ChatScopes.Workspace => $"workspace {WorkspaceId}",
        _ => "all documents",
    };

    private static string Id(string value, string name) =>
        IdPattern().IsMatch(value) ? value : throw new ArgumentException($"{name} must match {IdPattern()} (got {value.Length} characters).");

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$")]
    private static partial Regex IdPattern();
}
