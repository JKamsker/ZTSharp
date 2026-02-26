using System.Net;
using System.Net.Sockets;

namespace ZTSharp.Transport.Internal;

internal sealed class OsUdpReceiveLoop
{
    private readonly UdpClient _udp;
    private readonly Func<CancellationToken, ValueTask<UdpReceiveResult>> _receiveAsync;
    private readonly bool _enablePeerDiscovery;
    private readonly OsUdpPeerRegistry _peers;
    private readonly Func<ulong, ulong, ReadOnlyMemory<byte>, CancellationToken, Task> _dispatchFrameAsync;
    private readonly Func<ulong, ulong, IPEndPoint, OsUdpPeerDiscoveryProtocol.FrameType, CancellationToken, Task> _sendDiscoveryFrameAsync;
    private readonly Action<string>? _log;

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
    }

    public async Task RunAsync(CancellationToken cancellationToken)
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

            if (!NodeFrameCodec.TryDecode(result.Buffer.AsMemory(), out var networkId, out var sourceNodeId, out var payload))
            {
                continue;
            }

            var normalizedRemoteEndpoint = UdpEndpointNormalization.Normalize(result.RemoteEndPoint);
            if (_enablePeerDiscovery && OsUdpPeerDiscoveryProtocol.TryParsePayload(payload.Span, networkId, out var controlFrameType, out var discoveredNodeId))
            {
                if (discoveredNodeId != 0 && discoveredNodeId == sourceNodeId)
                {
                    _peers.RegisterDiscoveredPeer(networkId, discoveredNodeId, normalizedRemoteEndpoint);
                    if (_peers.TryGetLocalNodeId(networkId, out var localNodeId) && localNodeId != discoveredNodeId)
                    {
                        if (controlFrameType == OsUdpPeerDiscoveryProtocol.FrameType.PeerHello)
                        {
                            _ = SendDiscoveryFrameSafeAsync(
                                networkId,
                                localNodeId,
                                normalizedRemoteEndpoint,
                                OsUdpPeerDiscoveryProtocol.FrameType.PeerHelloResponse,
                                cancellationToken);
                        }
                    }
                }

                continue;
            }

            if (sourceNodeId != 0 &&
                _peers.TryGetPeers(networkId, out var peers) &&
                peers.TryGetValue(sourceNodeId, out var expectedEndpoint) &&
                expectedEndpoint.Equals(normalizedRemoteEndpoint))
            {
                // Valid authenticated-by-endpoint peer.
            }
            else
            {
                continue;
            }

            try
            {
                await _dispatchFrameAsync(sourceNodeId, networkId, payload, cancellationToken).ConfigureAwait(false);
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
