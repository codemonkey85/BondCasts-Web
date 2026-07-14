using System.Security.Cryptography;
using System.Text.Json;
using BondCasts.Feeds;
using BondCasts.Poller.Services.Cache;
using BondCasts.Poller.Services.CloudKit;
using Microsoft.Extensions.Logging;

namespace BondCasts.Poller.Services.Polling;

/// Tunables (Azure app settings / local.settings.json Values):
///   Poller__IntervalMinutes   base minutes between polls of one feed (default 20)
public sealed class PollerOptions
{
    public CloudKitOptions? CloudKit { get; init; } = CloudKitOptions.FromEnvironment();
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(
        int.TryParse(Environment.GetEnvironmentVariable("Poller__IntervalMinutes"), out var m) && m > 0 ? m : 20);

    /// PolledFeed rows the app hasn't re-touched in this long are expired
    /// (the app re-touches lastSeenAt weekly; ~30 days means the install is gone).
    public TimeSpan RegistrationMaxAge { get; } = TimeSpan.FromDays(30);

    /// Delivered NewEpisode announcements older than this are pruned.
    public TimeSpan AnnouncementMaxAge { get; } = TimeSpan.FromDays(30);

    /// A new-to-us item with a pubDate older than this is treated as backfill
    /// (feed re-added old episodes / rotated GUIDs), not news.
    public TimeSpan AnnouncePubDateWindow { get; } = TimeSpan.FromDays(14);
}

