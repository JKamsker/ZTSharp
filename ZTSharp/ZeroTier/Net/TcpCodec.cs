using System.Buffers.Binary;
using System.Net;

namespace ZTSharp.ZeroTier.Net;

internal static class TcpCodec
{
    public const byte ProtocolNumber = 0x06;
    public const int MinimumHeaderLength = 20;

    [Flags]
    public enum Flags : byte
    {
        Fin = 0x01,
        Syn = 0x02,
        Rst = 0x04,
        Psh = 0x08,
        Ack = 0x10,
        Urg = 0x20,
        Ece = 0x40,
        Cwr = 0x80
    }

    public static byte[] Encode(
        IPAddress sourceIp,
        IPAddress destinationIp,
        ushort sourcePort,
        ushort destinationPort,
        uint sequenceNumber,
        uint acknowledgmentNumber,
        Flags flags,
        ushort windowSize,
        ReadOnlySpan<byte> options,
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

        if ((options.Length % 4) != 0)
        {
            throw new ArgumentException("TCP options length must be a multiple of 4.", nameof(options));
        }

        var headerLength = MinimumHeaderLength + options.Length;
        if (headerLength > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "TCP header too large.");
        }

        var segment = new byte[headerLength + payload.Length];
        var span = segment.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(0, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(2, 2), destinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(4, 4), sequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(8, 4), acknowledgmentNumber);

        span[12] = (byte)((headerLength / 4) << 4); // data offset (high 4 bits)
        span[13] = (byte)flags;

        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(14, 2), windowSize);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(16, 2), 0); // checksum placeholder
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(18, 2), 0); // urgent pointer

        options.CopyTo(span.Slice(MinimumHeaderLength, options.Length));
        payload.CopyTo(span.Slice(headerLength));

        var checksum = ComputeChecksum(sourceIp, destinationIp, span);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(16, 2), checksum);

        return segment;
    }

    public static bool TryParse(
        ReadOnlySpan<byte> segment,
        out ushort sourcePort,
        out ushort destinationPort,
        out uint sequenceNumber,
        out uint acknowledgmentNumber,
        out Flags flags,
        out ushort windowSize,
        out ReadOnlySpan<byte> payload)
    {
        sourcePort = 0;
        destinationPort = 0;
        sequenceNumber = 0;
        acknowledgmentNumber = 0;
        flags = 0;
        windowSize = 0;
        payload = default;

        if (segment.Length < MinimumHeaderLength)
        {
            return false;
        }

        var dataOffset = (segment[12] >> 4) * 4;
        if (dataOffset < MinimumHeaderLength || dataOffset > segment.Length)
        {
            return false;
        }

        sourcePort = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(0, 2));
        destinationPort = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(2, 2));
        sequenceNumber = BinaryPrimitives.ReadUInt32BigEndian(segment.Slice(4, 4));
        acknowledgmentNumber = BinaryPrimitives.ReadUInt32BigEndian(segment.Slice(8, 4));
        flags = (Flags)segment[13];
        windowSize = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(14, 2));
        payload = segment.Slice(dataOffset);
        return true;
    }

    public static bool TryParseWithChecksum(
        IPAddress sourceIp,
        IPAddress destinationIp,
        ReadOnlySpan<byte> segment,
        out ushort sourcePort,
        out ushort destinationPort,
        out uint sequenceNumber,
        out uint acknowledgmentNumber,
        out Flags flags,
        out ushort windowSize,
        out ReadOnlySpan<byte> payload)
    {
        if (!TryParse(segment, out sourcePort, out destinationPort, out sequenceNumber, out acknowledgmentNumber, out flags, out windowSize, out payload))
        {
            return false;
        }

        try
        {
            return ComputeChecksum(sourceIp, destinationIp, segment) == 0;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    public static byte[] EncodeMssOption(ushort mss)
    {
        var option = new byte[4];
        option[0] = 2; // kind MSS
        option[1] = 4; // length
        BinaryPrimitives.WriteUInt16BigEndian(option.AsSpan(2, 2), mss);
        return option;
    }

    private static ushort ComputeChecksum(IPAddress sourceIp, IPAddress destinationIp, ReadOnlySpan<byte> tcpSegment)
    {
        ArgumentNullException.ThrowIfNull(sourceIp);
        ArgumentNullException.ThrowIfNull(destinationIp);

        Span<byte> srcBytes = stackalloc byte[16];
        Span<byte> dstBytes = stackalloc byte[16];

        if (!sourceIp.TryWriteBytes(srcBytes, out var srcWritten) ||
            !destinationIp.TryWriteBytes(dstBytes, out var dstWritten) ||
            srcWritten != dstWritten)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationIp), "Source and destination address families must match.");
        }

        return ComputeChecksum(srcBytes.Slice(0, srcWritten), dstBytes.Slice(0, dstWritten), tcpSegment);
    }

    private static ushort ComputeChecksum(ReadOnlySpan<byte> sourceIp, ReadOnlySpan<byte> destinationIp, ReadOnlySpan<byte> tcpSegment)
    {
        var sum = 0u;

        // pseudo header
        if (sourceIp.Length == 4)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(sourceIp.Slice(0, 2));
            sum += BinaryPrimitives.ReadUInt16BigEndian(sourceIp.Slice(2, 2));
            sum += BinaryPrimitives.ReadUInt16BigEndian(destinationIp.Slice(0, 2));
            sum += BinaryPrimitives.ReadUInt16BigEndian(destinationIp.Slice(2, 2));
            sum += ProtocolNumber;
            sum += (ushort)tcpSegment.Length;
        }
        else if (sourceIp.Length == 16)
        {
            for (var i = 0; i < 16; i += 2)
            {
                sum += BinaryPrimitives.ReadUInt16BigEndian(sourceIp.Slice(i, 2));
                sum += BinaryPrimitives.ReadUInt16BigEndian(destinationIp.Slice(i, 2));
            }

            var upperLayerLength = (uint)tcpSegment.Length;
            sum += (upperLayerLength >> 16) & 0xFFFF;
            sum += upperLayerLength & 0xFFFF;
            sum += ProtocolNumber;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(sourceIp), $"Unsupported address family length: {sourceIp.Length}.");
        }

        // tcp header+payload
        for (var i = 0; i < tcpSegment.Length; i += 2)
        {
            var word = (i + 1 < tcpSegment.Length)
                ? BinaryPrimitives.ReadUInt16BigEndian(tcpSegment.Slice(i, 2))
                : (ushort)(tcpSegment[i] << 8);
            sum += word;
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }
}
