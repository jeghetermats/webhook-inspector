using System.Net;
using System.Net.Sockets;

namespace WebhookInspector.Api.Replay;

/// <summary>
/// Decides whether a replay may connect to an address. It is asked about the exact address
/// the socket is about to connect to (see <see cref="ReplayClient"/>), never about a host name.
/// </summary>
public interface IReplayAddressPolicy
{
    /// <returns><c>null</c> if the connection is allowed, otherwise the reason it is not.</returns>
    string? Check(IPAddress address, int port);
}

/// <summary>
/// The production policy: only ports 80 and 443, and only addresses on the public internet.
/// Everything a server can reach that an outsider cannot (loopback, private networks, the
/// cloud metadata service at 169.254.169.254, link-local, CGNAT, multicast and reserved
/// ranges) is refused. IPv6 is allowed only in global unicast space (2000::/3), minus the
/// ranges that tunnel to IPv4 addresses, so an IPv6 form cannot smuggle in a private IPv4 one.
/// </summary>
public sealed class PublicAddressPolicy : IReplayAddressPolicy
{
    public static readonly IReadOnlyList<int> AllowedPorts = [80, 443];

    private static readonly IPNetwork[] BlockedIPv4 =
    [
        IPNetwork.Parse("0.0.0.0/8"),        // "this network"
        IPNetwork.Parse("10.0.0.0/8"),       // private
        IPNetwork.Parse("100.64.0.0/10"),    // carrier-grade NAT
        IPNetwork.Parse("127.0.0.0/8"),      // loopback
        IPNetwork.Parse("169.254.0.0/16"),   // link-local, including the metadata service
        IPNetwork.Parse("172.16.0.0/12"),    // private
        IPNetwork.Parse("192.0.0.0/24"),     // IETF protocol assignments
        IPNetwork.Parse("192.0.2.0/24"),     // documentation
        IPNetwork.Parse("192.88.99.0/24"),   // 6to4 relay
        IPNetwork.Parse("192.168.0.0/16"),   // private
        IPNetwork.Parse("198.18.0.0/15"),    // benchmarking
        IPNetwork.Parse("198.51.100.0/24"),  // documentation
        IPNetwork.Parse("203.0.113.0/24"),   // documentation
        IPNetwork.Parse("224.0.0.0/4"),      // multicast
        IPNetwork.Parse("240.0.0.0/4"),      // reserved, including broadcast
    ];

    private static readonly IPNetwork GlobalUnicastIPv6 = IPNetwork.Parse("2000::/3");

    private static readonly IPNetwork[] BlockedIPv6 =
    [
        IPNetwork.Parse("2001::/32"),        // Teredo: embeds an IPv4 address
        IPNetwork.Parse("2001:2::/48"),      // benchmarking
        IPNetwork.Parse("2001:10::/28"),     // ORCHID
        IPNetwork.Parse("2001:20::/28"),     // ORCHIDv2
        IPNetwork.Parse("2001:db8::/32"),    // documentation
        IPNetwork.Parse("2002::/16"),        // 6to4: embeds an IPv4 address
        IPNetwork.Parse("3fff::/20"),        // documentation
    ];

    public string? Check(IPAddress address, int port)
    {
        if (!AllowedPorts.Contains(port))
        {
            return "Only ports 80 and 443 are allowed.";
        }

        return IsPublic(address) ? null : "The target is a private or reserved address.";
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            // ::ffff:127.0.0.1 is 127.0.0.1.
            return IsPublic(address.MapToIPv4());
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => !BlockedIPv4.Any(network => network.Contains(address)),
            AddressFamily.InterNetworkV6 =>
                address.ScopeId == 0
                && GlobalUnicastIPv6.Contains(address)
                && !BlockedIPv6.Any(network => network.Contains(address)),
            _ => false,
        };
    }
}
