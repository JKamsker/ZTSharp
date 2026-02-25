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
        var read = await UserSpaceTcpClientTestHelpers.ReadExactAsync(client, buffer, buffer.Length, cts.Token);
        Assert.Equal(buffer.Length, read);
        Assert.Equal("helloworld", System.Text.Encoding.ASCII.GetString(buffer));
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

