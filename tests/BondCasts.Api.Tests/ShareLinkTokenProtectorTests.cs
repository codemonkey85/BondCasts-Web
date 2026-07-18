using System.Security.Cryptography;
using System.Text.Json;
using BondCasts.Api.Services;
using Xunit;

namespace BondCasts.Api.Tests;

public sealed class ShareLinkTokenProtectorTests
{
    [Fact]
    public void ResolvesEpisodeToken()
    {
        using var rsa = RSA.Create(2048);
        var protector = new ShareLinkTokenProtector(new Dictionary<string, RSA> { ["2026-07"] = rsa });

        var token = Token(rsa, "2026-07", new { k = "e", f = "https://example.com/feed.xml", g = "ep-1" });
        var payload = protector.ResolveEpisode(token);

        Assert.NotNull(payload);
        Assert.Equal("https://example.com/feed.xml", payload.Feed);
        Assert.Equal("ep-1", payload.Guid);
    }

    [Fact]
    public void ResolvesShowToken()
    {
        using var rsa = RSA.Create(2048);
        var protector = new ShareLinkTokenProtector(new Dictionary<string, RSA> { ["2026-07"] = rsa });

        var token = Token(rsa, "2026-07", new { k = "s", f = "https://example.com/feed.xml" });
        var payload = protector.ResolveShow(token);

        Assert.NotNull(payload);
        Assert.Equal("https://example.com/feed.xml", payload.Feed);
    }

    [Fact]
    public void RejectsUnknownKeyId()
    {
        using var oldKey = RSA.Create(2048);
        using var currentKey = RSA.Create(2048);
        var protector = new ShareLinkTokenProtector(new Dictionary<string, RSA> { ["2026-07"] = currentKey });

        var token = Token(oldKey, "2025-12", new { k = "s", f = "https://example.com/feed.xml" });

        Assert.Null(protector.ResolveShow(token));
    }

    [Theory]
    [InlineData("https://example.com/feed.xml?token=secret")]
    [InlineData("https://example.com/feed.xml#secret")]
    [InlineData("https://user:pass@example.com/feed.xml")]
    public void RejectsSensitiveFeedUrls(string feed)
    {
        using var rsa = RSA.Create(2048);
        var protector = new ShareLinkTokenProtector(new Dictionary<string, RSA> { ["2026-07"] = rsa });

        var token = Token(rsa, "2026-07", new { k = "s", f = feed });

        Assert.Null(protector.ResolveShow(token));
    }

    private static string Token(RSA rsa, string keyId, object payload)
    {
        var plainText = JsonSerializer.SerializeToUtf8Bytes(payload);
        var aesKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipherText = new byte[plainText.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(aesKey, 16))
        {
            aes.Encrypt(nonce, plainText, cipherText, tag);
        }

        return string.Join('.',
            "v1",
            keyId,
            Base64Url(rsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256)),
            Base64Url(nonce),
            Base64Url(cipherText.Concat(tag).ToArray()));
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
}
