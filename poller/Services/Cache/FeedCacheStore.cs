using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace BondCasts.Poller.Services.Cache;

/// Blob-backed cache of fully-parsed feeds, keyed by the app-derived feedHash.
/// The poller writes `{feedHash}.json` on each poll whose content changed; the
/// `GET /feed/{feedHash}` endpoint serves that blob verbatim.
///
/// Serving from this cache — never a live fetch on the request path — is the
/// whole point: it keeps a slow or hung podcast host off the HTTP thread (the
/// class of failure behind the #135 crash loop) and makes the endpoint a cheap
/// O(blob read). Lives on the Functions app's existing AzureWebJobsStorage
/// account, alongside the Table state.
public sealed class FeedCacheStore(BlobContainerClient container)
{
    public const string ContainerName = "feedcache";

    /// Blob metadata key holding the content ETag (a hash of the JSON), used to
    /// answer conditional GETs with a 304 without re-downloading the body.
    private const string ETagMetadataKey = "feedetag";

    /// Created lazily by the poller (mirrors the Table's EnsureTableAsync), so an
    /// unconfigured deployment never provisions storage.
    public Task EnsureContainerAsync(CancellationToken ct) =>
        container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);

    /// Uploads the serialized feed and records its content ETag in metadata.
    public async Task WriteAsync(string feedHash, byte[] json, string contentETag, CancellationToken ct)
    {
        var blob = container.GetBlobClient(BlobName(feedHash));
        using var stream = new MemoryStream(json);
        await blob.UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
            Metadata = new Dictionary<string, string> { [ETagMetadataKey] = contentETag },
        }, ct);
    }

    /// The stored content ETag, or null when the feed has never been polled (the
    /// endpoint then answers 404 and the client falls back to a direct fetch).
    /// Reads metadata only — no body transfer — so the common 304 path is cheap.
    public async Task<string?> ReadETagAsync(string feedHash, CancellationToken ct)
    {
        try
        {
            var properties = await container.GetBlobClient(BlobName(feedHash)).GetPropertiesAsync(cancellationToken: ct);
            if (properties.Value.Metadata.TryGetValue(ETagMetadataKey, out var etag))
                return etag;
            // Fallback for a blob written outside our path: the Azure blob ETag
            // is commonly quoted, so strip quotes — the caller re-quotes it and
            // compares it unquoted, and a quoted value would double up / never match.
            return properties.Value.ETag.ToString().Trim('"');
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// The cached JSON body, or null if the blob is gone (e.g. raced with an
    /// orphan-cleanup delete between the ETag read and this call).
    public async Task<byte[]?> ReadContentAsync(string feedHash, CancellationToken ct)
    {
        try
        {
            var result = await container.GetBlobClient(BlobName(feedHash)).DownloadContentAsync(ct);
            return result.Value.Content.ToArray();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// Drops the cached blob for a feed nobody registers anymore. A missing blob
    /// is not an error (the poll may never have cached it).
    public async Task DeleteAsync(string feedHash, CancellationToken ct) =>
        await container.GetBlobClient(BlobName(feedHash)).DeleteIfExistsAsync(cancellationToken: ct);

    private static string BlobName(string feedHash) => $"{feedHash}.json";
}
