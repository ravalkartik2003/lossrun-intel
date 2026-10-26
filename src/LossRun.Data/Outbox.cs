using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Contracts;

namespace LossRun.Data;

/// <summary>Outbox channels: commands go to the quote system through Service Bus; events go to subscribers through Event Grid.</summary>
public static class OutboxChannels
{
    public const string Command = "command";
    public const string Event = "event";
}

/// <summary>What the outbox carries (brief 12b). Command kinds map to quote-system endpoints; event kinds become CloudEvent types <c>lossrun.{kind}</c>.</summary>
public static class OutboxKinds
{
    public const string Flags = "quote.flags";
    public const string Annotation = "quote.annotation";
    public const string FollowUp = "quote.followup";
    public const string MemoLink = "quote.memo-link";
    public const string QuotePayload = "quote.payload";

    public const string UploadCompleted = "upload.completed";
    public const string ReviewDecided = "review.decided";

    /// <summary>Brief 24 step 3: the quote's data changed (upload completed, decision, override); <c>{quoteId, quoteRevision, cause}</c>.</summary>
    public const string QuoteRevised = "quote.revised";
    public const string FormatLearned = "format.learned";

    /// <summary>Brief 26 (ADR 0025): a new engineering escalation, and a change to one (count, status, tuning item).</summary>
    public const string EscalationRaised = "escalation.raised";
    public const string EscalationUpdated = "escalation.updated";
}

/// <summary>
/// One outbox row. <see cref="Id"/> is the Service Bus MessageId, the CloudEvent id and the Idempotency-Key the quote
/// system sees, so a re-publish after a crash is dropped twice over (duplicate detection, then the receiver).
/// </summary>
public sealed record OutboxMessage(string Id, string Channel, string Kind, string? QuoteId, string? Subject, string PayloadJson, DateTimeOffset CreatedAt,
    string CreatedBy)
{
    public DateTimeOffset? PublishedAt { get; init; }
    public int PublishAttempts { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? DeliveredAt { get; init; }
    public string? DeliveryStatus { get; init; }
    public string? DeliveryJson { get; init; }

    public static OutboxMessage Command(string kind, string quoteId, object payload, string createdBy, DateTimeOffset at, string? subject = null) =>
        new(NewId(), OutboxChannels.Command, kind, quoteId, subject, JsonSerializer.Serialize(payload, CanonicalJson.Options), at, createdBy);

    public static OutboxMessage Event(string kind, string? quoteId, string subject, object payload, string createdBy, DateTimeOffset at) =>
        new(NewId(), OutboxChannels.Event, kind, quoteId, subject, JsonSerializer.Serialize(payload, CanonicalJson.Options), at, createdBy);

    public static string NewId() => $"ob-{Guid.NewGuid():N}";

    /// <summary>Brief 30 item 6: the id of the row a keyed unit of work (a decision, an upload's completion) writes for this channel and kind, the same on every retry;
    /// the outbox primary key is then the unique constraint on (key, kind).</summary>
    public static string KeyedId(string key, string channel, string kind) =>
        "ob-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{key}|{channel}|{kind}")))[..32];

    /// <summary>This row re-keyed for <paramref name="key"/>.</summary>
    public OutboxMessage KeyedBy(string key) => this with { Id = KeyedId(key, Channel, Kind) };

    public OutboxEntry ToEntry() => new(Id, Channel, Kind, QuoteId, Subject, JsonDocument.Parse(PayloadJson).RootElement.Clone(), CreatedAt, CreatedBy, PublishedAt,
        PublishAttempts, LastError, DeliveredAt, DeliveryStatus, DeliveryJson is null ? null : JsonDocument.Parse(DeliveryJson).RootElement.Clone());
}

/// <summary>
/// Brief 24 step 3 (ADR 0024): the quote-level revision. A command or event written in the transaction that bumps the
/// quote's revision carries <see cref="Token"/> where the revision goes (<c>"quoteRevision": "{{quoteRevision}}"</c>,
/// <c>?revision={{quoteRevision}}</c>); the record store replaces it with the number it assigned, inside that transaction.
/// </summary>
public static class QuoteRevisions
{
    public const string Token = "{{quoteRevision}}";

    /// <summary>Why the revision moved (dbo.QuoteRevision.Cause).</summary>
    public const string UploadCompleted = "upload-completed";
    public const string ReviewDecided = "review-decided";
    public const string Override = "override";
    public const string ReopenDecided = "reopen-decided";

    /// <summary>Brief 30 item 16: a reopen takes a verified document out of the verified view, so the payload changes.</summary>
    public const string Reopened = "reopened";

    /// <summary>The JSON with the token replaced: the quoted token becomes a JSON number, the token inside a string becomes digits.</summary>
    public static string Apply(string json, int revision)
    {
        var n = revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return json.Replace($"\"{Token}\"", n, StringComparison.Ordinal).Replace(Token, n, StringComparison.Ordinal);
    }

    /// <summary>The rows with the token replaced (rows without it are returned as they are).</summary>
    public static IReadOnlyList<OutboxMessage> Apply(IReadOnlyList<OutboxMessage> outbox, int revision) =>
        [.. outbox.Select(o => o.PayloadJson.Contains(Token, StringComparison.Ordinal) ? o with { PayloadJson = Apply(o.PayloadJson, revision) } : o)];

    /// <summary>The <c>ETag</c> of a quote payload at a revision.</summary>
    public static string ETag(int revision) => $"\"q-{revision}\"";
}

/// <summary>Bump the quote's revision in the same transaction as the decision or the completion (brief 24 step 3).</summary>
public sealed record QuoteRevisionBump(string QuoteId, string Cause, string? DocumentId = null, int? DocumentRevision = null);

/// <summary>Publishes outbox rows right after the command commits (fast path) and on a timer (the sweep that makes it at-least-once).</summary>
public interface IOutboxRelay
{
    /// <summary>Publishes the given rows if still unpublished. Never throws: a failure stays pending for the sweep.</summary>
    Task RelayAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default);

    /// <summary>Publishes every row still pending after <paramref name="minAge"/>; returns how many were published.</summary>
    Task<int> SweepAsync(TimeSpan minAge, CancellationToken cancellationToken = default);
}
