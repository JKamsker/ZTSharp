using System.Buffers.Binary;

namespace JKamsker.LibZt.ZeroTier.Protocol;

internal static class ZtZeroTierFrameCodec
{
    public const ushort EtherTypeIpv4 = 0x0800;
    public const ushort EtherTypeIpv6 = 0x86DD;

    public static byte[] EncodeFramePayload(ulong networkId, ushort etherType, ReadOnlySpan<byte> frame)
    {
        var payload = new byte[8 + 2 + frame.Length];
        var span = payload.AsSpan();
        BinaryPrimitives.WriteUInt64BigEndian(span.Slice(0, 8), networkId);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(8, 2), etherType);
        frame.CopyTo(span.Slice(10));
        return payload;
    }

    public static byte[] EncodeExtFramePayload(
        ulong networkId,
        byte flags,
        ReadOnlySpan<byte> inlineCom,
        ZtZeroTierMac to,
        ZtZeroTierMac from,
        ushort etherType,
        ReadOnlySpan<byte> frame)
    {
        var payload = new byte[8 + 1 + inlineCom.Length + 6 + 6 + 2 + frame.Length];
        var span = payload.AsSpan();

        BinaryPrimitives.WriteUInt64BigEndian(span.Slice(0, 8), networkId);
        span[8] = flags;

        var offset = 9;
        inlineCom.CopyTo(span.Slice(offset));
        offset += inlineCom.Length;

        to.CopyTo(span.Slice(offset, 6));
        offset += 6;

        from.CopyTo(span.Slice(offset, 6));
        offset += 6;

        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(offset, 2), etherType);
        offset += 2;

        frame.CopyTo(span.Slice(offset));
        return payload;
    }

    public static bool TryParseFramePayload(
        ReadOnlySpan<byte> payload,
        out ulong networkId,
        out ushort etherType,
        out ReadOnlySpan<byte> frame)
    {
        networkId = 0;
        etherType = 0;
        frame = default;

        if (payload.Length < 8 + 2)
        {
            return false;
        }

        networkId = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(0, 8));
        etherType = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(8, 2));
        frame = payload.Slice(10);
        return true;
    }

    public static bool TryParseExtFramePayload(
        ReadOnlySpan<byte> payload,
        out ulong networkId,
        out byte flags,
        out ReadOnlySpan<byte> inlineCom,
        out ZtZeroTierMac to,
        out ZtZeroTierMac from,
        out ushort etherType,
        out ReadOnlySpan<byte> frame)
    {
        networkId = 0;
        flags = 0;
        inlineCom = default;
        to = default;
        from = default;
        etherType = 0;
        frame = default;

        if (payload.Length < 8 + 1 + 6 + 6 + 2)
        {
            return false;
        }

        networkId = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(0, 8));
        flags = payload[8];

        var offset = 9;
        if ((flags & 0x01) != 0)
        {
            if (!ZtZeroTierCertificateOfMembershipCodec.TryGetSerializedLength(payload.Slice(offset), out var comLen))
            {
                return false;
            }

            inlineCom = payload.Slice(offset, comLen);
            offset += comLen;
        }

        if (payload.Length < offset + 6 + 6 + 2)
        {
            return false;
        }

        to = ZtZeroTierMac.Read(payload.Slice(offset, 6));
        offset += 6;

        from = ZtZeroTierMac.Read(payload.Slice(offset, 6));
        offset += 6;

        etherType = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
        offset += 2;

        frame = payload.Slice(offset);
        return true;
    }
}
