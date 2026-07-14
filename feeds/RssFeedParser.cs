using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace BondCasts.Feeds;

/// Namespace-aware RSS 2.0 parser covering the `itunes:` namespace, the
/// Podcasting 2.0 `podcast:` namespace (locked / chapters / transcript),
/// `content:encoded`, and inline Podlove Simple Chapters.
///
/// This is the C# counterpart to the app's `RSSFeedParser.swift`. The two MUST
/// agree on how a feed is interpreted so a server-served feed (PodcastApp #174)
/// is indistinguishable from an on-device parse, and so a shared universal link
/// resolves to the same show/episode on the web as in the app.
public static class RssFeedParser
{
    // Namespace declarations vary (http/https, trailing slash, www.), so we match
    // on a normalized suffix rather than an exact URI — same approach as the app.
    private const string ItunesNsSuffix = "itunes.com/dtds/podcast-1.0.dtd";
    private const string PodcastNsSuffix = "podcastindex.org/namespace/1.0";
    // Early Podcasting 2.0 adopters declared the namespace with the spec's
    // GitHub URL; both URIs remain in the wild.
    private const string PodcastLegacyNsSuffix = "github.com/podcastindex-org/podcast-namespace";
    private const string ContentNsSuffix = "purl.org/rss/1.0/modules/content";
    private const string PscNsSuffix = "podlove.org/simple-chapters";

    public static ParsedFeed Parse(byte[] data, string feedUrl)
    {
        using var reader = XmlReader.Create(new MemoryStream(data), ReaderSettings());
        return Parse(XDocument.Load(reader), feedUrl);
    }

    public static ParsedFeed Parse(string xml, string feedUrl)
    {
        using var reader = XmlReader.Create(new StringReader(xml), ReaderSettings());
        return Parse(XDocument.Load(reader), feedUrl);
    }

