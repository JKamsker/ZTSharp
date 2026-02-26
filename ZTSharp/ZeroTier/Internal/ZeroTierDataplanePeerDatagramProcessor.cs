using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierDataplanePeerDatagramProcessor
{
    private readonly NodeId _localNodeId;
    private readonly ZeroTierDataplanePeerSecurity _peerSecurity;
    private readonly ZeroTierDataplanePeerPacketHandler _peerPackets;

    public ZeroTierDataplanePeerDatagramProcessor(
        NodeId localNodeId,
        ZeroTierDataplanePeerSecurity peerSecurity,
        ZeroTierDataplanePeerPacketHandler peerPackets)
    {
        ArgumentNullException.ThrowIfNull(peerSecurity);
        ArgumentNullException.ThrowIfNull(peerPackets);

        _localNodeId = localNodeId;
        _peerSecurity = peerSecurity;
        _peerPackets = peerPackets;
    }

    public async Task ProcessAsync(ZeroTierUdpDatagram datagram, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var packetBytes = datagram.Payload;
        if (!ZeroTierPacketCodec.TryDecode(packetBytes, out var decoded))
        {
            return;
        }

        if (decoded.Header.Destination != _localNodeId)
        {
            return;
        }

        var peerNodeId = decoded.Header.Source;

        if (decoded.Header.CipherSuite == 0 && decoded.Header.Verb == ZeroTierVerb.Hello)
        {
            await _peerSecurity
                .HandleHelloAsync(peerNodeId, decoded.Header.PacketId, packetBytes, datagram.RemoteEndPoint, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!_peerSecurity.TryGetPeerKey(peerNodeId, out var key))
        {
            _peerSecurity.EnsurePeerKeyAsync(peerNodeId);
            return;
        }

        if (!ZeroTierPacketCrypto.Dearmor(packetBytes, key))
        {
            return;
        }

        if ((packetBytes[ZeroTierPacketHeader.IndexVerb] & ZeroTierPacketHeader.VerbFlagCompressed) != 0)
        {
            if (!ZeroTierPacketCompression.TryUncompress(packetBytes, out var uncompressed))
            {
                return;
            }

            packetBytes = uncompressed;
        }

        await _peerPackets.HandleAsync(peerNodeId, packetBytes, cancellationToken).ConfigureAwait(false);
    }
}
