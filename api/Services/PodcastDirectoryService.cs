using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;

namespace BondCasts.Api.Services;

public sealed class PodcastDirectoryService
{
    public const string HttpClientName = "podcast-directory";
    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(5);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;

    public PodcastDirectoryService(IHttpClientFactory httpClientFactory, IMemoryCache cache)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
    }

    public async Task<IReadOnlyList<DirectoryPodcast>> SearchAsync(string term, int limit, CancellationToken ct)
    {
        term = term.Trim();
        limit = Math.Clamp(limit, 1, 25);
        var key = $"directory-search:{term.ToLowerInvariant()}:{limit}";
        if (_cache.TryGetValue(key, out IReadOnlyList<DirectoryPodcast>? cached) && cached is not null)
            return cached;

        var url = $"https://itunes.apple.com/search?media=podcast&entity=podcast&limit={limit}&term={Uri.EscapeDataString(term)}";
        var response = await _httpClientFactory.CreateClient(HttpClientName)
            .GetFromJsonAsync<ItunesResponse>(url, cancellationToken: ct);
        var results = response?.Results
            .Where(result => result.CollectionId is not null && !string.IsNullOrWhiteSpace(result.CollectionName))
            .Select(ToDirectoryPodcast)
            .ToArray() ?? [];
        _cache.Set(key, results, SearchCacheTtl);
        return results;
    }

    public async Task<DirectoryPodcast?> ConfirmAsync(long itunesId, string requestedFeedUrl, CancellationToken ct)
    {
        var url = $"https://itunes.apple.com/lookup?entity=podcast&id={itunesId}";
        var response = await _httpClientFactory.CreateClient(HttpClientName)
            .GetFromJsonAsync<ItunesResponse>(url, cancellationToken: ct);
        var result = response?.Results.FirstOrDefault(candidate => candidate.CollectionId == itunesId);
        if (result is null || string.IsNullOrWhiteSpace(result.FeedUrl)) return null;
        if (!Uri.TryCreate(result.FeedUrl, UriKind.Absolute, out var directoryUrl)
            || !Uri.TryCreate(requestedFeedUrl, UriKind.Absolute, out var requestedUrl)
            || directoryUrl != requestedUrl)
            return null;
        return ToDirectoryPodcast(result);
    }

    private static DirectoryPodcast ToDirectoryPodcast(ItunesResult result) => new(
        result.CollectionId!.Value,
        result.CollectionName!.Trim(),
        NullIfWhiteSpace(result.ArtistName),
        NullIfWhiteSpace(result.FeedUrl),
        NullIfWhiteSpace(result.ArtworkUrl600 ?? result.ArtworkUrl100),
        NullIfWhiteSpace(result.PrimaryGenreName));

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ItunesResponse([property: JsonPropertyName("results")] IReadOnlyList<ItunesResult> Results);
    private sealed record ItunesResult(
        [property: JsonPropertyName("collectionId")] long? CollectionId,
        [property: JsonPropertyName("collectionName")] string? CollectionName,
        [property: JsonPropertyName("artistName")] string? ArtistName,
        [property: JsonPropertyName("feedUrl")] string? FeedUrl,
        [property: JsonPropertyName("artworkUrl600")] string? ArtworkUrl600,
        [property: JsonPropertyName("artworkUrl100")] string? ArtworkUrl100,
        [property: JsonPropertyName("primaryGenreName")] string? PrimaryGenreName);
}

public sealed record DirectoryPodcast(
    [property: JsonPropertyName("itunesID")] long ItunesId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("feedURL")] string? FeedUrl,
    [property: JsonPropertyName("artworkURLString")] string? ArtworkUrl,
    [property: JsonPropertyName("genre")] string? Genre);
