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
    private sealed record Subscriber(
        ulong NodeId,
        Func<ulong, ulong, ReadOnlyMemory<byte>, CancellationToken, Task> OnFrameReceived);

    private readonly UdpClient _udp;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<Guid, Subscriber>> _networkSubscribers = new();
    private readonly OsUdpPeerRegistry _peers;
    private readonly CancellationTokenSource _receiverCts = new();
    private readonly Task _receiverLoop;
    private readonly bool _enablePeerDiscovery;

    public OsUdpNodeTransport(int localPort = 0, bool enableIpv6 = true, bool enablePeerDiscovery = true)
    {
        _enablePeerDiscovery = enablePeerDiscovery;
        _udp = OsUdpSocketFactory.Create(localPort, enableIpv6);
        _peers = new OsUdpPeerRegistry(enablePeerDiscovery, NormalizeEndpoint);

        _receiverLoop = Task.Run(ProcessReceiveLoopAsync);
    }

    public IPEndPoint LocalEndpoint
    {
        get
        {
            return NormalizeEndpoint((IPEndPoint)_udp.Client.LocalEndPoint!);
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
        var advertisedEndpoint = localEndpoint is null ? LocalEndpoint : NormalizeEndpoint(localEndpoint);
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

                await _udp
                    .SendAsync(frame, peer.Value, cancellationToken)
                    .ConfigureAwait(false);
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

        var remoteEndpoint = NormalizeEndpoint(endpoint);
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
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _receiverCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _receiverLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _udp.Dispose();
        _peers.Cleanup();
        _receiverCts.Dispose();
        _gate.Dispose();
    }

    private async Task ProcessReceiveLoopAsync()
    {
        var token = _receiverCts.Token;
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }

            if (!NodeFrameCodec.TryDecode(result.Buffer.AsMemory(), out var networkId, out var sourceNodeId, out var payload))
            {
                continue;
            }

            if (OsUdpPeerDiscoveryProtocol.TryParsePayload(payload.Span, out var controlFrameType, out var discoveredNodeId))
            {
                if (_enablePeerDiscovery && discoveredNodeId != 0)
                {
                    _peers.RegisterDiscoveredPeer(networkId, discoveredNodeId, result.RemoteEndPoint);
                    if (_peers.TryGetLocalNodeId(networkId, out var localNodeId) && localNodeId != discoveredNodeId)
                    {
                        if (controlFrameType == OsUdpPeerDiscoveryProtocol.FrameType.PeerHello)
                        {
                            await SendDiscoveryFrameAsync(
                                networkId,
                                localNodeId,
                                result.RemoteEndPoint,
                                OsUdpPeerDiscoveryProtocol.FrameType.PeerHelloResponse,
                                token).ConfigureAwait(false);
                        }
                    }
                }

                continue;
            }

            if (_enablePeerDiscovery &&
                sourceNodeId != 0 &&
                _peers.TryGetLocalNodeId(networkId, out var localNodeIdForDiscovery) &&
                localNodeIdForDiscovery != sourceNodeId)
            {
                _peers.RegisterDiscoveredPeer(networkId, sourceNodeId, result.RemoteEndPoint);
            }

            if (!_networkSubscribers.TryGetValue(networkId, out var subscribers))
            {
                continue;
            }

            foreach (var callback in subscribers.Values)
            {
                await callback
                    .OnFrameReceived(sourceNodeId, networkId, payload, token)
                    .ConfigureAwait(false);
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
        OsUdpPeerDiscoveryProtocol.WritePayload(frameType, nodeId, payload);

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

    private static IPEndPoint NormalizeEndpoint(IPEndPoint endpoint)
    {
        if (endpoint.Address.Equals(IPAddress.Any))
        {
            return new IPEndPoint(IPAddress.Loopback, endpoint.Port);
        }

        if (endpoint.Address.Equals(IPAddress.IPv6Any))
        {
            return new IPEndPoint(IPAddress.IPv6Loopback, endpoint.Port);
        }

        return endpoint;
    }
}
