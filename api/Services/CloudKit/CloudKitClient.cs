using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BondCasts.Api.Services.CloudKit;

/// Minimal CloudKit Web Services client using server-to-server key auth,
/// lifted from the proven spike tool (PodcastApp docs/spikes/CKSpikeTool).
///
/// Signs each request per Apple's spec: the message is
/// "{ISO8601 date}:{base64(SHA256(body))}:{URL subpath}", signed with
/// ECDSA P-256 / SHA-256. CloudKit requires the DER (RFC 3279) signature
/// encoding; .NET's default is IEEE P1363, so SignData must be called with
/// DSASignatureFormat.Rfc3279DerSequence.
public sealed class CloudKitClient(CloudKitOptions options, HttpClient http) : IDisposable
{
    private const string Host = "https://api.apple-cloudkit.com";
    private readonly ECDsa _key = options.LoadPrivateKey();

    public string Environment => options.Environment;

    /// POSTs a JSON body to a public-database operation ("records/modify",
    /// "records/query", "records/lookup"). Returns the parsed response and
    /// status code without throwing on HTTP errors, so callers can inspect
    /// CloudKit's error payloads (serverErrorCode etc.).
    public async Task<(int Status, JsonNode? Body)> PostAsync(
        string operation, JsonNode body, CancellationToken ct)
    {
        var subpath = $"/database/1/{options.Container}/{options.Environment}/public/{operation}";
        var bodyBytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        // CloudKit rejects fractional seconds; whole-second UTC only.
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

        using var request = new HttpRequestMessage(HttpMethod.Post, Host + subpath);
        request.Headers.Add("X-Apple-CloudKit-Request-KeyID", options.KeyId);
        request.Headers.Add("X-Apple-CloudKit-Request-ISO8601Date", date);
        request.Headers.Add("X-Apple-CloudKit-Request-SignatureV1", Sign(date, bodyBytes, subpath));
        request.Content = new ByteArrayContent(bodyBytes);
        request.Content.Headers.ContentType = new("application/json");

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? parsed = null;
        try { parsed = JsonNode.Parse(text); } catch (JsonException) { /* non-JSON error page */ }
        return ((int)response.StatusCode, parsed);
    }

    private string Sign(string date, byte[] bodyBytes, string subpath)
    {
        var bodyHash = Convert.ToBase64String(SHA256.HashData(bodyBytes));
        var message = Encoding.UTF8.GetBytes($"{date}:{bodyHash}:{subpath}");
        var der = _key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return Convert.ToBase64String(der);
    }

    public void Dispose() => _key.Dispose();
}
