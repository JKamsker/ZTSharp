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
    public async Task ConnectAsync_RetransmitsSyn_WhenNoSynAckArrives()
    {
        await using var link = new InMemoryIpv4Link();
        var localIp = IPAddress.Parse("10.0.0.1");
        var remoteIp = IPAddress.Parse("10.0.0.2");
        const ushort remotePort = 80;
        const ushort localPort = 50000;

        await using var client = new ZtUserSpaceTcpClient(link, localIp, remoteIp, remotePort, localPort: localPort, mss: 1200);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connectTask = client.ConnectAsync(cts.Token);

        var syn1 = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(ZtIpv4Codec.TryParse(syn1.Span, out _, out _, out _, out var syn1Payload));
        Assert.True(ZtTcpCodec.TryParse(syn1Payload, out _, out _, out var syn1Seq, out _, out var syn1Flags, out _, out _));
        Assert.Equal(ZtTcpCodec.Flags.Syn, syn1Flags);

        var syn2 = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(ZtIpv4Codec.TryParse(syn2.Span, out _, out _, out _, out var syn2Payload));
        Assert.True(ZtTcpCodec.TryParse(syn2Payload, out _, out _, out var syn2Seq, out _, out var syn2Flags, out _, out _));
        Assert.Equal(ZtTcpCodec.Flags.Syn, syn2Flags);
        Assert.Equal(syn1Seq, syn2Seq);

        var synAckTcp = ZtTcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localIp,
            sourcePort: remotePort,
            destinationPort: localPort,
            sequenceNumber: 1000,
            acknowledgmentNumber: unchecked(syn1Seq + 1),
            flags: ZtTcpCodec.Flags.Syn | ZtTcpCodec.Flags.Ack,
            windowSize: 65535,
            options: ReadOnlySpan<byte>.Empty,
            payload: ReadOnlySpan<byte>.Empty);

        link.Incoming.Writer.TryWrite(ZtIpv4Codec.Encode(remoteIp, localIp, ZtTcpCodec.ProtocolNumber, synAckTcp, identification: 1));

        await connectTask.WaitAsync(TimeSpan.FromSeconds(2));

        var ack = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(ZtIpv4Codec.TryParse(ack.Span, out _, out _, out _, out var ackPayload));
        Assert.True(ZtTcpCodec.TryParse(ackPayload, out _, out _, out _, out var ackAck, out var ackFlags, out _, out _));
        Assert.Equal(ZtTcpCodec.Flags.Ack, ackFlags);
        Assert.Equal(1001u, ackAck);
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

    [Fact]
    public async Task ReadAsync_ReassemblesOutOfOrderSegments()
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

        var clientSeq = unchecked(synSeq + 1);
        var serverSeqStart = 1001u;

        var worldTcp = ZtTcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localIp,
            sourcePort: remotePort,
            destinationPort: localPort,
            sequenceNumber: unchecked(serverSeqStart + 5),
            acknowledgmentNumber: clientSeq,
            flags: ZtTcpCodec.Flags.Ack | ZtTcpCodec.Flags.Psh,
            windowSize: 65535,
            options: ReadOnlySpan<byte>.Empty,
            payload: "world"u8);

        var helloTcp = ZtTcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localIp,
            sourcePort: remotePort,
            destinationPort: localPort,
            sequenceNumber: serverSeqStart,
            acknowledgmentNumber: clientSeq,
            flags: ZtTcpCodec.Flags.Ack | ZtTcpCodec.Flags.Psh,
            windowSize: 65535,
            options: ReadOnlySpan<byte>.Empty,
            payload: "hello"u8);

        link.Incoming.Writer.TryWrite(ZtIpv4Codec.Encode(remoteIp, localIp, ZtTcpCodec.ProtocolNumber, worldTcp, identification: 2));
        link.Incoming.Writer.TryWrite(ZtIpv4Codec.Encode(remoteIp, localIp, ZtTcpCodec.ProtocolNumber, helloTcp, identification: 3));

        var buffer = new byte[10];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var read = await ReadExactAsync(client, buffer, buffer.Length, cts.Token);
        Assert.Equal(buffer.Length, read);
        Assert.Equal("helloworld", System.Text.Encoding.ASCII.GetString(buffer));
    }

    [Fact]
    public async Task ReadAsync_SendsWindowUpdate_WhenReceiveWindowOpens()
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

        var payload = new byte[4096];
        Array.Fill(payload, (byte)'a');

        var clientSeq = unchecked(synSeq + 1);
        var serverSeqStart = 1001u;

        ushort lastWindow = 65535;
        for (var i = 0; i < 64; i++)
        {
            var dataTcp = ZtTcpCodec.Encode(
                sourceIp: remoteIp,
                destinationIp: localIp,
                sourcePort: remotePort,
                destinationPort: localPort,
                sequenceNumber: unchecked(serverSeqStart + (uint)(i * payload.Length)),
                acknowledgmentNumber: clientSeq,
                flags: ZtTcpCodec.Flags.Ack | ZtTcpCodec.Flags.Psh,
                windowSize: 65535,
                options: ReadOnlySpan<byte>.Empty,
                payload: payload);

            link.Incoming.Writer.TryWrite(ZtIpv4Codec.Encode(remoteIp, localIp, ZtTcpCodec.ProtocolNumber, dataTcp, identification: (ushort)(2 + i)));

            var ackPacket = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(ZtIpv4Codec.TryParse(ackPacket.Span, out _, out _, out _, out var ackPayload));
            Assert.True(ZtTcpCodec.TryParse(ackPayload, out _, out _, out _, out _, out var ackFlags, out lastWindow, out var ackTcpPayload));
            Assert.Equal(ZtTcpCodec.Flags.Ack, ackFlags);
            Assert.True(ackTcpPayload.IsEmpty);
        }

        Assert.Equal((ushort)0, lastWindow);

        var readBuffer = new byte[4096];
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var read = await client.ReadAsync(readBuffer, readCts.Token);
        Assert.Equal(readBuffer.Length, read);

        var update = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(ZtIpv4Codec.TryParse(update.Span, out _, out _, out _, out var updatePayload));
        Assert.True(ZtTcpCodec.TryParse(updatePayload, out _, out _, out _, out _, out var updateFlags, out var updateWindow, out var updateTcpPayload));
        Assert.Equal(ZtTcpCodec.Flags.Ack, updateFlags);
        Assert.True(updateTcpPayload.IsEmpty);
        Assert.NotEqual((ushort)0, updateWindow);
    }

    private static async Task<int> ReadExactAsync(ZtUserSpaceTcpClient client, byte[] buffer, int length, CancellationToken cancellationToken)
    {
        var readTotal = 0;
        while (readTotal < length)
        {
            var read = await client.ReadAsync(buffer.AsMemory(readTotal, length - readTotal), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return readTotal;
            }

            readTotal += read;
        }

        return readTotal;
    }

    private sealed class InMemoryIpv4Link : IZtUserSpaceIpLink
    {
        public Channel<ReadOnlyMemory<byte>> Incoming { get; } = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        public Channel<ReadOnlyMemory<byte>> Outgoing { get; } = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public ValueTask SendAsync(ReadOnlyMemory<byte> ipPacket, CancellationToken cancellationToken = default)
        {
            Outgoing.Writer.TryWrite(ipPacket);
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
