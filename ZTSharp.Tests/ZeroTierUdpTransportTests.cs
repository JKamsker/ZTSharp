using System.Net;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierUdpTransportTests
{
    [Fact]
    public async Task CanSendAndReceiveLoopbackDatagrams()
    {
        await using var a = new ZeroTierUdpTransport(localPort: 0, enableIpv6: true);
        await using var b = new ZeroTierUdpTransport(localPort: 0, enableIpv6: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var ping = "ping"u8.ToArray();
        var bReachable = b.LocalEndpoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Loopback, b.LocalEndpoint.Port)
            : new IPEndPoint(IPAddress.Loopback, b.LocalEndpoint.Port);
        await a.SendAsync(bReachable, ping, cts.Token);

        var receivedPing = await b.ReceiveAsync(cts.Token);
        Assert.True(receivedPing.Payload.AsSpan().SequenceEqual(ping));

        var pong = "pong"u8.ToArray();
        await b.SendAsync(receivedPing.RemoteEndPoint, pong, cts.Token);

        var receivedPong = await a.ReceiveAsync(cts.Token);
        Assert.True(receivedPong.Payload.AsSpan().SequenceEqual(pong));
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledConcurrently_WithoutThrowing()
    {
        var transport = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => transport.DisposeAsync().AsTask())
            .ToArray();

        await Task.WhenAll(tasks);

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            transport.SendAsync(new IPEndPoint(IPAddress.Loopback, 1), new byte[] { 0x00 }));
    }
}
