using BondCasts.Api.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BondCasts.Api.Tests;

public sealed class FeedServiceTests
{
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/feed.xml")]
    public async Task ResolveFeedRejectsNonHttpSchemesWithSpecificMessage(string value)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new FeedService(
            new ThrowingHttpClientFactory(),
            cache,
            NullLogger<FeedService>.Instance);

        var error = await Assert.ThrowsAsync<UnsafeFeedUrlException>(
            () => service.ResolveFeedAsync(value, CancellationToken.None));

        Assert.Equal("Feed URL must be an absolute http(s) URL.", error.Message);
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("Invalid feed URLs must fail before creating an HTTP client.");
    }
}
