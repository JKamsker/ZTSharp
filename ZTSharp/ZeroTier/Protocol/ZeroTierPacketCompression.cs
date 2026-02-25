namespace ZTSharp.ZeroTier.Protocol;

internal static class ZeroTierPacketCompression
{
    // ZT_PROTO_MAX_PACKET_LENGTH = ZT_MAX_PACKET_FRAGMENTS (7) * ZT_DEFAULT_PHYSMTU (1432) = 10024
    private const int MaxPacketLength = 10024;

    public static bool TryUncompress(ReadOnlySpan<byte> packet, out byte[] uncompressedPacket)
    {
        if (packet.Length < ZeroTierPacketHeader.IndexPayload)
        {
            uncompressedPacket = Array.Empty<byte>();
            return false;
        }

        if ((packet[ZeroTierPacketHeader.IndexVerb] & ZeroTierPacketHeader.VerbFlagCompressed) == 0)
        {
            uncompressedPacket = packet.ToArray();
            return true;
        }

        var maxPayload = MaxPacketLength - ZeroTierPacketHeader.IndexPayload;
        var payload = new byte[maxPayload];
        if (!ZeroTierLz4.TryDecompress(packet.Slice(ZeroTierPacketHeader.IndexPayload), payload, out var payloadLength))
        {
            uncompressedPacket = Array.Empty<byte>();
            return false;
        }

        var result = new byte[ZeroTierPacketHeader.IndexPayload + payloadLength];
        packet.Slice(0, ZeroTierPacketHeader.IndexPayload).CopyTo(result);
        result[ZeroTierPacketHeader.IndexVerb] &= unchecked((byte)~ZeroTierPacketHeader.VerbFlagCompressed);
        payload.AsSpan(0, payloadLength).CopyTo(result.AsSpan(ZeroTierPacketHeader.IndexPayload));
        uncompressedPacket = result;
        return true;
    }
}
