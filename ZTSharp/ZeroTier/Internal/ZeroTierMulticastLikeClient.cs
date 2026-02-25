using System.Buffers.Binary;
using System.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierMulticastLikeClient
{
    public static async Task SendAsync(
        ZeroTierUdpTransport udp,
        NodeId rootNodeId,
        IPEndPoint rootEndpoint,
        byte[] rootKey,
        byte rootProtocolVersion,
        NodeId localNodeId,
        ulong networkId,
        IReadOnlyList<ZeroTierMulticastGroup> groups,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(rootEndpoint);
        ArgumentNullException.ThrowIfNull(rootKey);
        ArgumentNullException.ThrowIfNull(groups);

        if (groups.Count == 0)
        {
            return;
        }

        var payload = new byte[18 * groups.Count];
        var span = payload.AsSpan();

        var offset = 0;
        foreach (var group in groups)
        {
            BinaryPrimitives.WriteUInt64BigEndian(span.Slice(offset, 8), networkId);
            offset += 8;
            group.Mac.CopyTo(span.Slice(offset, 6));
            offset += 6;
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(offset, 4), group.Adi);
            offset += 4;
        }

        var packetId = ZeroTierPacketIdGenerator.GeneratePacketId();
        var header = new ZeroTierPacketHeader(
            PacketId: packetId,
            Destination: rootNodeId,
            Source: localNodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.MulticastLike);

        var packet = ZeroTierPacketCodec.Encode(header, payload);
        ZeroTierPacketCrypto.Armor(packet, ZeroTierPacketCrypto.SelectOutboundKey(rootKey, rootProtocolVersion), encryptPayload: true);
        await udp.SendAsync(rootEndpoint, packet, cancellationToken).ConfigureAwait(false);
    }

}
