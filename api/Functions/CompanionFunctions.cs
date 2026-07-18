using System.Net;
using System.Web;
using BondCasts.Api.Models;
using BondCasts.Api.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace BondCasts.Api.Functions;

public sealed class CompanionFunctions
{
    private readonly PodcastDirectoryService _directory;
    private readonly FeedService _feeds;

    public CompanionFunctions(PodcastDirectoryService directory, FeedService feeds)
    {
        _directory = directory;
        _feeds = feeds;
    }

    [Function("companion-config")]
    public async Task<HttpResponseData> Configuration(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "companion/config")] HttpRequestData req,
        CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable("BONDCASTS_CLOUDKIT_WEB_API_TOKEN");
        var environment = Environment.GetEnvironmentVariable("BONDCASTS_CLOUDKIT_ENVIRONMENT")?.ToLowerInvariant();
        if (environment is not ("development" or "production")) environment = "production";
        var enabled = !string.IsNullOrWhiteSpace(token);
        var writesEnabled = enabled && bool.TryParse(
            Environment.GetEnvironmentVariable("BONDCASTS_CLOUDKIT_WRITES_ENABLED"), out var writes) && writes;

        return await Json(req, HttpStatusCode.OK, new
        {
            enabled,
            containerIdentifier = "iCloud.com.bondcodes.PodcastApp",
            environment,
            apiToken = enabled ? token : null,
            writesEnabled
        }, ct, "no-store");
    }

    [Function("podcast-search")]
    public async Task<HttpResponseData> Search(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "podcasts/search")] HttpRequestData req,
        CancellationToken ct)
    {
        var query = HttpUtility.ParseQueryString(req.Url.Query);
        var term = query["term"]?.Trim() ?? string.Empty;
        if (term.Length is < 2 or > 100)
            return await Json(req, HttpStatusCode.BadRequest, new { error = "Enter between 2 and 100 characters." }, ct);
        var limit = int.TryParse(query["limit"], out var parsedLimit) ? parsedLimit : 20;

        try
        {
            var results = await _directory.SearchAsync(term, limit, ct);
            return await Json(req, HttpStatusCode.OK, new { results }, ct, "public, max-age=300");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return await Json(req, HttpStatusCode.BadGateway, new { error = "Podcast search is temporarily unavailable." }, ct);
        }
    }

    [Function("podcast-resolve")]
    public async Task<HttpResponseData> Resolve(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "podcasts/resolve")] HttpRequestData req,
        CancellationToken ct)
    {
        var query = HttpUtility.ParseQueryString(req.Url.Query);
        var feedUrl = query["url"]?.Trim() ?? string.Empty;
        if (feedUrl.Length is < 8 or > 2048)
            return await Json(req, HttpStatusCode.BadRequest, new { error = "Enter a valid podcast feed URL." }, ct);

        try
        {
            var resolved = await _feeds.ResolveFeedAsync(feedUrl, ct);
            DirectoryPodcast? directory = null;
            if (long.TryParse(query["itunesID"], out var itunesId) && itunesId > 0)
                directory = await _directory.ConfirmAsync(itunesId, resolved.RequestedUrl, ct);

            var feed = resolved.Feed;
            return await Json(req, HttpStatusCode.OK, new ResolvedPodcastResponse(
                resolved.RequestedUrl,
                resolved.FinalUrl,
                feed.Title,
                feed.Author,
                feed.ArtworkUrl ?? directory?.ArtworkUrl,
                feed.Description,
                feed.Episodes.Count,
                directory?.ItunesId,
                PodcastWebsiteUrl.FromFeed(feed.Link)), ct, "public, max-age=900");
        }
        catch (UnsafeFeedUrlException ex)
        {
            return await Json(req, HttpStatusCode.BadRequest, new { error = ex.Message }, ct);
        }
        catch (FeedResolutionException ex)
        {
            return await Json(req, HttpStatusCode.UnprocessableEntity, new { error = ex.Message }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return await Json(req, HttpStatusCode.BadGateway, new { error = "The podcast host could not be reached." }, ct);
        }
    }

    private static async Task<HttpResponseData> Json(
        HttpRequestData request,
        HttpStatusCode status,
        object body,
        CancellationToken ct,
        string cacheControl = "no-store")
    {
        var response = request.CreateResponse(status);
        response.Headers.Add("Cache-Control", cacheControl);
        response.Headers.Add("X-Content-Type-Options", "nosniff");
        await response.WriteAsJsonAsync(body, ct);
        return response;
    }
}
