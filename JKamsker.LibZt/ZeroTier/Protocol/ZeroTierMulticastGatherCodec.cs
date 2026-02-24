using System.Buffers.Binary;

namespace JKamsker.LibZt.ZeroTier.Protocol;

internal static class ZeroTierMulticastGatherCodec
{
    public static byte[] EncodeRequestPayload(
        ulong networkId,
        in ZeroTierMulticastGroup group,
        uint gatherLimit,
        ReadOnlySpan<byte> inlineCom = default)
    {
        if (gatherLimit == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gatherLimit), gatherLimit, "Gather limit must be non-zero.");
        }

        var hasCom = !inlineCom.IsEmpty;
        var flags = hasCom ? (byte)0x01 : (byte)0x00;

        var payload = new byte[8 + 1 + 6 + 4 + 4 + (hasCom ? inlineCom.Length : 0)];
        var span = payload.AsSpan();
        BinaryPrimitives.WriteUInt64BigEndian(span.Slice(0, 8), networkId);
        span[8] = flags;
        group.Mac.CopyTo(span.Slice(9, 6));
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(15, 4), group.Adi);
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(19, 4), gatherLimit);

        if (hasCom)
        {
            inlineCom.CopyTo(span.Slice(23));
        }

        return payload;
    }

    public static bool TryParseOkPayload(
        ReadOnlySpan<byte> payload,
        out ulong networkId,
        out ZeroTierMulticastGroup group,
        out uint totalKnown,
        out NodeId[] members)
    {
        networkId = 0;
        group = default;
        totalKnown = 0;
        members = Array.Empty<NodeId>();

        if (payload.Length < 8 + 6 + 4 + 4 + 2)
        {
            return false;
        }

        networkId = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(0, 8));
        var mac = ZeroTierMac.Read(payload.Slice(8, 6));
        var adi = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(14, 4));
        group = new ZeroTierMulticastGroup(mac, adi);
        totalKnown = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(18, 4));
        var added = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(22, 2));

        var required = 24 + (added * 5);
        if (payload.Length < required)
        {
            return false;
        }

        var list = new NodeId[added];
        var p = 24;
        for (var i = 0; i < list.Length; i++)
        {
            var nodeId =
                ((ulong)payload[p] << 32) |
                ((ulong)payload[p + 1] << 24) |
                ((ulong)payload[p + 2] << 16) |
                ((ulong)payload[p + 3] << 8) |
                payload[p + 4];
            list[i] = new NodeId(nodeId);
            p += 5;
        }

        members = list;
        return true;
    }
}

