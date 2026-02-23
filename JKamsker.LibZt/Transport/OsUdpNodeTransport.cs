using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace JKamsker.LibZt.Transport;

/// <summary>
/// OS UDP transport adapter for external endpoint integration.
/// </summary>
internal sealed class OsUdpNodeTransport : IZtNodeTransport, IAsyncDisposable
{
    private sealed record Subscriber(
        ulong NodeId,
        Func<ulong, ulong, ReadOnlyMemory<byte>, CancellationToken, Task> OnFrameReceived);

    private readonly UdpClient _udp;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<Guid, Subscriber>> _networkSubscribers = new();
    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, IPEndPoint>> _networkPeers = new();
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

    public Task<Guid> JoinNetworkAsync(
        ulong networkId,
        ulong nodeId,
        Func<ulong, ulong, ReadOnlyMemory<byte>, CancellationToken, Task> onFrameReceived,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(nodeId);
        ArgumentNullException.ThrowIfNull(onFrameReceived);
        cancellationToken.ThrowIfCancellationRequested();

        var registrationId = Guid.NewGuid();
        var subscribers = _networkSubscribers.GetOrAdd(
            networkId,
            _ => new ConcurrentDictionary<Guid, Subscriber>());
        subscribers[registrationId] = new Subscriber(nodeId, onFrameReceived);
        return Task.FromResult(registrationId);
    }

    public async Task LeaveNetworkAsync(ulong networkId, Guid registrationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_networkSubscribers.TryGetValue(networkId, out var subscribers))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            subscribers.TryRemove(registrationId, out _);
            if (subscribers.IsEmpty)
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

        var frame = NodeFrameCodec.Encode(networkId, sourceNodeId, payload.Span);
        var sendTasks = new List<Task>();
        foreach (var peer in peers)
        {
            if (peer.Key == sourceNodeId)
            {
                continue;
            }

            sendTasks.Add(_udp.SendAsync(frame, peer.Value, cancellationToken).AsTask());
        }

        return Task.WhenAll(sendTasks);
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

            if (!NodeFrameCodec.TryDecode(result.Buffer, out var networkId, out var sourceNodeId, out var payload))
            {
                continue;
            }

            if (!_networkSubscribers.TryGetValue(networkId, out var subscribers))
            {
                continue;
            }

            var callbacks = new List<Task>(subscribers.Count);
            foreach (var callback in subscribers.Values)
            {
                callbacks.Add(callback.OnFrameReceived(sourceNodeId, networkId, payload, token));
            }

            if (callbacks.Count > 0)
            {
                await Task.WhenAll(callbacks).ConfigureAwait(false);
            }
        }
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
