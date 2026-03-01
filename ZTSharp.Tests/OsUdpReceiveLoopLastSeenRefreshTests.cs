using System.Net;
using System.Net.Sockets;
using ZTSharp.Transport;
using ZTSharp.Transport.Internal;

namespace ZTSharp.Tests;

public sealed class OsUdpReceiveLoopLastSeenRefreshTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    [Fact]
    public async Task ReceiveLoop_RefreshesPeerLastSeen_EvenWhenDispatchThrows()
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);

        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var peers = new OsUdpPeerRegistry(enablePeerDiscovery: false, UdpEndpointNormalization.Normalize, timeProvider: time);
        var networkId = 0xABCDEF10UL;
        var sourceNodeId = 0x1234UL;
        var remoteEndpoint = new IPEndPoint(IPAddress.Loopback, 40100);
        peers.AddOrUpdatePeer(networkId, sourceNodeId, remoteEndpoint);

        time.Advance(TimeSpan.FromMinutes(4));
        var appPayload = new byte[] { 0x42 };
        var frame = NodeFrameCodec.Encode(networkId, sourceNodeId, appPayload);
        var result = new UdpReceiveResult(frame.ToArray(), remoteEndpoint);

        var dispatched = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task DispatchAsync(ulong _, ulong __, ReadOnlyMemory<byte> ___, CancellationToken ____)
        {
            dispatched.TrySetResult(true);
            throw new InvalidOperationException("synthetic dispatch fault");
        }

        static Task SendDiscoveryAsync(ulong _, ulong __, IPEndPoint ___, OsUdpPeerDiscoveryProtocol.FrameType ____, CancellationToken _____)
            => Task.CompletedTask;

        var callCount = 0;
        async ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref callCount) == 1)
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
        _ = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(peers.TryGetPeers(networkId, out var networkPeers));
        Assert.True(networkPeers.ContainsKey(sourceNodeId));

        cts.Cancel();
        await run;
    }
}
