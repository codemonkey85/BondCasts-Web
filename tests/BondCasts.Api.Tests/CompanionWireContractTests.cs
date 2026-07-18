using System.Text.Json;
using BondCasts.Api.Models;
using Xunit;

namespace BondCasts.Api.Tests;

public sealed class CompanionWireContractTests
{
    [Fact]
    public void ResolvedPodcast_UsesBrowserFieldNames()
    {
        var value = new ResolvedPodcastResponse(
            "https://example.com/requested",
            "https://example.com/final",
            "Example",
            "Publisher",
            null,
            null,
            12,
            1234,
            "https://publisher.example/show?from=rss");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        var root = document.RootElement;
        Assert.Equal("Example", root.GetProperty("title").GetString());
        Assert.Equal("Publisher", root.GetProperty("author").GetString());
        Assert.Equal("https://example.com/final", root.GetProperty("feedURL").GetString());
        Assert.Equal("https://publisher.example/show?from=rss", root.GetProperty("websiteURL").GetString());
        Assert.False(root.TryGetProperty("Title", out _));
        Assert.False(root.TryGetProperty("Author", out _));
    }

    [Theory]
    [InlineData("https://publisher.example/show", "https://publisher.example/show")]
    [InlineData("  http://publisher.example/show  ", "http://publisher.example/show")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("/shows/example", null)]
    [InlineData("not a URL", null)]
    [InlineData("http:/missing-host", null)]
    [InlineData("ftp://publisher.example/show", null)]
    [InlineData("javascript:alert(1)", null)]
    public void PodcastWebsiteUrl_AllowsOnlyAbsoluteHttpUrls(string? value, string? expected)
    {
        Assert.Equal(expected, PodcastWebsiteUrl.FromFeed(value));
    }

    [Fact]
    public void ResolvedPodcast_OmitsMissingWebsiteUrl()
    {
        var value = new ResolvedPodcastResponse(
            "https://example.com/requested",
            "https://example.com/final",
            "Example",
            null,
            null,
            null,
            0,
            null,
            null);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        Assert.False(document.RootElement.TryGetProperty("websiteURL", out _));
    }
}