/// One poll cycle over every registered feed:
/// PolledFeed rows -> union by feedHash -> expire stale registrations ->
/// conditional GET each due feed -> FULL parse (shared with the app + web) ->
/// announce ONE NewEpisode per feed with discoveries (collapsed; each record
/// write is one banner per subscribed device) -> cache the parsed feed for the
/// server-backed feed endpoint (#174) -> persist per-feed state -> prune old
/// announcements.
public sealed class FeedPoller(
    PollerOptions options,
    FeedPollStateStore stateStore,
    FeedCacheStore cacheStore,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory)
{
    public const string HttpClientName = "poller";
    private const int MaxParallelFetches = 4;
    private const long MaxFeedBytes = 16 * 1024 * 1024; // guard against pathological feeds
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(24);
    private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(24);

    private readonly ILogger<FeedPoller> _logger = loggerFactory.CreateLogger<FeedPoller>();

    public async Task RunAsync(CancellationToken ct)
    {
        if (options.CloudKit is not { } ckOptions)
        {
            _logger.LogDebug("CloudKit credentials not configured; poller is idle.");
            return;
        }

        using var cloudKit = new CloudKitClient(ckOptions, httpClientFactory.CreateClient(HttpClientName));
        var store = new PushStore(cloudKit, loggerFactory.CreateLogger<PushStore>());
        await stateStore.EnsureTableAsync(ct);
        await cacheStore.EnsureContainerAsync(ct);

        var registrations = await store.FetchPolledFeedsAsync(ct);
        var cutoff = DateTimeOffset.UtcNow - options.RegistrationMaxAge;
        var stale = registrations.Where(r => r.LastSeenAt < cutoff).ToList();
        var fresh = registrations.Where(r => r.LastSeenAt >= cutoff).ToList();

        if (stale.Count > 0)
        {
            _logger.LogInformation("Expiring {Count} PolledFeed registrations not seen in 30d.", stale.Count);
            await store.DeleteRecordsAsync("PolledFeed", stale.Select(r => r.RecordName).ToList(), ct);
        }

        // Union device registrations: many devices may register the same feed.
        var feeds = fresh
            .GroupBy(r => r.FeedHash)
            .ToDictionary(g => g.Key, g => g.First().FeedUrl);

        var states = await stateStore.LoadAllAsync(ct);

        // Drop state (and cached blob) for feeds nobody registers anymore.
        foreach (var orphanHash in states.Keys.Except(feeds.Keys).ToList())
        {
            await stateStore.DeleteAsync(orphanHash, ct);
            await cacheStore.DeleteAsync(orphanHash, ct);
            states.Remove(orphanHash);
        }

        var now = DateTimeOffset.UtcNow;
        var due = feeds
            .Select(f => states.TryGetValue(f.Key, out var s)
                ? s
                : new FeedPollState { FeedHash = f.Key, FeedUrl = f.Value })
            .Where(s => s.NextPollAt <= now)
            .ToList();

        _logger.LogInformation("Polling {Due} of {Total} registered feeds.", due.Count, feeds.Count);

        await Parallel.ForEachAsync(
            due,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelFetches, CancellationToken = ct },
            async (state, token) =>
            {
                state.FeedUrl = feeds[state.FeedHash]; // registrations win over cached URL
                await PollFeedAsync(state, store, token);
                await stateStore.SaveAsync(state, token);
            });

        var lastPrune = await stateStore.GetLastPruneAsync(ct);
        if (lastPrune is null || now - lastPrune > PruneEvery)
        {
            await store.PruneNewEpisodesAsync(options.AnnouncementMaxAge, ct);
            await stateStore.SetLastPruneAsync(now, ct);
        }
    }

    private async Task PollFeedAsync(FeedPollState state, PushStore store, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, state.FeedUrl);
            // Only revalidate against the origin once we already hold a cached
            // blob for this feed. A feed tracked before the blob cache existed
            // (#174) has a stored ETag / Last-Modified but no blob, so an origin
            // 304 would MarkSuccess and return WITHOUT ever populating the cache
            // — leaving /feed/{hash} a permanent 404 until the feed happened to
            // publish. Forcing a full 200 on the first cache-less poll backfills
            // the blob; every later poll revalidates conditionally as before.
            if (!string.IsNullOrEmpty(state.CacheETag))
            {
                if (!string.IsNullOrEmpty(state.ETag))
                    request.Headers.TryAddWithoutValidation("If-None-Match", state.ETag);
                if (!string.IsNullOrEmpty(state.LastModified))
                    request.Headers.TryAddWithoutValidation("If-Modified-Since", state.LastModified);
            }

            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                // Unchanged upstream, and we sent validators only because a blob
                // already exists, so that cached blob is still valid. (A first,
                // cache-less poll skips the validators above and can't land here.)
                MarkSuccess(state);
                return;
            }
            response.EnsureSuccessStatusCode();

            var body = await ReadBodyAsync(response, ct);
            var feed = RssFeedParser.Parse(body, state.FeedUrl);

            var known = state.KnownIdentities.ToHashSet();
            var newItems = feed.Episodes
                .Select(episode => (Episode: episode, Hash: FeedPollStateStore.IdentityHash(episode.Guid)))
                .Where(x => !known.Contains(x.Hash))
                .ToList();

            if (!state.IsFirstObservation && newItems.Count > 0)
                await AnnounceAsync(state, store, feed, newItems.Select(x => x.Episode).ToList(), ct);

            // Newest-first merge; SaveAsync caps the window length.
            state.KnownIdentities = newItems.Select(x => x.Hash).Concat(state.KnownIdentities).ToList();
            state.ETag = response.Headers.ETag?.ToString();
            state.LastModified = response.Content.Headers.LastModified?.ToString("R");
            MarkSuccess(state);

            await UpdateCacheAsync(state, feed, ct);
        }
        // HttpClient.Timeout surfaces as TaskCanceledException (an
        // OperationCanceledException) even when our token never fired; a hung
        // feed host must take the failure/backoff path, not abort the run.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            state.FailureCount += 1;
            var backoff = options.PollInterval * Math.Pow(2, Math.Min(state.FailureCount, 6));
            state.NextPollAt = DateTimeOffset.UtcNow + (backoff > MaxBackoff ? MaxBackoff : backoff);
            state.LastPolledAt = DateTimeOffset.UtcNow;
            _logger.LogWarning(ex, "Poll failed for feed {Hash} ({Url}); failure #{Count}.",
                state.FeedHash, state.FeedUrl, state.FailureCount);
        }
    }

    /// Serializes the parsed feed and caches it for the /feed/{feedHash}
    /// endpoint, but only when its content changed since the last write — an
    /// unchanged re-upload would churn the ETag and defeat client 304s. A
    /// storage failure is logged, never fatal: the poll (and its push) stands.
    private async Task UpdateCacheAsync(FeedPollState state, ParsedFeed feed, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(feed, FeedJson.Options);
            var etag = ContentHash(json);
            if (etag == state.CacheETag) return;
            await cacheStore.WriteAsync(state.FeedHash, json, etag, ct);
            state.CacheETag = etag;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to cache parsed feed {Hash}.", state.FeedHash);
        }
    }

    private async Task AnnounceAsync(
        FeedPollState state, PushStore store, ParsedFeed feed,
        List<ParsedEpisode> newItems, CancellationToken ct)
    {
        // Backfill guard: an old pubDate on a new-to-us item means the feed
        // re-added history or rotated GUIDs, not that an episode dropped.
        var freshCutoff = DateTimeOffset.UtcNow - options.AnnouncePubDateWindow;
        var announceable = newItems
            .Where(i => i.PublishedAt is null || i.PublishedAt >= freshCutoff)
            .OrderByDescending(i => i.PublishedAt ?? DateTimeOffset.MaxValue)
            .ToList();
        if (announceable.Count == 0) return;

        var newest = announceable[0];
        var showTitle = string.IsNullOrEmpty(feed.Title)
            ? new Uri(state.FeedUrl).Host
            : feed.Title;
        var alertBody = announceable.Count == 1
            ? (string.IsNullOrEmpty(newest.Title) ? "New episode" : newest.Title)
            : $"{announceable.Count} new episodes";

        await store.AnnounceNewEpisodeAsync(
            state.FeedHash, showTitle, alertBody, newest.Guid, announceable.Count, ct);
        _logger.LogInformation("Announced {Count} new episode(s) for {Show} ({Hash}).",
            announceable.Count, showTitle, state.FeedHash);
    }

    private void MarkSuccess(FeedPollState state)
    {
        var jitter = TimeSpan.FromSeconds(Random.Shared.Next(0, (int)(options.PollInterval.TotalSeconds / 5)));
        state.FailureCount = 0;
        state.LastPolledAt = DateTimeOffset.UtcNow;
        state.NextPollAt = DateTimeOffset.UtcNow + options.PollInterval + jitter;
    }

    /// Reads the response body into memory with a hard size cap so a
    /// pathological multi-hundred-MB "feed" can't exhaust the worker.
    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxFeedBytes)
                throw new InvalidOperationException($"Feed exceeds {MaxFeedBytes}-byte cap.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// 128-bit hex of the served JSON — a content ETag that changes exactly when
    /// the served bytes change, regardless of the upstream host's ETag habits.
    private static string ContentHash(byte[] json) =>
        Convert.ToHexString(SHA256.HashData(json))[..32].ToLowerInvariant();
}
