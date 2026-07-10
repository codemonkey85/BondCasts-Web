using System.Globalization;
using System.Xml.Linq;
using BondCasts.Api.Models;

namespace BondCasts.Api.Services;

/// Namespace-aware RSS 2.0 parser covering the `itunes:` namespace and
/// `content:encoded`. This is the C# counterpart to the app's
/// `RSSFeedParser.swift`; the two must agree on how a feed is interpreted so a
/// shared link resolves to the same show/episode on the web as in the app.
///
/// Episode identity is the `<guid>` element's text, matched verbatim against
/// the `guid` query parameter carried by the universal link.
public static class RssFeedParser
{
    // Namespace declarations vary (http/https, trailing slash), so we match on a
    // normalized suffix rather than an exact URI — same approach as the app.
    private const string ItunesNsSuffix = "itunes.com/dtds/podcast-1.0.dtd";
    private const string ContentNsSuffix = "purl.org/rss/1.0/modules/content";

    public static ParsedFeed Parse(string xml, string feedUrl)
    {
        var doc = XDocument.Parse(xml, LoadOptions.None);
        var channel = doc.Root?.Element("channel")
            ?? throw new FormatException("Not an RSS feed: no <channel> element.");

        var itunesNs = ResolveNamespace(doc, ItunesNsSuffix);

        var episodes = channel.Elements("item")
            .Select(item => ParseItem(item, itunesNs))
            .ToList();

        return new ParsedFeed(
            FeedUrl: feedUrl,
            Title: (channel.Element("title")?.Value ?? string.Empty).Trim(),
            Description: channel.Element("description")?.Value?.Trim(),
            Author: itunesNs is null ? null : channel.Element(itunesNs + "author")?.Value?.Trim(),
            ArtworkUrl: ChannelArtwork(channel, itunesNs),
            // The plain (non-namespaced) <link> is the show's website; the atom
            // self-link is namespaced, so this won't accidentally grab the feed URL.
            Link: channel.Element("link")?.Value?.Trim(),
            IsExplicit: ParseExplicit(itunesNs is null ? null : channel.Element(itunesNs + "explicit")?.Value),
            Episodes: episodes);
    }

    private static ParsedEpisode ParseItem(XElement item, XNamespace? itunesNs)
    {
        // content:encoded is richer notes when present; fall back to description.
        var contentEncoded = ResolveNamespace(item.Document!, ContentNsSuffix) is { } contentNs
            ? item.Element(contentNs + "encoded")?.Value
            : null;

        return new ParsedEpisode(
            Guid: item.Element("guid")?.Value?.Trim() is { Length: > 0 } g ? g : null,
            Title: item.Element("title")?.Value?.Trim(),
            Description: (contentEncoded ?? item.Element("description")?.Value)?.Trim(),
            ArtworkUrl: itunesNs is null ? null : (string?)item.Element(itunesNs + "image")?.Attribute("href"),
            EnclosureUrl: (string?)item.Element("enclosure")?.Attribute("url"),
            Link: item.Element("link")?.Value?.Trim(),
            PublishedAt: ParseRfc822(item.Element("pubDate")?.Value),
            DurationSeconds: itunesNs is null ? null : ParseDuration(item.Element(itunesNs + "duration")?.Value),
            IsExplicit: ParseExplicit(itunesNs is null ? null : item.Element(itunesNs + "explicit")?.Value));
    }

    private static string? ChannelArtwork(XElement channel, XNamespace? itunesNs)
    {
        // Prefer <itunes:image href="…">, fall back to the standard <image><url>.
        if (itunesNs is not null && (string?)channel.Element(itunesNs + "image")?.Attribute("href") is { Length: > 0 } href)
            return href;
        return channel.Element("image")?.Element("url")?.Value?.Trim() is { Length: > 0 } url ? url : null;
    }

    /// Finds the <itunes:image>/<content:encoded> namespace actually declared on
    /// this document by matching a normalized suffix (handles http/https and
    /// trailing-slash variants the same way the Swift parser does).
    private static XNamespace? ResolveNamespace(XDocument doc, string suffix)
    {
        foreach (var attr in doc.Root!.Attributes().Where(a => a.IsNamespaceDeclaration))
        {
            var normalized = attr.Value.ToLowerInvariant()
                .Replace("https://", string.Empty)
                .Replace("http://", string.Empty)
                .TrimStart('/');
            if (normalized.StartsWith("www.")) normalized = normalized[4..];
            if (normalized.StartsWith(suffix)) return XNamespace.Get(attr.Value);
        }
        return null;
    }

    private static bool ParseExplicit(string? value) =>
        value?.Trim().ToLowerInvariant() is "yes" or "true" or "explicit";

    private static DateTimeOffset? ParseRfc822(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out var dto) ? dto : null;
    }

    /// iTunes duration is either seconds ("3600") or H:MM:SS / MM:SS.
    private static double? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds))
            return seconds;

        double total = 0;
        foreach (var part in value.Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.Any, CultureInfo.InvariantCulture, out var n)) return null;
            total = total * 60 + n;
        }
        return total;
    }
}
