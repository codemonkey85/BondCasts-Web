using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BondCasts.Feeds;

/// The single serializer configuration for the feed wire format. Every date is
/// emitted as a fraction-less UTC ISO-8601 string because the app decodes with
/// `JSONDecoder.dateDecodingStrategy = .iso8601`, whose formatter accepts
/// `2026-07-13T12:34:56Z` but NOT fractional seconds — the .NET default
/// (`2026-07-13T12:34:56.1234567+00:00`) would fail to decode and silently
/// force every client onto the direct-fetch fallback. This contract is pinned
/// by FeedJsonDateFormatTests (tests/BondCasts.Feeds.Tests); a change here
/// must trip those tests, never production.
public static class FeedJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            // Optional fields are emitted as explicit `null` (never omitted) so
            // the app's synthesized Decodable always finds the keys it expects.
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            // The body is served as application/json (never embedded in HTML),
            // so relaxed escaping is safe and keeps HTML-heavy summaryHTML from
            // bloating into < soup. The app's JSONDecoder reads both forms
            // identically, so this is a size win with no contract change.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new SwiftIso8601DateTimeOffsetConverter());
        return options;
    }
}

/// Writes/reads ISO-8601 in UTC with second precision (no fractional seconds),
/// matching Swift's `.iso8601` strategy. Registered once in `FeedJson.Options`;
/// System.Text.Json routes `DateTimeOffset?` through it automatically.
public sealed class SwiftIso8601DateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DateTimeOffset.Parse(
            reader.GetString() ?? string.Empty,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(
            value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
}
