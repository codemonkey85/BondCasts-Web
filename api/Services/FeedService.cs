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
    private const int MaxRedirects = 5;
    private const int MaxFeedBytes = 12 * 1024 * 1024;

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
        try
        {
            return (await ResolveFeedAsync(feedUrl, ct)).Feed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch/parse feed {FeedUrl}.", feedUrl);
            return null;
        }
    }

    public async Task<ResolvedFeed> ResolveFeedAsync(string feedUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var requested)
            || (requested.Scheme != Uri.UriSchemeHttp && requested.Scheme != Uri.UriSchemeHttps))
            throw new UnsafeFeedUrlException("Feed URL must be an absolute http(s) URL.");

        var cacheEligible = FeedUrlPolicy.CanCacheFeed(requested, isLocked: false);
        var cacheKey = $"resolved-feed:{requested.AbsoluteUri}";
        if (cacheEligible && _cache.TryGetValue(cacheKey, out ResolvedFeed? cached) && cached is not null)
            return cached;

        var current = requested;
        var client = _httpClientFactory.CreateClient(HttpClientName);
        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            if (!await FeedUrlPolicy.IsPublicHttpUrlAsync(current, ct))
                throw new UnsafeFeedUrlException("Feed URL must resolve to a public internet address.");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (IsRedirect(response.StatusCode))
            {
                if (redirect == MaxRedirects || response.Headers.Location is null)
                    throw new FeedResolutionException("The feed redirected too many times or returned an invalid redirect.");
                current = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw new FeedResolutionException($"The podcast host returned HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength is > MaxFeedBytes)
                throw new FeedResolutionException("The podcast feed is too large to resolve safely.");

            var data = await ReadBoundedAsync(response.Content, ct);
            ParsedFeed parsed;
            try
            {
                parsed = RssFeedParser.Parse(data, current.AbsoluteUri);
            }
            catch (Exception ex) when (ex is FormatException or System.Xml.XmlException)
            {
                throw new FeedResolutionException("The URL did not return a valid podcast RSS feed.", ex);
            }
            if (string.IsNullOrWhiteSpace(parsed.Title))
                throw new FeedResolutionException("The podcast feed does not contain a title.");

            var resolved = new ResolvedFeed(requested.AbsoluteUri, current.AbsoluteUri, parsed);
            if (cacheEligible && FeedUrlPolicy.CanCacheFeed(current, parsed.IsLocked))
                _cache.Set(cacheKey, resolved, CacheTtl);
            return resolved;
        }

        throw new FeedResolutionException("The podcast feed could not be resolved.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (output.Length + read > MaxFeedBytes)
                throw new FeedResolutionException("The podcast feed is too large to resolve safely.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static bool IsRedirect(System.Net.HttpStatusCode status) => status is
        System.Net.HttpStatusCode.MovedPermanently or
        System.Net.HttpStatusCode.Redirect or
        System.Net.HttpStatusCode.SeeOther or
        System.Net.HttpStatusCode.TemporaryRedirect or
        System.Net.HttpStatusCode.PermanentRedirect;
}

public sealed record ResolvedFeed(string RequestedUrl, string FinalUrl, ParsedFeed Feed);

public sealed class UnsafeFeedUrlException(string message) : Exception(message);

public sealed class FeedResolutionException : Exception
{
    public FeedResolutionException(string message) : base(message) { }
    public FeedResolutionException(string message, Exception innerException) : base(message, innerException) { }
}
