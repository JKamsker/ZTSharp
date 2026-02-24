using System.Net;
using System.Threading.Channels;
using JKamsker.LibZt.ZeroTier.Net;

namespace JKamsker.LibZt.Tests;

public sealed class ZtUserSpaceTcpStressTests
{
    [Fact]
    public async Task Stress_ManyConcurrentConnections_CanHandshake_And_ExchangeData()
    {
        const int connections = 32;
        const int payloadBytes = 64 * 1024;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var tasks = Enumerable.Range(0, connections)
            .Select(async i =>
            {
                var (clientLink, serverLink) = InMemoryIpv4Link.CreatePair();

                var serverIp = IPAddress.Parse("10.0.0.1");
                var clientIp = IPAddress.Parse("10.0.0.2");
                const ushort serverPort = 8080;
                var clientPort = (ushort)(50000 + i);

                await using var server = new ZtUserSpaceTcpServerConnection(serverLink, serverIp, serverPort, clientIp, clientPort);
                await using var client = new ZtUserSpaceTcpClient(clientLink, clientIp, serverIp, remotePort: serverPort, localPort: clientPort);

                await Task.WhenAll(
                        server.AcceptAsync(cts.Token),
                        client.ConnectAsync(cts.Token))
                    ;

                var payload = new byte[payloadBytes];
                FillPattern(payload);

                await client.WriteAsync(payload, cts.Token);

                var received = new byte[payloadBytes];
                var read = await ReadExactAsync(server, received, payloadBytes, cts.Token);
                Assert.Equal(payloadBytes, read);
                Assert.Equal(payload, received);

                var reply = new byte[2048];
                FillPattern(reply);
                await server.WriteAsync(reply, cts.Token);

                var replyReceived = new byte[reply.Length];
                var replyRead = await ReadExactAsync(client, replyReceived, replyReceived.Length, cts.Token);
                Assert.Equal(reply.Length, replyRead);
                Assert.Equal(reply, replyReceived);
            })
            .ToArray();

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Stress_LargePayload_CanTransfer_BothDirections()
    {
        var (clientLink, serverLink) = InMemoryIpv4Link.CreatePair();

        var serverIp = IPAddress.Parse("10.0.0.1");
        var clientIp = IPAddress.Parse("10.0.0.2");
        const ushort serverPort = 8080;
        const ushort clientPort = 50000;

        await using var server = new ZtUserSpaceTcpServerConnection(serverLink, serverIp, serverPort, clientIp, clientPort);
        await using var client = new ZtUserSpaceTcpClient(clientLink, clientIp, serverIp, remotePort: serverPort, localPort: clientPort);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await Task.WhenAll(
                server.AcceptAsync(cts.Token),
                client.ConnectAsync(cts.Token))
            ;

        var payload = new byte[1024 * 1024];
        FillPattern(payload);

        var received = new byte[payload.Length];
        var readTask = ReadExactAsync(server, received, received.Length, cts.Token);
        var writeTask = client.WriteAsync(payload, cts.Token).AsTask();

        var received2 = new byte[payload.Length];
        var readTask2 = ReadExactAsync(client, received2, received2.Length, cts.Token);
        var writeTask2 = server.WriteAsync(payload, cts.Token).AsTask();

        await Task.WhenAll(writeTask, readTask);
        Assert.Equal(payload, received);

        await Task.WhenAll(writeTask2, readTask2);
        Assert.Equal(payload, received2);
    }

    [Fact]
    public async Task Stress_SlowReader_BlocksWriterUntilWindowUpdate()
    {
        var (clientLink, serverLink) = InMemoryIpv4Link.CreatePair();

        var serverIp = IPAddress.Parse("10.0.0.1");
        var clientIp = IPAddress.Parse("10.0.0.2");
        const ushort serverPort = 8080;
        const ushort clientPort = 50000;

        await using var server = new ZtUserSpaceTcpServerConnection(serverLink, serverIp, serverPort, clientIp, clientPort);
        await using var client = new ZtUserSpaceTcpClient(clientLink, clientIp, serverIp, remotePort: serverPort, localPort: clientPort);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Task.WhenAll(
                server.AcceptAsync(cts.Token),
                client.ConnectAsync(cts.Token))
            ;

        var payload = new byte[512 * 1024];
        FillPattern(payload);

        var writeTask = client.WriteAsync(payload, cts.Token).AsTask();

        await Task.Delay(200, cts.Token);
        Assert.False(writeTask.IsCompleted, "Writer should block when the remote receive window reaches 0.");

        var received = new byte[payload.Length];
        var readTask = ReadExactAsync(server, received, received.Length, cts.Token);

        await Task.WhenAll(writeTask, readTask);
        Assert.Equal(payload, received);
    }

    private static void FillPattern(Span<byte> buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)i;
        }
    }

    private static async Task<int> ReadExactAsync(ZtUserSpaceTcpClient client, byte[] buffer, int length, CancellationToken cancellationToken)
    {
        var readTotal = 0;
        while (readTotal < length)
        {
            var read = await client.ReadAsync(buffer.AsMemory(readTotal, length - readTotal), cancellationToken);
            if (read == 0)
            {
                return readTotal;
            }

            readTotal += read;
        }

        return readTotal;
    }

    private static async Task<int> ReadExactAsync(ZtUserSpaceTcpServerConnection server, byte[] buffer, int length, CancellationToken cancellationToken)
    {
        var readTotal = 0;
        while (readTotal < length)
        {
            var read = await server.ReadAsync(buffer.AsMemory(readTotal, length - readTotal), cancellationToken);
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
