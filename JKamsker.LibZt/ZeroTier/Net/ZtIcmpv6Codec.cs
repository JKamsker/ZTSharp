using System.Buffers.Binary;
using System.Net;

namespace JKamsker.LibZt.ZeroTier.Net;

internal static class ZtIcmpv6Codec
{
    public const byte ProtocolNumber = 0x3A;
    public const int MinimumHeaderLength = 4;

    public static bool TryParse(
        ReadOnlySpan<byte> message,
        out byte type,
        out byte code,
        out ReadOnlySpan<byte> body)
    {
        type = 0;
        code = 0;
        body = default;

        if (message.Length < MinimumHeaderLength)
        {
            return false;
        }

        type = message[0];
        code = message[1];
        body = message.Slice(MinimumHeaderLength);
        return true;
    }

    public static ushort ComputeChecksum(IPAddress sourceIp, IPAddress destinationIp, ReadOnlySpan<byte> icmpMessage)
    {
        ArgumentNullException.ThrowIfNull(sourceIp);
        ArgumentNullException.ThrowIfNull(destinationIp);

        if (sourceIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 ||
            destinationIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceIp), "Only IPv6 is supported.");
        }

        var sum = 0u;

        // Pseudo header
        var src = sourceIp.GetAddressBytes();
        var dst = destinationIp.GetAddressBytes();

        for (var i = 0; i < 16; i += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(src.AsSpan(i, 2));
            sum += BinaryPrimitives.ReadUInt16BigEndian(dst.AsSpan(i, 2));
        }

        var upperLayerLength = (uint)icmpMessage.Length;
        sum += (upperLayerLength >> 16) & 0xFFFF;
        sum += upperLayerLength & 0xFFFF;
        sum += ProtocolNumber;

        // ICMPv6 message
        for (var i = 0; i < icmpMessage.Length; i += 2)
        {
            var word = (i + 1 < icmpMessage.Length)
                ? BinaryPrimitives.ReadUInt16BigEndian(icmpMessage.Slice(i, 2))
                : (ushort)(icmpMessage[i] << 8);
            sum += word;
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }
}

