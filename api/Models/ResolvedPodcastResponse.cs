using System.Text.Json.Serialization;

namespace BondCasts.Api.Models;

/// <summary>Browser wire contract for a feed verified by the server.</summary>
public sealed record ResolvedPodcastResponse(
    [property: JsonPropertyName("requestedFeedURL")] string RequestedFeedUrl,
    [property: JsonPropertyName("feedURL")] string FeedUrl,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("artworkURLString")] string? ArtworkUrl,
    [property: JsonPropertyName("feedDescription")] string? FeedDescription,
    [property: JsonPropertyName("episodeCount")] int EpisodeCount,
    [property: JsonPropertyName("itunesID")] long? ItunesId,
    [property: JsonPropertyName("isLocked")] bool IsLocked,
    [property: JsonPropertyName("websiteURL")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WebsiteUrl);
