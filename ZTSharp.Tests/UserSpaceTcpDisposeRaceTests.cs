using System.Net;
using ZTSharp.ZeroTier.Net;

namespace ZTSharp.Tests;

public sealed class UserSpaceTcpDisposeRaceTests
{
    [Fact]
    public async Task DisposeAsync_ConcurrentWithInboundData_DoesNotThrow()
    {
        await using var link = new InspectableIpv4Link();
        var localIp = IPAddress.Parse("10.0.0.1");
        var remoteIp = IPAddress.Parse("10.0.0.2");
        const ushort remotePort = 80;
        const ushort localPort = 50000;

        await using var client = new UserSpaceTcpClient(link, localIp, remoteIp, remotePort, localPort: localPort, mss: 1200);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connectTask = client.ConnectAsync(cts.Token);

        var syn = await link.Outgoing.Reader.ReadAsync(cts.Token);
        Assert.True(Ipv4Codec.TryParse(syn.Span, out _, out _, out _, out var synPayload));
        Assert.True(TcpCodec.TryParse(synPayload, out _, out _, out var synSeq, out _, out _, out _, out _));

        const uint remoteIss = 1000;
        var synAckTcp = TcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localIp,
            sourcePort: remotePort,
            destinationPort: localPort,
            sequenceNumber: remoteIss,
            acknowledgmentNumber: unchecked(synSeq + 1),
            flags: TcpCodec.Flags.Syn | TcpCodec.Flags.Ack,
            windowSize: 65535,
            options: ReadOnlySpan<byte>.Empty,
            payload: ReadOnlySpan<byte>.Empty);
        link.Incoming.Writer.TryWrite(Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, synAckTcp, identification: 1));

        await connectTask;
        _ = await link.Outgoing.Reader.ReadAsync(cts.Token); // final ACK

        using var injectCts = new CancellationTokenSource();
        var injectTask = Task.Run(async () =>
        {
            var remoteSeq = remoteIss + 1;
            var payload = new byte[800];

            while (!injectCts.IsCancellationRequested)
            {
                var tcp = TcpCodec.Encode(
                    sourceIp: remoteIp,
                    destinationIp: localIp,
                    sourcePort: remotePort,
                    destinationPort: localPort,
                    sequenceNumber: remoteSeq,
                    acknowledgmentNumber: unchecked(synSeq + 1),
                    flags: TcpCodec.Flags.Ack | TcpCodec.Flags.Psh,
                    windowSize: 65535,
                    options: ReadOnlySpan<byte>.Empty,
                    payload: payload);

                var ipv4 = Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, tcp, identification: 2);
                link.Incoming.Writer.TryWrite(ipv4);

                remoteSeq = unchecked(remoteSeq + (uint)payload.Length);
                await Task.Yield();
            }
        }, CancellationToken.None);

        await client.DisposeAsync();

        injectCts.Cancel();
        await injectTask;
    }
}

