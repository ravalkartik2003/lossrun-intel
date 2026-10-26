using LossRun.Api.Contracts;
using LossRun.Data;

namespace LossRun.Chat;

/// <summary>
/// Ad-hoc workspaces (brief 12 §3): a private scope for files just uploaded. Uploads with the workspace id go through the
/// normal pipeline but belong to no quote; the workspace expires after a TTL and its chunks leave the index.
/// </summary>
public sealed class ChatWorkspaces(IRecordStore records, ChatIndexer? indexer, TimeProvider clock, int ttlHours,
    Func<string, CancellationToken, Task<UploadPurge>>? purgeUpload = null)
{
    public IRecordStore Records => records;

    public async Task<ChatWorkspace> CreateAsync(string userId, string role, string? name, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var workspace = new ChatWorkspace($"ws-{Guid.NewGuid():N}"[..19], userId, string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(name.Trim().Length, 200)],
            now, now.AddHours(ttlHours));
        await records.SaveWorkspaceAsync(workspace, new Actor(userId, role, "api"), "workspace.create", cancellationToken).ConfigureAwait(false);
        return workspace;
    }

    /// <summary>The owner's workspace, or why not: 404 for absent or someone else's (indistinguishable), 410 for deleted or expired.</summary>
    public async Task<(ChatWorkspace? Workspace, int Status, string? Message)> GetOwnedAsync(string userId, string workspaceId, CancellationToken cancellationToken = default)
    {
        var w = await records.GetWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        if (w is null || w.OwnerUserId != userId)
        {
            return (null, 404, $"Workspace '{workspaceId}' not found.");
        }

        return w.DeletedAt is not null || w.ExpiresAt <= clock.GetUtcNow() ? (w, 410, $"Workspace '{workspaceId}' has expired or was deleted.") : (w, 200, null);
    }

    /// <summary>Deletes the owner's workspace: marked deleted (audited), its chunks removed from the index.</summary>
    public async Task<(int Status, string? Message)> DeleteAsync(string userId, string role, string workspaceId, CancellationToken cancellationToken = default)
    {
        var (w, status, message) = await GetOwnedAsync(userId, workspaceId, cancellationToken).ConfigureAwait(false);
        if (w is null || w.DeletedAt is not null)
        {
            return (w is null ? status : 410, message ?? "Already deleted.");
        }

        await records.SaveWorkspaceAsync(w with { DeletedAt = clock.GetUtcNow() }, new Actor(userId, role, "api"), "workspace.delete", cancellationToken).ConfigureAwait(false);
        if (indexer is not null)
        {
            await indexer.DropWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        }

        return (204, null);
    }

    /// <summary>
    /// Hourly timer (12 Answers): an expired workspace leaves the index, its uploads' files and extraction JSON are deleted
    /// (they were ad-hoc, never tied to a quote), and one <c>workspace.expired</c> audit event lists what was removed.
    /// ChatTurn ledger rows stay (append-only; they hold questions and answers, no document content).
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        var expired = await records.ExpiredWorkspacesAsync(clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        foreach (var w in expired)
        {
            var chunks = indexer is null ? 0 : await indexer.DropWorkspaceAsync(w.WorkspaceId, cancellationToken).ConfigureAwait(false);
            var purged = new List<UploadPurge>();
            if (purgeUpload is not null)
            {
                foreach (var uploadId in await records.UploadsOfWorkspaceAsync(w.WorkspaceId, cancellationToken).ConfigureAwait(false))
                {
                    purged.Add(await purgeUpload(uploadId, cancellationToken).ConfigureAwait(false));
                }
            }

            var detail = System.Text.Json.JsonSerializer.Serialize(new
            {
                indexChunks = chunks,
                uploads = purged.Select(p => new { p.UploadId, deleted = p.Deleted.Count, retained = p.Retained, files = p.Deleted }),
            });
            await records.SaveWorkspaceAsync(w with { DeletedAt = clock.GetUtcNow() }, Actor.System, "workspace.expired", cancellationToken, detail).ConfigureAwait(false);
        }

        return expired.Count;
    }
}
