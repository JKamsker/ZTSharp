using System.Buffers.Binary;
using System.Net;
using ZTSharp.ZeroTier.Internal;

namespace ZTSharp.ZeroTier.Net;

internal static class Ipv6Codec
{
    public const byte Version = 6;
    public const int HeaderLength = 40;
    private const int MaxExtensionHeaderChain = 8;

    public static bool IsExtensionHeader(byte nextHeader)
        => nextHeader is 0 or 43 or 44 or 50 or 51 or 60 or 135 or 139 or 140;

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

    public static bool TryParseTransportPayload(
        ReadOnlySpan<byte> packet,
        out IPAddress source,
        out IPAddress destination,
        out byte protocol,
        out byte hopLimit,
        out ReadOnlySpan<byte> transportPayload)
    {
        transportPayload = default;
        protocol = 0;

        if (!TryParse(packet, out source, out destination, out var nextHeader, out hopLimit, out var payload))
        {
            return false;
        }

        return TryWalkExtensionHeaders(payload, nextHeader, out protocol, out transportPayload, out _);
    }

    public static bool TryParseTransportPayload(
        ReadOnlySpan<byte> packet,
        out IPAddress source,
        out IPAddress destination,
        out byte protocol,
        out byte hopLimit,
        out ReadOnlySpan<byte> transportPayload,
        out int transportPayloadOffset)
    {
        transportPayload = default;
        transportPayloadOffset = 0;
        protocol = 0;

        if (!TryParse(packet, out source, out destination, out var nextHeader, out hopLimit, out var payload))
        {
            return false;
        }

        if (!TryWalkExtensionHeaders(payload, nextHeader, out protocol, out transportPayload, out var offsetFromPayload))
        {
            return false;
        }

        transportPayloadOffset = HeaderLength + offsetFromPayload;
        return true;
    }

    private static bool TryWalkExtensionHeaders(
        ReadOnlySpan<byte> payload,
        byte nextHeader,
        out byte protocol,
        out ReadOnlySpan<byte> transportPayload,
        out int transportPayloadOffsetFromPayload)
    {
        protocol = nextHeader;
        transportPayload = payload;
        transportPayloadOffsetFromPayload = 0;

        var offset = 0;
        for (var i = 0; i < MaxExtensionHeaderChain && IsExtensionHeader(protocol); i++)
        {
            var remaining = payload.Length - offset;
            if (remaining < 2)
            {
                return false;
            }

            if (protocol is 0 or 43 or 60 or 135 or 139 or 140)
            {
                if (remaining < 8)
                {
                    return false;
                }

                var headerNext = payload[offset];
                var hdrExtLen = payload[offset + 1];
                var headerLength = (hdrExtLen + 1) * 8;
                if (headerLength > remaining)
                {
                    return false;
                }

                offset += headerLength;
                protocol = headerNext;
                continue;
            }

            if (protocol == 44)
            {
                if (remaining < 8)
                {
                    return false;
                }

                var headerNext = payload[offset];
                var fragmentOffsetAndFlags = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset + 2, 2));
                var fragmentOffset = (fragmentOffsetAndFlags >> 3) & 0x1FFF;
                var reservedBits = fragmentOffsetAndFlags & 0x6;
                var moreFragments = (fragmentOffsetAndFlags & 0x1) != 0;
                if (fragmentOffset != 0 || moreFragments || reservedBits != 0)
                {
                    if (ZeroTierTrace.Enabled)
                    {
                        ZeroTierTrace.WriteLine("[zerotier] Drop: IPv6 fragments are not supported.");
                    }

                    return false;
                }

                offset += 8;
                protocol = headerNext;
                continue;
            }

            if (protocol == 51)
            {
                if (remaining < 8)
                {
                    return false;
                }

                var headerNext = payload[offset];
                var payloadLen32 = payload[offset + 1];
                var headerLength = (payloadLen32 + 2) * 4;
                if (headerLength > remaining)
                {
                    return false;
                }

                offset += headerLength;
                protocol = headerNext;
                continue;
            }

            if (protocol == 50)
            {
                return false;
            }
        }

        transportPayloadOffsetFromPayload = offset;
        transportPayload = payload.Slice(offset);
        return !IsExtensionHeader(protocol);
    }
}
