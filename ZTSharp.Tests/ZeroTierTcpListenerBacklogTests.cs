using System.Net;
using System.Reflection;
using System.Threading.Channels;
using ZTSharp.ZeroTier;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierTcpListenerBacklogTests
{
    [Fact]
    public async Task ConnectionFlood_DoesNotGrowUnbounded_WhenNotAccepting()
    {
        var localIp = IPAddress.Parse("10.0.0.2");
        await using var runtime = CreateRuntime(localManagedIpV4: localIp);
        await using var listener = new ZeroTierTcpListener(runtime, localIp, localPort: 23460, acceptQueueCapacity: 8);

        var capacity = listener.AcceptQueueCapacity;
        var attempts = capacity + 1;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var clients = new List<UserSpaceTcpClient>(attempts);

        for (var i = 0; i < attempts; i++)
        {
            var (clientLink, serverLink) = InMemoryIpv4Link.CreatePair();

            var serverIp = IPAddress.Parse("10.0.0.1");
            const ushort serverPort = 8080;
            var clientIp = IPAddress.Parse("10.0.0.2");
            var clientPort = (ushort)(50000 + i);

            var server = new UserSpaceTcpServerConnection(serverLink, serverIp, serverPort, clientIp, clientPort);
            var client = new UserSpaceTcpClient(clientLink, clientIp, serverIp, remotePort: serverPort, localPort: clientPort);
            clients.Add(client);

            var handleTask = InvokeHandleAcceptedConnectionAsync(listener, server, CancellationToken.None);
            var connectTask = client.ConnectAsync(cts.Token);

            await Task.WhenAll(handleTask, connectTask);
        }

        Assert.Equal(capacity, listener.PendingAcceptCount);
        Assert.Equal(attempts - capacity, listener.DroppedAcceptCount);

        for (var i = 0; i < capacity; i++)
        {
            var accepted = await listener.AcceptAsync(cts.Token);
            await Task.WhenAll(
                clients[i].DisposeAsync().AsTask(),
                accepted.DisposeAsync().AsTask());
        }

        Assert.Equal(0, listener.PendingAcceptCount);

        for (var i = capacity; i < clients.Count; i++)
        {
            await clients[i].DisposeAsync();
        }
    }

    private static Task InvokeHandleAcceptedConnectionAsync(ZeroTierTcpListener listener, UserSpaceTcpServerConnection connection, CancellationToken cancellationToken)
    {
        var method = typeof(ZeroTierTcpListener).GetMethod("HandleAcceptedConnectionAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (Task)method!.Invoke(listener, new object[] { connection, cancellationToken })!;
    }

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "UDP transport ownership transfers to ZeroTierDataplaneRuntime, which is disposed by the caller.")]
    private static ZeroTierDataplaneRuntime CreateRuntime(IPAddress localManagedIpV4)
        => new(
            udp: new ZeroTierUdpTransport(localPort: 0, enableIpv6: false),
            rootNodeId: new NodeId(0x1111111111),
            rootEndpoint: new IPEndPoint(IPAddress.Loopback, 9999),
            rootKey: new byte[48],
            rootProtocolVersion: 12,
            localIdentity: ZeroTierTestIdentities.CreateFastIdentity(0x2222222222),
            networkId: 1,
            localManagedIpsV4: new[] { localManagedIpV4 },
            localManagedIpsV6: Array.Empty<IPAddress>(),
            inlineCom: new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 });

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
