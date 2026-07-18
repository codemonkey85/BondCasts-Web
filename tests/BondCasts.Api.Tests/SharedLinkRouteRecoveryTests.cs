using System.Reflection;
using BondCasts.Api.Functions;
using Xunit;

namespace BondCasts.Api.Tests;

public sealed class SharedLinkRouteRecoveryTests
{
    [Theory]
    [InlineData("/s/v1.2026-07.token", "s", "v1.2026-07.token")]
    [InlineData("https://bondcasts.com/e/v1.2026-07.token?ignored=true", "e", "v1.2026-07.token")]
    [InlineData("/s/v1.2026-07.token#fragment", "s", "v1.2026-07.token")]
    [InlineData("/s/v1.2026-07.token%2Dpart", "s", "v1.2026-07.token-part")]
    public void RecoversTokenFromOriginalPath(string value, string prefix, string expected)
    {
        Assert.Equal(expected, Invoke(value, prefix));
    }

    [Theory]
    [InlineData("/api/s/*", "s")]
    [InlineData("/s/token/extra", "s")]
    [InlineData("/e/token", "s")]
    [InlineData("", "s")]
    public void RejectsUnexpectedOriginalPathShapes(string value, string prefix)
    {
        Assert.Null(Invoke(value, prefix));
    }

    private static string? Invoke(string value, string prefix)
    {
        var method = typeof(LinkPreviewFunctions).GetMethod(
            "SharedLinkTokenFromOriginalPath",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string?)method.Invoke(null, [value, prefix]);
    }
}
