using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BondCasts.Feeds;

namespace BondCasts.Api.Rendering;

/// Renders the server-side HTML for episode/show landing pages. The Open Graph
/// tags are baked into the returned markup so link crawlers (iMessage, Slack,
/// Shared with You) unfurl a rich card — crawlers don't run JS, so this must
/// happen server-side, which is the whole reason the Function exists.
public sealed partial class PageRenderer
{
    private const string SiteName = "BondCasts";
    private const string DefaultImage = "https://bondcasts.com/assets/apple-touch-icon.png";

    public string RenderEpisode(ParsedFeed feed, ParsedEpisode episode, string originalUrl)
    {
        var showTitle = Coalesce(feed.Title, SiteName);
        var epTitle = Coalesce(episode.Title, "Episode");
        // Hero art prefers episode-specific artwork, then the show's; DefaultImage
        // is only for og:image so the visible page never shows the app icon as "art".
        var artwork = Coalesce(episode.ArtworkUrl, feed.ArtworkUrl);
        var notes = PlainText(episode.Summary);
        var ogDescription = Truncate($"{showTitle} · {Summarize(notes)}", 200);

        var subtitleParts = new List<string> { HtmlEncode(showTitle) };
        if (episode.PublishedAt is { } date) subtitleParts.Add(HtmlEncode(date.ToString("MMMM d, yyyy")));
        if (FormatDuration(episode.DurationSeconds) is { } dur) subtitleParts.Add(HtmlEncode(dur));

        var body = notes.Length > 0
            ? $"<div class=\"notes\"><p>{HtmlEncode(Truncate(notes, 1200))}</p></div>"
            : string.Empty;

        // Direct link to this episode's page, plus the show's own site.
        var links = LinkRow(
            ExternalLink(episode.Link, "Episode page"),
            ExternalLink(feed.Link, "Show website"));

        return RenderPage(
            documentTitle: $"{epTitle} — {showTitle}",
            ogTitle: epTitle,
            ogDescription: ogDescription,
            ogImage: Coalesce(artwork, DefaultImage),
            ogType: "article",
            heroImage: artwork,
            headingHtml: HtmlEncode(epTitle),
            subtitleHtml: string.Join(" · ", subtitleParts),
            bodyHtml: body,
            linksHtml: links,
            openLabel: "Open episode in BondCasts",
            originalUrl: originalUrl);
    }

    public string RenderShow(ParsedFeed feed, string originalUrl)
    {
        var showTitle = Coalesce(feed.Title, SiteName);
        var artwork = feed.ArtworkUrl ?? string.Empty;
        var description = PlainText(feed.Description);
        var ogDescription = Truncate(Summarize(description), 200);

        var subtitle = feed.Author is { Length: > 0 } author ? HtmlEncode(author) : null;
        var body = description.Length > 0
            ? $"<div class=\"notes\"><p>{HtmlEncode(Truncate(description, 1200))}</p></div>"
            : string.Empty;

        var links = LinkRow(ExternalLink(feed.Link, "Visit website"));

        return RenderPage(
            documentTitle: $"{showTitle} — {SiteName}",
            ogTitle: showTitle,
            ogDescription: ogDescription.Length > 0 ? ogDescription : $"Listen to {showTitle} on BondCasts.",
            ogImage: Coalesce(artwork, DefaultImage),
            ogType: "website",
            heroImage: artwork,
            headingHtml: HtmlEncode(showTitle),
            subtitleHtml: subtitle,
            bodyHtml: body,
            linksHtml: links,
            openLabel: "Open podcast in BondCasts",
            originalUrl: originalUrl);
    }

    /// The generic fallback when the feed can't be fetched or the guid isn't
    /// found — mirrors the old static episode.html/show.html so a link never
    /// looks broken.
    public string RenderFallback(string kind, string? feedUrl, string originalUrl)
    {
        var body = new StringBuilder();
        body.Append($"<p class=\"lede\">If you have {SiteName} installed, this link opens the {kind} right in the app. Otherwise, get {SiteName} for iPhone, iPad, and Mac.</p>");
        if (feedUrl is { Length: > 0 })
            body.Append($"<p style=\"color:var(--muted);font-size:.9rem;\">Podcast feed: <a href=\"{HtmlEncode(feedUrl)}\" rel=\"noopener\">{HtmlEncode(feedUrl)}</a></p>");

        return RenderPage(
            documentTitle: $"{Capitalize(kind)} — {SiteName}",
            ogTitle: $"Listen on {SiteName}",
            ogDescription: $"Open this {kind} in {SiteName}.",
            ogImage: DefaultImage,
            ogType: "website",
            heroImage: string.Empty,
            headingHtml: $"Open this {HtmlEncode(kind)} in {SiteName}",
            subtitleHtml: null,
            bodyHtml: body.ToString(),
            linksHtml: string.Empty,
            openLabel: "Get BondCasts",
            originalUrl: originalUrl);
    }

