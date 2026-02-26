using System.Net;
using ZTSharp.ZeroTier.Net;

namespace ZTSharp.Tests;

public sealed class UserSpaceTcpClientIoTests
{
    [Fact]
    public async Task WriteAsync_WaitsForAck()
    {
        await using var link = new InspectableIpv4Link();
        var localIp = IPAddress.Parse("10.0.0.1");
        var remoteIp = IPAddress.Parse("10.0.0.2");
        const ushort remotePort = 80;
        const ushort localPort = 50000;

        await using var client = new UserSpaceTcpClient(link, localIp, remoteIp, remotePort, localPort: localPort, mss: 1200);

        var connectTask = client.ConnectAsync();
        var syn = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Ipv4Codec.TryParse(syn.Span, out _, out _, out _, out var synPayload));
        Assert.True(TcpCodec.TryParse(synPayload, out _, out _, out var synSeq, out _, out _, out _, out _));

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

        link.Incoming.Writer.TryWrite(Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, synAckTcp, identification: 1));

        await connectTask.WaitAsync(TimeSpan.FromSeconds(2));
        _ = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); // final ACK

        var writeTask = client.WriteAsync("hi"u8.ToArray(), CancellationToken.None).AsTask();

        var data = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Ipv4Codec.TryParse(data.Span, out _, out _, out _, out var dataPayload));
        Assert.True(TcpCodec.TryParse(dataPayload, out _, out _, out var dataSeq, out var dataAck, out var dataFlags, out _, out var tcpPayload));
        Assert.Equal(TcpCodec.Flags.Ack | TcpCodec.Flags.Psh, dataFlags);
        Assert.Equal(2, tcpPayload.Length);

        var expectedAck = unchecked(dataSeq + (uint)tcpPayload.Length);
        var ackTcp = TcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localIp,
            sourcePort: remotePort,
            destinationPort: localPort,
            sequenceNumber: dataAck,
            acknowledgmentNumber: expectedAck,
            flags: TcpCodec.Flags.Ack,
            windowSize: 65535,
            options: ReadOnlySpan<byte>.Empty,
            payload: ReadOnlySpan<byte>.Empty);

        link.Incoming.Writer.TryWrite(Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, ackTcp, identification: 2));

        await writeTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task WriteAsync_Completes_WhenAckArrivesAfterTimeout()
    {
        await using var link = new InspectableIpv4Link();
        var localIp = IPAddress.Parse("10.0.0.1");
        var remoteIp = IPAddress.Parse("10.0.0.2");
        const ushort remotePort = 80;
        const ushort localPort = 50000;

        await using var client = new UserSpaceTcpClient(link, localIp, remoteIp, remotePort, localPort: localPort, mss: 1200);

        var connectTask = client.ConnectAsync();
        var syn = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Ipv4Codec.TryParse(syn.Span, out _, out _, out _, out var synPayload));
        Assert.True(TcpCodec.TryParse(synPayload, out _, out _, out var synSeq, out _, out _, out _, out _));

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

        link.Incoming.Writer.TryWrite(Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, synAckTcp, identification: 1));

        await connectTask.WaitAsync(TimeSpan.FromSeconds(2));
        _ = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); // final ACK

        var writeTask = client.WriteAsync("hi"u8.ToArray(), CancellationToken.None).AsTask();

        var firstData = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Ipv4Codec.TryParse(firstData.Span, out _, out _, out _, out var firstDataPayload));
        Assert.True(TcpCodec.TryParse(firstDataPayload, out _, out _, out var dataSeq, out var dataAck, out var dataFlags, out _, out var tcpPayload));
        Assert.Equal(TcpCodec.Flags.Ack | TcpCodec.Flags.Psh, dataFlags);
        Assert.Equal(2, tcpPayload.Length);

        var expectedAck = unchecked(dataSeq + (uint)tcpPayload.Length);

        var retransmitted = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(Ipv4Codec.TryParse(retransmitted.Span, out _, out _, out _, out var retransmitPayload));
        Assert.True(TcpCodec.TryParse(retransmitPayload, out _, out _, out var retransmitSeq, out _, out var retransmitFlags, out _, out var retransmitTcpPayload));
        Assert.Equal(dataSeq, retransmitSeq);
        Assert.Equal(TcpCodec.Flags.Ack | TcpCodec.Flags.Psh, retransmitFlags);
        Assert.Equal(2, retransmitTcpPayload.Length);

        var ackTcp = TcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localIp,
            sourcePort: remotePort,
            destinationPort: localPort,
            sequenceNumber: dataAck,
            acknowledgmentNumber: expectedAck,
            flags: TcpCodec.Flags.Ack,
            windowSize: 65535,
            options: ReadOnlySpan<byte>.Empty,
            payload: ReadOnlySpan<byte>.Empty);

        link.Incoming.Writer.TryWrite(Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, ackTcp, identification: 2));

        await writeTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task WriteAsync_RespectsRemoteMssAdvertisedInSynAck()
    {
        await using var link = new InspectableIpv4Link();
        var localIp = IPAddress.Parse("10.0.0.1");
        var remoteIp = IPAddress.Parse("10.0.0.2");
        const ushort remotePort = 80;
        const ushort localPort = 50000;
        const ushort remoteMss = 536;

        await using var client = new UserSpaceTcpClient(link, localIp, remoteIp, remotePort, localPort: localPort, mss: 1200);

        var connectTask = client.ConnectAsync();
        var syn = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Ipv4Codec.TryParse(syn.Span, out _, out _, out _, out var synPayload));
        Assert.True(TcpCodec.TryParse(synPayload, out _, out _, out var synSeq, out _, out _, out _, out _));

        var synAckTcp = TcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localIp,
            sourcePort: remotePort,
            destinationPort: localPort,
            sequenceNumber: 1000,
            acknowledgmentNumber: unchecked(synSeq + 1),
            flags: TcpCodec.Flags.Syn | TcpCodec.Flags.Ack,
            windowSize: 65535,
            options: TcpCodec.EncodeMssOption(remoteMss),
            payload: ReadOnlySpan<byte>.Empty);

        link.Incoming.Writer.TryWrite(Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, synAckTcp, identification: 1));

        await connectTask.WaitAsync(TimeSpan.FromSeconds(2));
        _ = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); // final ACK

        var payload = new byte[600];
        Array.Fill(payload, (byte)'a');

        var writeTask = client.WriteAsync(payload, CancellationToken.None).AsTask();

        var firstData = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Ipv4Codec.TryParse(firstData.Span, out _, out _, out _, out var firstDataPayload));
        Assert.True(TcpCodec.TryParse(firstDataPayload, out _, out _, out var firstSeq, out var firstAck, out var firstFlags, out _, out var firstTcpPayload));
        Assert.Equal(TcpCodec.Flags.Ack | TcpCodec.Flags.Psh, firstFlags);
        var firstPayloadLength = firstTcpPayload.Length;
        Assert.Equal(remoteMss, firstPayloadLength);

        var ack1Tcp = TcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localIp,
            sourcePort: remotePort,
            destinationPort: localPort,
            sequenceNumber: firstAck,
            acknowledgmentNumber: unchecked(firstSeq + (uint)firstPayloadLength),
            flags: TcpCodec.Flags.Ack,
            windowSize: 65535,
            options: ReadOnlySpan<byte>.Empty,
            payload: ReadOnlySpan<byte>.Empty);

        link.Incoming.Writer.TryWrite(Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, ack1Tcp, identification: 2));

        var secondData = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Ipv4Codec.TryParse(secondData.Span, out _, out _, out _, out var secondDataPayload));
        Assert.True(TcpCodec.TryParse(secondDataPayload, out _, out _, out var secondSeq, out var secondAck, out var secondFlags, out _, out var secondTcpPayload));
        Assert.Equal(TcpCodec.Flags.Ack | TcpCodec.Flags.Psh, secondFlags);
        Assert.Equal(unchecked(firstSeq + (uint)firstPayloadLength), secondSeq);
        var secondPayloadLength = secondTcpPayload.Length;
        Assert.Equal(payload.Length - remoteMss, secondPayloadLength);

        var ack2Tcp = TcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localIp,
            sourcePort: remotePort,
            destinationPort: localPort,
            sequenceNumber: secondAck,
            acknowledgmentNumber: unchecked(secondSeq + (uint)secondPayloadLength),
            flags: TcpCodec.Flags.Ack,
            windowSize: 65535,
            options: ReadOnlySpan<byte>.Empty,
            payload: ReadOnlySpan<byte>.Empty);

        link.Incoming.Writer.TryWrite(Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, ack2Tcp, identification: 3));

        await writeTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ReadAsync_ReassemblesOutOfOrderSegments()
    {
        await using var link = new InspectableIpv4Link();
        var localIp = IPAddress.Parse("10.0.0.1");
        var remoteIp = IPAddress.Parse("10.0.0.2");
        const ushort remotePort = 80;
        const ushort localPort = 50000;

        await using var client = new UserSpaceTcpClient(link, localIp, remoteIp, remotePort, localPort: localPort, mss: 1200);

        var connectTask = client.ConnectAsync();

        var syn = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Ipv4Codec.TryParse(syn.Span, out _, out _, out _, out var synPayload));
        Assert.True(TcpCodec.TryParse(synPayload, out _, out _, out var synSeq, out _, out _, out _, out _));

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

        link.Incoming.Writer.TryWrite(Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, synAckTcp, identification: 1));

        await connectTask.WaitAsync(TimeSpan.FromSeconds(2));
        _ = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); // final ACK

        var clientSeq = unchecked(synSeq + 1);
        var serverSeqStart = 1001u;

        var worldTcp = TcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localIp,
            sourcePort: remotePort,
            destinationPort: localPort,
            sequenceNumber: unchecked(serverSeqStart + 5),
            acknowledgmentNumber: clientSeq,
            flags: TcpCodec.Flags.Ack | TcpCodec.Flags.Psh,
            windowSize: 65535,
            options: ReadOnlySpan<byte>.Empty,
            payload: "world"u8);

        var helloTcp = TcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localIp,
            sourcePort: remotePort,
            destinationPort: localPort,
            sequenceNumber: serverSeqStart,
            acknowledgmentNumber: clientSeq,
            flags: TcpCodec.Flags.Ack | TcpCodec.Flags.Psh,
            windowSize: 65535,
            options: ReadOnlySpan<byte>.Empty,
            payload: "hello"u8);

        link.Incoming.Writer.TryWrite(Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, worldTcp, identification: 2));
        link.Incoming.Writer.TryWrite(Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, helloTcp, identification: 3));

        var buffer = new byte[10];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var read = await UserSpaceTcpTestHelpers.ReadExactAsync(client, buffer, buffer.Length, cts.Token);
        Assert.Equal(buffer.Length, read);
        Assert.Equal("helloworld", System.Text.Encoding.ASCII.GetString(buffer));
    }

    [Fact]
    public async Task ReadAsync_ThrowsAfterDrain_WhenRemoteReset()
    {
        await using var link = new InspectableIpv4Link();
        var localIp = IPAddress.Parse("10.0.0.1");
        var remoteIp = IPAddress.Parse("10.0.0.2");
        const ushort remotePort = 80;
        const ushort localPort = 50000;

        await using var client = new UserSpaceTcpClient(link, localIp, remoteIp, remotePort, localPort: localPort, mss: 1200);

        var connectTask = client.ConnectAsync();

        var syn = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Ipv4Codec.TryParse(syn.Span, out _, out _, out _, out var synPayload));
        Assert.True(TcpCodec.TryParse(synPayload, out _, out _, out var synSeq, out _, out _, out _, out _));

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

        link.Incoming.Writer.TryWrite(Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, synAckTcp, identification: 1));

        await connectTask.WaitAsync(TimeSpan.FromSeconds(2));
        _ = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); // final ACK

        var clientSeq = unchecked(synSeq + 1);
        var serverSeqStart = 1001u;

        var helloTcp = TcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localIp,
            sourcePort: remotePort,
            destinationPort: localPort,
            sequenceNumber: serverSeqStart,
            acknowledgmentNumber: clientSeq,
            flags: TcpCodec.Flags.Ack | TcpCodec.Flags.Psh,
            windowSize: 65535,
            options: ReadOnlySpan<byte>.Empty,
            payload: "hello"u8);

        var rstTcp = TcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localIp,
            sourcePort: remotePort,
            destinationPort: localPort,
            sequenceNumber: unchecked(serverSeqStart + 5),
            acknowledgmentNumber: clientSeq,
            flags: TcpCodec.Flags.Rst | TcpCodec.Flags.Ack,
            windowSize: 65535,
            options: ReadOnlySpan<byte>.Empty,
            payload: ReadOnlySpan<byte>.Empty);

        link.Incoming.Writer.TryWrite(Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, helloTcp, identification: 2));
        link.Incoming.Writer.TryWrite(Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, rstTcp, identification: 3));

        var buffer = new byte[5];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var read = await UserSpaceTcpTestHelpers.ReadExactAsync(client, buffer, buffer.Length, cts.Token);
        Assert.Equal(buffer.Length, read);
        Assert.Equal("hello", System.Text.Encoding.ASCII.GetString(buffer));

        var nextReadBuffer = new byte[1];
        using var nextCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var ex = await Assert.ThrowsAsync<IOException>(async () => await client.ReadAsync(nextReadBuffer, nextCts.Token));
        Assert.Contains("reset", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_DropsSegmentsWithInvalidChecksum()
    {
        await using var link = new InspectableIpv4Link();
        var localIp = IPAddress.Parse("10.0.0.1");
        var remoteIp = IPAddress.Parse("10.0.0.2");
        const ushort remotePort = 80;
        const ushort localPort = 50000;

        await using var client = new UserSpaceTcpClient(link, localIp, remoteIp, remotePort, localPort: localPort, mss: 1200);

        var connectTask = client.ConnectAsync();

        var syn = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Ipv4Codec.TryParse(syn.Span, out _, out _, out _, out var synPayload));
        Assert.True(TcpCodec.TryParse(synPayload, out _, out _, out var synSeq, out _, out _, out _, out _));

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

        link.Incoming.Writer.TryWrite(Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, synAckTcp, identification: 1));

        await connectTask.WaitAsync(TimeSpan.FromSeconds(2));
        _ = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); // final ACK

        var clientSeq = unchecked(synSeq + 1);
        var serverSeqStart = 1001u;

        var helloTcp = TcpCodec.Encode(
            sourceIp: remoteIp,
            destinationIp: localIp,
            sourcePort: remotePort,
            destinationPort: localPort,
            sequenceNumber: serverSeqStart,
            acknowledgmentNumber: clientSeq,
            flags: TcpCodec.Flags.Ack | TcpCodec.Flags.Psh,
            windowSize: 65535,
            options: ReadOnlySpan<byte>.Empty,
            payload: "hello"u8);

        var corruptedHelloTcp = (byte[])helloTcp.Clone();
        corruptedHelloTcp[corruptedHelloTcp.Length - 1] ^= 0x01;

        link.Incoming.Writer.TryWrite(Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, corruptedHelloTcp, identification: 2));
        link.Incoming.Writer.TryWrite(Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, helloTcp, identification: 3));

        var buffer = new byte[5];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var read = await UserSpaceTcpTestHelpers.ReadExactAsync(client, buffer, buffer.Length, cts.Token);
        Assert.Equal(buffer.Length, read);
        Assert.Equal("hello", System.Text.Encoding.ASCII.GetString(buffer));
    }

    [Fact]
    public async Task ReadAsync_SendsWindowUpdate_WhenReceiveWindowOpens()
    {
        await using var link = new InspectableIpv4Link();
        var localIp = IPAddress.Parse("10.0.0.1");
        var remoteIp = IPAddress.Parse("10.0.0.2");
        const ushort remotePort = 80;
        const ushort localPort = 50000;

        await using var client = new UserSpaceTcpClient(link, localIp, remoteIp, remotePort, localPort: localPort, mss: 1200);

        var connectTask = client.ConnectAsync();

        var syn = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Ipv4Codec.TryParse(syn.Span, out _, out _, out _, out var synPayload));
        Assert.True(TcpCodec.TryParse(synPayload, out _, out _, out var synSeq, out _, out _, out _, out _));

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

        link.Incoming.Writer.TryWrite(Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, synAckTcp, identification: 1));

        await connectTask.WaitAsync(TimeSpan.FromSeconds(2));
        _ = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); // final ACK

        var payload = new byte[4096];
        Array.Fill(payload, (byte)'a');

        var clientSeq = unchecked(synSeq + 1);
        var serverSeqStart = 1001u;

        ushort lastWindow = 65535;
        for (var i = 0; i < 64; i++)
        {
            var dataTcp = TcpCodec.Encode(
                sourceIp: remoteIp,
                destinationIp: localIp,
                sourcePort: remotePort,
                destinationPort: localPort,
                sequenceNumber: unchecked(serverSeqStart + (uint)(i * payload.Length)),
                acknowledgmentNumber: clientSeq,
                flags: TcpCodec.Flags.Ack | TcpCodec.Flags.Psh,
                windowSize: 65535,
                options: ReadOnlySpan<byte>.Empty,
                payload: payload);

            link.Incoming.Writer.TryWrite(Ipv4Codec.Encode(remoteIp, localIp, TcpCodec.ProtocolNumber, dataTcp, identification: (ushort)(2 + i)));

            var ackPacket = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(Ipv4Codec.TryParse(ackPacket.Span, out _, out _, out _, out var ackPayload));
            Assert.True(TcpCodec.TryParse(ackPayload, out _, out _, out _, out _, out var ackFlags, out lastWindow, out var ackTcpPayload));
            Assert.Equal(TcpCodec.Flags.Ack, ackFlags);
            Assert.True(ackTcpPayload.IsEmpty);
        }

        Assert.Equal((ushort)0, lastWindow);

        var readBuffer = new byte[4096];
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var read = await client.ReadAsync(readBuffer, readCts.Token);
        Assert.Equal(readBuffer.Length, read);

        var update = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Ipv4Codec.TryParse(update.Span, out _, out _, out _, out var updatePayload));
        Assert.True(TcpCodec.TryParse(updatePayload, out _, out _, out _, out _, out var updateFlags, out var updateWindow, out var updateTcpPayload));
        Assert.Equal(TcpCodec.Flags.Ack, updateFlags);
        Assert.True(updateTcpPayload.IsEmpty);
        Assert.NotEqual((ushort)0, updateWindow);
    }
}
