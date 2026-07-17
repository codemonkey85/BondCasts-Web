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
            1234);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        var root = document.RootElement;
        Assert.Equal("Example", root.GetProperty("title").GetString());
        Assert.Equal("Publisher", root.GetProperty("author").GetString());
        Assert.Equal("https://example.com/final", root.GetProperty("feedURL").GetString());
        Assert.False(root.TryGetProperty("Title", out _));
        Assert.False(root.TryGetProperty("Author", out _));
    }
}
