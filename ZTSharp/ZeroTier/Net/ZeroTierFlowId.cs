using System.Buffers.Binary;

namespace ZTSharp.ZeroTier.Net;

internal static class ZeroTierFlowId
{
    public static uint Derive(ReadOnlySpan<byte> ipPacket)
    {
        if (Ipv4Codec.TryParse(ipPacket, out var src, out var dst, out var protocol, out var payload))
        {
            return DeriveFromTransportTuple(src.GetAddressBytes(), dst.GetAddressBytes(), protocol, payload);
        }

        if (Ipv6Codec.TryParse(ipPacket, out src, out dst, out var nextHeader, out _, out payload))
        {
            return DeriveFromTransportTuple(src.GetAddressBytes(), dst.GetAddressBytes(), nextHeader, payload);
        }

        return 0;
    }

    private static uint DeriveFromTransportTuple(
        ReadOnlySpan<byte> sourceAddress,
        ReadOnlySpan<byte> destinationAddress,
        byte protocol,
        ReadOnlySpan<byte> transportPayload)
    {
        ushort srcPort = 0;
        ushort dstPort = 0;

        if (protocol == TcpCodec.ProtocolNumber)
        {
            _ = TcpCodec.TryParse(transportPayload, out srcPort, out dstPort, out _, out _, out _, out _, out _);
        }
        else if (protocol == UdpCodec.ProtocolNumber)
        {
            _ = UdpCodec.TryParse(transportPayload, out srcPort, out dstPort, out _);
        }
        else
        {
            return 0;
        }

        var swapped = CompareEndpoints(sourceAddress, srcPort, destinationAddress, dstPort) > 0;
        var aAddress = swapped ? destinationAddress : sourceAddress;
        var aPort = swapped ? dstPort : srcPort;
        var bAddress = swapped ? sourceAddress : destinationAddress;
        var bPort = swapped ? srcPort : dstPort;

        var hash = Fnva32.OffsetBasis;
        hash = Fnva32.AddByte(hash, protocol);
        hash = Fnva32.AddBytes(hash, aAddress);
        hash = Fnva32.AddUInt16(hash, aPort);
        hash = Fnva32.AddBytes(hash, bAddress);
        hash = Fnva32.AddUInt16(hash, bPort);
        return hash;
    }

    private static int CompareEndpoints(
        ReadOnlySpan<byte> aAddress,
        ushort aPort,
        ReadOnlySpan<byte> bAddress,
        ushort bPort)
    {
        var cmp = aAddress.SequenceCompareTo(bAddress);
        if (cmp != 0)
        {
            return cmp;
        }

        return aPort.CompareTo(bPort);
    }

    private static class Fnva32
    {
        public const uint OffsetBasis = 2166136261u;
        private const uint Prime = 16777619u;

        public static uint AddByte(uint hash, byte value)
        {
            hash ^= value;
            return unchecked(hash * Prime);
        }

        public static uint AddBytes(uint hash, ReadOnlySpan<byte> values)
        {
            for (var i = 0; i < values.Length; i++)
            {
                hash = AddByte(hash, values[i]);
            }

            return hash;
        }

        public static uint AddUInt16(uint hash, ushort value)
        {
            Span<byte> buf = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buf, value);
            return AddBytes(hash, buf);
        }
    }
}
