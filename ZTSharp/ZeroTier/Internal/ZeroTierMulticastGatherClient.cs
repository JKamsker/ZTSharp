using System.Buffers.Binary;
using System.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierMulticastGatherClient
{
    private const int IndexPayload = ZeroTierPacketHeader.IndexPayload;

    private const int OkIndexInReVerb = ZeroTierPacketHeader.IndexPayload;
    private const int OkIndexInRePacketId = OkIndexInReVerb + 1;
    private const int OkIndexPayload = OkIndexInRePacketId + 8;

    public static async Task<(uint TotalKnown, NodeId[] Members)> GatherAsync(
        ZeroTierUdpTransport udp,
        NodeId rootNodeId,
        IPEndPoint rootEndpoint,
        byte[] rootKey,
        byte rootProtocolVersion,
        NodeId localNodeId,
        ulong networkId,
        ZeroTierMulticastGroup group,
        uint gatherLimit,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => await GatherAsync(
                udp,
                rootNodeId,
                rootEndpoint,
                rootKey,
                rootProtocolVersion,
                localNodeId,
                networkId,
                group,
                gatherLimit,
                inlineCom: default,
                timeout,
                cancellationToken)
            .ConfigureAwait(false);

    public static async Task<(uint TotalKnown, NodeId[] Members)> GatherAsync(
        ZeroTierUdpTransport udp,
        NodeId rootNodeId,
        IPEndPoint rootEndpoint,
        byte[] rootKey,
        byte rootProtocolVersion,
        NodeId localNodeId,
        ulong networkId,
        ZeroTierMulticastGroup group,
        uint gatherLimit,
        ReadOnlyMemory<byte> inlineCom,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(rootEndpoint);
        ArgumentNullException.ThrowIfNull(rootKey);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");
        }

        var payload = ZeroTierMulticastGatherCodec.EncodeRequestPayload(networkId, group, gatherLimit, inlineCom.Span);

        var packetId = ZeroTierPacketIdGenerator.GeneratePacketId();
        var header = new ZeroTierPacketHeader(
            PacketId: packetId,
            Destination: rootNodeId,
            Source: localNodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.MulticastGather);

        var packet = ZeroTierPacketCodec.Encode(header, payload);
        ZeroTierPacketCrypto.Armor(packet, ZeroTierPacketCrypto.SelectOutboundKey(rootKey, rootProtocolVersion), encryptPayload: true);
        var requestPacketId = BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(0, 8));
        await udp.SendAsync(rootEndpoint, packet, cancellationToken).ConfigureAwait(false);

        return await ZeroTierTimeouts
            .RunWithTimeoutAsync(timeout, operation: "MULTICAST_GATHER response", WaitForResponseAsync, cancellationToken)
            .ConfigureAwait(false);

        async ValueTask<(uint TotalKnown, NodeId[] Members)> WaitForResponseAsync(CancellationToken token)
        {
            while (true)
            {
                var datagram = await udp.ReceiveAsync(token).ConfigureAwait(false);

                var packetBytes = datagram.Payload;
                if (!ZeroTierPacketCodec.TryDecode(packetBytes, out var decoded))
                {
                    continue;
                }

                if (decoded.Header.Source != rootNodeId)
                {
                    continue;
                }

                if (!ZeroTierPacketCrypto.Dearmor(packetBytes, rootKey))
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
                if (verb == ZeroTierVerb.Error)
                {
                    if (packetBytes.Length < IndexPayload + 1 + 8 + 1)
                    {
                        continue;
                    }

                    var errorInReVerb = (ZeroTierVerb)(packetBytes[IndexPayload] & 0x1F);
                    if (errorInReVerb != ZeroTierVerb.MulticastGather)
                    {
                        continue;
                    }

                    var errorInRePacketId = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(IndexPayload + 1, 8));
                    if (errorInRePacketId != requestPacketId)
                    {
                        continue;
                    }

                    var errorCode = packetBytes[IndexPayload + 1 + 8];
                    ulong? errorNetworkId = null;
                    if (packetBytes.Length >= IndexPayload + 1 + 8 + 1 + 8)
                    {
                        errorNetworkId = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(IndexPayload + 1 + 8 + 1, 8));
                    }

                    throw new InvalidOperationException(ZeroTierErrorFormatting.FormatError(errorInReVerb, errorCode, errorNetworkId));
                }

                if (verb != ZeroTierVerb.Ok)
                {
                    continue;
                }

                if (packetBytes.Length < OkIndexPayload)
                {
                    continue;
                }

                var inReVerb = (ZeroTierVerb)(packetBytes[OkIndexInReVerb] & 0x1F);
                if (inReVerb != ZeroTierVerb.MulticastGather)
                {
                    continue;
                }

                var inRePacketId = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(OkIndexInRePacketId, 8));
                if (inRePacketId != requestPacketId)
                {
                    continue;
                }

                if (!ZeroTierMulticastGatherCodec.TryParseOkPayload(
                        packetBytes.AsSpan(OkIndexPayload),
                        out var okNetworkId,
                        out _,
                        out var totalKnown,
                        out var members) ||
                    okNetworkId != networkId)
                {
                    continue;
                }

                return (totalKnown, members);
            }
        }
    }

}
