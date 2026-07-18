namespace BondCasts.Api.Models;

/// <summary>Validates the publisher website parsed from a verified RSS channel.</summary>
public static class PodcastWebsiteUrl
{
    public static string? FromFeed(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        // Preserve the feed-supplied spelling instead of normalizing it so the
        // browser receives the publisher's exact canonical channel link.
        return trimmed;
    }
}
