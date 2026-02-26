using System.Net;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierUdpMultiTransportTests
{
    [Fact]
    public async Task ReceiveAsync_ForwardsDatagramsFromAllSockets_WithSocketId()
    {
        await using var socket1 = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false, localSocketId: 1);
        await using var socket2 = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false, localSocketId: 2);
        await using var multi = new ZeroTierUdpMultiTransport(new[] { socket1, socket2 });

        await using var sender = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);

        var endpoint1 = TestUdpEndpoints.ToLoopback(socket1.LocalEndpoint);
        var endpoint2 = TestUdpEndpoints.ToLoopback(socket2.LocalEndpoint);

        await sender.SendAsync(endpoint1, new byte[] { 0x01 });
        await sender.SendAsync(endpoint2, new byte[] { 0x02 });

        var seen = new HashSet<int>();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (seen.Count < 2 && DateTimeOffset.UtcNow < deadline)
        {
            var datagram = await multi.ReceiveAsync(TimeSpan.FromSeconds(2));
            seen.Add(datagram.LocalSocketId);
        }

        Assert.Contains(1, seen);
        Assert.Contains(2, seen);
    }

    [Fact]
    public async Task SendAsync_UsesSelectedSocket_AsUdpSourceEndpoint()
    {
        await using var socket1 = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false, localSocketId: 1);
        await using var socket2 = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false, localSocketId: 2);
        await using var multi = new ZeroTierUdpMultiTransport(new[] { socket1, socket2 });

        await using var receiver = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        var receiverEndpoint = TestUdpEndpoints.ToLoopback(receiver.LocalEndpoint);

        await multi.SendAsync(localSocketId: 1, receiverEndpoint, new byte[] { 0x11 });
        var datagram1 = await receiver.ReceiveAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(socket1.LocalEndpoint.Port, datagram1.RemoteEndPoint.Port);

        await multi.SendAsync(localSocketId: 2, receiverEndpoint, new byte[] { 0x22 });
        var datagram2 = await receiver.ReceiveAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(socket2.LocalEndpoint.Port, datagram2.RemoteEndPoint.Port);
    }
}

