using System.Text.Json.Serialization;

namespace BondCasts.Feeds;

/// A fully parsed podcast feed. This is the SHARED representation used by both
/// the web link-preview renderer (api) and the server-backed feed endpoint
/// (poller). The [JsonPropertyName]s are the wire contract with the app's
/// `ParsedFeed` (Swift, FeedModels.swift): they must match its property names
/// verbatim so `ServerFeedSource` decodes a server-served feed byte-for-byte
/// the same as an on-device parse. `Link` is an extra key the app's decoder
/// simply ignores; it exists for the web renderer's "show website" row.
public sealed record ParsedFeed
{
    [JsonPropertyName("feedURL")] public required string FeedUrl { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("feedDescription")] public string? Description { get; init; }
    [JsonPropertyName("author")] public string? Author { get; init; }
    [JsonPropertyName("artworkURLString")] public string? ArtworkUrl { get; init; }
    [JsonPropertyName("link")] public string? Link { get; init; }
    [JsonPropertyName("isExplicit")] public bool IsExplicit { get; init; }
    [JsonPropertyName("isLocked")] public bool IsLocked { get; init; }
    [JsonPropertyName("episodes")] public IReadOnlyList<ParsedEpisode> Episodes { get; init; } = [];
}

/// One parsed feed item. Property names again mirror the app's `ParsedEpisode`.
public sealed record ParsedEpisode
{
    [JsonPropertyName("guid")] public required string Guid { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("summaryHTML")] public string? Summary { get; init; }
    [JsonPropertyName("enclosureURLString")] public string? EnclosureUrl { get; init; }
    [JsonPropertyName("enclosureType")] public string? EnclosureType { get; init; }
    [JsonPropertyName("link")] public string? Link { get; init; }
    [JsonPropertyName("pubDate")] public DateTimeOffset? PublishedAt { get; init; }
    [JsonPropertyName("durationSeconds")] public double? DurationSeconds { get; init; }
    [JsonPropertyName("episodeNumber")] public int? EpisodeNumber { get; init; }
    [JsonPropertyName("seasonNumber")] public int? SeasonNumber { get; init; }
    [JsonPropertyName("artworkURLString")] public string? ArtworkUrl { get; init; }
    [JsonPropertyName("isExplicit")] public bool IsExplicit { get; init; }
    [JsonPropertyName("chaptersURLString")] public string? ChaptersUrl { get; init; }
    [JsonPropertyName("chaptersType")] public string? ChaptersType { get; init; }
    [JsonPropertyName("transcripts")] public IReadOnlyList<TranscriptRef> Transcripts { get; init; } = [];
    [JsonPropertyName("pscChapters")] public IReadOnlyList<PscChapter> PscChapters { get; init; } = [];
}

/// A `<podcast:transcript>` reference (mirrors the app's `TranscriptRef`).
public sealed record TranscriptRef
{
    [JsonPropertyName("urlString")] public required string UrlString { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("language")] public string? Language { get; init; }
}

/// One inline Podlove Simple Chapters entry (mirrors the app's `PSCChapter`).
public sealed record PscChapter
{
    [JsonPropertyName("start")] public double Start { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("href")] public string? Href { get; init; }
    [JsonPropertyName("image")] public string? Image { get; init; }
}
