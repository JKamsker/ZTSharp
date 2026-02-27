using System.Net;
using System.Threading.Channels;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierDataplaneShutdownTests
{
    [Fact]
    public async Task DispatcherLoopAsync_ExitsCleanly_WhenUdpTransportIsDisposed()
    {
        await using var udp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        await using var sender = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);

        var rootEndpoint = TestUdpEndpoints.ToLoopback(sender.LocalEndpoint);
        var rootClient = new ZeroTierDataplaneRootClient(
            udp,
            rootNodeId: new NodeId(0x1111111111),
            rootEndpoint: rootEndpoint,
            rootKey: new byte[48],
            rootProtocolVersion: 12,
            localNodeId: new NodeId(0x2222222222),
            networkId: 1,
            inlineCom: Array.Empty<byte>());

        var loops = new ZeroTierDataplaneRxLoops(
            udp,
            rootNodeId: new NodeId(0x1111111111),
            rootEndpoint: rootEndpoint,
            rootKey: new byte[48],
            localNodeId: new NodeId(0x2222222222),
            rootClient: rootClient,
            peerDatagrams: new NoopPeerDatagrams());

        var peerChannel = Channel.CreateUnbounded<ZeroTierUdpDatagram>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        using var cts = new CancellationTokenSource();
        var dispatcher = Task.Run(() => loops.DispatcherLoopAsync(peerChannel, cts.Token), CancellationToken.None);

        var peerPacket = ZeroTierPacketCodec.Encode(
            new ZeroTierPacketHeader(
                PacketId: 1,
                Destination: new NodeId(0x2222222222),
                Source: new NodeId(0x3333333333),
                Flags: 0,
                Mac: 0,
                VerbRaw: 0),
            ReadOnlySpan<byte>.Empty);

        await sender.SendAsync(TestUdpEndpoints.ToLoopback(udp.LocalEndpoint), peerPacket);
        _ = await ReadAsyncWithTimeout(peerChannel.Reader, TimeSpan.FromSeconds(2));

        await udp.DisposeAsync();

        await dispatcher.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
    }

    private static async Task<T> ReadAsyncWithTimeout<T>(ChannelReader<T> reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await reader.ReadAsync(cts.Token);
    }

    private sealed class NoopPeerDatagrams : IZeroTierDataplanePeerDatagramProcessor
    {
        public Task ProcessAsync(ZeroTierUdpDatagram datagram, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
