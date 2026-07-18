using System.Net;
using System.Net.Sockets;
using System.IO.Compression;

namespace BondCasts.Api.Services;

/// <summary>
/// Rejects URLs that could turn the public feed resolver into an SSRF proxy.
/// Every redirect target is checked separately by <see cref="FeedService"/>.
/// </summary>
public static class FeedUrlPolicy
{
    /// <summary>
    /// Resolves and connects to the validated public address itself. This closes
    /// the DNS-rebinding gap that would exist if validation and HttpClient's
    /// connection performed two unrelated DNS lookups.
    /// </summary>
    public static HttpMessageHandler CreateSafeHttpMessageHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = ConnectToPublicAddressAsync
    };

    public static async Task<bool> IsPublicHttpUrlAsync(Uri uri, CancellationToken ct)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrWhiteSpace(uri.Host))
            return false;
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return false;

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
        }
        catch
        {
            return false;
        }

        return addresses.Length > 0 && addresses.All(IsPublicAddress);
    }

    /// Query strings and fragments frequently carry capability tokens for paid
    /// or private podcast feeds. Such feeds may still be fetched for the user's
    /// one request, but must not be retained in shared/in-memory caches.
    public static bool HasPotentiallySensitiveComponents(Uri uri) =>
        !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment);

    public static bool CanCacheFeed(Uri uri, bool isLocked) =>
        !isLocked && !HasPotentiallySensitiveComponents(uri);

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] is 0 or 10 or 127 || b[0] >= 224) return false;
            if (b[0] == 100 && b[1] is >= 64 and <= 127) return false;
            if (b[0] == 169 && b[1] == 254) return false;
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] == 198 && b[1] is 18 or 19) return false;
            // Protocol assignments and documentation-only networks.
            if (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) return false;
            if (b[0] == 198 && b[1] == 51 && b[2] == 100) return false;
            if (b[0] == 203 && b[1] == 0 && b[2] == 113) return false;
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
                return false;
            var b = address.GetAddressBytes();
            // 2001:db8::/32 is reserved for documentation and examples.
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8)
                return false;
            // fc00::/7 unique-local addresses.
            return (b[0] & 0xfe) != 0xfc;
        }

        return false;
    }

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        Exception? lastError = null;
        foreach (var address in addresses.Where(IsPublicAddress))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                lastError = ex;
                socket.Dispose();
            }
        }

        throw new HttpRequestException(
            "The feed host did not resolve to a reachable public internet address.",
            lastError);
    }
}
