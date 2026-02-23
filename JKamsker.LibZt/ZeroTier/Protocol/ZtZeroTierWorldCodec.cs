using System.Buffers.Binary;
using System.Net;
using JKamsker.LibZt.ZeroTier.Internal;

namespace JKamsker.LibZt.ZeroTier.Protocol;

internal static class ZtZeroTierWorldCodec
{
    private const int MaxRoots = 4;
    private const int MaxStableEndpointsPerRoot = 32;

    public static ZtZeroTierWorld Decode(ReadOnlySpan<byte> data)
    {
        var offset = 0;

        var typeByte = ReadByte(data, ref offset);
        var type = typeByte switch
        {
            0 => ZtZeroTierWorldType.Null,
            1 => ZtZeroTierWorldType.Planet,
            127 => ZtZeroTierWorldType.Moon,
            _ => throw new FormatException($"Invalid world type: {typeByte}.")
        };

        var id = ReadUInt64(data, ref offset);
        var timestamp = ReadUInt64(data, ref offset);
        var updatesMustBeSignedBy = ReadBytes(data, ref offset, ZtZeroTierWorld.C25519PublicKeyLength).ToArray();
        var signature = ReadBytes(data, ref offset, ZtZeroTierWorld.C25519SignatureLength).ToArray();

        var numRoots = ReadByte(data, ref offset);
        if (numRoots > MaxRoots)
        {
            throw new FormatException($"World contains too many roots ({numRoots}).");
        }

        var roots = new List<ZtZeroTierWorldRoot>(numRoots);
        for (var i = 0; i < numRoots; i++)
        {
            var identity = ReadIdentity(data, ref offset);
            var numStableEndpoints = ReadByte(data, ref offset);
            if (numStableEndpoints > MaxStableEndpointsPerRoot)
            {
                throw new FormatException($"Root contains too many stable endpoints ({numStableEndpoints}).");
            }

            var stableEndpoints = new List<IPEndPoint>(numStableEndpoints);
            for (var j = 0; j < numStableEndpoints; j++)
            {
                if (TryReadInetEndpoint(data, ref offset, out var endpoint))
                {
                    stableEndpoints.Add(endpoint);
                }
            }

            roots.Add(new ZtZeroTierWorldRoot(identity, stableEndpoints));
        }

        if (type == ZtZeroTierWorldType.Moon)
        {
            var dictLength = ReadUInt16(data, ref offset);
            SkipBytes(data, ref offset, dictLength);
        }

        return new ZtZeroTierWorld(type, id, timestamp, updatesMustBeSignedBy, signature, roots);
    }

    private static ZtZeroTierIdentity ReadIdentity(ReadOnlySpan<byte> data, ref int offset)
    {
        var identity = ZtZeroTierIdentityCodec.Deserialize(data.Slice(offset), out var consumed);
        offset += consumed;
        return identity;
    }

    private static bool TryReadInetEndpoint(ReadOnlySpan<byte> data, ref int offset, out IPEndPoint endpoint)
    {
        endpoint = default!;
        var type = ReadByte(data, ref offset);
        switch (type)
        {
            case 0:
                return false;

            case 0x01:
            case 0x02:
                SkipBytes(data, ref offset, 6);
                return false;

            case 0x03:
                var length = ReadUInt16(data, ref offset);
                SkipBytes(data, ref offset, length);
                return false;

            case 0x04:
                var ipv4 = ReadBytes(data, ref offset, 4);
                var port4 = ReadUInt16(data, ref offset);
                endpoint = new IPEndPoint(new IPAddress(ipv4), port4);
                return true;

            case 0x06:
                var ipv6 = ReadBytes(data, ref offset, 16);
                var port6 = ReadUInt16(data, ref offset);
                endpoint = new IPEndPoint(new IPAddress(ipv6), port6);
                return true;

            default:
                throw new FormatException($"Invalid InetAddress type: {type}.");
        }
    }

    private static byte ReadByte(ReadOnlySpan<byte> data, ref int offset)
    {
        if ((uint)offset >= (uint)data.Length)
        {
            throw new FormatException("Unexpected end of world data.");
        }

        return data[offset++];
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, ref int offset)
    {
        var value = ReadBytes(data, ref offset, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(value);
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> data, ref int offset)
    {
        var value = ReadBytes(data, ref offset, 8);
        return BinaryPrimitives.ReadUInt64BigEndian(value);
    }

    private static ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> data, ref int offset, int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Length must be non-negative.");
        }

        if ((uint)(offset + length) > (uint)data.Length)
        {
            throw new FormatException("Unexpected end of world data.");
        }

        var slice = data.Slice(offset, length);
        offset += length;
        return slice;
    }

    private static void SkipBytes(ReadOnlySpan<byte> data, ref int offset, int length)
    {
        _ = ReadBytes(data, ref offset, length);
    }


}
