using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace JKamsker.LibZt;

internal static class PeerEndpointCodec
{
    private const byte Version = 1;
    private const byte TagV4 = 4;
    private const byte TagV6 = 6;

    public const int MaxEncodedLength = 1 + 1 + sizeof(ushort) + 16 + sizeof(long);

    public static int GetEncodedLength(IPEndPoint endpoint)
    {
        return endpoint.AddressFamily switch
        {
            AddressFamily.InterNetwork => 1 + 1 + sizeof(ushort) + 4,
            AddressFamily.InterNetworkV6 => MaxEncodedLength,
            _ => throw new NotSupportedException($"Unsupported address family: {endpoint.AddressFamily}")
        };
    }

    public static bool TryEncode(IPEndPoint endpoint, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;
        var requiredLength = GetEncodedLength(endpoint);
        if (destination.Length < requiredLength)
        {
            return false;
        }

        destination[0] = Version;

        var address = endpoint.Address;
        var addressFamily = address.AddressFamily;
        if (addressFamily == AddressFamily.InterNetwork)
        {
            destination[1] = TagV4;
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), (ushort)endpoint.Port);
            if (!address.TryWriteBytes(destination.Slice(4, 4), out var written) || written != 4)
            {
                return false;
            }

            bytesWritten = requiredLength;
            return true;
        }

        if (addressFamily == AddressFamily.InterNetworkV6)
        {
            destination[1] = TagV6;
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), (ushort)endpoint.Port);
            if (!address.TryWriteBytes(destination.Slice(4, 16), out var written) || written != 16)
            {
                return false;
            }

            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(20, 8), address.ScopeId);
            bytesWritten = requiredLength;
            return true;
        }

        return false;
    }

    public static bool TryDecode(ReadOnlySpan<byte> encoded, out IPEndPoint endpoint)
    {
        endpoint = default!;
        if (encoded.Length < 1 + 1 + sizeof(ushort) || encoded[0] != Version)
        {
            return false;
        }

        var port = BinaryPrimitives.ReadUInt16BigEndian(encoded.Slice(2, 2));
        var tag = encoded[1];

        if (tag == TagV4)
        {
            if (encoded.Length < 1 + 1 + sizeof(ushort) + 4)
            {
                return false;
            }

            endpoint = new IPEndPoint(new IPAddress(encoded.Slice(4, 4)), port);
            return true;
        }

        if (tag == TagV6)
        {
            if (encoded.Length < MaxEncodedLength)
            {
                return false;
            }

            var address = new IPAddress(encoded.Slice(4, 16));
            var scopeId = BinaryPrimitives.ReadInt64LittleEndian(encoded.Slice(20, 8));
            if (scopeId != 0)
            {
                address.ScopeId = scopeId;
            }

            endpoint = new IPEndPoint(address, port);
            return true;
        }

        return false;
    }
}

