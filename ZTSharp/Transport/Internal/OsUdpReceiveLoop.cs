using System.Net;
using System.Net.Sockets;

namespace ZTSharp.Transport.Internal;

internal sealed class OsUdpReceiveLoop
{
    private readonly UdpClient _udp;
    private readonly bool _enablePeerDiscovery;
    private readonly OsUdpPeerRegistry _peers;
    private readonly Func<ulong, ulong, ReadOnlyMemory<byte>, CancellationToken, Task> _dispatchFrameAsync;
    private readonly Func<ulong, ulong, IPEndPoint, OsUdpPeerDiscoveryProtocol.FrameType, CancellationToken, Task> _sendDiscoveryFrameAsync;

    public OsUdpReceiveLoop(
        UdpClient udp,
        bool enablePeerDiscovery,
        OsUdpPeerRegistry peers,
        Func<ulong, ulong, ReadOnlyMemory<byte>, CancellationToken, Task> dispatchFrameAsync,
        Func<ulong, ulong, IPEndPoint, OsUdpPeerDiscoveryProtocol.FrameType, CancellationToken, Task> sendDiscoveryFrameAsync)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentNullException.ThrowIfNull(dispatchFrameAsync);
        ArgumentNullException.ThrowIfNull(sendDiscoveryFrameAsync);

        _udp = udp;
        _enablePeerDiscovery = enablePeerDiscovery;
        _peers = peers;
        _dispatchFrameAsync = dispatchFrameAsync;
        _sendDiscoveryFrameAsync = sendDiscoveryFrameAsync;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
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
                            await _sendDiscoveryFrameAsync(
                                    networkId,
                                    localNodeId,
                                    normalizedRemoteEndpoint,
                                    OsUdpPeerDiscoveryProtocol.FrameType.PeerHelloResponse,
                                    cancellationToken)
                                .ConfigureAwait(false);
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
}
