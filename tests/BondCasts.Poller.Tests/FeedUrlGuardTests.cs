using System.Net;
using BondCasts.Poller.Services.Polling;
using Xunit;

namespace BondCasts.Poller.Tests;

/// Pins the SSRF guard (#20): registered feed URLs come from the CloudKit
/// public database (any iCloud user can write one), so the accepted shapes
/// and the blocked address ranges are a security contract, not a heuristic.
public sealed class FeedUrlGuardTests
{
    [Theory]
    [InlineData("https://example.com/feed.xml")]
    [InlineData("http://example.com/feed.xml")]
    [InlineData("https://example.com:8443/feed.xml?format=rss")]
    // Literal-IP URLs pass the shape check; the address itself is judged at
    // connect time (see the IsBlockedAddress cases below).
    [InlineData("http://169.254.169.254/latest/meta-data")]
    public void TryValidate_AcceptsPlainHttpUrls(string url)
    {
        Assert.True(FeedUrlGuard.TryValidate(url, out _));
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/feed.xml")]
    [InlineData("gopher://example.com/")]
    [InlineData("feed://example.com/feed.xml")]
    [InlineData("https://user:secret@example.com/feed.xml")] // userinfo
    [InlineData("https://user@example.com/feed.xml")]        // userinfo, no password
    [InlineData("relative/feed.xml")]
    [InlineData("not a url at all")]
    [InlineData("")]
    public void TryValidate_RejectsNonHttpAndUserinfoUrls(string url)
    {
        Assert.False(FeedUrlGuard.TryValidate(url, out var reason));
        Assert.NotEmpty(reason);
    }

    [Theory]
    [InlineData("127.0.0.1")]         // loopback
    [InlineData("127.8.8.8")]         // anywhere in 127/8
    [InlineData("10.0.0.1")]          // private 10/8
    [InlineData("172.16.0.1")]        // private 172.16/12 lower bound
    [InlineData("172.31.255.255")]    // private 172.16/12 upper bound
    [InlineData("192.168.1.1")]       // private 192.168/16
    [InlineData("169.254.169.254")]   // Azure/AWS instance metadata service
    [InlineData("169.254.0.1")]       // link-local generally
    [InlineData("100.64.0.1")]        // CGNAT 100.64/10 (used inside Azure)
    [InlineData("0.0.0.0")]           // "this network"
    [InlineData("224.0.0.1")]         // multicast
    [InlineData("255.255.255.255")]   // broadcast
    [InlineData("::1")]               // IPv6 loopback
    [InlineData("::")]                // IPv6 any
    [InlineData("fe80::1")]           // IPv6 link-local
    [InlineData("fc00::1")]           // IPv6 unique-local lower half
    [InlineData("fd12:3456::1")]      // IPv6 unique-local upper half
    [InlineData("fec0::1")]           // IPv6 site-local (deprecated, still resolvable)
    [InlineData("ff02::1")]           // IPv6 multicast
    [InlineData("::ffff:10.0.0.1")]   // v4-mapped private — must unwrap, not pass
    [InlineData("::ffff:127.0.0.1")]  // v4-mapped loopback
    [InlineData("::ffff:169.254.169.254")] // v4-mapped IMDS
    public void IsBlockedAddress_BlocksInternalRanges(string address)
    {
        Assert.True(FeedUrlGuard.IsBlockedAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.255.255")]    // just below 172.16/12
    [InlineData("172.32.0.1")]        // just above 172.16/12
    [InlineData("100.63.255.255")]    // just below 100.64/10
    [InlineData("100.128.0.1")]       // just above 100.64/10
    [InlineData("192.167.1.1")]       // not 192.168/16
    [InlineData("9.255.255.255")]     // just below 10/8
    [InlineData("11.0.0.1")]          // just above 10/8
    [InlineData("2001:4860:4860::8888")] // public IPv6
    [InlineData("::ffff:8.8.8.8")]    // v4-mapped public
    public void IsBlockedAddress_AllowsPublicAddresses(string address)
    {
        Assert.False(FeedUrlGuard.IsBlockedAddress(IPAddress.Parse(address)));
    }
}
