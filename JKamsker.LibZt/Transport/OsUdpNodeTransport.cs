using System.Collections.Concurrent;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;

namespace JKamsker.LibZt.Transport;

/// <summary>
/// OS UDP transport adapter for external endpoint integration.
/// </summary>
internal sealed class OsUdpNodeTransport : IZtNodeTransport, IAsyncDisposable
{
    private sealed record Subscriber(
        ulong NodeId,
        Func<ulong, ulong, ReadOnlyMemory<byte>, CancellationToken, Task> OnFrameReceived);

    private enum ControlFrameType : byte
    {
        PeerHello = 1,
        PeerHelloResponse = 2
    }

    private static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, IPEndPoint>> _networkDirectory = new();
    private const int ControlFrameNodeOffset = 1;
    private const int ControlFrameNodeLength = sizeof(ulong);
    private const int ControlFrameLength = 1 + ControlFrameNodeLength;

    private readonly UdpClient _udp;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<Guid, Subscriber>> _networkSubscribers = new();
    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, IPEndPoint>> _networkPeers = new();
    private readonly ConcurrentDictionary<ulong, ulong> _localNodeIds = new();
    private readonly CancellationTokenSource _receiverCts = new();
    private readonly Task _receiverLoop;

    public OsUdpNodeTransport(int localPort = 0, bool enableIpv6 = true)
    {
        _udp = new UdpClient(enableIpv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
        if (enableIpv6)
        {
            _udp.Client.DualMode = true;
            _udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, localPort));
        }
        else
        {
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));
        }

        _receiverLoop = Task.Run(ProcessReceiveLoopAsync);
    }

    public IPEndPoint LocalEndpoint
    {
        get
        {
            return NormalizeEndpointForLocalDelivery((IPEndPoint)_udp.Client.LocalEndPoint!);
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
        var advertisedEndpoint = localEndpoint is null ? LocalEndpoint : NormalizeEndpointForRemoteDelivery(localEndpoint);
        _localNodeIds[networkId] = nodeId;
        var subscribers = _networkSubscribers.GetOrAdd(
            networkId,
            _ => new ConcurrentDictionary<Guid, Subscriber>());
        subscribers[registrationId] = new Subscriber(nodeId, onFrameReceived);

        var discoveredPeers = _networkDirectory.GetOrAdd(networkId, _ => new ConcurrentDictionary<ulong, IPEndPoint>());
        discoveredPeers[nodeId] = advertisedEndpoint;

        var localPeers = _networkPeers.GetOrAdd(networkId, _ => new ConcurrentDictionary<ulong, IPEndPoint>());
        foreach (var peer in discoveredPeers)
        {
            if (peer.Key == nodeId)
            {
                continue;
            }

            localPeers[peer.Key] = peer.Value;
        }

        var helloTasks = new List<Task>(discoveredPeers.Count);
        foreach (var peer in discoveredPeers)
        {
            if (peer.Key == nodeId)
            {
                continue;
            }

            helloTasks.Add(SendDiscoveryFrameAsync(
                networkId,
                nodeId,
                peer.Value,
                ControlFrameType.PeerHello,
                cancellationToken));
        }

        if (helloTasks.Count > 0)
        {
            await Task.WhenAll(helloTasks).ConfigureAwait(false);
        }

        return registrationId;
    }

    public async Task LeaveNetworkAsync(ulong networkId, Guid registrationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_networkSubscribers.TryGetValue(networkId, out var subscribers) &&
            subscribers.TryGetValue(registrationId, out var localSubscriber))
        {
            if (_localNodeIds.TryGetValue(networkId, out var localNodeId) && localNodeId == localSubscriber.NodeId)
            {
                _localNodeIds.TryRemove(networkId, out _);
                if (_networkDirectory.TryGetValue(networkId, out var discoveredPeers))
                {
                    discoveredPeers.TryRemove(localNodeId, out _);
                    if (discoveredPeers.IsEmpty)
                    {
                        _networkDirectory.TryRemove(networkId, out _);
                    }
                }
            }
        }

        _networkPeers.TryRemove(networkId, out _);
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

    public Task SendFrameAsync(
        ulong networkId,
        ulong sourceNodeId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_networkPeers.TryGetValue(networkId, out var peers))
        {
            return Task.CompletedTask;
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
            Task? firstTask = null;
            List<Task>? sendTasks = null;
            foreach (var peer in peers)
            {
                if (peer.Key == sourceNodeId)
                {
                    continue;
                }

                var sendTask = _udp.SendAsync(frame, peer.Value, cancellationToken).AsTask();
                if (firstTask is null)
                {
                    firstTask = sendTask;
                    continue;
                }

                sendTasks ??= new List<Task>(2);
                if (sendTasks.Count == 0)
                {
                    sendTasks.Add(firstTask);
                }

                sendTasks.Add(sendTask);
            }

            if (sendTasks is not null)
            {
                return Task.WhenAll(sendTasks);
            }

            if (firstTask is not null)
            {
                return firstTask;
            }

            return Task.CompletedTask;
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

    public ValueTask AddPeerAsync(ulong networkId, ulong nodeId, IPEndPoint endpoint)
    {
        ArgumentOutOfRangeException.ThrowIfZero(nodeId);
        ArgumentNullException.ThrowIfNull(endpoint);

        var peers = _networkPeers.GetOrAdd(networkId, _ => new ConcurrentDictionary<ulong, IPEndPoint>());
        peers[nodeId] = NormalizeEndpointForRemoteDelivery(endpoint);
        return ValueTask.CompletedTask;
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
        foreach (var local in _localNodeIds)
        {
            if (_networkDirectory.TryGetValue(local.Key, out var discoveredPeers))
            {
                discoveredPeers.TryRemove(local.Value, out _);
                if (discoveredPeers.IsEmpty)
                {
                    _networkDirectory.TryRemove(local.Key, out _);
                }
            }
        }

        _networkPeers.Clear();
        _localNodeIds.Clear();
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

            if (!NodeFrameCodec.TryDecode(result.Buffer.AsMemory(), out var networkId, out var sourceNodeId, out var payload))
            {
                continue;
            }

            if (TryParseControlPayload(payload.Span, out var controlFrameType, out var discoveredNodeId))
            {
                if (discoveredNodeId != 0)
                {
                    RegisterDiscoveredPeer(networkId, discoveredNodeId, result.RemoteEndPoint);
                    if (_localNodeIds.TryGetValue(networkId, out var localNodeId) && localNodeId != discoveredNodeId)
                    {
                        if (controlFrameType == ControlFrameType.PeerHello)
                        {
                            await SendDiscoveryFrameAsync(
                                networkId,
                                localNodeId,
                                result.RemoteEndPoint,
                                ControlFrameType.PeerHelloResponse,
                                token).ConfigureAwait(false);
                        }
                    }
                }

                continue;
            }

            if (!_networkSubscribers.TryGetValue(networkId, out var subscribers))
            {
                continue;
            }

            Task? firstCallback = null;
            List<Task>? callbacks = null;
            foreach (var callback in subscribers.Values)
            {
                var callbackTask = callback.OnFrameReceived(sourceNodeId, networkId, payload, token);
                if (firstCallback is null)
                {
                    firstCallback = callbackTask;
                    continue;
                }

                callbacks ??= new List<Task>(2);
                if (callbacks.Count == 0)
                {
                    callbacks.Add(firstCallback);
                }

                callbacks.Add(callbackTask);
            }

            if (callbacks is not null)
            {
                await Task.WhenAll(callbacks).ConfigureAwait(false);
            }
            else if (firstCallback is not null)
            {
                await firstCallback.ConfigureAwait(false);
            }
        }
    }

    private async Task SendDiscoveryFrameAsync(
        ulong networkId,
        ulong nodeId,
        IPEndPoint endpoint,
        ControlFrameType frameType,
        CancellationToken cancellationToken)
    {
        Span<byte> payload = stackalloc byte[ControlFrameLength];
        WriteControlPayload(frameType, nodeId, payload);

        var frame = ArrayPool<byte>.Shared.Rent(NodeFrameCodec.GetEncodedLength(ControlFrameLength));
        try
        {
            if (!NodeFrameCodec.TryEncode(networkId, nodeId, payload, frame, out var frameLength))
            {
                throw new InvalidOperationException("Encoded control frame did not fit destination buffer.");
            }

            await _udp
                .SendAsync(frame.AsMemory(0, frameLength), endpoint, cancellationToken)
                .AsTask()
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    private static void WriteControlPayload(ControlFrameType frameType, ulong nodeId, Span<byte> payload)
    {
        payload[0] = (byte)frameType;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.Slice(ControlFrameNodeOffset), nodeId);
    }

    private static bool TryParseControlPayload(
        ReadOnlySpan<byte> payload,
        out ControlFrameType frameType,
        out ulong nodeId)
    {
        frameType = ControlFrameType.PeerHello;
        nodeId = 0;
        if (payload.Length < ControlFrameLength)
        {
            return false;
        }

        frameType = (ControlFrameType)payload[0];
        if (frameType != ControlFrameType.PeerHello && frameType != ControlFrameType.PeerHelloResponse)
        {
            return false;
        }

        nodeId = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(ControlFrameNodeOffset));
        return true;
    }

    private void RegisterDiscoveredPeer(ulong networkId, ulong sourceNodeId, IPEndPoint remoteEndpoint)
    {
        var endpoint = NormalizeEndpointForRemoteDelivery(remoteEndpoint);
        var peers = _networkPeers.GetOrAdd(networkId, _ => new ConcurrentDictionary<ulong, IPEndPoint>());
        peers[sourceNodeId] = endpoint;
        var directoryPeers = _networkDirectory.GetOrAdd(networkId, _ => new ConcurrentDictionary<ulong, IPEndPoint>());
        directoryPeers[sourceNodeId] = endpoint;
    }

    private static IPEndPoint NormalizeEndpointForLocalDelivery(IPEndPoint endpoint)
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

    private static IPEndPoint NormalizeEndpointForRemoteDelivery(IPEndPoint endpoint)
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
