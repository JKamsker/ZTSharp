using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace ZTSharp;

internal static class NetworkAddressCodec
{
    private const byte Version = 1;
    private const byte TagV4 = 4;
    private const byte TagV6 = 6;
    private const int HeaderLength = 1 + sizeof(int);
    private const int EntryV4Length = 1 + 1 + 4;
    private const int EntryV6Length = 1 + 1 + 16 + sizeof(long);

    public static int GetEncodedLength(IReadOnlyList<NetworkAddress> addresses)
    {
        var total = HeaderLength;
        for (var i = 0; i < addresses.Count; i++)
        {
            total += addresses[i].AddressFamily == AddressFamily.InterNetwork ? EntryV4Length : EntryV6Length;
        }

        return total;
    }

    public static bool TryEncode(
        IReadOnlyList<NetworkAddress> addresses,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (addresses.Count > int.MaxValue)
        {
            return false;
        }

        var requiredLength = GetEncodedLength(addresses);
        if (destination.Length < requiredLength)
        {
            return false;
        }

        destination[0] = Version;
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(1, 4), addresses.Count);
        var offset = HeaderLength;

        for (var i = 0; i < addresses.Count; i++)
        {
            var entry = addresses[i];
            var prefixLength = entry.PrefixLength;
            var address = entry.Address;
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                if (prefixLength is < 0 or > 32)
                {
                    return false;
                }

                destination[offset] = TagV4;
                destination[offset + 1] = (byte)prefixLength;
                if (!address.TryWriteBytes(destination.Slice(offset + 2, 4), out var written) || written != 4)
                {
                    return false;
                }

                offset += EntryV4Length;
                continue;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (prefixLength is < 0 or > 128)
                {
                    return false;
                }

                destination[offset] = TagV6;
                destination[offset + 1] = (byte)prefixLength;
                if (!address.TryWriteBytes(destination.Slice(offset + 2, 16), out var written) || written != 16)
                {
                    return false;
                }

                BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset + 18, 8), address.ScopeId);
                offset += EntryV6Length;
                continue;
            }

            return false;
        }

        bytesWritten = requiredLength;
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<byte> encoded, out NetworkAddress[] addresses)
    {
        addresses = Array.Empty<NetworkAddress>();
        if (encoded.Length < HeaderLength || encoded[0] != Version)
        {
            return false;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(encoded.Slice(1, 4));
        if (count < 0)
        {
            return false;
        }

        if (count == 0)
        {
            return true;
        }

        var decoded = new NetworkAddress[count];
        var offset = HeaderLength;

        for (var i = 0; i < count; i++)
        {
            if (offset + 2 > encoded.Length)
            {
                return false;
            }

            var tag = encoded[offset];
            var prefix = encoded[offset + 1];
            if (tag == TagV4)
            {
                if (offset + EntryV4Length > encoded.Length)
                {
                    return false;
                }

                decoded[i] = new NetworkAddress(new IPAddress(encoded.Slice(offset + 2, 4)), prefix);
                offset += EntryV4Length;
                continue;
            }

            if (tag == TagV6)
            {
                if (offset + EntryV6Length > encoded.Length)
                {
                    return false;
                }

                var address = new IPAddress(encoded.Slice(offset + 2, 16));
                var scopeId = BinaryPrimitives.ReadInt64LittleEndian(encoded.Slice(offset + 18, 8));
                if (scopeId != 0)
                {
                    address.ScopeId = scopeId;
                }

                decoded[i] = new NetworkAddress(address, prefix);
                offset += EntryV6Length;
                continue;
            }

            return false;
        }

        addresses = decoded;
        return true;
    }
}

