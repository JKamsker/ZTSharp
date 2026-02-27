using System.Net;
using System.Threading.Channels;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierDataplaneRxLoops
{
    private readonly IZeroTierUdpTransport _udp;
    private readonly NodeId _rootNodeId;
    private readonly IPEndPoint _rootEndpoint;
    private readonly byte[] _rootKey;
    private readonly NodeId _localNodeId;
    private readonly ZeroTierDataplaneRootClient _rootClient;
    private readonly IZeroTierDataplanePeerDatagramProcessor _peerDatagrams;
    private readonly Func<ZeroTierVerb, ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask>? _handleRootControlAsync;
    private readonly Action? _onPeerQueueDrop;
    private readonly bool _acceptDirectPeerDatagrams;

    private int _traceRxRemaining = 200;

    public ZeroTierDataplaneRxLoops(
        IZeroTierUdpTransport udp,
        NodeId rootNodeId,
        IPEndPoint rootEndpoint,
        byte[] rootKey,
        NodeId localNodeId,
        ZeroTierDataplaneRootClient rootClient,
        IZeroTierDataplanePeerDatagramProcessor peerDatagrams,
        bool acceptDirectPeerDatagrams = false,
        Func<ZeroTierVerb, ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask>? handleRootControlAsync = null,
        Action? onPeerQueueDrop = null)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(rootEndpoint);
        ArgumentNullException.ThrowIfNull(rootKey);
        ArgumentNullException.ThrowIfNull(rootClient);
        ArgumentNullException.ThrowIfNull(peerDatagrams);

        _udp = udp;
        _rootNodeId = rootNodeId;
        _rootEndpoint = rootEndpoint;
        _rootKey = rootKey;
        _localNodeId = localNodeId;
        _rootClient = rootClient;
        _peerDatagrams = peerDatagrams;
        _acceptDirectPeerDatagrams = acceptDirectPeerDatagrams;
        _handleRootControlAsync = handleRootControlAsync;
        _onPeerQueueDrop = onPeerQueueDrop;
    }

    public async Task DispatcherLoopAsync(Channel<ZeroTierUdpDatagram> peerQueue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(peerQueue);
        var peerWriter = peerQueue.Writer;
        while (!cancellationToken.IsCancellationRequested)
        {
            ZeroTierUdpDatagram datagram;
            try
            {
                datagram = await _udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ChannelClosedException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (!_acceptDirectPeerDatagrams && !datagram.RemoteEndPoint.Equals(_rootEndpoint))
            {
                var peek = datagram.Payload.AsSpan();
                if (peek.Length < ZeroTierPacketHeader.Length)
                {
                    continue;
                }

                var source = new NodeId(
                    ZeroTierBinaryPrimitives.ReadUInt40BigEndian(
                        peek.Slice(ZeroTierPacketHeader.IndexSource, 5)));
                if (source != _rootNodeId)
                {
                    continue;
                }
            }

            var packetBytes = datagram.Payload;
            if (!ZeroTierPacketCodec.TryDecode(packetBytes, out var decoded))
            {
                continue;
            }

            if (decoded.Header.Destination != _localNodeId)
            {
                continue;
            }

            if (ZeroTierTrace.Enabled && _traceRxRemaining > 0)
            {
                _traceRxRemaining--;
                ZeroTierTrace.WriteLine(
                    $"[zerotier] RX raw: src={decoded.Header.Source} dst={decoded.Header.Destination} cipher={decoded.Header.CipherSuite} flags=0x{decoded.Header.Flags:x2} verbRaw=0x{decoded.Header.VerbRaw:x2} via {datagram.RemoteEndPoint}.");
            }

            if (decoded.Header.Source == _rootNodeId)
            {
                if (ZeroTierTrace.Enabled && !datagram.RemoteEndPoint.Equals(_rootEndpoint))
                {
                    ZeroTierTrace.WriteLine($"[zerotier] RX root packet via {datagram.RemoteEndPoint} (expected {_rootEndpoint}).");
                }

                if (!ZeroTierPacketCrypto.Dearmor(packetBytes, _rootKey))
                {
                    continue;
                }

                if ((packetBytes[ZeroTierPacketHeader.IndexVerb] & ZeroTierPacketHeader.VerbFlagCompressed) != 0)
                {
                    if (!ZeroTierPacketCompression.TryUncompress(packetBytes, out var uncompressed))
                    {
                        continue;
                    }

                    packetBytes = uncompressed;
                }

                var verb = (ZeroTierVerb)(packetBytes[ZeroTierPacketHeader.IndexVerb] & 0x1F);
                var payload = packetBytes.AsMemory(ZeroTierPacketHeader.IndexPayload);

                if (!_rootClient.TryDispatchResponse(verb, payload.Span) && _handleRootControlAsync is not null)
                {
                    try
                    {
                        await _handleRootControlAsync(verb, payload, datagram.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
#pragma warning disable CA1031 // Root loop must survive per-packet faults.
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        ZeroTierTrace.WriteLine($"[zerotier] Root packet handler fault: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                continue;
            }

            if (!_acceptDirectPeerDatagrams && !datagram.RemoteEndPoint.Equals(_rootEndpoint))
            {
                continue;
            }

            if (!peerWriter.TryWrite(datagram))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _onPeerQueueDrop?.Invoke();
                _ = peerQueue.Reader.TryRead(out _);
                peerWriter.TryWrite(datagram);
                continue;
            }
        }
    }

    public async Task PeerLoopAsync(ChannelReader<ZeroTierUdpDatagram> peerReader, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ZeroTierUdpDatagram datagram;
            try
            {
                datagram = await peerReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ChannelClosedException)
            {
                return;
            }

            try
            {
                await _peerDatagrams.ProcessAsync(datagram, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Peer loop must survive per-packet faults.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                ZeroTierTrace.WriteLine($"[zerotier] Peer loop processor fault: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
