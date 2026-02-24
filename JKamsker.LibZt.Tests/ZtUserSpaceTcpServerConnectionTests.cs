using System.Net;
using System.Text;
using System.Threading.Channels;
using JKamsker.LibZt.ZeroTier.Net;

namespace JKamsker.LibZt.Tests;

public sealed class ZtUserSpaceTcpServerConnectionTests
{
    [Fact]
    public async Task AcceptAsync_And_ConnectAsync_CanExchangeData()
    {
        var (clientLink, serverLink) = InMemoryIpv4Link.CreatePair();

        var serverIp = IPAddress.Parse("10.0.0.1");
        var clientIp = IPAddress.Parse("10.0.0.2");
        const ushort serverPort = 8080;
        const ushort clientPort = 50000;

        await using var server = new ZtUserSpaceTcpServerConnection(serverLink, serverIp, serverPort, clientIp, clientPort);
        await using var client = new ZtUserSpaceTcpClient(clientLink, clientIp, serverIp, remotePort: serverPort, localPort: clientPort);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await Task.WhenAll(
                server.AcceptAsync(cts.Token),
                client.ConnectAsync(cts.Token));

        await client.WriteAsync(Encoding.ASCII.GetBytes("hello"), cts.Token);

        var buffer = new byte[5];
        var read = await server.ReadAsync(buffer, cts.Token);
        Assert.Equal(5, read);
        Assert.Equal("hello", Encoding.ASCII.GetString(buffer));

        await server.WriteAsync(Encoding.ASCII.GetBytes("world"), cts.Token);

        var buffer2 = new byte[5];
        var read2 = await client.ReadAsync(buffer2, cts.Token);
        Assert.Equal(5, read2);
        Assert.Equal("world", Encoding.ASCII.GetString(buffer2));
    }

    private sealed class InMemoryIpv4Link : IZtUserSpaceIpv4Link
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

        public ValueTask SendAsync(ReadOnlyMemory<byte> ipv4Packet, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_peer is null, this);

            _peer!._incoming.Writer.TryWrite(ipv4Packet.ToArray());
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
