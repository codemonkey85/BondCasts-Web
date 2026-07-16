using System.Text.Json;
using System.Text.RegularExpressions;
using BondCasts.Feeds;
using Xunit;

namespace BondCasts.Feeds.Tests;

/// Pins the feed wire format's date shape (#21). The app decodes /feed/{hash}
/// bodies with Swift's `JSONDecoder.dateDecodingStrategy = .iso8601`, whose
/// formatter accepts `2026-07-13T12:34:56Z` but NOT fractional seconds or
/// UTC offsets. Reverting to .NET's default DateTimeOffset serialization
/// (`2026-07-13T12:34:56.1234567+00:00`) would make every 200 response fail
/// to decode client-side, and the client's silent direct-fetch fallback would
/// hide the breakage completely — so the contract is enforced here instead.
public sealed class FeedJsonDateFormatTests
{
    /// Whole-second UTC ISO-8601, the only date shape Swift's `.iso8601`
    /// strategy accepts.
    private static readonly Regex SwiftIso8601 =
        new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", RegexOptions.Compiled);

    /// A pubDate deliberately carrying both hazards: sub-second precision and
    /// a non-UTC offset.
    private static readonly DateTimeOffset HazardousDate =
        new(2026, 7, 13, 8, 34, 56, 789, TimeSpan.FromHours(-4));

    private static ParsedFeed SampleFeed(DateTimeOffset? pubDate) => new()
    {
        FeedUrl = "https://example.com/feed.xml",
        Title = "Sample Show",
        Episodes =
        [
            new ParsedEpisode
            {
                Guid = "https://example.com/?post_type=episode&p=12345",
                Title = "Sample Episode",
                PublishedAt = pubDate,
            },
        ],
    };

    [Fact]
    public void PubDate_IsWholeSecondUtc()
    {
        var json = JsonSerializer.Serialize(SampleFeed(HazardousDate), FeedJson.Options);
        using var document = JsonDocument.Parse(json);
        var pubDate = document.RootElement
            .GetProperty("episodes")[0]
            .GetProperty("pubDate")
            .GetString();

        Assert.NotNull(pubDate);
        Assert.Matches(SwiftIso8601, pubDate);
        // Fractional seconds truncated, offset converted to UTC.
        Assert.Equal("2026-07-13T12:34:56Z", pubDate);
    }

    [Fact]
    public void SerializedFeed_ContainsNoFractionalSecondsAnywhere()
    {
        var json = JsonSerializer.Serialize(SampleFeed(HazardousDate), FeedJson.Options);
        // The signature of a leaked default serializer: HH:mm:ss followed by
        // a fraction or an explicit offset instead of the bare Z.
        Assert.DoesNotMatch(@"\d{2}:\d{2}:\d{2}[\.+]", json);
        Assert.DoesNotMatch(@"\d{2}:\d{2}:\d{2}-", json);
    }

    [Fact]
    public void NullPubDate_IsEmittedAsExplicitNull()
    {
        // The app's synthesized Decodable expects every key present;
        // FeedJson.Options must never switch to omitting nulls.
        var json = JsonSerializer.Serialize(SampleFeed(null), FeedJson.Options);
        using var document = JsonDocument.Parse(json);
        var pubDate = document.RootElement.GetProperty("episodes")[0].GetProperty("pubDate");
        Assert.Equal(JsonValueKind.Null, pubDate.ValueKind);
    }

    [Fact]
    public void PubDate_RoundTripsThroughTheConverter()
    {
        var json = JsonSerializer.Serialize(SampleFeed(HazardousDate), FeedJson.Options);
        var decoded = JsonSerializer.Deserialize<ParsedFeed>(json, FeedJson.Options);

        Assert.NotNull(decoded);
        Assert.Equal(
            HazardousDate.ToUniversalTime().AddMilliseconds(-789),
            decoded.Episodes[0].PublishedAt);
    }
}
