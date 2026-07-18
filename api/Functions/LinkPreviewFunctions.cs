using System.Net;
using System.Text.Json;
using System.Web;
using BondCasts.Api.Rendering;
using BondCasts.Api.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace BondCasts.Api.Functions;

/// Server-rendered landing pages for the universal-link paths. Static Web Apps
/// rewrites `/episode` → `/api/episode`, `/show` → `/api/show`, `/e/*` →
/// `/api/e/*`, and `/s/*` → `/api/s/*` (see staticwebapp.config.json), so these
/// answer on bondcasts.com directly.
///
/// The links carry the same identifiers the app uses: `feed` (the podcast's RSS
/// URL) identifies the show; `guid` (the RSS <guid>) identifies the episode.
public sealed class LinkPreviewFunctions
{
    private readonly FeedService _feeds;
    private readonly PageRenderer _renderer;
    private readonly ShareLinkTokenProtector _tokens;
    private readonly ILogger<LinkPreviewFunctions> _logger;

    public LinkPreviewFunctions(
        FeedService feeds,
        PageRenderer renderer,
        ShareLinkTokenProtector tokens,
        ILogger<LinkPreviewFunctions> logger)
    {
        _feeds = feeds;
        _renderer = renderer;
        _tokens = tokens;
        _logger = logger;
    }

