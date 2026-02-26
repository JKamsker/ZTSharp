using System.Buffers.Binary;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierArp
{
    public static bool TryParseRequest(
        ReadOnlySpan<byte> packet,
        out ReadOnlySpan<byte> senderMac,
        out ReadOnlySpan<byte> senderIp,
        out ReadOnlySpan<byte> targetIp)
    {
        senderMac = default;
        senderIp = default;
        targetIp = default;

        if (packet.Length < 28)
        {
            return false;
        }

        var htype = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(0, 2));
        var ptype = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        var hlen = packet[4];
        var plen = packet[5];
        var oper = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2));

        if (htype != 1 || ptype != ZeroTierFrameCodec.EtherTypeIpv4 || hlen != 6 || plen != 4 || oper != 1)
        {
            return false;
        }

        senderMac = packet.Slice(8, 6);
        senderIp = packet.Slice(14, 4);
        targetIp = packet.Slice(24, 4);
        return true;
    }

    public static byte[] BuildReply(
        ZeroTierMac localMac,
        ReadOnlySpan<byte> localManagedIpV4,
        ReadOnlySpan<byte> requesterMac,
        ReadOnlySpan<byte> requesterIp)
    {
        if (localManagedIpV4.Length != 4)
        {
            throw new ArgumentOutOfRangeException(nameof(localManagedIpV4), "Local managed IPv4 must be 4 bytes.");
        }

        var reply = new byte[28];
        var span = reply.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(0, 2), 1); // HTYPE ethernet
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(2, 2), ZeroTierFrameCodec.EtherTypeIpv4); // PTYPE IPv4
        span[4] = 6; // HLEN
        span[5] = 4; // PLEN
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(6, 2), 2); // OPER reply

        Span<byte> localMacBytes = stackalloc byte[6];
        localMac.CopyTo(localMacBytes);

        localMacBytes.CopyTo(span.Slice(8, 6)); // SHA
        localManagedIpV4.CopyTo(span.Slice(14, 4)); // SPA
        requesterMac.CopyTo(span.Slice(18, 6)); // THA
        requesterIp.CopyTo(span.Slice(24, 4)); // TPA

        return reply;
    }
}

