using System.Net;
using WebhookInspector.Api.Replay;

namespace WebhookInspector.Api.Tests.Replay;

public class AddressPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")] // cloud metadata service
    [InlineData("169.254.0.1")]
    [InlineData("100.64.0.1")]      // carrier-grade NAT
    [InlineData("100.127.255.255")]
    [InlineData("192.0.0.170")]
    [InlineData("192.0.2.10")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
    [InlineData("224.0.0.1")]       // multicast
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]   // IPv4-mapped loopback
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::127.0.0.1")]        // IPv4-compatible (deprecated)
    [InlineData("fc00::1")]            // unique local
    [InlineData("fd12:3456:789a::1")]
    [InlineData("fe80::1")]            // link-local
    [InlineData("ff02::1")]            // multicast
    [InlineData("64:ff9b::7f00:1")]    // NAT64 of 127.0.0.1
    [InlineData("2001:db8::1")]        // documentation
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")] // Teredo
    [InlineData("2002:7f00:1::1")]     // 6to4 of 127.0.0.1
    public void Blocks_addresses_outsiders_cannot_reach(string address)
    {
        Assert.False(PublicAddressPolicy.IsPublic(IPAddress.Parse(address)));
        Assert.NotNull(new PublicAddressPolicy().Check(IPAddress.Parse(address), 443));
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("140.82.112.3")]
    [InlineData("100.63.255.255")]  // just below CGNAT
    [InlineData("100.128.0.0")]     // just above CGNAT
    [InlineData("172.15.255.255")]  // just below 172.16/12
    [InlineData("172.32.0.0")]      // just above it
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2a00:1450:400f:80d::200e")]
    public void Allows_public_addresses(string address)
    {
        Assert.True(PublicAddressPolicy.IsPublic(IPAddress.Parse(address)));
        Assert.Null(new PublicAddressPolicy().Check(IPAddress.Parse(address), 443));
    }

    [Fact]
    public void Scoped_IPv6_addresses_are_blocked()
    {
        var address = IPAddress.Parse("2606:4700:4700::1111");
        address.ScopeId = 3;
        Assert.False(PublicAddressPolicy.IsPublic(address));
    }

    [Theory]
    [InlineData(80, true)]
    [InlineData(443, true)]
    [InlineData(22, false)]
    [InlineData(5432, false)]
    [InlineData(8080, false)]
    public void Only_ports_80_and_443(int port, bool allowed)
    {
        var reason = new PublicAddressPolicy().Check(IPAddress.Parse("1.1.1.1"), port);
        Assert.Equal(allowed, reason is null);
    }
}