    // Podcast feeds in the wild carry <!DOCTYPE> declarations and stray entities;
    // ignore the DTD (the default Prohibit throws) and bound entity expansion.
    private static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        MaxCharactersFromEntities = 1024,
    };

    private static ParsedFeed Parse(XDocument doc, string feedUrl)
    {
        var channel = doc.Root?.Element("channel")
            ?? throw new FormatException("Not an RSS feed: no <channel> element.");

        var itunes = ResolveNamespace(doc, ItunesNsSuffix);
        var podcast = ResolveNamespace(doc, PodcastNsSuffix) ?? ResolveNamespace(doc, PodcastLegacyNsSuffix);
        var content = ResolveNamespace(doc, ContentNsSuffix);
        var psc = ResolveNamespace(doc, PscNsSuffix);

        var channelExplicit = ParseExplicit(
            itunes is null ? null : channel.Element(itunes + "explicit")?.Value);

        var episodes = new List<ParsedEpisode>();
        foreach (var item in channel.Elements("item"))
        {
            if (ParseItem(item, itunes, podcast, content, psc, channelExplicit) is { } episode)
                episodes.Add(episode);
        }

        return new ParsedFeed
        {
            FeedUrl = feedUrl,
            Title = (channel.Element("title")?.Value ?? string.Empty).Trim(),
            Description = Trimmed(channel.Element("description")?.Value),
            Author = itunes is null ? null : Trimmed(channel.Element(itunes + "author")?.Value),
            ArtworkUrl = ChannelArtwork(channel, itunes),
            // The plain (non-namespaced) <link> is the show's website; the atom
            // self-link is namespaced, so this won't grab the feed URL.
            Link = Trimmed(channel.Element("link")?.Value),
            IsExplicit = channelExplicit,
            // <podcast:locked>yes</podcast:locked>: the owner forbids
            // redistribution (premium/paid feeds). Drives sharing suppression.
            IsLocked = podcast is not null && string.Equals(
                channel.Element(podcast + "locked")?.Value?.Trim(), "yes", StringComparison.OrdinalIgnoreCase),
            Episodes = episodes,
        };
    }

    private static ParsedEpisode? ParseItem(
        XElement item, XNamespace? itunes, XNamespace? podcast,
        XNamespace? content, XNamespace? psc, bool channelExplicit)
    {
        var enclosure = item.Element("enclosure");
        var enclosureUrl = (string?)enclosure?.Attribute("url");
        // Without an enclosure there is nothing to play; skip the item — the app
        // parser does the same, so server and device agree on the episode set.
        if (string.IsNullOrEmpty(enclosureUrl)) return null;

        var guid = Trimmed(item.Element("guid")?.Value);

        // content:encoded is richer notes when present; fall back to description.
        var contentEncoded = content is null ? null : item.Element(content + "encoded")?.Value;

        var transcripts = new List<TranscriptRef>();
        if (podcast is not null)
        {
            foreach (var transcript in item.Elements(podcast + "transcript"))
            {
                var url = (string?)transcript.Attribute("url");
                var type = (string?)transcript.Attribute("type");
                if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(type))
                    transcripts.Add(new TranscriptRef
                    {
                        UrlString = url,
                        Type = type,
                        Language = (string?)transcript.Attribute("language"),
                    });
            }
        }

        var pscChapters = new List<PscChapter>();
        if (psc is not null)
        {
            foreach (var chapter in item.Descendants(psc + "chapter"))
            {
                if (ParseDuration((string?)chapter.Attribute("start")) is { } start)
                    pscChapters.Add(new PscChapter
                    {
                        Start = start,
                        Title = (string?)chapter.Attribute("title"),
                        Href = (string?)chapter.Attribute("href"),
                        Image = (string?)chapter.Attribute("image"),
                    });
            }
        }

        var chapters = podcast is null ? null : item.Element(podcast + "chapters");
        var itemExplicit = itunes is not null && ParseExplicit(item.Element(itunes + "explicit")?.Value);

        return new ParsedEpisode
        {
            // guid falls back to the enclosure URL, matching the app's resolved
            // episode key so a shared link's guid resolves identically.
            Guid = string.IsNullOrEmpty(guid) ? enclosureUrl : guid,
            Title = Trimmed(item.Element("title")?.Value) is { Length: > 0 } title ? title : "Untitled Episode",
            Summary = Trimmed(contentEncoded ?? item.Element("description")?.Value),
            EnclosureUrl = enclosureUrl,
            EnclosureType = (string?)enclosure?.Attribute("type"),
            Link = Trimmed(item.Element("link")?.Value),
            PublishedAt = ParseRfc822(item.Element("pubDate")?.Value),
            DurationSeconds = itunes is null ? null : ParseDuration(item.Element(itunes + "duration")?.Value),
            EpisodeNumber = itunes is null ? null : ParseInt(item.Element(itunes + "episode")?.Value),
            SeasonNumber = itunes is null ? null : ParseInt(item.Element(itunes + "season")?.Value),
            ArtworkUrl = itunes is null ? null : (string?)item.Element(itunes + "image")?.Attribute("href"),
            IsExplicit = itemExplicit || channelExplicit,
            ChaptersUrl = (string?)chapters?.Attribute("url"),
            ChaptersType = (string?)chapters?.Attribute("type"),
            Transcripts = transcripts,
            PscChapters = pscChapters,
        };
    }

    private static string? ChannelArtwork(XElement channel, XNamespace? itunes)
    {
        // Prefer <itunes:image href="…">, fall back to <image><url>.
        if (itunes is not null &&
            (string?)channel.Element(itunes + "image")?.Attribute("href") is { Length: > 0 } href)
            return href;
        return Trimmed(channel.Element("image")?.Element("url")?.Value);
    }

    /// Finds a namespace actually declared on the document by matching a
    /// normalized suffix (handles http/https, trailing slash, and www. the same
    /// way the Swift parser does).
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

    /// itunes:explicit: `true`/`false` (current spec) or legacy `yes`/`explicit`.
    private static bool ParseExplicit(string? value) =>
        value?.Trim().ToLowerInvariant() is "true" or "yes" or "explicit";

    private static int? ParseInt(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static DateTimeOffset? ParseRfc822(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out var dto) ? dto : null;
    }

    /// itunes:duration / PSC start: plain seconds, `MM:SS`, or `HH:MM:SS`.
    private static double? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds)
            && !value.Contains(':'))
            return seconds;

        double total = 0;
        foreach (var part in value.Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.Any, CultureInfo.InvariantCulture, out var n)) return null;
            total = total * 60 + n;
        }
        return total;
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
