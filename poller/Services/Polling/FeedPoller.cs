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

    /// A single poll that finds more than this many new-to-us fresh items for
    /// one feed is treated as a flood (a partial re-baseline, evicted history,
    /// or a rotated-GUID feed) rather than genuine news: the items are still
    /// absorbed into the known window, but no push is sent. A continuously
    /// polled feed publishes at most one or two per interval, so this only
    /// trips on anomalies.
    public int MaxAnnouncePerCycle { get; } = 3;
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
            // A PolledFeed row is user-writable input (any iCloud account can
            // register one): refuse anything but plain http(s) before it
            // reaches the network stack. Resolved-address checks (private
            // ranges, the IMDS endpoint) happen per-connection in
            // FeedUrlGuard.GuardedConnectAsync. Throwing here lands in the
            // normal failure/backoff path below.
            if (!FeedUrlGuard.TryValidate(state.FeedUrl, out var refusal))
                throw new InvalidOperationException($"Refusing feed URL: {refusal}.");

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
            var identified = feed.Episodes
                .Select(episode => (Episode: episode, Hash: FeedPollStateStore.IdentityHash(StableIdentity(episode))))
                .ToList();
            var newItems = identified.Where(x => !known.Contains(x.Hash)).ToList();

            // Re-baseline guard: a non-empty known window that the current feed
            // (which must itself be non-empty — an empty parse is not a
            // re-baseline) overlaps ZERO of means the identity scheme changed (a
            // deploy that altered GUID/identity derivation) or the feed rotated
            // its whole catalog — not that every episode is genuinely new. Any
            // count then, including a single item, would be a spurious banner
            // (a "1 new episode" storm across every feed on deploy), so reseed
            // silently instead of announcing. Derived from the already-computed
            // counts (all identified items are new ⇒ zero overlap).
            var rebaseline = known.Count > 0 && identified.Count > 0 && newItems.Count == identified.Count;
            if (rebaseline)
                _logger.LogInformation(
                    "Re-baselining {Hash}: no overlap with the known window; seeding {Count} identities silently.",
                    state.FeedHash, identified.Count);

            // pubDate high-water mark: the newest episode pubDate this feed has
            // ever shown us. We announce only items strictly newer than it, which
            // is immune to the two flood sources identity tracking can't fully
            // stop — GUID rotation (a re-seen item gets a fresh identity) and
            // known-window eviction — because a re-seen episode never has a
            // pubDate beyond the mark. Seed it SILENTLY (announce nothing) on a
            // first observation, a re-baseline, or an install predating the field
            // (null mark), then only genuinely newer episodes ever announce.
            var maxPub = feed.Episodes
                .Select(e => e.PublishedAt)
                .Where(d => d.HasValue)
                .Select(d => d!.Value)
                .DefaultIfEmpty(DateTimeOffset.MinValue)
                .Max();
            var seeding = state.IsFirstObservation || rebaseline || state.HighWaterPublishedAt is null;

            if (!seeding && newItems.Count > 0)
                await AnnounceAsync(state, store, feed, newItems.Select(x => x.Episode).ToList(), ct);

            // Advance the mark to the newest pubDate seen (never rewind).
            if (maxPub > (state.HighWaterPublishedAt ?? DateTimeOffset.MinValue))
                state.HighWaterPublishedAt = maxPub;

            // Rebuild the known window from the CURRENT feed ordered by pubDate
            // (newest first), then keep previously-known identities no longer
            // present. Retention is now pubDate-based rather than
            // document-position based: a recent episode sitting late in a large
            // feed (.NET Rocks has 2000+ items) can no longer be evicted past
            // the cap and re-announced every cycle. SaveAsync caps the length.
            var currentByRecency = identified
                .OrderByDescending(x => x.Episode.PublishedAt ?? DateTimeOffset.MinValue)
                .Select(x => x.Hash);
            state.KnownIdentities = currentByRecency
                .Concat(state.KnownIdentities)
                .Distinct()
                .ToList();
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
            _logger.LogWarning(ex, "Poll failed for feed {Hash}; failure #{Count}.",
                state.FeedHash, state.FailureCount);
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
        // Announce only items strictly newer than the feed's high-water mark.
        // The caller skips this method while seeding (first observation /
        // re-baseline / legacy null row), so in normal operation the mark is
        // already set; the `?? 14-day window` is a defensive fallback for any
        // unforeseen path that reaches here with a null mark. This is the
        // primary backfill/re-announce guard: re-added history, rotated GUIDs,
        // and evicted-then-re-seen items all carry a pubDate at or below the
        // mark and are dropped. A null pubDate can't be proven newer, so it's
        // never announced.
        var floor = state.HighWaterPublishedAt ?? (DateTimeOffset.UtcNow - options.AnnouncePubDateWindow);
        var announceable = newItems
            .Where(i => i.PublishedAt is { } p && p > floor)
            .OrderByDescending(i => i.PublishedAt)
            .ToList();
        if (announceable.Count == 0) return;

        // Flood guard: an anomalously large fresh batch (a partial re-baseline,
        // evicted history, or a rotated-GUID feed) is absorbed into the known
        // window by the caller but not pushed — a continuously polled feed
        // publishes at most one or two per interval.
        if (announceable.Count > options.MaxAnnouncePerCycle)
        {
            _logger.LogWarning(
                "Suppressing {Count} announcements for {Hash} (> cap {Cap}); seeding silently.",
                announceable.Count, state.FeedHash, options.MaxAnnouncePerCycle);
            return;
        }

        var newest = announceable[0];
        var showTitle = string.IsNullOrEmpty(feed.Title)
            ? new Uri(state.FeedUrl).Host
            : feed.Title;
        var alertBody = announceable.Count == 1
            ? (string.IsNullOrEmpty(newest.Title) ? "New episode" : newest.Title)
            : $"{announceable.Count} new episodes";

        await store.AnnounceNewEpisodeAsync(
            state.FeedHash, showTitle, alertBody, newest.Guid, announceable.Count, ct);
        _logger.LogInformation("Announced {Count} new episode(s) for feed {Hash}.",
            announceable.Count, state.FeedHash);
    }

    /// Identity used for new-episode detection. When a feed provides no
    /// &lt;guid&gt;, the parser falls back to the enclosure URL (so shared-link
    /// keys stay aligned with the app). For tokenized private feeds
    /// (supportingcast/Patreon) that URL carries a rotating signed token
    /// (?token=…&amp;expires=…), so a naive identity changes every fetch and the
    /// episode looks perpetually new — the source of the Vergecast flood. Strip
    /// the query/fragment in that fallback case only. The announced episodeGUID
    /// still uses the full episode.Guid, so the app's episode key is unaffected.
    private static string StableIdentity(ParsedEpisode episode)
    {
        if (episode.Guid != episode.EnclosureUrl) return episode.Guid;
        // GetLeftPart(Path) drops both query and fragment, so strip when either
        // is present (a signed token can ride in the fragment, not just ?query).
        return Uri.TryCreate(episode.Guid, UriKind.Absolute, out var uri)
               && (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            ? uri.GetLeftPart(UriPartial.Path)
            : episode.Guid;
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
