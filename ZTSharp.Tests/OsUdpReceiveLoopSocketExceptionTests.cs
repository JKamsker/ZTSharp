using System.Net;
using System.Net.Sockets;
using ZTSharp.Transport;
using ZTSharp.Transport.Internal;

namespace ZTSharp.Tests;

public sealed class OsUdpReceiveLoopSocketExceptionTests
{
    [Fact]
    public async Task OsUdpReceiveLoop_Continues_WhenReceiveThrowsNonConnectionResetSocketException()
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);

        var networkId = 0xABCDEF01UL;
        var sourceNodeId = 0x1234UL;
        var remoteEndpoint = new IPEndPoint(IPAddress.Loopback, 40001);
        var peers = new OsUdpPeerRegistry(enablePeerDiscovery: false, UdpEndpointNormalization.Normalize);
        peers.AddOrUpdatePeer(networkId, sourceNodeId, remoteEndpoint);

        var dispatched = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task DispatchAsync(ulong source, ulong network, ReadOnlyMemory<byte> payload, CancellationToken _)
        {
            dispatched.TrySetResult(payload);
            return Task.CompletedTask;
        }

        static Task SendDiscoveryAsync(ulong _, ulong __, IPEndPoint ___, OsUdpPeerDiscoveryProtocol.FrameType ____, CancellationToken _____)
            => Task.CompletedTask;

        var appPayload = new byte[] { 0x42 };
        var frame = NodeFrameCodec.Encode(networkId, sourceNodeId, appPayload);
        var result = new UdpReceiveResult(frame.ToArray(), remoteEndpoint);

        var callCount = 0;
        async ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken ct)
        {
            var call = Interlocked.Increment(ref callCount);
            if (call == 1)
            {
                throw new SocketException((int)SocketError.NetworkUnreachable);
            }

            if (call == 2)
            {
                return result;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new OperationCanceledException(ct);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var loop = new OsUdpReceiveLoop(
            udp,
            enablePeerDiscovery: false,
            peers,
            DispatchAsync,
            SendDiscoveryAsync,
            receiveAsync: ReceiveAsync);

        var run = loop.RunAsync(cts.Token);

        var payload = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(payload.Span.SequenceEqual(appPayload));

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task OsUdpReceiveLoop_DoesNotBlockOnDiscoveryReplySends()
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);

        var networkId = 0xBEEFCAFEUL;
        var localNodeId = 0x1111UL;
        var remoteNodeId = 0x2222UL;
        var remoteEndpoint = new IPEndPoint(IPAddress.Loopback, 40002);

        var peers = new OsUdpPeerRegistry(enablePeerDiscovery: true, UdpEndpointNormalization.Normalize);
        peers.SetLocalNodeId(networkId, localNodeId);
        peers.AddOrUpdatePeer(networkId, remoteNodeId, remoteEndpoint);

        var sendStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task SendDiscoveryAsync(ulong sendNetworkId, ulong sendNodeId, IPEndPoint endpoint, OsUdpPeerDiscoveryProtocol.FrameType frameType, CancellationToken cancellationToken)
        {
            Assert.Equal(networkId, sendNetworkId);
            Assert.Equal(localNodeId, sendNodeId);
            Assert.Equal(remoteEndpoint, endpoint);
            Assert.Equal(OsUdpPeerDiscoveryProtocol.FrameType.PeerHelloResponse, frameType);

            sendStarted.TrySetResult(true);
            _ = cancellationToken;
            await allowSend.Task;
        }

        var appPayload = new byte[] { 0x99 };
        var dispatched = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task DispatchAsync(ulong source, ulong network, ReadOnlyMemory<byte> payload, CancellationToken _)
        {
            if (source == remoteNodeId && network == networkId && payload.Span.SequenceEqual(appPayload))
            {
                dispatched.TrySetResult(true);
            }

            return Task.CompletedTask;
        }

        Span<byte> discoveryPayload = stackalloc byte[OsUdpPeerDiscoveryProtocol.PayloadLength];
        OsUdpPeerDiscoveryProtocol.WritePayload(
            OsUdpPeerDiscoveryProtocol.FrameType.PeerHello,
            remoteNodeId,
            networkId,
            discoveryPayload);

        var controlFrame = NodeFrameCodec.Encode(networkId, remoteNodeId, discoveryPayload);
        var appFrame = NodeFrameCodec.Encode(networkId, remoteNodeId, appPayload);

        var receiveQueue = new Queue<UdpReceiveResult>(capacity: 2);
        receiveQueue.Enqueue(new UdpReceiveResult(controlFrame.ToArray(), remoteEndpoint));
        receiveQueue.Enqueue(new UdpReceiveResult(appFrame.ToArray(), remoteEndpoint));

        async ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken ct)
        {
            if (receiveQueue.Count > 0)
            {
                return receiveQueue.Dequeue();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new OperationCanceledException(ct);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var loop = new OsUdpReceiveLoop(
            udp,
            enablePeerDiscovery: true,
            peers,
            DispatchAsync,
            SendDiscoveryAsync,
            receiveAsync: ReceiveAsync);

        var run = loop.RunAsync(cts.Token);

        _ = await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        _ = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));

        allowSend.TrySetResult(true);
        cts.Cancel();
        await run;
    }
}
