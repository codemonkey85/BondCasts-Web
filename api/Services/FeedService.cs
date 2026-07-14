using BondCasts.Feeds;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BondCasts.Api.Services;

/// Fetches and parses a podcast feed, caching the parsed result so bursts of
/// link-preview crawler hits don't re-download a (potentially multi-MB) feed
/// each time. Server-to-server fetch, so no browser CORS restrictions apply.
public sealed class FeedService
{
    public const string HttpClientName = "feed";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);
    private const long MaxFeedBytes = 12 * 1024 * 1024; // guard against pathological feeds

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<FeedService> _logger;

    public FeedService(IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<FeedService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    /// Returns the parsed feed, or null if it can't be fetched/parsed (the caller
    /// then falls back to a generic preview rather than erroring).
    public async Task<ParsedFeed?> GetFeedAsync(string feedUrl, CancellationToken ct)
    {
        if (!IsSafeFeedUrl(feedUrl)) return null;

        if (_cache.TryGetValue(feedUrl, out ParsedFeed? cached))
            return cached;

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(feedUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is > MaxFeedBytes)
            {
                _logger.LogWarning("Feed {FeedUrl} exceeds size cap; skipping.", feedUrl);
                return null;
            }

            var xml = await response.Content.ReadAsStringAsync(ct);
            var feed = RssFeedParser.Parse(xml, feedUrl);
            _cache.Set(feedUrl, feed, CacheTtl);
            return feed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch/parse feed {FeedUrl}.", feedUrl);
            return null;
        }
    }

    /// Only allow absolute http(s) URLs. Blocks the obvious SSRF footguns
    /// (file://, non-web schemes) before we make a server-side request.
    private static bool IsSafeFeedUrl(string feedUrl) =>
        Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
