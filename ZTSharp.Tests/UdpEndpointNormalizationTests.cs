using System.Net;
using ZTSharp.Transport.Internal;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class UdpEndpointNormalizationTests
{
    [Fact]
    public async Task LocalEndpointOnWildcardBind_IsNotRewrittenToLoopback()
    {
        await using var transport = new ZeroTierUdpTransport(localPort: 0, enableIpv6: true);

        var endpoint = transport.LocalEndpoint;
        Assert.False(IPAddress.IsLoopback(endpoint.Address));
        Assert.True(endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public async Task SendAsync_RejectsUnspecifiedRemoteEndpoint(string address)
    {
        await using var transport = new ZeroTierUdpTransport(localPort: 0, enableIpv6: true);

        var remote = new IPEndPoint(IPAddress.Parse(address), 9999);
        await Assert.ThrowsAsync<ArgumentException>(() => transport.SendAsync(remote, payload: new byte[] { 0x01 }));
    }

    [Fact]
    public void NormalizeForAdvertisement_RewritesWildcardToLoopback()
    {
        var v4 = UdpEndpointNormalization.NormalizeForAdvertisement(new IPEndPoint(IPAddress.Any, 1234));
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 1234), v4);

        var v6 = UdpEndpointNormalization.NormalizeForAdvertisement(new IPEndPoint(IPAddress.IPv6Any, 2345));
        Assert.Equal(new IPEndPoint(IPAddress.IPv6Loopback, 2345), v6);
    }
}
