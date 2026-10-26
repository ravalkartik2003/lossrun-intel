using System.Text.Json.Nodes;
using LossRun.Api.Contracts;
using LossRun.Data;
using LossRun.Rules;

namespace LossRun.Orchestration;

/// <summary>
/// Brief 24 step 3 (ADR 0024): what the quote system is told when a quote's data changes. Every change bumps the quote's
/// revision in its own transaction and writes, in that transaction, a <c>quote.revised</c> event and (once the upload's
/// document reviews are decided) a <c>quote.payload</c> command: the summary, the revision, the readiness, the documents
/// that changed, and the payload URL pinned to that revision. The revision is <see cref="QuoteRevisions.Token"/> here;
/// the record store writes the number it assigned.
/// </summary>
public static class QuotePushes
{
    /// <summary>The payload as of the revision this row is written with (409 revision-superseded once a newer one exists).</summary>
    public static string PayloadUrl(string quoteId) => $"/api/v1/quotes/{Uri.EscapeDataString(quoteId)}/quote-payload?revision={QuoteRevisions.Token}";

    /// <summary>One changed document: its revision and how many of its fields changed status (all of them for a new document).</summary>
    public sealed record ChangedDocument(string DocumentId, int Revision, string? ReviewState, int FieldStatusChanges);

    public static OutboxMessage Revised(string quoteId, string cause, string? documentId, string createdBy, DateTimeOffset at) =>
        OutboxMessage.Event(OutboxKinds.QuoteRevised, quoteId, $"quotes/{quoteId}", new { quoteId, quoteRevision = QuoteRevisions.Token, cause, documentId }, createdBy, at);

    /// <param name="extra">Fields of the summary that depend on the cause (the upload's counts and cost, or the decision).</param>
    public static OutboxMessage Payload(string quoteId, string cause, JsonObject readiness, IReadOnlyList<ChangedDocument> changed, JsonObject extra, string createdBy,
        DateTimeOffset at, string subject)
    {
        var body = new JsonObject
        {
            ["quoteId"] = quoteId, ["quoteRevision"] = QuoteRevisions.Token, ["cause"] = cause, ["readiness"] = readiness,
            ["changedDocuments"] = new JsonArray([.. changed.Select(c => (JsonNode)new JsonObject
            {
                ["documentId"] = c.DocumentId, ["revision"] = c.Revision, ["reviewState"] = c.ReviewState, ["fieldStatusChanges"] = c.FieldStatusChanges,
            })]),
            ["quotePayloadUrl"] = PayloadUrl(quoteId),
        };
        foreach (var (name, value) in extra)
        {
            body[name] = value?.DeepClone();
        }

        return OutboxMessage.Command(OutboxKinds.QuotePayload, quoteId, body, createdBy, at, subject);
    }

    /// <summary>Fields whose status differs between two revisions of a document (every field when there is no earlier one).</summary>
    public static int FieldStatusChanges(DocumentResult? before, DocumentResult after)
    {
        var next = FieldStatusEvaluator.Evaluate(after.Document, after.ReviewState, after.RuleOutcomes);
        if (before is null)
        {
            return next.Count;
        }

        var previous = FieldStatusEvaluator.Evaluate(before.Document, before.ReviewState, before.RuleOutcomes);
        return next.Count(n => !previous.TryGetValue(n.Key, out var p) || p != n.Value) + previous.Keys.Count(k => !next.ContainsKey(k));
    }

    /// <summary>The quote's readiness with <paramref name="replacing"/> in place of its stored revision (the state the transaction commits).</summary>
    public static async Task<JsonObject> ReadinessAsync(IRecordStore records, string quoteId, DocumentResult? replacing, CancellationToken cancellationToken)
    {
        var results = (await records.QuoteResultsAsync(quoteId, cancellationToken).ConfigureAwait(false)).Select(r => r.Result)
            .Where(r => replacing is null || r.DocumentId != replacing.DocumentId).ToList();
        if (replacing is not null)
        {
            results.Add(replacing);
        }

        var excluded = results.Where(r => r.ReviewState == ReviewActions.Reject).ToList();
        return QuoteAnalytics.Readiness(new QuoteAnalytics.Account(quoteId, [.. results.Except(excluded)], [], "", null, excluded));
    }
}