    [Function("episode")]
    public async Task<HttpResponseData> Episode(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "episode")] HttpRequestData req,
        CancellationToken ct)
    {
        var query = HttpUtility.ParseQueryString(req.Url.Query);
        var feedUrl = query["feed"];
        var guid = query["guid"];
        var canonical = req.Url.ToString();

        if (string.IsNullOrWhiteSpace(feedUrl) || string.IsNullOrWhiteSpace(guid))
            return await Html(req, _renderer.RenderFallback("episode", feedUrl, canonical));

        return await RenderResolvedEpisode(req, feedUrl, guid, canonical, ct);
    }

    [Function("show")]
    public async Task<HttpResponseData> Show(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "show")] HttpRequestData req,
        CancellationToken ct)
    {
        var query = HttpUtility.ParseQueryString(req.Url.Query);
        var feedUrl = query["feed"];
        var canonical = req.Url.ToString();

        if (string.IsNullOrWhiteSpace(feedUrl))
            return await Html(req, _renderer.RenderFallback("podcast", feedUrl, canonical));

        var feed = await _feeds.GetFeedAsync(feedUrl, ct);
        return feed is null
            ? await Html(req, _renderer.RenderFallback("podcast", feedUrl, canonical))
            : await Html(req, _renderer.RenderShow(feed, canonical));
    }

    [Function("shared-episode")]
    public async Task<HttpResponseData> SharedEpisode(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "e/{token}")] HttpRequestData req,
        string token,
        CancellationToken ct)
    {
        token = EffectiveSharedLinkToken(req, "e", token);
        var canonical = CanonicalSharedLinkUrl("e", token);
        var payload = _tokens.ResolveEpisode(token);
        return payload is null || payload.Guid is null
            ? await Html(req, _renderer.RenderOpaqueFallback("episode", token, canonical, openUrl: canonical))
            : await RenderResolvedEpisode(req, payload.Feed, payload.Guid, canonical, ct, opaqueFallbackToken: token, openUrl: canonical);
    }

    [Function("shared-show")]
    public async Task<HttpResponseData> SharedShow(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "s/{token}")] HttpRequestData req,
        string token,
        CancellationToken ct)
    {
        token = EffectiveSharedLinkToken(req, "s", token);
        var canonical = CanonicalSharedLinkUrl("s", token);
        var payload = _tokens.ResolveShow(token);
        if (payload is null)
            return await Html(req, _renderer.RenderOpaqueFallback("podcast", token, canonical, openUrl: canonical));

        var feed = await _feeds.GetFeedAsync(payload.Feed, ct);
        return feed is null
            ? await Html(req, _renderer.RenderOpaqueFallback("podcast", token, canonical, openUrl: canonical))
            : await Html(req, _renderer.RenderShow(feed, canonical, openUrl: canonical));
    }

    [Function("resolve-shared-episode")]
    public Task<HttpResponseData> ResolveSharedEpisode(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "share/episode/{token}")] HttpRequestData req,
        string token)
    {
        var payload = _tokens.ResolveEpisode(token);
        return payload is null || payload.Guid is null
            ? Json(req, HttpStatusCode.NotFound, new { error = "not_found" })
            : Json(req, HttpStatusCode.OK, new { feed = payload.Feed, guid = payload.Guid });
    }

    [Function("resolve-shared-show")]
    public Task<HttpResponseData> ResolveSharedShow(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "share/show/{token}")] HttpRequestData req,
        string token)
    {
        var payload = _tokens.ResolveShow(token);
        return payload is null
            ? Json(req, HttpStatusCode.NotFound, new { error = "not_found" })
            : Json(req, HttpStatusCode.OK, new { feed = payload.Feed });
    }

    private async Task<HttpResponseData> RenderResolvedEpisode(
        HttpRequestData req,
        string feedUrl,
        string guid,
        string canonical,
        CancellationToken ct,
        string? opaqueFallbackToken = null,
        string? openUrl = null)
    {
        var feed = await _feeds.GetFeedAsync(feedUrl, ct);
        var episode = feed?.Episodes.FirstOrDefault(e => string.Equals(e.Guid, guid, StringComparison.Ordinal));

        if (feed is null || episode is null)
        {
            // Feed unreachable, or the guid has aged out of the feed window
            // (the same limitation the app hits) — serve the generic card.
            _logger.LogInformation("Episode not resolved (feed={FeedNull}, guid found={Found}).", feed is null, episode is not null);
            if (!string.IsNullOrEmpty(opaqueFallbackToken))
                return await Html(req, _renderer.RenderOpaqueFallback("episode", opaqueFallbackToken, canonical, openUrl: openUrl));

            return await Html(req, _renderer.RenderFallback("episode", feedUrl, canonical));
        }

        return await Html(req, _renderer.RenderEpisode(feed, episode, canonical, openUrl: openUrl));
    }

    private static string CanonicalSharedLinkUrl(string prefix, string token)
    {
        return $"https://bondcasts.com/{prefix}/{Uri.EscapeDataString(token)}";
    }

    private static string EffectiveSharedLinkToken(HttpRequestData req, string prefix, string routeToken)
    {
        if (routeToken != "*")
            return routeToken;

        return SharedLinkTokenFromHeader(req, "x-ms-original-url", prefix)
            ?? SharedLinkTokenFromHeader(req, "x-original-url", prefix)
            ?? routeToken;
    }

    private static string? SharedLinkTokenFromHeader(HttpRequestData req, string headerName, string prefix)
    {
        if (!req.Headers.TryGetValues(headerName, out var values))
            return null;

        foreach (var value in values)
        {
            if (SharedLinkTokenFromOriginalPath(value, prefix) is { } token)
                return token;
        }

        return null;
    }

    private static string? SharedLinkTokenFromOriginalPath(string value, string prefix)
    {
        var path = Uri.TryCreate(value, UriKind.Absolute, out var absolute)
                   && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            ? absolute.AbsolutePath
            : value.Split('?', '#')[0];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || !string.Equals(segments[0], prefix, StringComparison.Ordinal))
            return null;

        if (HasMalformedPercentEncoding(segments[1]))
            return null;

        var token = Uri.UnescapeDataString(segments[1]);
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static bool HasMalformedPercentEncoding(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
                continue;

            if (index + 2 >= value.Length
                || !Uri.IsHexDigit(value[index + 1])
                || !Uri.IsHexDigit(value[index + 2]))
                return true;
        }

        return false;
    }

    private static async Task<HttpResponseData> Html(HttpRequestData req, string html)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "text/html; charset=utf-8");
        // Let the CDN/crawler cache briefly; matches the feed cache horizon and
        // keeps repeat unfurls cheap.
        response.Headers.Add("Cache-Control", "public, max-age=900");
        await response.WriteStringAsync(html);
        return response;
    }

    private static async Task<HttpResponseData> Json(HttpRequestData req, HttpStatusCode status, object body)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "private, no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(body));
        return response;
    }
}
