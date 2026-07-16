using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace BondCasts.Poller.Services.CloudKit;

/// A device registration for one feed, read back from a PolledFeed record.
/// The app is the ONLY place feedHash is derived (first 16 hex chars of
/// SHA-256 over its normalized feed URL) — the server must READ hashes from
/// these rows, never recompute, so there is no cross-language drift.
public sealed record PolledFeedRow(
    string RecordName, string FeedUrl, string FeedHash, DateTimeOffset LastSeenAt);

/// Typed operations over the app container's public database for the poller:
/// read/expire PolledFeed registrations, announce NewEpisode discoveries,
/// prune old announcements. Record type and field names mirror the app's
/// ServerPushService (PolledFeed: feedURL/feedHash/lastSeenAt; NewEpisode:
/// feedHash/showTitle/alertBody/episodeGUID/episodeGUIDHash/episodeCount).
public sealed class PushStore(CloudKitClient cloudKit, ILogger<PushStore> logger)
{
    private const string PolledFeedType = "PolledFeed";
    private const string NewEpisodeType = "NewEpisode";
    private const int ModifyChunkSize = 100; // same chunking the app uses
    private const int QueryPageSize = 200;

    // CloudKit's push payload truncates string values longer than 100 chars;
    // trim what we write so banners never show a mid-word cut with "…" lost.
    private const int MaxAlertFieldLength = 100;

    /// All PolledFeed registrations, paginated. Throws on any non-200 page so
    /// a partial read is never mistaken for the full registration set (expiry
    /// deletes rows absent from this list's fresh side).
    public async Task<List<PolledFeedRow>> FetchPolledFeedsAsync(CancellationToken ct)
    {
        var rows = new List<PolledFeedRow>();
        JsonNode? continuation = null;
        do
        {
            var body = new JsonObject
            {
                ["query"] = new JsonObject { ["recordType"] = PolledFeedType },
                ["resultsLimit"] = QueryPageSize,
            };
            if (continuation is not null)
                body["continuationMarker"] = continuation.DeepClone();

            var (status, response) = await cloudKit.PostAsync("records/query", body, ct);
            if (status != 200)
                throw new InvalidOperationException(
                    $"PolledFeed query failed: HTTP {status} {response?["serverErrorCode"]}");

            foreach (var record in response?["records"]?.AsArray() ?? [])
            {
                var fields = record?["fields"];
                var recordName = record?["recordName"]?.GetValue<string>();
                var feedUrl = fields?["feedURL"]?["value"]?.GetValue<string>();
                var feedHash = fields?["feedHash"]?["value"]?.GetValue<string>();
                var lastSeenMs = fields?["lastSeenAt"]?["value"]?.GetValue<long>();
                if (recordName is null || feedUrl is null || feedHash is null || lastSeenMs is null)
                {
                    logger.LogWarning("Skipping malformed PolledFeed record {Name}.", recordName ?? "?");
                    continue;
                }
                rows.Add(new PolledFeedRow(
                    recordName, feedUrl, feedHash,
                    DateTimeOffset.FromUnixTimeMilliseconds(lastSeenMs.Value)));
            }
            continuation = response?["continuationMarker"];
        } while (continuation is not null);
        return rows;
    }

    /// Writes ONE NewEpisode announcement for a feed discovery cycle. Multi-
    /// episode discoveries are collapsed by the caller into a single record
    /// (episodeCount > 1, alertBody "N new episodes") because every record
    /// write fans out as one banner per subscribed device.
    public async Task AnnounceNewEpisodeAsync(
        string feedHash, string showTitle, string alertBody, string? episodeGuid,
        int episodeCount, CancellationToken ct)
    {
        var fields = new JsonObject
        {
            ["feedHash"] = new JsonObject { ["value"] = feedHash },
            ["showTitle"] = new JsonObject { ["value"] = Truncate(showTitle) },
            ["alertBody"] = new JsonObject { ["value"] = Truncate(alertBody) },
            ["episodeCount"] = new JsonObject { ["value"] = episodeCount },
        };
        // episodeGUID is only meaningful when count == 1 (deep-link target).
        // Stored untruncated: Apple truncates the PUSH payload's copy at 100
        // chars on its own; the record keeps the full value. Because of that
        // truncation, long URL-shaped GUIDs (the WordPress ?p=… pattern) reach
        // the app cut off, so also ship a short hash the app can match against
        // hashes of its own full GUIDs (#19; client side PodcastApp#195).
        if (episodeCount == 1 && !string.IsNullOrEmpty(episodeGuid))
        {
            fields["episodeGUID"] = new JsonObject { ["value"] = episodeGuid };
            fields["episodeGUIDHash"] = new JsonObject { ["value"] = GuidHash(episodeGuid) };
        }

        var body = new JsonObject
        {
            ["operations"] = new JsonArray(new JsonObject
            {
                ["operationType"] = "create",
                ["record"] = new JsonObject
                {
                    ["recordType"] = NewEpisodeType,
                    ["recordName"] = $"ne-{feedHash}-{DateTime.UtcNow:yyyyMMddHHmmss}",
                    ["fields"] = fields,
                },
            }),
        };

        var (status, response) = await cloudKit.PostAsync("records/modify", body, ct);
        var recordError = (response?["records"]?.AsArray() ?? [])
            .Select(r => r?["serverErrorCode"]?.GetValue<string>())
            .FirstOrDefault(code => code is not null);
        if (status != 200 || recordError is not null)
            throw new InvalidOperationException(
                $"NewEpisode create failed: HTTP {status} {recordError ?? response?["serverErrorCode"]?.GetValue<string>()}");
    }

