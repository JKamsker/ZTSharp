namespace JKamsker.LibZt.ZeroTier.Protocol;

internal static class ZtZeroTierPacketCompression
{
    // ZT_PROTO_MAX_PACKET_LENGTH = ZT_MAX_PACKET_FRAGMENTS (7) * ZT_DEFAULT_PHYSMTU (1432) = 10024
    private const int MaxPacketLength = 10024;
    private const int IndexVerb = 27;
    private const int IndexPayload = 28;

    public static bool TryUncompress(ReadOnlySpan<byte> packet, out byte[] uncompressedPacket)
    {
        if (packet.Length < IndexPayload)
        {
            uncompressedPacket = Array.Empty<byte>();
            return false;
        }

        if ((packet[IndexVerb] & ZtZeroTierPacketHeader.VerbFlagCompressed) == 0)
        {
            uncompressedPacket = packet.ToArray();
            return true;
        }

        var maxPayload = MaxPacketLength - IndexPayload;
        var payload = new byte[maxPayload];
        if (!ZtZeroTierLz4.TryDecompress(packet.Slice(IndexPayload), payload, out var payloadLength))
        {
            uncompressedPacket = Array.Empty<byte>();
            return false;
        }

        var result = new byte[IndexPayload + payloadLength];
        packet.Slice(0, IndexPayload).CopyTo(result);
        result[IndexVerb] &= unchecked((byte)~ZtZeroTierPacketHeader.VerbFlagCompressed);
        payload.AsSpan(0, payloadLength).CopyTo(result.AsSpan(IndexPayload));
        uncompressedPacket = result;
        return true;
    }
}

