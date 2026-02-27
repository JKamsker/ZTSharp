using System.Collections.Concurrent;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using ZTSharp.Transport.Internal;

namespace ZTSharp.Transport;

/// <summary>
/// OS UDP transport adapter for external endpoint integration.
/// </summary>
internal sealed class OsUdpNodeTransport : INodeTransport, IAsyncDisposable
{
    private static readonly TimeSpan PeerDiscoveryRefreshInterval = TimeSpan.FromSeconds(90);

    private sealed record Subscriber(
        ulong NodeId,
        Func<ulong, ulong, ReadOnlyMemory<byte>, CancellationToken, Task> OnFrameReceived);

    private readonly UdpClient _udp;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<Guid, Subscriber>> _networkSubscribers = new();
    private readonly ConcurrentDictionary<ulong, IPEndPoint> _advertisedEndpoints = new();
    private readonly OsUdpPeerRegistry _peers;
    private readonly OsUdpReceiveLoop _receiver;
    private readonly CancellationTokenSource _receiverCts = new();
    private readonly Task _receiverLoop;
    private readonly bool _enablePeerDiscovery;
    private readonly CancellationTokenSource _peerRefreshCts = new();
    private readonly Task? _peerRefreshLoop;

    public OsUdpNodeTransport(int localPort = 0, bool enableIpv6 = true, bool enablePeerDiscovery = true)
    {
        _enablePeerDiscovery = enablePeerDiscovery;
        _udp = OsUdpSocketFactory.Create(localPort, enableIpv6);
        _peers = new OsUdpPeerRegistry(enablePeerDiscovery, UdpEndpointNormalization.Normalize);

        _receiver = new OsUdpReceiveLoop(
            _udp,
            enablePeerDiscovery,
            _peers,
            DispatchFrameAsync,
            SendDiscoveryFrameAsync);

        _receiverLoop = Task.Run(() => _receiver.RunAsync(_receiverCts.Token));

        if (enablePeerDiscovery)
        {
            _peerRefreshLoop = Task.Run(() => RefreshLocalDiscoveryEntriesAsync(_peerRefreshCts.Token));
        }
    }

    public IPEndPoint LocalEndpoint
    {
        get
        {
            return UdpEndpointNormalization.Normalize((IPEndPoint)_udp.Client.LocalEndPoint!);
        }
    }

    public async Task<Guid> JoinNetworkAsync(
        ulong networkId,
        ulong nodeId,
        Func<ulong, ulong, ReadOnlyMemory<byte>, CancellationToken, Task> onFrameReceived,
        IPEndPoint? localEndpoint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(nodeId);
        ArgumentNullException.ThrowIfNull(onFrameReceived);
        cancellationToken.ThrowIfCancellationRequested();

        var registrationId = Guid.NewGuid();
        var advertisedEndpoint = localEndpoint is null
            ? UdpEndpointNormalization.NormalizeForAdvertisement(LocalEndpoint)
            : UdpEndpointNormalization.NormalizeForAdvertisement(localEndpoint);
        _advertisedEndpoints[networkId] = advertisedEndpoint;
        var subscribers = _networkSubscribers.GetOrAdd(
            networkId,
            _ => new ConcurrentDictionary<Guid, Subscriber>());
        subscribers[registrationId] = new Subscriber(nodeId, onFrameReceived);

        foreach (var peer in _peers.RegisterLocalAndGetKnownPeers(networkId, nodeId, advertisedEndpoint))
        {
            await SendDiscoveryFrameAsync(
                networkId,
                nodeId,
                peer.Value,
                OsUdpPeerDiscoveryProtocol.FrameType.PeerHello,
                cancellationToken).ConfigureAwait(false);
        }

        return registrationId;
    }

    public async Task LeaveNetworkAsync(ulong networkId, Guid registrationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_networkSubscribers.TryGetValue(networkId, out var subscribers) &&
            subscribers.TryGetValue(registrationId, out var localSubscriber))
        {
            _ = _peers.TryRemoveLocalNodeIdIfMatch(networkId, localSubscriber.NodeId);
        }

