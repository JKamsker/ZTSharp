using System.Buffers.Binary;
using System.Net;

namespace ZTSharp.ZeroTier.Protocol;

internal readonly record struct ZeroTierRendezvous(
    byte Flags,
    NodeId With,
    IPEndPoint Endpoint);

internal static class ZeroTierRendezvousCodec
{
    private const int AddressLength = 5;
    private const int MinPayloadLength = 1 + AddressLength + 2 + 1;

    public static bool TryParse(ReadOnlySpan<byte> payload, out ZeroTierRendezvous rendezvous)
    {
        if (payload.Length < MinPayloadLength)
        {
            rendezvous = default;
            return false;
        }

        var flags = payload[0];
        var with = new NodeId(ZeroTierBinaryPrimitives.ReadUInt40BigEndian(payload.Slice(1, AddressLength)));
        var port = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(1 + AddressLength, 2));
        var addrLen = payload[1 + AddressLength + 2];

        if (port == 0 || (addrLen != 4 && addrLen != 16))
        {
            rendezvous = default;
            return false;
        }

        if (payload.Length < MinPayloadLength + addrLen)
        {
            rendezvous = default;
            return false;
        }

        var address = new IPAddress(payload.Slice(MinPayloadLength, addrLen).ToArray());
        rendezvous = new ZeroTierRendezvous(flags, with, new IPEndPoint(address, port));
        return true;
    }

}
