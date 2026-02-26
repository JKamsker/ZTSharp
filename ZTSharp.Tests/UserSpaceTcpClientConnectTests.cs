using System.Net;
using ZTSharp.ZeroTier.Net;

namespace ZTSharp.Tests;

public sealed class UserSpaceTcpClientConnectTests
{
    [Fact]
    public async Task ConnectAsync_SendsSyn_AndCompletesOnSynAck()
    {
        await using var link = new InspectableIpv4Link();
        var localIp = IPAddress.Parse("10.0.0.1");
        var remoteIp = IPAddress.Parse("10.0.0.2");
        const ushort remotePort = 80;
        const ushort localPort = 50000;

        await using var client = new UserSpaceTcpClient(link, localIp, remoteIp, remotePort, localPort: localPort, mss: 1200);

        var connectTask = client.ConnectAsync();

        var syn = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Ipv4Codec.TryParse(syn.Span, out var synSrc, out var synDst, out var protocol, out var synPayload));
        Assert.Equal(localIp, synSrc);
        Assert.Equal(remoteIp, synDst);
        Assert.Equal(TcpCodec.ProtocolNumber, protocol);

        Assert.True(TcpCodec.TryParse(synPayload, out var synSrcPort, out var synDstPort, out var synSeq, out _, out var synFlags, out _, out var synTcpPayload));
        Assert.Equal(localPort, synSrcPort);
        Assert.Equal(remotePort, synDstPort);
        Assert.Equal(TcpCodec.Flags.Syn, synFlags);
        Assert.True(synTcpPayload.IsEmpty);

        var synAckTcp = TcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localIp,
            sourcePort: remotePort,
            destinationPort: localPort,
            sequenceNumber: 1000,
            acknowledgmentNumber: unchecked(synSeq + 1),
            flags: TcpCodec.Flags.Syn | TcpCodec.Flags.Ack,
            windowSize: 65535,
            options: ReadOnlySpan<byte>.Empty,
            payload: ReadOnlySpan<byte>.Empty);

        var synAckIpv4 = Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, synAckTcp, identification: 1);
        link.Incoming.Writer.TryWrite(synAckIpv4);

        await connectTask.WaitAsync(TimeSpan.FromSeconds(2));

        var ack = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Ipv4Codec.TryParse(ack.Span, out _, out _, out _, out var ackPayload));
        Assert.True(TcpCodec.TryParse(ackPayload, out _, out _, out var ackSeq, out var ackAck, out var ackFlags, out _, out var ackTcpPayload));
        Assert.Equal(TcpCodec.Flags.Ack, ackFlags);
        Assert.Equal(unchecked(synSeq + 1), ackSeq);
        Assert.Equal(1001u, ackAck);
        Assert.True(ackTcpPayload.IsEmpty);
    }

    [Fact]
    public async Task ConnectAsync_RetransmitsSyn_WhenNoSynAckArrives()
    {
        await using var link = new InspectableIpv4Link();
        var localIp = IPAddress.Parse("10.0.0.1");
        var remoteIp = IPAddress.Parse("10.0.0.2");
        const ushort remotePort = 80;
        const ushort localPort = 50000;

        await using var client = new UserSpaceTcpClient(link, localIp, remoteIp, remotePort, localPort: localPort, mss: 1200);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connectTask = client.ConnectAsync(cts.Token);

        var syn1 = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Ipv4Codec.TryParse(syn1.Span, out _, out _, out _, out var syn1Payload));
        Assert.True(TcpCodec.TryParse(syn1Payload, out _, out _, out var syn1Seq, out _, out var syn1Flags, out _, out _));
        Assert.Equal(TcpCodec.Flags.Syn, syn1Flags);

        var syn2 = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Ipv4Codec.TryParse(syn2.Span, out _, out _, out _, out var syn2Payload));
        Assert.True(TcpCodec.TryParse(syn2Payload, out _, out _, out var syn2Seq, out _, out var syn2Flags, out _, out _));
        Assert.Equal(TcpCodec.Flags.Syn, syn2Flags);
        Assert.Equal(syn1Seq, syn2Seq);

        var synAckTcp = TcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localIp,
            sourcePort: remotePort,
            destinationPort: localPort,
            sequenceNumber: 1000,
            acknowledgmentNumber: unchecked(syn1Seq + 1),
            flags: TcpCodec.Flags.Syn | TcpCodec.Flags.Ack,
            windowSize: 65535,
            options: ReadOnlySpan<byte>.Empty,
            payload: ReadOnlySpan<byte>.Empty);

        link.Incoming.Writer.TryWrite(Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, synAckTcp, identification: 1));

        await connectTask.WaitAsync(TimeSpan.FromSeconds(2));

        var ack = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Ipv4Codec.TryParse(ack.Span, out _, out _, out _, out var ackPayload));
        Assert.True(TcpCodec.TryParse(ackPayload, out _, out _, out _, out var ackAck, out var ackFlags, out _, out _));
        Assert.Equal(TcpCodec.Flags.Ack, ackFlags);
        Assert.Equal(1001u, ackAck);
    }
}

