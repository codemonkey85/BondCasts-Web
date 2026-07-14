using System.Net;
using BondCasts.Poller.Services.Cache;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace BondCasts.Poller.Functions;

/// Serves the poller's already-parsed feed cache over HTTP for the app's
/// `ServerFeedSource` (PodcastApp #174). The app requests
/// `GET /feed/{feedHash}` and decodes the body as its `ParsedFeed`; anything
/// other than a clean 200/304 makes it fall back to a direct on-device fetch.
///
/// This handler NEVER fetches a feed itself — it only reads the blob the timer
/// poller last wrote. A feed the poller hasn't cached yet is a 404 (fresh
/// follow / locked feed / never registered), which the client treats as
/// "fall back", so a slow or hung podcast host can never stall this endpoint.
public sealed class FeedServeFunctions(FeedCacheStore cache, ILogger<FeedServeFunctions> logger)
{
    [Function("ServeFeed")]
    public async Task<HttpResponseData> Serve(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "feed/{feedHash}")] HttpRequestData req,
        string feedHash,
        CancellationToken ct)
    {
        // feedHash is the first 16 hex chars of a SHA-256; reject anything else
        // cheaply rather than round-trip to storage on a junk path.
        if (!IsFeedHash(feedHash))
            return req.CreateResponse(HttpStatusCode.NotFound);

        // X-Feed-URL is advisory only. Registration happens through the app's
        // CloudKit PolledFeed records, and we must not fetch on this path, so we
        // note the request for first-poll visibility WITHOUT logging the URL
        // value (it can carry feed secrets the client chose to send).
        if (req.Headers.Contains("X-Feed-URL"))
            logger.LogDebug("Feed {Hash} requested before it was cached.", feedHash);

        var etag = await cache.ReadETagAsync(feedHash, ct);
        if (etag is null)
            return req.CreateResponse(HttpStatusCode.NotFound);

        // If-None-Match may arrive as multiple header lines AND/OR one line
        // holding a comma-separated list ("a", "b"); flatten both before match.
        if (req.Headers.TryGetValues("If-None-Match", out var providedETags) &&
            providedETags.SelectMany(v => v.Split(',')).Any(candidate => ETagMatches(candidate, etag)))
        {
            var notModified = req.CreateResponse(HttpStatusCode.NotModified);
            notModified.Headers.Add("ETag", Quote(etag));
            return notModified;
        }

        var body = await cache.ReadContentAsync(feedHash, ct);
        if (body is null) // raced with an orphan-cleanup delete
            return req.CreateResponse(HttpStatusCode.NotFound);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("ETag", Quote(etag));
        // The body changes whenever the poller re-parses; require revalidation
        // rather than let an intermediary serve a stale feed.
        response.Headers.Add("Cache-Control", "no-cache");
        await response.Body.WriteAsync(body, ct);
        return response;
    }

    private static bool IsFeedHash(string value) =>
        value.Length == 16 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// Compares a client-supplied validator to our stored content hash,
    /// tolerating the quotes, weak-validator (`W/`) prefix, and `*` (matches any
    /// existing representation) that HTTP allows.
    private static bool ETagMatches(string provided, string stored)
    {
        var value = provided.Trim();
        if (value == "*") return true;
        if (value.StartsWith("W/", StringComparison.Ordinal)) value = value[2..];
        return value.Trim().Trim('"') == stored;
    }

    private static string Quote(string etag) => $"\"{etag}\"";
}
