using System.Net;
using BondCasts.Api.Services;
using Xunit;

namespace BondCasts.Api.Tests;

public sealed class FeedUrlPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("2001:db8::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    public void RejectsNonPublicAddresses(string value) =>
        Assert.False(FeedUrlPolicy.IsPublicAddress(IPAddress.Parse(value)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void AcceptsPublicAddresses(string value) =>
        Assert.True(FeedUrlPolicy.IsPublicAddress(IPAddress.Parse(value)));
}
