using System.Net;
using System.Net.Sockets;
using ZTSharp.ZeroTier.Internal;

namespace ZTSharp.Tests;

public sealed class ZeroTierDirectEndpointSelectionTests
{
    [Fact]
    public void Normalize_CanonicalizesIpv4MappedIpv6_ForUniquenessAndOrdering()
    {
        var relay = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 9993);
        var endpoints = new[]
        {
            new IPEndPoint(IPAddress.Parse("::ffff:8.8.8.8"), 9993),
            new IPEndPoint(IPAddress.Parse("8.8.8.8"), 9993),
            new IPEndPoint(IPAddress.Parse("10.0.0.1"), 9993),
            new IPEndPoint(IPAddress.Parse("::ffff:10.0.0.1"), 9993),
        };

        var normalized = ZeroTierDirectEndpointSelection.Normalize(endpoints, relay, maxEndpoints: 10);

        Assert.Equal(2, normalized.Length);
        Assert.All(normalized, endpoint =>
        {
            Assert.Equal(AddressFamily.InterNetwork, endpoint.AddressFamily);
            Assert.False(endpoint.Address.IsIPv4MappedToIPv6);
        });

        Assert.Equal(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 9993), normalized[0]);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 9993), normalized[1]);
    }
}

