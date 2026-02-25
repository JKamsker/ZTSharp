using System.Threading.Channels;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierDataplaneRxLoops
{
    private const int IndexVerb = 27;

    private readonly ZeroTierUdpTransport _udp;
    private readonly NodeId _rootNodeId;
    private readonly byte[] _rootKey;
    private readonly NodeId _localNodeId;
    private readonly ZeroTierDataplaneRootClient _rootClient;
    private readonly ZeroTierDataplanePeerDatagramProcessor _peerDatagrams;

    private int _traceRxRemaining = 200;

    public ZeroTierDataplaneRxLoops(
        ZeroTierUdpTransport udp,
        NodeId rootNodeId,
        byte[] rootKey,
        NodeId localNodeId,
        ZeroTierDataplaneRootClient rootClient,
        ZeroTierDataplanePeerDatagramProcessor peerDatagrams)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(rootKey);
        ArgumentNullException.ThrowIfNull(rootClient);
        ArgumentNullException.ThrowIfNull(peerDatagrams);

        _udp = udp;
        _rootNodeId = rootNodeId;
        _rootKey = rootKey;
        _localNodeId = localNodeId;
        _rootClient = rootClient;
        _peerDatagrams = peerDatagrams;
    }

    public async Task DispatcherLoopAsync(ChannelWriter<ZeroTierUdpDatagram> peerWriter, CancellationToken cancellationToken)
    {
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
            catch (ObjectDisposedException)
            {
                return;
            }

            var packetBytes = datagram.Payload.ToArray();
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
                if (!ZeroTierPacketCrypto.Dearmor(packetBytes, _rootKey))
                {
                    continue;
                }

                if ((packetBytes[IndexVerb] & ZeroTierPacketHeader.VerbFlagCompressed) != 0)
                {
                    if (!ZeroTierPacketCompression.TryUncompress(packetBytes, out var uncompressed))
                    {
                        continue;
                    }

                    packetBytes = uncompressed;
                }

                var verb = (ZeroTierVerb)(packetBytes[IndexVerb] & 0x1F);
                var payload = packetBytes.AsSpan(ZeroTierPacketHeader.Length);

                _rootClient.TryDispatchResponse(verb, payload);
                continue;
            }

            if (!peerWriter.TryWrite(datagram))
            {
                return;
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

            await _peerDatagrams.ProcessAsync(datagram, cancellationToken).ConfigureAwait(false);
        }
    }
}

