using System.Buffers.Binary;
using System.Net;

namespace JKamsker.LibZt.ZeroTier.Protocol;

internal static class ZtZeroTierInetAddressCodec
{
    public static int GetSerializedLength(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => 1 + 4 + 2,
            System.Net.Sockets.AddressFamily.InterNetworkV6 => 1 + 16 + 2,
            _ => throw new NotSupportedException($"Unsupported address family: {endpoint.AddressFamily}.")
        };
    }

    public static int Serialize(IPEndPoint endpoint, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var addressBytes = endpoint.Address.GetAddressBytes();
        if (endpoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            const int length = 1 + 4 + 2;
            if (destination.Length < length)
            {
                throw new ArgumentException($"Destination must be at least {length} bytes.", nameof(destination));
            }

            destination[0] = 0x04;
            addressBytes.CopyTo(destination.Slice(1, 4));
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(5, 2), (ushort)endpoint.Port);
            return length;
        }

        if (endpoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            const int length = 1 + 16 + 2;
            if (destination.Length < length)
            {
                throw new ArgumentException($"Destination must be at least {length} bytes.", nameof(destination));
            }

            destination[0] = 0x06;
            addressBytes.CopyTo(destination.Slice(1, 16));
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(17, 2), (ushort)endpoint.Port);
            return length;
        }

        throw new NotSupportedException($"Unsupported address family: {endpoint.AddressFamily}.");
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> data, out IPEndPoint? endpoint, out int bytesRead)
    {
        endpoint = null;
        bytesRead = 0;
        if (data.IsEmpty)
        {
            return false;
        }

        var type = data[0];
        switch (type)
        {
            case 0:
                bytesRead = 1;
                endpoint = null;
                return true;

            case 0x01:
            case 0x02:
                if (data.Length < 7)
                {
                    return false;
                }

                bytesRead = 7;
                endpoint = null;
                return true;

            case 0x03:
                if (data.Length < 3)
                {
                    return false;
                }

                var otherLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(1, 2));
                var total = 3 + otherLength;
                if (data.Length < total)
                {
                    return false;
                }

                bytesRead = total;
                endpoint = null;
                return true;

            case 0x04:
                if (data.Length < 7)
                {
                    return false;
                }

                var ipv4 = new IPAddress(data.Slice(1, 4));
                var port4 = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(5, 2));
                bytesRead = 7;
                endpoint = new IPEndPoint(ipv4, port4);
                return true;

            case 0x06:
                if (data.Length < 19)
                {
                    return false;
                }

                var ipv6 = new IPAddress(data.Slice(1, 16));
                var port6 = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(17, 2));
                bytesRead = 19;
                endpoint = new IPEndPoint(ipv6, port6);
                return true;

            default:
                throw new FormatException($"Invalid InetAddress type: {type}.");
        }
    }
}

