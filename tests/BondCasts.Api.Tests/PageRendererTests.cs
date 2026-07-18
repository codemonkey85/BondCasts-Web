using BondCasts.Api.Rendering;
using BondCasts.Feeds;
using Xunit;

namespace BondCasts.Api.Tests;

public sealed class PageRendererTests
{
    [Fact]
    public void ShowPage_UsesCanonicalUrlForOpenGraphAndCallout()
    {
        var html = new PageRenderer().RenderShow(
            SampleFeed(),
            "https://bondcasts.com/s/v1.2026-07.token",
            openUrl: "https://bondcasts.com/s/v1.2026-07.token");

        Assert.Contains("""<meta property="og:url" content="https://bondcasts.com/s/v1.2026-07.token">""", html);
        Assert.Contains("""<a class="brand" href="https://bondcasts.com/s/v1.2026-07.token">Open podcast in BondCasts</a>""", html);
    }

    [Fact]
    public void EpisodePage_UsesCanonicalUrlForOpenGraphAndCallout()
    {
        var feed = SampleFeed();
        var html = new PageRenderer().RenderEpisode(
            feed,
            feed.Episodes[0],
            "https://bondcasts.com/e/v1.2026-07.token",
            openUrl: "https://bondcasts.com/e/v1.2026-07.token");

        Assert.Contains("""<meta property="og:url" content="https://bondcasts.com/e/v1.2026-07.token">""", html);
        Assert.Contains("""<a class="brand" href="https://bondcasts.com/e/v1.2026-07.token">Open episode in BondCasts</a>""", html);
    }

    [Fact]
    public void PageCallout_FallsBackToHomeForUnsafeOpenUrl()
    {
        var html = new PageRenderer().RenderShow(
            SampleFeed(),
            "https://bondcasts.com/s/v1.2026-07.token",
            openUrl: "https://example.com/s/v1.2026-07.token");

        Assert.Contains("""<a class="brand" href="/">Open podcast in BondCasts</a>""", html);
    }

    [Fact]
    public void PageCallout_AllowsBondCastsUrlsCaseInsensitively()
    {
        var html = new PageRenderer().RenderShow(
            SampleFeed(),
            "https://bondcasts.com/s/v1.2026-07.token",
            openUrl: "HTTPS://BondCasts.com/s/v1.2026-07.token");

        Assert.Contains("""<a class="brand" href="HTTPS://BondCasts.com/s/v1.2026-07.token">Open podcast in BondCasts</a>""", html);
    }

    private static ParsedFeed SampleFeed() => new()
    {
        FeedUrl = "https://feeds.example.com/show.xml",
        Title = "Sample Show",
        Description = "A sample podcast.",
        Author = "Sample Author",
        Episodes =
        [
            new ParsedEpisode
            {
                Guid = "episode-1",
                Title = "Sample Episode",
                Summary = "A sample episode.",
            },
        ],
    };
}
