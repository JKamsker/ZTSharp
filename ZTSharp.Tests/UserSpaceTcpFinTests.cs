using System.Net;
using System.Threading.Channels;
using ZTSharp.ZeroTier.Net;

namespace ZTSharp.Tests;

public sealed class UserSpaceTcpFinTests
{
    [Fact]
    public async Task DisposeAsync_Client_CausesServerStreamEof()
    {
        var (clientLink, serverLink) = InMemoryIpv4Link.CreatePair();

        var serverIp = IPAddress.Parse("10.0.0.1");
        var clientIp = IPAddress.Parse("10.0.0.2");
        const ushort serverPort = 8080;
        const ushort clientPort = 50000;

        await using var server = new UserSpaceTcpServerConnection(serverLink, serverIp, serverPort, clientIp, clientPort);
        await using var client = new UserSpaceTcpClient(clientLink, clientIp, serverIp, remotePort: serverPort, localPort: clientPort);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(server.AcceptAsync(cts.Token), client.ConnectAsync(cts.Token));

        await client.DisposeAsync();

        var buffer = new byte[1];
        var read = await server.ReadAsync(buffer, cts.Token);
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task DisposeAsync_Server_CausesClientStreamEof()
    {
        var (clientLink, serverLink) = InMemoryIpv4Link.CreatePair();

        var serverIp = IPAddress.Parse("10.0.0.1");
        var clientIp = IPAddress.Parse("10.0.0.2");
        const ushort serverPort = 8080;
        const ushort clientPort = 50000;

        await using var server = new UserSpaceTcpServerConnection(serverLink, serverIp, serverPort, clientIp, clientPort);
        await using var client = new UserSpaceTcpClient(clientLink, clientIp, serverIp, remotePort: serverPort, localPort: clientPort);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(server.AcceptAsync(cts.Token), client.ConnectAsync(cts.Token));

        await server.DisposeAsync();

        var buffer = new byte[1];
        var read = await client.ReadAsync(buffer, cts.Token);
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task SendFinWithRetriesAsync_IsIdempotentWithRespectToSequenceNumber()
    {
        await using var link = new InspectableIpv4Link();
        var localIp = IPAddress.Parse("10.0.0.1");
        var remoteIp = IPAddress.Parse("10.0.0.2");
        const ushort localPort = 50000;
        const ushort remotePort = 80;

        var receiver = new UserSpaceTcpReceiver();
        receiver.Initialize(initialRecvNext: 1234);

        await using var sender = new UserSpaceTcpSender(link, localIp, remoteIp, localPort, remotePort, mss: 1200, receiver);
        sender.InitializeSendState(iss: 1000);

        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendFinWithRetriesAsync(receiver.RecvNext, cts.Token));
        }

        var fin1 = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Ipv4Codec.TryParse(fin1.Span, out _, out _, out _, out var fin1Payload));
        Assert.True(TcpCodec.TryParse(fin1Payload, out _, out _, out var fin1Seq, out _, out var fin1Flags, out _, out _));
        Assert.True((fin1Flags & TcpCodec.Flags.Fin) != 0);

        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendFinWithRetriesAsync(receiver.RecvNext, cts.Token));
        }

        var fin2 = await link.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Ipv4Codec.TryParse(fin2.Span, out _, out _, out _, out var fin2Payload));
        Assert.True(TcpCodec.TryParse(fin2Payload, out _, out _, out var fin2Seq, out _, out var fin2Flags, out _, out _));
        Assert.True((fin2Flags & TcpCodec.Flags.Fin) != 0);

        Assert.Equal(fin1Seq, fin2Seq);
    }

    private sealed class InMemoryIpv4Link : IUserSpaceIpLink
    {
        private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private InMemoryIpv4Link? _peer;

        public static (InMemoryIpv4Link A, InMemoryIpv4Link B) CreatePair()
        {
            var a = new InMemoryIpv4Link();
            var b = new InMemoryIpv4Link();
            a._peer = b;
            b._peer = a;
            return (a, b);
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> ipPacket, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_peer is null, this);

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

