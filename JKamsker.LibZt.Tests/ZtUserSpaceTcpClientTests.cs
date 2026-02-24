using System.Net;
using System.Threading.Channels;
using JKamsker.LibZt.ZeroTier.Net;

namespace JKamsker.LibZt.Tests;

public sealed class ZtUserSpaceTcpClientTests
{
    [Fact]
    public async Task ConnectAsync_SendsSyn_AndCompletesOnSynAck()
    {
        await using var link = new InMemoryIpv4Link();
        var localIp = IPAddress.Parse("10.0.0.1");
        var remoteIp = IPAddress.Parse("10.0.0.2");
        const ushort remotePort = 80;
        const ushort localPort = 50000;

        await using var client = new ZtUserSpaceTcpClient(link, localIp, remoteIp, remotePort, localPort: localPort, mss: 1200);

        var connectTask = client.ConnectAsync();

        var syn = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(ZtIpv4Codec.TryParse(syn.Span, out var synSrc, out var synDst, out var protocol, out var synPayload));
        Assert.Equal(localIp, synSrc);
        Assert.Equal(remoteIp, synDst);
        Assert.Equal(ZtTcpCodec.ProtocolNumber, protocol);

        Assert.True(ZtTcpCodec.TryParse(synPayload, out var synSrcPort, out var synDstPort, out var synSeq, out _, out var synFlags, out _, out var synTcpPayload));
        Assert.Equal(localPort, synSrcPort);
        Assert.Equal(remotePort, synDstPort);
        Assert.Equal(ZtTcpCodec.Flags.Syn, synFlags);
        Assert.True(synTcpPayload.IsEmpty);

        var synAckTcp = ZtTcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localIp,
            sourcePort: remotePort,
            destinationPort: localPort,
            sequenceNumber: 1000,
            acknowledgmentNumber: unchecked(synSeq + 1),
            flags: ZtTcpCodec.Flags.Syn | ZtTcpCodec.Flags.Ack,
            windowSize: 65535,
            options: ReadOnlySpan<byte>.Empty,
            payload: ReadOnlySpan<byte>.Empty);

        var synAckIpv4 = ZtIpv4Codec.Encode(remoteIp, localIp, ZtTcpCodec.ProtocolNumber, synAckTcp, identification: 1);
        link.Incoming.Writer.TryWrite(synAckIpv4);

        await connectTask.WaitAsync(TimeSpan.FromSeconds(2));

        var ack = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(ZtIpv4Codec.TryParse(ack.Span, out _, out _, out _, out var ackPayload));
        Assert.True(ZtTcpCodec.TryParse(ackPayload, out _, out _, out var ackSeq, out var ackAck, out var ackFlags, out _, out var ackTcpPayload));
        Assert.Equal(ZtTcpCodec.Flags.Ack, ackFlags);
        Assert.Equal(unchecked(synSeq + 1), ackSeq);
        Assert.Equal(1001u, ackAck);
        Assert.True(ackTcpPayload.IsEmpty);
    }

    [Fact]
    public async Task WriteAsync_WaitsForAck()
    {
        await using var link = new InMemoryIpv4Link();
        var localIp = IPAddress.Parse("10.0.0.1");
        var remoteIp = IPAddress.Parse("10.0.0.2");
        const ushort remotePort = 80;
        const ushort localPort = 50000;

        await using var client = new ZtUserSpaceTcpClient(link, localIp, remoteIp, remotePort, localPort: localPort, mss: 1200);

        var connectTask = client.ConnectAsync();
        var syn = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(ZtIpv4Codec.TryParse(syn.Span, out _, out _, out _, out var synPayload));
        Assert.True(ZtTcpCodec.TryParse(synPayload, out _, out _, out var synSeq, out _, out _, out _, out _));

        var synAckTcp = ZtTcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localIp,
            sourcePort: remotePort,
            destinationPort: localPort,
            sequenceNumber: 1000,
            acknowledgmentNumber: unchecked(synSeq + 1),
            flags: ZtTcpCodec.Flags.Syn | ZtTcpCodec.Flags.Ack,
            windowSize: 65535,
            options: ReadOnlySpan<byte>.Empty,
            payload: ReadOnlySpan<byte>.Empty);

        link.Incoming.Writer.TryWrite(ZtIpv4Codec.Encode(remoteIp, localIp, ZtTcpCodec.ProtocolNumber, synAckTcp, identification: 1));

        await connectTask.WaitAsync(TimeSpan.FromSeconds(2));
        _ = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); // final ACK

        var writeTask = client.WriteAsync("hi"u8.ToArray(), CancellationToken.None).AsTask();

        var data = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(ZtIpv4Codec.TryParse(data.Span, out _, out _, out _, out var dataPayload));
        Assert.True(ZtTcpCodec.TryParse(dataPayload, out _, out _, out var dataSeq, out var dataAck, out var dataFlags, out _, out var tcpPayload));
        Assert.Equal(ZtTcpCodec.Flags.Ack | ZtTcpCodec.Flags.Psh, dataFlags);
        Assert.Equal(2, tcpPayload.Length);

        var expectedAck = unchecked(dataSeq + (uint)tcpPayload.Length);
        var ackTcp = ZtTcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localIp,
            sourcePort: remotePort,
            destinationPort: localPort,
            sequenceNumber: dataAck,
            acknowledgmentNumber: expectedAck,
            flags: ZtTcpCodec.Flags.Ack,
            windowSize: 65535,
            options: ReadOnlySpan<byte>.Empty,
            payload: ReadOnlySpan<byte>.Empty);

        link.Incoming.Writer.TryWrite(ZtIpv4Codec.Encode(remoteIp, localIp, ZtTcpCodec.ProtocolNumber, ackTcp, identification: 2));

        await writeTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class InMemoryIpv4Link : IZtUserSpaceIpv4Link
    {
        public Channel<ReadOnlyMemory<byte>> Incoming { get; } = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        public Channel<ReadOnlyMemory<byte>> Outgoing { get; } = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public ValueTask SendAsync(ReadOnlyMemory<byte> ipv4Packet, CancellationToken cancellationToken = default)
        {
            Outgoing.Writer.TryWrite(ipv4Packet);
            return ValueTask.CompletedTask;
        }

        public ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
            => Incoming.Reader.ReadAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            Incoming.Writer.TryComplete();
            Outgoing.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}

