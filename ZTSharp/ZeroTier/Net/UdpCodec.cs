using System.Buffers.Binary;
using System.Net;

namespace ZTSharp.ZeroTier.Net;

internal static class UdpCodec
{
    public const byte ProtocolNumber = 0x11;
    public const int HeaderLength = 8;

    public static byte[] Encode(
        IPAddress sourceIp,
        IPAddress destinationIp,
        ushort sourcePort,
        ushort destinationPort,
        ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(sourceIp);
        ArgumentNullException.ThrowIfNull(destinationIp);

        if (sourceIp.AddressFamily != destinationIp.AddressFamily)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationIp), "Source and destination address families must match.");
        }

        if (sourceIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork &&
            sourceIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceIp), "Only IPv4 and IPv6 are supported.");
        }

        if (payload.Length > ushort.MaxValue - HeaderLength)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Payload too large.");
        }

        var length = HeaderLength + payload.Length;
        var segment = new byte[length];
        var span = segment.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(0, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(2, 2), destinationPort);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(4, 2), (ushort)length);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(6, 2), 0); // checksum placeholder

        payload.CopyTo(span.Slice(HeaderLength));

        var checksum = ComputeChecksum(sourceIp, destinationIp, span);
        if (checksum == 0)
        {
            // For IPv4 UDP, a checksum of 0 means "no checksum". If the computed checksum is 0, it is transmitted as 0xFFFF.
            checksum = 0xFFFF;
        }

        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(6, 2), checksum);
        return segment;
    }

    public static bool TryParse(
        ReadOnlySpan<byte> segment,
        out ushort sourcePort,
        out ushort destinationPort,
        out ReadOnlySpan<byte> payload)
    {
        sourcePort = 0;
        destinationPort = 0;
        payload = default;

        if (segment.Length < HeaderLength)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(4, 2));
        if (length < HeaderLength || length > segment.Length)
        {
            return false;
        }

        sourcePort = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(0, 2));
        destinationPort = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(2, 2));
        payload = segment.Slice(HeaderLength, length - HeaderLength);
        return true;
    }

    private static ushort ComputeChecksum(IPAddress sourceIp, IPAddress destinationIp, ReadOnlySpan<byte> udpSegment)
    {
        var sum = 0u;

        // pseudo header
        if (sourceIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var src = sourceIp.GetAddressBytes();
            var dst = destinationIp.GetAddressBytes();

            sum += BinaryPrimitives.ReadUInt16BigEndian(src.AsSpan(0, 2));
            sum += BinaryPrimitives.ReadUInt16BigEndian(src.AsSpan(2, 2));
            sum += BinaryPrimitives.ReadUInt16BigEndian(dst.AsSpan(0, 2));
            sum += BinaryPrimitives.ReadUInt16BigEndian(dst.AsSpan(2, 2));
            sum += ProtocolNumber;
            sum += (ushort)udpSegment.Length;
        }
        else if (sourceIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var src = sourceIp.GetAddressBytes();
            var dst = destinationIp.GetAddressBytes();

            for (var i = 0; i < 16; i += 2)
            {
                sum += BinaryPrimitives.ReadUInt16BigEndian(src.AsSpan(i, 2));
                sum += BinaryPrimitives.ReadUInt16BigEndian(dst.AsSpan(i, 2));
            }

            var upperLayerLength = (uint)udpSegment.Length;
            sum += (upperLayerLength >> 16) & 0xFFFF;
            sum += upperLayerLength & 0xFFFF;
            sum += ProtocolNumber;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(sourceIp), $"Unsupported address family: {sourceIp.AddressFamily}.");
        }

        // udp header+payload
        for (var i = 0; i < udpSegment.Length; i += 2)
        {
            var word = (i + 1 < udpSegment.Length)
                ? BinaryPrimitives.ReadUInt16BigEndian(udpSegment.Slice(i, 2))
                : (ushort)(udpSegment[i] << 8);
            sum += word;
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }
}
