using System.Buffers.Binary;
using System.Net;

namespace JKamsker.LibZt.ZeroTier.Net;

internal static class Ipv6Codec
{
    public const byte Version = 6;
    public const int HeaderLength = 40;

    public static byte[] Encode(
        IPAddress source,
        IPAddress destination,
        byte nextHeader,
        ReadOnlySpan<byte> payload,
        byte hopLimit = 64,
        byte trafficClass = 0,
        uint flowLabel = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        if (source.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 ||
            destination.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "Only IPv6 is supported.");
        }

        if (flowLabel > 0xFFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(flowLabel), flowLabel, "Flow label must be <= 0xFFFFF.");
        }

        if (payload.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Payload too large.");
        }

        var packet = new byte[HeaderLength + payload.Length];
        var span = packet.AsSpan();

        var vtf = ((uint)Version << 28) | ((uint)trafficClass << 20) | (flowLabel & 0xFFFFF);
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0, 4), vtf);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(4, 2), (ushort)payload.Length);
        span[6] = nextHeader;
        span[7] = hopLimit;
        source.GetAddressBytes().CopyTo(span.Slice(8, 16));
        destination.GetAddressBytes().CopyTo(span.Slice(24, 16));

        payload.CopyTo(span.Slice(HeaderLength));
        return packet;
    }

    public static bool TryParse(
        ReadOnlySpan<byte> packet,
        out IPAddress source,
        out IPAddress destination,
        out byte nextHeader,
        out byte hopLimit,
        out ReadOnlySpan<byte> payload)
    {
        source = IPAddress.None;
        destination = IPAddress.None;
        nextHeader = 0;
        hopLimit = 0;
        payload = default;

        if (packet.Length < HeaderLength)
        {
            return false;
        }

        var version = packet[0] >> 4;
        if (version != Version)
        {
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(4, 2));
        if (packet.Length < HeaderLength + payloadLength)
        {
            return false;
        }

        nextHeader = packet[6];
        hopLimit = packet[7];
        source = new IPAddress(packet.Slice(8, 16));
        destination = new IPAddress(packet.Slice(24, 16));
        payload = packet.Slice(HeaderLength, payloadLength);
        return true;
    }
}

