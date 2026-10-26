using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace LossRun.Formats.Tests;

/// <summary>
/// Brief 30 item 36: an in-memory stand-in for the Blob SDK types the format registry uses (upload with If-None-Match / If-Match, download
/// with an ETag), so the registry's publish protocol is tested offline. <see cref="FailIndexWrites"/> makes the next index uploads fail
/// with 412, the way a busy index does.
/// </summary>
internal sealed class FakeBlobContainer : BlobContainerClient
{
    public Dictionary<string, (BinaryData Data, ETag ETag)> Blobs { get; } = new(StringComparer.Ordinal);
    public int FailIndexWrites { get; set; }

    public override BlobClient GetBlobClient(string blobName) => new FakeBlob(this, blobName);

    private sealed class FakeBlob(FakeBlobContainer owner, string name) : BlobClient
    {
        public override string Name => name;

        public override Task<Response<BlobContentInfo>> UploadAsync(BinaryData content, BlobUploadOptions options, CancellationToken cancellationToken = default)
        {
            var exists = owner.Blobs.TryGetValue(name, out var current);
            if (name == "index.json" && owner.FailIndexWrites > 0)
            {
                owner.FailIndexWrites--;
                throw new RequestFailedException(412, "index changed");
            }

            if (options.Conditions?.IfNoneMatch == ETag.All && exists || options.Conditions?.IfMatch is { } match && (!exists || current.ETag != match))
            {
                throw new RequestFailedException(exists ? 409 : 412, "condition not met");
            }

            var etag = new ETag(Guid.NewGuid().ToString("N"));
            owner.Blobs[name] = (content, etag);
            return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobContentInfo(etag, DateTimeOffset.UtcNow, null, null, null, null, 0), new FakeResponse(201)));
        }

        public override Task<Response<BlobDownloadResult>> DownloadContentAsync(CancellationToken cancellationToken = default)
        {
            if (!owner.Blobs.TryGetValue(name, out var blob))
            {
                throw new RequestFailedException(404, "not found");
            }

            var details = BlobsModelFactory.BlobDownloadDetails(eTag: blob.ETag);
            return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobDownloadResult(blob.Data, details), new FakeResponse(200)));
        }
    }

    private sealed class FakeResponse(int status) : Response
    {
        public override int Status => status;
        public override string ReasonPhrase => "";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = "";

        public override void Dispose()
        {
        }

        protected override bool ContainsHeader(string name) => false;
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];

        protected override bool TryGetHeader(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
        {
            value = null;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }
    }
}
