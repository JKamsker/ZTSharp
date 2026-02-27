using System.Net;
using System.Threading.Channels;
using ZTSharp.ZeroTier.Net;

namespace ZTSharp.Tests;

public sealed class UserSpaceTcpHandshakeTests
{
    [Fact]
    public async Task LostFinalAck_ServerAcceptCompletesAfterSynAckRetransmit()
    {
        var droppedFinalAck = 0;

        var (clientLink, serverLink) = FilteringIpv4Link.CreatePair(
            shouldDropClientToServer: packet =>
            {
                if (Volatile.Read(ref droppedFinalAck) != 0)
                {
                    return false;
                }

                if (!TryParseTcp(packet.Span, out var flags, out var payloadLength))
                {
                    return false;
                }

                if (flags == TcpCodec.Flags.Ack && payloadLength == 0)
                {
                    Interlocked.Exchange(ref droppedFinalAck, 1);
                    return true;
                }

                return false;
            });

        var serverIp = IPAddress.Parse("10.0.0.1");
        var clientIp = IPAddress.Parse("10.0.0.2");
        const ushort serverPort = 8080;
        const ushort clientPort = 50000;

        await using var server = new UserSpaceTcpServerConnection(serverLink, serverIp, serverPort, clientIp, clientPort);
        await using var client = new UserSpaceTcpClient(clientLink, clientIp, serverIp, remotePort: serverPort, localPort: clientPort);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(server.AcceptAsync(cts.Token), client.ConnectAsync(cts.Token));

        Assert.Equal(1, Volatile.Read(ref droppedFinalAck));
    }

    private static bool TryParseTcp(ReadOnlySpan<byte> ipv4Packet, out TcpCodec.Flags flags, out int payloadLength)
    {
        flags = 0;
        payloadLength = 0;

        if (!Ipv4Codec.TryParse(ipv4Packet, out _, out _, out var protocol, out var ipPayload))
        {
            return false;
        }

        if (protocol != TcpCodec.ProtocolNumber)
        {
            return false;
        }

        if (!TcpCodec.TryParse(ipPayload, out _, out _, out _, out _, out flags, out _, out var tcpPayload))
        {
            return false;
        }

        payloadLength = tcpPayload.Length;
        return true;
    }

    private sealed class FilteringIpv4Link : IUserSpaceIpLink
    {
        private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly Func<ReadOnlyMemory<byte>, bool>? _shouldDropOutgoing;
        private FilteringIpv4Link? _peer;

        private FilteringIpv4Link(Func<ReadOnlyMemory<byte>, bool>? shouldDropOutgoing)
        {
            _shouldDropOutgoing = shouldDropOutgoing;
        }

        public static (FilteringIpv4Link Client, FilteringIpv4Link Server) CreatePair(
            Func<ReadOnlyMemory<byte>, bool>? shouldDropClientToServer = null,
            Func<ReadOnlyMemory<byte>, bool>? shouldDropServerToClient = null)
        {
            var client = new FilteringIpv4Link(shouldDropClientToServer);
            var server = new FilteringIpv4Link(shouldDropServerToClient);
            client._peer = server;
            server._peer = client;
            return (client, server);
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> ipPacket, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_peer is null, this);

            if (_shouldDropOutgoing?.Invoke(ipPacket) == true)
            {
                return ValueTask.CompletedTask;
            }

            _peer!._incoming.Writer.TryWrite(ipPacket.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
            => _incoming.Reader.ReadAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            _incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}