    /// Deletes records in chunks; per-record failures are logged and skipped
    /// (a failed expiry retries naturally on the next cycle).
    public async Task DeleteRecordsAsync(
        string recordType, IReadOnlyList<string> recordNames, CancellationToken ct)
    {
        foreach (var chunk in recordNames.Chunk(ModifyChunkSize))
        {
            var operations = new JsonArray();
            foreach (var name in chunk)
            {
                operations.Add(new JsonObject
                {
                    ["operationType"] = "forceDelete",
                    ["record"] = new JsonObject
                    {
                        ["recordType"] = recordType,
                        ["recordName"] = name,
                    },
                });
            }
            var (status, response) = await cloudKit.PostAsync(
                "records/modify", new JsonObject { ["operations"] = operations }, ct);
            if (status != 200)
                logger.LogWarning("Delete chunk of {Count} {Type} records failed: HTTP {Status} {Code}.",
                    chunk.Length, recordType, status, response?["serverErrorCode"]);
        }
    }

    /// NewEpisode rows older than maxAge have long since delivered their
    /// pushes; prune our own litter. Uses record creation timestamps from a
    /// paginated query.
    public async Task PruneNewEpisodesAsync(TimeSpan maxAge, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var stale = new List<string>();
        JsonNode? continuation = null;
        do
        {
            var body = new JsonObject
            {
                ["query"] = new JsonObject { ["recordType"] = NewEpisodeType },
                ["resultsLimit"] = QueryPageSize,
                ["desiredKeys"] = new JsonArray("feedHash"),
            };
            if (continuation is not null)
                body["continuationMarker"] = continuation.DeepClone();

            var (status, response) = await cloudKit.PostAsync("records/query", body, ct);
            if (status != 200)
            {
                logger.LogWarning("NewEpisode prune query failed: HTTP {Status} {Code}.",
                    status, response?["serverErrorCode"]);
                return;
            }
            foreach (var record in response?["records"]?.AsArray() ?? [])
            {
                var createdMs = record?["created"]?["timestamp"]?.GetValue<long>();
                var name = record?["recordName"]?.GetValue<string>();
                if (name is not null && createdMs is not null &&
                    DateTimeOffset.FromUnixTimeMilliseconds(createdMs.Value) < cutoff)
                    stale.Add(name);
            }
            continuation = response?["continuationMarker"];
        } while (continuation is not null);

        if (stale.Count > 0)
        {
            logger.LogInformation("Pruning {Count} NewEpisode records older than {Days}d.",
                stale.Count, maxAge.TotalDays);
            await DeleteRecordsAsync(NewEpisodeType, stale, ct);
        }
    }

    /// First 16 hex chars (lowercase) of SHA-256 over the exact episodeGUID
    /// string written to the record — the same derivation as the app's
    /// `FeedURL.feedHash`, so the client recomputes it from its full parsed
    /// GUID with code it already has. NOT the poller's internal
    /// `FeedPollStateStore.IdentityHash` (uppercase, and hashes a
    /// query-stripped identity for tokenized enclosure-URL fallbacks).
    public static string GuidHash(string guid) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(guid)))[..16].ToLowerInvariant();

    private static string Truncate(string value) =>
        value.Length <= MaxAlertFieldLength ? value : value[..(MaxAlertFieldLength - 1)] + "…";
}
