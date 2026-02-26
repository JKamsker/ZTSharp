using System.Buffers.Binary;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierDataplanePeerDatagramProcessor
    : IZeroTierDataplanePeerDatagramProcessor
{
    private readonly NodeId _localNodeId;
    private readonly ZeroTierDataplanePeerSecurity _peerSecurity;
    private readonly ZeroTierDataplanePeerPacketHandler _peerPackets;
    private readonly ZeroTierPeerPhysicalPathTracker _peerPaths;
    private readonly ZeroTierPeerEchoManager _peerEcho;
    private readonly ZeroTierExternalSurfaceAddressTracker _surfaceAddresses;

    public ZeroTierDataplanePeerDatagramProcessor(
        NodeId localNodeId,
        ZeroTierDataplanePeerSecurity peerSecurity,
        ZeroTierDataplanePeerPacketHandler peerPackets,
        ZeroTierPeerPhysicalPathTracker peerPaths,
        ZeroTierPeerEchoManager peerEcho,
        ZeroTierExternalSurfaceAddressTracker surfaceAddresses)
    {
        ArgumentNullException.ThrowIfNull(peerSecurity);
        ArgumentNullException.ThrowIfNull(peerPackets);
        ArgumentNullException.ThrowIfNull(peerPaths);
        ArgumentNullException.ThrowIfNull(peerEcho);
        ArgumentNullException.ThrowIfNull(surfaceAddresses);

        _localNodeId = localNodeId;
        _peerSecurity = peerSecurity;
        _peerPackets = peerPackets;
        _peerPaths = peerPaths;
        _peerEcho = peerEcho;
        _surfaceAddresses = surfaceAddresses;
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

        if (decoded.Header.HopCount == 0)
        {
            _peerPaths.ObserveHop0(peerNodeId, datagram.LocalSocketId, datagram.RemoteEndPoint);
            await _peerEcho
                .TrySendEchoProbeAsync(peerNodeId, datagram.LocalSocketId, datagram.RemoteEndPoint, key, cancellationToken)
                .ConfigureAwait(false);
        }

        var verb = (ZeroTierVerb)(packetBytes[ZeroTierPacketHeader.IndexVerb] & 0x1F);
        var payload = packetBytes.AsMemory(ZeroTierPacketHeader.IndexPayload);

        if (verb == ZeroTierVerb.Echo)
        {
            await _peerEcho
                .HandleEchoRequestAsync(
                    peerNodeId,
                    datagram.LocalSocketId,
                    datagram.RemoteEndPoint,
                    inRePacketId: decoded.Header.PacketId,
                    payload,
                    key,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var payloadSpan = payload.Span;
        if (verb == ZeroTierVerb.Ok && payloadSpan.Length >= 1 + 8)
        {
            var inReVerb = (ZeroTierVerb)(payloadSpan[0] & 0x1F);
            if (inReVerb == ZeroTierVerb.Echo)
            {
                var inRePacketId = BinaryPrimitives.ReadUInt64BigEndian(payloadSpan.Slice(1, 8));
                _peerEcho.HandleEchoOk(peerNodeId, datagram.LocalSocketId, datagram.RemoteEndPoint, inRePacketId, payloadSpan.Slice(1 + 8));
                return;
            }

            if (inReVerb == ZeroTierVerb.Hello)
            {
                if (ZeroTierHelloOkParser.TryParseDecryptedOkHello(packetBytes, out var ok))
                {
                    _peerSecurity.ObservePeerProtocolVersion(peerNodeId, ok.RemoteProtocolVersion);
                    _peerEcho.ObserveHelloOkRtt(peerNodeId, datagram.LocalSocketId, datagram.RemoteEndPoint, ok.TimestampEcho);

                    if (ok.ExternalSurfaceAddress is { } surface)
                    {
                        _surfaceAddresses.Observe(peerNodeId, datagram.LocalSocketId, surface);
                    }
                }

                return;
            }
        }

        await _peerPackets.HandleAsync(peerNodeId, packetBytes, cancellationToken).ConfigureAwait(false);
    }
}
