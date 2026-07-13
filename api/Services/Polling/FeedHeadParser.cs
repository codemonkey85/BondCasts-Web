using System.Globalization;
using System.Xml;

namespace BondCasts.Api.Services.Polling;

public sealed record FeedHead(string? ChannelTitle, IReadOnlyList<FeedHeadItem> Items);

/// Identity is the first non-empty of guid / enclosure URL / "title|pubDate",
/// matching how feeds without <guid> still get stable per-item identity.
public sealed record FeedHeadItem(string Identity, string? Title, DateTimeOffset? PublishedAt);

/// Streaming head-parse of an RSS feed: channel title plus per-item
/// guid/title/pubDate/enclosure only, stopping after MaxItems. New-episode
/// detection doesn't need the full parser (BondCasts.Api.Services.RssFeedParser),
/// and stopping early bounds the read on pathological multi-MB feeds.
public static class FeedHeadParser
{
    private const int MaxItems = 250;

    public static async Task<FeedHead> ParseAsync(Stream stream, CancellationToken ct)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        });

        string? channelTitle = null;
        var items = new List<FeedHeadItem>();

        // ReadElementContentAsStringAsync leaves the reader positioned on the
        // node AFTER the consumed element, so the loop advances explicitly and
        // `continue`s (without reading) after any content consumption — a
        // trailing ReadAsync there would silently skip the next sibling.
        if (!await reader.ReadAsync()) return new FeedHead(null, items);
        while (!reader.EOF)
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element && reader.Prefix.Length == 0)
            {
                // Channel title is <rss><channel><title> (depth 2); the depth
                // check skips deeper plain <title>s like <image><title>.
                if (channelTitle is null && reader.Depth == 2 && reader.LocalName == "title")
                {
                    channelTitle = (await reader.ReadElementContentAsStringAsync()).Trim();
                    continue;
                }
                if (reader.LocalName == "item")
                {
                    items.Add(await ParseItemAsync(reader, ct));
                    if (items.Count >= MaxItems) break;
                    continue;
                }
            }
            if (!await reader.ReadAsync()) break;
        }
        return new FeedHead(channelTitle, items);
    }

    /// Entered with the reader on the <item> start tag; returns with it just
    /// past the matching end tag.
    private static async Task<FeedHeadItem> ParseItemAsync(XmlReader reader, CancellationToken ct)
    {
        string? guid = null, title = null, pubDate = null, enclosureUrl = null;
        var depth = reader.Depth;
        var isEmpty = reader.IsEmptyElement;
        await reader.ReadAsync();

        while (!isEmpty && !reader.EOF)
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                await reader.ReadAsync();
                break;
            }
            if (reader.NodeType == XmlNodeType.Element && reader.Prefix.Length == 0)
            {
                switch (reader.LocalName)
                {
                    case "guid":
                        guid = (await reader.ReadElementContentAsStringAsync()).Trim();
                        continue;
                    case "title" when title is null:
                        title = (await reader.ReadElementContentAsStringAsync()).Trim();
                        continue;
                    case "pubDate":
                        pubDate = (await reader.ReadElementContentAsStringAsync()).Trim();
                        continue;
                    case "enclosure":
                        enclosureUrl = reader.GetAttribute("url");
                        break;
                }
            }
            await reader.ReadAsync();
        }

        var publishedAt = ParseRfc822(pubDate);
        var identity = !string.IsNullOrEmpty(guid) ? guid
            : !string.IsNullOrEmpty(enclosureUrl) ? enclosureUrl
            : $"{title}|{pubDate}";
        return new FeedHeadItem(identity, title, publishedAt);
    }

    private static DateTimeOffset? ParseRfc822(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out var dto) ? dto : null;
    }
}