        _peers.RemoveNetworkPeers(networkId);
        _advertisedEndpoints.TryRemove(networkId, out _);
        if (!_networkSubscribers.TryGetValue(networkId, out var networkSubscribers))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            networkSubscribers.TryRemove(registrationId, out _);
            if (networkSubscribers.IsEmpty)
            {
                _networkSubscribers.TryRemove(networkId, out _);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SendFrameAsync(
        ulong networkId,
        ulong sourceNodeId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_peers.TryGetPeers(networkId, out var peers))
        {
            return;
        }

        var frameBuffer = ArrayPool<byte>.Shared.Rent(NodeFrameCodec.GetEncodedLength(payload.Length));
        try
        {
            if (!NodeFrameCodec.TryEncode(
                    networkId,
                    sourceNodeId,
                    payload.Span,
                    frameBuffer,
                    out var frameLength))
            {
                throw new InvalidOperationException("Encoded frame did not fit destination buffer.");
            }

            var frame = frameBuffer.AsMemory(0, frameLength);
            foreach (var peer in peers)
            {
                if (peer.Key == sourceNodeId)
                {
                    continue;
                }

                try
                {
                    await _udp
                        .SendAsync(frame, peer.Value.Endpoint, cancellationToken)
                        .ConfigureAwait(false);
                    _peers.RefreshPeerLastSeen(networkId, peer.Key);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException or ArgumentException)
                {
                    continue;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frameBuffer);
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async ValueTask AddPeerAsync(ulong networkId, ulong nodeId, IPEndPoint endpoint)
    {
        ArgumentOutOfRangeException.ThrowIfZero(nodeId);
        ArgumentNullException.ThrowIfNull(endpoint);

        UdpEndpointNormalization.ValidateRemoteEndpoint(endpoint, nameof(endpoint));
        var remoteEndpoint = UdpEndpointNormalization.Normalize(endpoint);
        _peers.AddOrUpdatePeer(networkId, nodeId, remoteEndpoint);

        if (!_enablePeerDiscovery)
        {
            return;
        }

        if (!_peers.TryGetLocalNodeId(networkId, out var localNodeId) || localNodeId == 0 || localNodeId == nodeId)
        {
            return;
        }

        try
        {
            await SendDiscoveryFrameAsync(
                    networkId,
                    localNodeId,
                    remoteEndpoint,
                    OsUdpPeerDiscoveryProtocol.FrameType.PeerHello,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or SocketException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _receiverCts.CancelAsync().ConfigureAwait(false);
        await _peerRefreshCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _receiverLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_receiverCts.IsCancellationRequested)
        {
        }

        if (_peerRefreshLoop is not null)
        {
            try
            {
                await _peerRefreshLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_peerRefreshCts.IsCancellationRequested)
            {
            }
        }

        _udp.Dispose();
        _peers.Cleanup();
        _receiverCts.Dispose();
        _peerRefreshCts.Dispose();
        _gate.Dispose();
    }

    private async Task RefreshLocalDiscoveryEntriesAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PeerDiscoveryRefreshInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var entry in _advertisedEndpoints)
            {
                if (_peers.TryGetLocalNodeId(entry.Key, out var localNodeId) && localNodeId != 0)
                {
                    _peers.RefreshLocalRegistration(entry.Key, localNodeId, entry.Value);
                }
            }
        }
    }

    private async Task DispatchFrameAsync(ulong sourceNodeId, ulong networkId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (!_networkSubscribers.TryGetValue(networkId, out var subscribers))
        {
            return;
        }

        foreach (var callback in subscribers.Values)
        {
            try
            {
                await callback
                    .OnFrameReceived(sourceNodeId, networkId, payload, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // Subscriber errors must not kill the receive loop.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

    private async Task SendDiscoveryFrameAsync(
        ulong networkId,
        ulong nodeId,
        IPEndPoint endpoint,
        OsUdpPeerDiscoveryProtocol.FrameType frameType,
        CancellationToken cancellationToken)
    {
        Span<byte> payload = stackalloc byte[OsUdpPeerDiscoveryProtocol.PayloadLength];
        OsUdpPeerDiscoveryProtocol.WritePayload(frameType, nodeId, networkId, payload);

        var frame = ArrayPool<byte>.Shared.Rent(NodeFrameCodec.GetEncodedLength(OsUdpPeerDiscoveryProtocol.PayloadLength));
        try
        {
            if (!NodeFrameCodec.TryEncode(networkId, nodeId, payload, frame, out var frameLength))
            {
                throw new InvalidOperationException("Encoded control frame did not fit destination buffer.");
            }

            await _udp
                .SendAsync(frame.AsMemory(0, frameLength), endpoint, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    internal void SetDatagramObserverForTests(Action<IPEndPoint>? observer)
        => _receiver.DatagramReceivedForTests = observer;

}
