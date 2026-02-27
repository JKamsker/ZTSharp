namespace ZTSharp.ZeroTier.Protocol;

internal static class ZeroTierPacketCompression
{
    public static bool TryUncompress(ReadOnlySpan<byte> packet, out byte[] uncompressedPacket)
    {
        if (packet.Length > ZeroTierProtocolLimits.MaxPacketBytes)
        {
            uncompressedPacket = Array.Empty<byte>();
            return false;
        }

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

        var maxPayload = ZeroTierProtocolLimits.MaxPacketBytes - ZeroTierPacketHeader.IndexPayload;
        var payload = System.Buffers.ArrayPool<byte>.Shared.Rent(maxPayload);
        int payloadLength;
        try
        {
            if (!ZeroTierLz4.TryDecompress(packet.Slice(ZeroTierPacketHeader.IndexPayload), payload.AsSpan(0, maxPayload), out payloadLength))
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
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(payload, clearArray: true);
        }
    }
}
