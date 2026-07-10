using System.Net;
using System.Web;
using BondCasts.Api.Rendering;
using BondCasts.Api.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace BondCasts.Api.Functions;

/// Server-rendered landing pages for the universal-link paths. Static Web Apps
/// rewrites `/episode` → `/api/episode` and `/show` → `/api/show` (see
/// staticwebapp.config.json), so these answer on bondcasts.com directly.
///
/// The links carry the same identifiers the app uses: `feed` (the podcast's RSS
/// URL) identifies the show; `guid` (the RSS <guid>) identifies the episode.
public sealed class LinkPreviewFunctions
{
    private readonly FeedService _feeds;
    private readonly PageRenderer _renderer;
    private readonly ILogger<LinkPreviewFunctions> _logger;

    public LinkPreviewFunctions(FeedService feeds, PageRenderer renderer, ILogger<LinkPreviewFunctions> logger)
    {
        _feeds = feeds;
        _renderer = renderer;
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

        var feed = await _feeds.GetFeedAsync(feedUrl, ct);
        var episode = feed?.Episodes.FirstOrDefault(e => string.Equals(e.Guid, guid, StringComparison.Ordinal));

        if (feed is null || episode is null)
        {
            // Feed unreachable, or the guid has aged out of the feed window
            // (the same limitation the app hits) — serve the generic card.
            _logger.LogInformation("Episode not resolved (feed={FeedNull}, guid found={Found}).", feed is null, episode is not null);
            return await Html(req, _renderer.RenderFallback("episode", feedUrl, canonical));
        }

        return await Html(req, _renderer.RenderEpisode(feed, episode, canonical));
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
}