    private static string RenderPage(
        string documentTitle, string ogTitle, string ogDescription, string ogImage,
        string ogType, string heroImage, string headingHtml, string? subtitleHtml,
        string bodyHtml, string linksHtml, string openLabel, string originalUrl)
    {
        // The visible hero: cover art (when the feed has real artwork) beside the
        // title + subtitle. Art is cross-origin (the podcast host's CDN), so no
        // referrer is sent and decoding is async to keep first paint snappy.
        var artHtml = string.IsNullOrEmpty(heroImage)
            ? string.Empty
            : $"<img class=\"preview-art\" src=\"{HtmlEncode(heroImage)}\" alt=\"{HtmlEncode(ogTitle)} artwork\" width=\"176\" height=\"176\" loading=\"eager\" decoding=\"async\" referrerpolicy=\"no-referrer\">";
        var subtitleH = string.IsNullOrEmpty(subtitleHtml)
            ? string.Empty
            : $"<p class=\"lede\">{subtitleHtml}</p>";
        var hero = $"""
              <div class="preview-hero{(string.IsNullOrEmpty(heroImage) ? " no-art" : "")}">
                  {artHtml}
                  <div class="preview-head">
                    <h1>{headingHtml}</h1>
                    {subtitleH}
                  </div>
                </div>
            """;

        // og:url reflects the canonical share link so unfurls attribute correctly.
        return $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>{HtmlEncode(documentTitle)}</title>
          <meta name="description" content="{HtmlEncode(ogDescription)}">
          <meta name="theme-color" content="#0a0f1e">
          <meta property="og:type" content="{ogType}">
          <meta property="og:site_name" content="{SiteName}">
          <meta property="og:title" content="{HtmlEncode(ogTitle)}">
          <meta property="og:description" content="{HtmlEncode(ogDescription)}">
          <meta property="og:image" content="{HtmlEncode(ogImage)}">
          <meta property="og:url" content="{HtmlEncode(originalUrl)}">
          <meta name="twitter:card" content="summary_large_image">
          <link rel="icon" href="/assets/favicon.svg" type="image/svg+xml">
          <link rel="icon" href="/favicon.ico" sizes="any">
          <link rel="apple-touch-icon" href="/assets/apple-touch-icon.png">
          <link rel="stylesheet" href="/styles.css">
        </head>
        <body>
          <header class="site-header">
            <div class="wrap">
              <a class="brand" href="/">
                <img src="/assets/logo.svg" alt="" width="30" height="30">
                <span>BondCasts</span>
              </a>
              <nav class="site-nav">
                <a class="nav-plain" href="/#features">Features</a>
                <a class="nav-plain" href="/support.html">Support</a>
                <a class="nav-plain" href="/privacy.html">Privacy</a>
              </nav>
            </div>
          </header>

          <main class="content">
            {hero}
            {bodyHtml}
            {linksHtml}
            <div class="callout">
              <a class="brand" href="/">{HtmlEncode(openLabel)}</a>
            </div>
          </main>

          <footer class="site-footer">
            <div class="wrap">
              <a class="brand" href="/">
                <img src="/assets/logo.svg" alt="" width="26" height="26">
                <span>BondCasts</span>
              </a>
              <nav>
                <a href="/support.html">Support</a>
                <a href="/privacy.html">Privacy</a>
                <a href="mailto:support@bondcasts.com">Contact</a>
                <a href="https://bondcodes.com">Michael Bond</a>
              </nav>
            </div>
            <div class="wrap" style="padding-top:0;color:var(--muted);font-size:0.85rem;">
              <span>&copy; 2026 BondCasts. Made with care in SwiftUI.</span>
            </div>
          </footer>
        </body>
        </html>
        """;
    }

    // MARK: - Text helpers

    private static string HtmlEncode(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

    /// Wraps one or more feed-provided links in a row, dropping any that were
    /// empty (missing or non-http). Returns "" when nothing survives.
    private static string LinkRow(params string[] anchors)
    {
        var present = anchors.Where(a => a.Length > 0);
        var joined = string.Join("", present);
        return joined.Length == 0 ? string.Empty : $"<p class=\"preview-links\">{joined}</p>";
    }

    /// Builds an external anchor for a feed-supplied URL. The URL is untrusted
    /// (feed content), so only absolute http/https is honored and the link is
    /// rel="nofollow ugc" + target=_blank; anything else renders as nothing.
    private static string ExternalLink(string? url, string label)
    {
        var safe = SafeUrl(url);
        return safe.Length == 0
            ? string.Empty
            : $"<a class=\"preview-link\" href=\"{HtmlEncode(safe)}\" target=\"_blank\" rel=\"noopener nofollow ugc\">{HtmlEncode(label)}<span aria-hidden=\"true\"> ↗</span></a>";
    }

    private static string SafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        url = url.Trim();
        return Uri.TryCreate(url, UriKind.Absolute, out var u)
               && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? url
            : string.Empty;
    }

    private static string Coalesce(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    /// Strips all HTML tags and decodes entities. Feeds carry arbitrary markup,
    /// so we render notes as plain text rather than trusting feed HTML (which
    /// would be an XSS vector). A dedicated sanitizer (e.g. Ganss.Xss) could be
    /// swapped in later if we want to preserve safe formatting.
    private static string PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var stripped = TagRegex().Replace(html, " ");
        return WhitespaceRegex().Replace(WebUtility.HtmlDecode(stripped), " ").Trim();
    }

    private static string Summarize(string text)
    {
        var end = text.IndexOfAny(['.', '!', '?']);
        return end is > 40 and < 180 ? text[..(end + 1)] : text;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max].TrimEnd() + "…";

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string? FormatDuration(double? seconds)
    {
        if (seconds is not > 0) return null;
        var ts = TimeSpan.FromSeconds(seconds.Value);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours} hr {ts.Minutes} min"
            : $"{ts.Minutes} min";
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
