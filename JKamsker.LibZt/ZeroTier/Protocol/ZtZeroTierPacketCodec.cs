using System.Buffers.Binary;

namespace JKamsker.LibZt.ZeroTier.Protocol;

internal static class ZtZeroTierPacketCodec
{
    private const int UInt64Length = 8;
    private const int AddressLength = 5;

    private const int IndexPacketId = 0;
    private const int IndexDestination = 8;
    private const int IndexSource = 13;
    private const int IndexFlags = 18;
    private const int IndexMac = 19;
    private const int IndexVerb = 27;
    private const int IndexPayload = 28;

    public static bool TryDecode(ReadOnlyMemory<byte> packet, out ZtZeroTierPacketView decoded)
    {
        if (packet.Length < ZtZeroTierPacketHeader.Length)
        {
            decoded = default;
            return false;
        }

        var span = packet.Span;
        var header = new ZtZeroTierPacketHeader(
            PacketId: ReadUInt64(span, IndexPacketId),
            Destination: new ZtNodeId(ReadUInt40(span.Slice(IndexDestination, AddressLength))),
            Source: new ZtNodeId(ReadUInt40(span.Slice(IndexSource, AddressLength))),
            Flags: span[IndexFlags],
            Mac: ReadUInt64(span, IndexMac),
            VerbRaw: span[IndexVerb]);

        decoded = new ZtZeroTierPacketView(packet, header);
        return true;
    }

    public static byte[] Encode(in ZtZeroTierPacketHeader header, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[IndexPayload + payload.Length];
        var span = packet.AsSpan();

        WriteUInt64(span, IndexPacketId, header.PacketId);
        WriteUInt40(span.Slice(IndexDestination, AddressLength), header.Destination.Value);
        WriteUInt40(span.Slice(IndexSource, AddressLength), header.Source.Value);
        span[IndexFlags] = header.Flags;
        WriteUInt64(span, IndexMac, header.Mac);
        span[IndexVerb] = header.VerbRaw;
        payload.CopyTo(span.Slice(IndexPayload));

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

    private static ulong ReadUInt40(ReadOnlySpan<byte> value)
    {
        if (value.Length < AddressLength)
        {
            throw new ArgumentException("Address must be at least 5 bytes.", nameof(value));
        }

        return
            ((ulong)value[0] << 32) |
            ((ulong)value[1] << 24) |
            ((ulong)value[2] << 16) |
            ((ulong)value[3] << 8) |
            value[4];
    }

    private static void WriteUInt40(Span<byte> destination, ulong value)
    {
        if (destination.Length < AddressLength)
        {
            throw new ArgumentException("Destination must be at least 5 bytes.", nameof(destination));
        }

        destination[0] = (byte)((value >> 32) & 0xFF);
        destination[1] = (byte)((value >> 24) & 0xFF);
        destination[2] = (byte)((value >> 16) & 0xFF);
        destination[3] = (byte)((value >> 8) & 0xFF);
        destination[4] = (byte)(value & 0xFF);
    }
}
