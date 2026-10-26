using System.Text.Json;
using Azure.Storage.Blobs;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;
using LossRun.Formats;
using LossRun.Ingestion;
using Microsoft.EntityFrameworkCore;

namespace LossRun.DbDeploy;

/// <summary>
/// <c>lossrun-db backfill</c> (brief 09 §4): loads the brief-07 Blob layout into SQL, idempotent by document id.
/// Blob kept only the latest revision of a result, so a backfilled claim's temporal history starts at the backfill;
/// earlier overrides survive as audit rows (source <c>backfill</c>, no claim id) from the audit JSONL.
/// </summary>
public static class Backfill
{
    private static readonly Actor Actor = new("backfill", "system", "backfill");

    public static async Task<string> RunAsync(Uri blobEndpoint, string connectionString, string formatsContainer, Action<string> log, CancellationToken cancellationToken = default)
    {
        var service = new BlobServiceClient(blobEndpoint, AzureCredentials.Default());
        var extractions = service.GetBlobContainerClient("extractions");
        var options = LossRunDbContext.Options(connectionString);
        LossRunDbContext Factory() => new(options);
        var store = new SqlRecordStore(Factory, new ClaimCommandService(Factory));
        int uploads = 0, documents = 0, reviews = 0, usage = 0, audits = 0, formats = 0;

        // Formats first: documents reference them.
        var sqlFormats = new SqlFormatRegistry(Factory, new InMemoryFormatRegistry());
        var blobFormats = new BlobFormatRegistry(service.GetBlobContainerClient(formatsContainer));
        foreach (var entry in await blobFormats.GetIndexAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await blobFormats.GetAsync(entry.FormatId, entry.Version, cancellationToken).ConfigureAwait(false) is { } spec)
            {
                await sqlFormats.ImportAsync(spec, await blobFormats.GetStatsAsync(entry.FormatId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                formats++;
            }
        }

        await foreach (var blob in extractions.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "uploads/", cancellationToken).ConfigureAwait(false))
        {
            if (!blob.Name.EndsWith("/manifest.json", StringComparison.Ordinal))
            {
                continue;
            }

            var manifest = await ReadAsync<UploadManifest>(extractions, blob.Name, cancellationToken).ConfigureAwait(false);
            if (manifest is null)
            {
                continue;
            }

            await store.UpdateManifestAsync(manifest.UploadId, _ => manifest, Actor, cancellationToken).ConfigureAwait(false);
            uploads++;

            await using (var db = Factory())
            {
                if (!await db.UsageEvents.AnyAsync(u => u.RunId == manifest.UploadId, cancellationToken).ConfigureAwait(false))
                {
                    var lines = await ReadLinesAsync(extractions, $"uploads/{manifest.UploadId}/usage.jsonl", cancellationToken).ConfigureAwait(false);
                    foreach (var line in lines)
                    {
                        await store.RecordUsageAsync(manifest.UploadId, JsonSerializer.Deserialize<UsageEvent>(line, CanonicalJson.Options)!, cancellationToken).ConfigureAwait(false);
                        usage++;
                    }
                }
            }

            foreach (var item in manifest.Items.Where(i => i.Classification == "LossRun"))
            {
                var result = await ReadAsync<DocumentResult>(extractions, $"uploads/{manifest.UploadId}/documents/{item.DocumentId}/result.json", cancellationToken)
                    .ConfigureAwait(false);
                if (result is not null)
                {
                    await store.SaveResultAsync(result, Actor, cancellationToken).ConfigureAwait(false);
                    documents++;
                }

                if (await ReadAsync<ReviewItem>(extractions, $"reviews/{item.DocumentId}.json", cancellationToken).ConfigureAwait(false) is { } review)
                {
                    await store.OpenReviewAsync(review, cancellationToken).ConfigureAwait(false);
                    reviews++;
                }
            }

            log($"  {manifest.UploadId}: {manifest.Items.Count} item(s)");
        }

        // Audit JSONL → AuditEvent (once: skipped when backfill audit rows exist).
        await using (var db = Factory())
        {
            if (!await db.AuditEvents.AnyAsync(a => a.Source == "backfill" && a.Action != "upload.create" && a.Action != "extraction.persist", cancellationToken).ConfigureAwait(false))
            {
                await foreach (var blob in extractions.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "audit/", cancellationToken).ConfigureAwait(false))
                {
                    foreach (var line in await ReadLinesAsync(extractions, blob.Name, cancellationToken).ConfigureAwait(false))
                    {
                        var e = JsonSerializer.Deserialize<AuditEvent>(line, CanonicalJson.Options)!;
                        await store.Commands.AuditAsync(e, Actor, cancellationToken).ConfigureAwait(false);
                        audits++;
                    }
                }
            }
        }

        return $"backfill: {formats} format version(s), {uploads} upload(s), {documents} document(s), {reviews} review(s), {usage} usage event(s), {audits} audit event(s)";
    }

    private static async Task<T?> ReadAsync<T>(BlobContainerClient container, string path, CancellationToken cancellationToken)
    {
        var blob = container.GetBlobClient(path);
        if (!await blob.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return default;
        }

        var content = await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(content.Value.Content.ToString(), CanonicalJson.Options);
    }

    private static async Task<IReadOnlyList<string>> ReadLinesAsync(BlobContainerClient container, string path, CancellationToken cancellationToken)
    {
        var blob = container.GetBlobClient(path);
        if (!await blob.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var content = await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
        return content.Value.Content.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
