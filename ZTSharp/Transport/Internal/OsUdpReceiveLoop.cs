using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace ZTSharp.Transport.Internal;

internal sealed class OsUdpReceiveLoop
{
    private const int MaxHelloResponseCacheEntries = 4096;
    private const int MaxPendingDiscoverySends = 256;
    private const long PeerHelloResponseMinIntervalMs = 1000;

    private readonly record struct DiscoverySendRequest(
        ulong NetworkId,
        ulong LocalNodeId,
        IPEndPoint Endpoint,
        OsUdpPeerDiscoveryProtocol.FrameType FrameType);

    private readonly UdpClient _udp;
    private readonly Func<CancellationToken, ValueTask<UdpReceiveResult>> _receiveAsync;
    private readonly bool _enablePeerDiscovery;
    private readonly OsUdpPeerRegistry _peers;
    private readonly Func<ulong, ulong, ReadOnlyMemory<byte>, CancellationToken, Task> _dispatchFrameAsync;
    private readonly Func<ulong, ulong, IPEndPoint, OsUdpPeerDiscoveryProtocol.FrameType, CancellationToken, Task> _sendDiscoveryFrameAsync;
    private readonly Action<string>? _log;
    private readonly Dictionary<(ulong NetworkId, ulong NodeId), long> _helloResponseLastSentMs = new();
    private readonly Channel<DiscoverySendRequest> _discoverySendQueue;

    internal Action<IPEndPoint>? DatagramReceivedForTests { get; set; }

    public OsUdpReceiveLoop(
        UdpClient udp,
        bool enablePeerDiscovery,
        OsUdpPeerRegistry peers,
        Func<ulong, ulong, ReadOnlyMemory<byte>, CancellationToken, Task> dispatchFrameAsync,
        Func<ulong, ulong, IPEndPoint, OsUdpPeerDiscoveryProtocol.FrameType, CancellationToken, Task> sendDiscoveryFrameAsync,
        Action<string>? log = null,
        Func<CancellationToken, ValueTask<UdpReceiveResult>>? receiveAsync = null)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentNullException.ThrowIfNull(dispatchFrameAsync);
        ArgumentNullException.ThrowIfNull(sendDiscoveryFrameAsync);

        _udp = udp;
        _receiveAsync = receiveAsync ?? (ct => udp.ReceiveAsync(ct));
        _enablePeerDiscovery = enablePeerDiscovery;
        _peers = peers;
        _dispatchFrameAsync = dispatchFrameAsync;
        _sendDiscoveryFrameAsync = sendDiscoveryFrameAsync;
        _log = log;

        _discoverySendQueue = Channel.CreateBounded<DiscoverySendRequest>(new BoundedChannelOptions(MaxPendingDiscoverySends)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var discoverySendLoop = _enablePeerDiscovery
            ? RunDiscoverySendLoopAsync(cancellationToken)
            : Task.CompletedTask;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _receiveAsync(cancellationToken).ConfigureAwait(false);
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
                catch (SocketException ex)
                {
                    _log?.Invoke($"OS UDP receive failed (SocketException {ex.SocketErrorCode}: {ex.Message}).");
                    continue;
                }
                catch (InvalidOperationException ex)
                {
                    _log?.Invoke($"OS UDP receive failed (InvalidOperationException: {ex.Message}).");
                    continue;
                }

                var normalizedRemoteEndpoint = UdpEndpointNormalization.Normalize(result.RemoteEndPoint);
                DatagramReceivedForTests?.Invoke(normalizedRemoteEndpoint);

                if (!NodeFrameCodec.TryDecode(result.Buffer.AsMemory(), out var networkId, out var sourceNodeId, out var payload))
                {
                    continue;
                }

                if (_enablePeerDiscovery &&
                    _peers.TryGetLocalNodeId(networkId, out var localNodeId) &&
                    localNodeId != 0 &&
                    OsUdpPeerDiscoveryProtocol.TryParsePayload(payload.Span, networkId, out var controlFrameType, out var discoveredNodeId))
                {
                    if (discoveredNodeId != 0 && discoveredNodeId == sourceNodeId)
                    {
                        _peers.RegisterDiscoveredPeer(networkId, discoveredNodeId, normalizedRemoteEndpoint);
                        if (localNodeId != discoveredNodeId &&
                            controlFrameType == OsUdpPeerDiscoveryProtocol.FrameType.PeerHello &&
                            ShouldSendHelloResponse(networkId, discoveredNodeId))
                        {
                            QueueDiscoverySend(
                                networkId,
                                localNodeId,
                                normalizedRemoteEndpoint,
                                OsUdpPeerDiscoveryProtocol.FrameType.PeerHelloResponse);
                        }
                    }

                    continue;
                }

                if (sourceNodeId == 0 ||
                    !_peers.TryGetPeers(networkId, out var peers) ||
                    !peers.TryGetValue(sourceNodeId, out var expectedEntry))
                {
                    continue;
                }

                var expectedEndpoint = expectedEntry.Endpoint;
                if (!expectedEndpoint.Equals(normalizedRemoteEndpoint))
                {
                    continue;
                }

                try
                {
                    await _dispatchFrameAsync(sourceNodeId, networkId, payload, cancellationToken).ConfigureAwait(false);
                    _peers.RefreshPeerLastSeen(networkId, sourceNodeId);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // Receive loop must survive dispatch failures.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }
        }
        finally
        {
            _discoverySendQueue.Writer.TryComplete();
            try
            {
                await discoverySendLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private bool ShouldSendHelloResponse(ulong networkId, ulong remoteNodeId)
    {
        var key = (networkId, remoteNodeId);
        var nowMs = Environment.TickCount64;
        if (_helloResponseLastSentMs.TryGetValue(key, out var lastMs))
        {
            if (unchecked(nowMs - lastMs) < PeerHelloResponseMinIntervalMs)
            {
                return false;
            }
        }

        _helloResponseLastSentMs[key] = nowMs;

        if (_helloResponseLastSentMs.Count > MaxHelloResponseCacheEntries)
        {
            var overflow = _helloResponseLastSentMs.Count - MaxHelloResponseCacheEntries;
            var toRemove = _helloResponseLastSentMs
                .OrderBy(pair => pair.Value)
                .Take(overflow)
                .Select(pair => pair.Key)
                .ToArray();

            for (var i = 0; i < toRemove.Length; i++)
            {
                _helloResponseLastSentMs.Remove(toRemove[i]);
            }
        }

        return true;
    }

    private void QueueDiscoverySend(
        ulong networkId,
        ulong localNodeId,
        IPEndPoint endpoint,
        OsUdpPeerDiscoveryProtocol.FrameType frameType)
        => _discoverySendQueue.Writer.TryWrite(new DiscoverySendRequest(networkId, localNodeId, endpoint, frameType));

    private async Task RunDiscoverySendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in _discoverySendQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await SendDiscoveryFrameSafeAsync(
                        request.NetworkId,
                        request.LocalNodeId,
                        request.Endpoint,
                        request.FrameType,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task SendDiscoveryFrameSafeAsync(
        ulong networkId,
        ulong localNodeId,
        IPEndPoint endpoint,
        OsUdpPeerDiscoveryProtocol.FrameType frameType,
        CancellationToken cancellationToken)
    {
        try
        {
            await _sendDiscoveryFrameAsync(networkId, localNodeId, endpoint, frameType, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
#pragma warning disable CA1031 // Discovery send must not kill the receive loop.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
