using System.Buffers.Binary;
using System.Net;

namespace JKamsker.LibZt.ZeroTier.Net;

internal static class ZtIpv4Codec
{
    public const byte Version = 4;
    public const int MinimumHeaderLength = 20;

    public static byte[] Encode(
        IPAddress source,
        IPAddress destination,
        byte protocol,
        ReadOnlySpan<byte> payload,
        ushort identification,
        byte ttl = 64)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        if (source.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            destination.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "Only IPv4 is supported.");
        }

        if (payload.Length > ushort.MaxValue - MinimumHeaderLength)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Payload too large.");
        }

        var totalLength = MinimumHeaderLength + payload.Length;
        var packet = new byte[totalLength];
        var span = packet.AsSpan();

        span[0] = (byte)((Version << 4) | 5); // V=4, IHL=5 (20 bytes)
        span[1] = 0; // DSCP/ECN
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(2, 2), (ushort)totalLength);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(4, 2), identification);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(6, 2), 0); // flags/fragment offset
        span[8] = ttl;
        span[9] = protocol;
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(10, 2), 0); // checksum placeholder
        source.GetAddressBytes().CopyTo(span.Slice(12, 4));
        destination.GetAddressBytes().CopyTo(span.Slice(16, 4));

        payload.CopyTo(span.Slice(MinimumHeaderLength));

        var checksum = ComputeHeaderChecksum(span.Slice(0, MinimumHeaderLength));
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(10, 2), checksum);

        return packet;
    }

    public static bool TryParse(
        ReadOnlySpan<byte> packet,
        out IPAddress source,
        out IPAddress destination,
        out byte protocol,
        out ReadOnlySpan<byte> payload)
    {
        source = IPAddress.None;
        destination = IPAddress.None;
        protocol = 0;
        payload = default;

        if (packet.Length < MinimumHeaderLength)
        {
            return false;
        }

        var version = packet[0] >> 4;
        if (version != Version)
        {
            return false;
        }

        var headerLength = (packet[0] & 0x0F) * 4;
        if (headerLength < MinimumHeaderLength || headerLength > packet.Length)
        {
            return false;
        }

        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        if (totalLength < headerLength || totalLength > packet.Length)
        {
            return false;
        }

        protocol = packet[9];
        source = new IPAddress(packet.Slice(12, 4));
        destination = new IPAddress(packet.Slice(16, 4));
        payload = packet.Slice(headerLength, totalLength - headerLength);
        return true;
    }

    private static ushort ComputeHeaderChecksum(ReadOnlySpan<byte> header)
    {
        var sum = 0u;
        for (var i = 0; i < header.Length; i += 2)
        {
            var word = (i + 1 < header.Length)
                ? BinaryPrimitives.ReadUInt16BigEndian(header.Slice(i, 2))
                : (ushort)(header[i] << 8);
            sum += word;
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }
}

