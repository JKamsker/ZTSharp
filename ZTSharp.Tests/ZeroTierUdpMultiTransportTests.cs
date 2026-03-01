using System.Net;
using System.Reflection;
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

    [Fact]
    public async Task DisposeAsync_DisposesUnderlyingSockets_AndMarksTransportDisposed()
    {
        var socket1 = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false, localSocketId: 1);
        var socket2 = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false, localSocketId: 2);
        var multi = new ZeroTierUdpMultiTransport(new[] { socket1, socket2 });

        try
        {
            await multi.DisposeAsync();

            Assert.Throws<ObjectDisposedException>(() => _ = multi.LocalSockets);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                multi.SendAsync(new IPEndPoint(IPAddress.Loopback, 1), new byte[] { 0x00 }));

            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                socket1.SendAsync(new IPEndPoint(IPAddress.Loopback, 1), new byte[] { 0x01 }));
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                socket2.SendAsync(new IPEndPoint(IPAddress.Loopback, 1), new byte[] { 0x02 }));
        }
        finally
        {
            await multi.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_RemainsBestEffort_WhenForwarderFaults()
    {
        var socket1 = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false, localSocketId: 1);
        var socket2 = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false, localSocketId: 2);
        var multi = new ZeroTierUdpMultiTransport(new[] { socket1, socket2 });

        try
        {
            var forwardersField = typeof(ZeroTierUdpMultiTransport).GetField("_forwarders", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(forwardersField);

            var forwarders = (Task[]?)forwardersField!.GetValue(multi);
            Assert.NotNull(forwarders);
            Assert.NotEmpty(forwarders!);

            forwarders![0] = Task.WhenAll(forwarders[0], Task.FromException(new InvalidOperationException("boom")));

            await multi.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                socket1.SendAsync(new IPEndPoint(IPAddress.Loopback, 1), new byte[] { 0x01 }));
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                socket2.SendAsync(new IPEndPoint(IPAddress.Loopback, 1), new byte[] { 0x02 }));
        }
        finally
        {
            await multi.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledConcurrently_WithoutThrowing()
    {
        var socket1 = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false, localSocketId: 1);
        var socket2 = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false, localSocketId: 2);
        var multi = new ZeroTierUdpMultiTransport(new[] { socket1, socket2 });

        try
        {
            var tasks = Enumerable.Range(0, 20)
                .Select(_ => multi.DisposeAsync().AsTask())
                .ToArray();

            await Task.WhenAll(tasks);

            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                socket1.SendAsync(new IPEndPoint(IPAddress.Loopback, 1), new byte[] { 0x01 }));
        }
        finally
        {
            await multi.DisposeAsync();
        }
    }
}

