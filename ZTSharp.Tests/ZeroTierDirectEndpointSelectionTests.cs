using System.Net;
using ZTSharp.ZeroTier.Internal;

namespace ZTSharp.Tests;

public sealed class ZeroTierDirectEndpointSelectionTests
{
    [Fact]
    public void Normalize_ExcludesRelayEndpoint_AndUnspecifiedAddresses()
    {
        var relay = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 9999);

        var endpoints = new[]
        {
            relay,
            new IPEndPoint(IPAddress.Any, 1234),
            new IPEndPoint(IPAddress.IPv6Any, 1234),
            new IPEndPoint(IPAddress.Parse("8.8.8.8"), 0),
            new IPEndPoint(IPAddress.Parse("8.8.8.8"), 1234),
        };

        var normalized = ZeroTierDirectEndpointSelection.Normalize(endpoints, relayEndpoint: relay, maxEndpoints: 16);

        Assert.Single(normalized);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 1234), normalized[0]);
    }

    [Fact]
    public void Normalize_OrdersPublicBeforePrivate_AndDedupes()
    {
        var relay = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 9999);

        var publicV4 = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 1000);
        var publicV6 = new IPEndPoint(IPAddress.Parse("2001:4860:4860::8888"), 1001);
        var privateV4 = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 1002);
        var privateV6 = new IPEndPoint(IPAddress.Parse("fc00::1"), 1003);

        var normalized = ZeroTierDirectEndpointSelection.Normalize(
            endpoints: new[]
            {
                privateV6,
                relay,
                publicV6,
                privateV4,
                publicV4,
                publicV4,
            },
            relayEndpoint: relay,
            maxEndpoints: 16);

        Assert.Equal(new[] { publicV4, publicV6, privateV4, privateV6 }, normalized);
    }

    [Fact]
    public void Normalize_TreatsIpv4MappedIpv6LikeIpv4_ForDedupe()
    {
        var relay = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 9999);

        var v4 = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 1000);
        var v4Mapped = new IPEndPoint(IPAddress.Parse("::ffff:8.8.8.8"), 1000);

        var normalized = ZeroTierDirectEndpointSelection.Normalize(
            endpoints: new[] { v4Mapped, v4 },
            relayEndpoint: relay,
            maxEndpoints: 16);

        Assert.Single(normalized);
        Assert.Equal(v4, normalized[0]);
    }
}
