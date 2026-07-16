using System.Net;
using System.Net.Sockets;

namespace BondCasts.Poller.Services.Polling;

/// SSRF guard for registered feed URLs (#20). PolledFeed rows live in the
/// CloudKit public database, where any iCloud user can register one, so a
/// registered URL is attacker-controlled input: it must never point the
/// poller at internal endpoints (the Azure instance metadata service at
/// 169.254.169.254, private ranges, loopback) or non-HTTP schemes.
///
/// Two layers, both required:
/// - TryValidate: URL shape (scheme, userinfo) before a request is built.
/// - GuardedConnectAsync: every address a host resolves to is re-checked at
///   socket-connect time, which is what actually stops a public host that
///   DNS-resolves to — or 30x-redirects to — an internal address (each
///   redirect hop reconnects through this callback). Checking at connect
///   rather than pre-flight also leaves no resolve-then-fetch TOCTOU window.
public static class FeedUrlGuard
{
    /// Shape checks that don't need the network: exactly http/https and no
    /// userinfo (a `user:pass@host` URL is a smuggling vector, and no real
    /// podcast host uses one).
    public static bool TryValidate(string url, out string reason)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            reason = "not an absolute URI";
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            reason = $"scheme '{uri.Scheme}' is not http(s)";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            reason = "URL carries a userinfo component";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    /// Addresses the poller must never open a socket to.
    public static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                0 => true,                                  // 0.0.0.0/8 "this network"
                10 => true,                                 // 10/8 private
                100 when b[1] is >= 64 and <= 127 => true,  // 100.64/10 CGNAT (used inside Azure)
                127 => true,                                // loopback
                169 when b[1] == 254 => true,               // link-local, incl. 169.254.169.254 IMDS
                172 when b[1] is >= 16 and <= 31 => true,   // 172.16/12 private
                192 when b[1] == 168 => true,               // 192.168/16 private
                >= 224 => true,                             // multicast, reserved, broadcast
                _ => false,
            };
        }

        return IPAddress.IsLoopback(address)   // ::1
            || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal          // fe80::/10
            || address.IsIPv6SiteLocal          // fec0::/10 (deprecated but resolvable)
            || address.IsIPv6UniqueLocal        // fc00::/7
            || address.IsIPv6Multicast;         // ff00::/8
    }

    /// SocketsHttpHandler.ConnectCallback: resolve, drop blocked addresses,
    /// connect to what's left. A host with no allowed address fails the
    /// request, which the poller's normal failure/backoff path absorbs.
    public static async ValueTask<Stream> GuardedConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, ct);
        var allowed = addresses.Where(a => !IsBlockedAddress(a)).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException(
                $"Host '{host}' resolves only to blocked (private/internal) addresses.");

        // Parameterless ctor = dual-mode IPv6 socket, reaches v4 and v6 alike.
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
