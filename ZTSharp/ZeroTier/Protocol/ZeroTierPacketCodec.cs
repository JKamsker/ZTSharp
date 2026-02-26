using System.Buffers.Binary;

namespace ZTSharp.ZeroTier.Protocol;

internal static class ZeroTierPacketCodec
{
    private const int UInt64Length = 8;
    private const int AddressLength = 5;

    public static bool TryDecode(ReadOnlyMemory<byte> packet, out ZeroTierPacketView decoded)
    {
        if (packet.Length < ZeroTierPacketHeader.Length)
        {
            decoded = default;
            return false;
        }

        var span = packet.Span;
        var header = new ZeroTierPacketHeader(
            PacketId: ReadUInt64(span, ZeroTierPacketHeader.IndexPacketId),
            Destination: new NodeId(ZeroTierBinaryPrimitives.ReadUInt40BigEndian(span.Slice(ZeroTierPacketHeader.IndexDestination, AddressLength))),
            Source: new NodeId(ZeroTierBinaryPrimitives.ReadUInt40BigEndian(span.Slice(ZeroTierPacketHeader.IndexSource, AddressLength))),
            Flags: span[ZeroTierPacketHeader.IndexFlags],
            Mac: ReadUInt64(span, ZeroTierPacketHeader.IndexMac),
            VerbRaw: span[ZeroTierPacketHeader.IndexVerb]);

        decoded = new ZeroTierPacketView(packet, header);
        return true;
    }

    public static byte[] Encode(in ZeroTierPacketHeader header, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[ZeroTierPacketHeader.IndexPayload + payload.Length];
        var span = packet.AsSpan();

        WriteUInt64(span, ZeroTierPacketHeader.IndexPacketId, header.PacketId);
        ZeroTierBinaryPrimitives.WriteUInt40BigEndian(span.Slice(ZeroTierPacketHeader.IndexDestination, AddressLength), header.Destination.Value);
        ZeroTierBinaryPrimitives.WriteUInt40BigEndian(span.Slice(ZeroTierPacketHeader.IndexSource, AddressLength), header.Source.Value);
        span[ZeroTierPacketHeader.IndexFlags] = header.Flags;
        WriteUInt64(span, ZeroTierPacketHeader.IndexMac, header.Mac);
        span[ZeroTierPacketHeader.IndexVerb] = header.VerbRaw;
        payload.CopyTo(span.Slice(ZeroTierPacketHeader.IndexPayload));

        return packet;
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> span, int index)
    {
        return BinaryPrimitives.ReadUInt64BigEndian(span.Slice(index, UInt64Length));
    }

    private static void WriteUInt64(Span<byte> span, int index, ulong value)
    {
        BinaryPrimitives.WriteUInt64BigEndian(span.Slice(index, UInt64Length), value);
    }

}
