using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BondCasts.Api.Services;

public sealed class ShareLinkTokenProtector
{
    private const string Version = "v1";
    private const int AesGcmTagBytes = 16;
    private readonly IReadOnlyDictionary<string, RSA> _privateKeys;

    public ShareLinkTokenProtector()
        : this(LoadPrivateKeysFromEnvironment())
    {
    }

    public ShareLinkTokenProtector(IReadOnlyDictionary<string, RSA> privateKeys)
    {
        _privateKeys = privateKeys;
    }

    public ShareLinkPayload? ResolveEpisode(string token) =>
        Resolve(token) is { Kind: "e", Feed: { Length: > 0 }, Guid: { Length: > 0 } } payload
            ? payload
            : null;

    public ShareLinkPayload? ResolveShow(string token) =>
        Resolve(token) is { Kind: "s", Feed: { Length: > 0 } } payload
            ? payload
            : null;

    private ShareLinkPayload? Resolve(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 5 || parts[0] != Version)
                return null;

            var keyId = parts[1];
            if (!_privateKeys.TryGetValue(keyId, out var privateKey))
                return null;

            var encryptedKey = FromBase64Url(parts[2]);
            var nonce = FromBase64Url(parts[3]);
            var cipherAndTag = FromBase64Url(parts[4]);
            if (cipherAndTag.Length <= AesGcmTagBytes)
                return null;

            var aesKey = privateKey.Decrypt(encryptedKey, RSAEncryptionPadding.OaepSHA256);
            var cipherText = cipherAndTag[..^AesGcmTagBytes];
            var tag = cipherAndTag[^AesGcmTagBytes..];
            var plainText = new byte[cipherText.Length];
            using (var aes = new AesGcm(aesKey, AesGcmTagBytes))
            {
                aes.Decrypt(nonce, cipherText, tag, plainText);
            }

            var payload = JsonSerializer.Deserialize<ShareLinkPayload>(plainText);
            return IsShareable(payload?.Feed) ? payload : null;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }

    private static bool IsShareable(string? feedUrl)
    {
        if (string.IsNullOrWhiteSpace(feedUrl)
            || !Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        return !FeedUrlPolicy.HasPotentiallySensitiveComponents(uri);
    }

    private static IReadOnlyDictionary<string, RSA> LoadPrivateKeysFromEnvironment()
    {
        var keys = new Dictionary<string, RSA>(StringComparer.Ordinal);
        foreach (var keyId in Environment.GetEnvironmentVariable("ShareLinks__KeyIds")?
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 ?? [])
        {
            var envName = $"ShareLinks__PrivateKeys__{keyId.Replace('-', '_')}";
            var encodedPem = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrWhiteSpace(encodedPem))
                continue;

            var pem = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedPem));
            var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            keys[keyId] = rsa;
        }

        return keys;
    }

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}

public sealed record ShareLinkPayload(
    [property: JsonPropertyName("k")] string Kind,
    [property: JsonPropertyName("f")] string Feed,
    [property: JsonPropertyName("g")] string? Guid);
