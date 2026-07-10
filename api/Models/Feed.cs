namespace BondCasts.Api.Models;

/// A parsed podcast feed. Mirrors the fields BondCasts' own `RSSFeedParser`
/// surfaces, so the web renders shows/episodes the same way the app does.
public sealed record ParsedFeed(
    string FeedUrl,
    string Title,
    string? Description,
    string? Author,
    string? ArtworkUrl,
    bool IsExplicit,
    IReadOnlyList<ParsedEpisode> Episodes);

public sealed record ParsedEpisode(
    string? Guid,
    string? Title,
    string? Description,
    string? ArtworkUrl,
    string? EnclosureUrl,
    DateTimeOffset? PublishedAt,
    double? DurationSeconds,
    bool IsExplicit);
