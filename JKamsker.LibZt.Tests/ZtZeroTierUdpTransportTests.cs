using JKamsker.LibZt.ZeroTier.Transport;

namespace JKamsker.LibZt.Tests;

public sealed class ZtZeroTierUdpTransportTests
{
    [Fact]
    public async Task CanSendAndReceiveLoopbackDatagrams()
    {
        await using var a = new ZtZeroTierUdpTransport(localPort: 0, enableIpv6: true);
        await using var b = new ZtZeroTierUdpTransport(localPort: 0, enableIpv6: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var ping = "ping"u8.ToArray();
        await a.SendAsync(b.LocalEndpoint, ping, cts.Token);

        var receivedPing = await b.ReceiveAsync(cts.Token);
        Assert.True(receivedPing.Payload.Span.SequenceEqual(ping));

        var pong = "pong"u8.ToArray();
        await b.SendAsync(receivedPing.RemoteEndPoint, pong, cts.Token);

        var receivedPong = await a.ReceiveAsync(cts.Token);
        Assert.True(receivedPong.Payload.Span.SequenceEqual(pong));
    }
}

