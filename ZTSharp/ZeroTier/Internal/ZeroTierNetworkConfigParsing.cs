using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierNetworkConfigParsing
{
    public static IPAddress[] ParseManagedIps(byte[] dictionaryBytes)
    {
        var ips = new HashSet<IPAddress>();

        if (ZeroTierDictionary.TryGet(dictionaryBytes, "I", out var staticIpsBlob))
        {
            var data = staticIpsBlob.AsSpan();
            while (!data.IsEmpty)
            {
                if (!ZeroTierInetAddressCodec.TryDeserialize(data, out var endpoint, out var read) || read <= 0)
                {
                    break;
                }

                if (endpoint is not null)
                {
                    var bits = endpoint.Port;
                    var address = endpoint.Address;
                    if (!IsNetworkRoute(address, bits))
                    {
                        ips.Add(address);
                    }
                }

                data = data.Slice(read);
            }
        }
        else
        {
            TryAddLegacyIps(dictionaryBytes, "v4s", ips);
            TryAddLegacyIps(dictionaryBytes, "v6s", ips);
        }

        return ips
            .OrderBy(ip => ip.AddressFamily)
            .ThenBy(ip => ip.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    public static bool TryParseConfigChunk(
        byte[] packetBytes,
        int payloadStart,
        out ulong networkId,
        out ReadOnlySpan<byte> chunkData,
        out ulong configUpdateId,
        out uint totalLength,
        out uint chunkIndex,
        out byte[]? signature,
        out ReadOnlySpan<byte> signatureMessage)
    {
        networkId = 0;
        chunkData = default;
        configUpdateId = 0;
        totalLength = 0;
        chunkIndex = 0;
        signature = null;
        signatureMessage = default;

        if (payloadStart < 0 || payloadStart + 10 > packetBytes.Length)
        {
            return false;
        }

        var ptr = payloadStart;
        networkId = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(ptr, 8));
        ptr += 8;
        var chunkLen = BinaryPrimitives.ReadUInt16BigEndian(packetBytes.AsSpan(ptr, 2));
        ptr += 2;

        if (ptr + chunkLen > packetBytes.Length)
        {
            return false;
        }

        chunkData = packetBytes.AsSpan(ptr, chunkLen);
        ptr += chunkLen;

        if (ptr >= packetBytes.Length)
        {
            return true; // legacy unsigned single-chunk config
        }

        var signatureStart = ptr;

        ptr += 1; // flags
        if (ptr + 8 + 4 + 4 + 1 + 2 > packetBytes.Length)
        {
            return false;
        }

        configUpdateId = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(ptr, 8));
        ptr += 8;
        totalLength = BinaryPrimitives.ReadUInt32BigEndian(packetBytes.AsSpan(ptr, 4));
        ptr += 4;
        chunkIndex = BinaryPrimitives.ReadUInt32BigEndian(packetBytes.AsSpan(ptr, 4));
        ptr += 4;

        var sigCount = packetBytes[ptr++];
        var sigLen = BinaryPrimitives.ReadUInt16BigEndian(packetBytes.AsSpan(ptr, 2));
        ptr += 2;

        if (sigCount != 1 || sigLen != 96 || ptr + sigLen > packetBytes.Length)
        {
            return false;
        }

        signature = packetBytes.AsSpan(ptr, sigLen).ToArray();
        signatureMessage = packetBytes.AsSpan(payloadStart, signatureStart - payloadStart + 1 + 8 + 4 + 4);
        return true;
    }

    private static void TryAddLegacyIps(byte[] dictionaryBytes, string key, HashSet<IPAddress> destination)
    {
        if (!ZeroTierDictionary.TryGet(dictionaryBytes, key, out var valueBytes) || valueBytes.Length == 0)
        {
            return;
        }

        var value = Encoding.ASCII.GetString(valueBytes);
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var slash = part.IndexOf('/', StringComparison.Ordinal);
            var ipText = slash >= 0 ? part.Substring(0, slash) : part;
            var bitsText = slash >= 0 ? part.Substring(slash + 1) : string.Empty;
            if (!IPAddress.TryParse(ipText, out var ip))
            {
                continue;
            }

            _ = int.TryParse(bitsText, NumberStyles.None, CultureInfo.InvariantCulture, out var bits);
            if (IsNetworkRoute(ip, bits))
            {
                continue;
            }

            destination.Add(ip);
        }
    }

    private static bool IsNetworkRoute(IPAddress address, int bits)
    {
        if (bits <= 0)
        {
            return false;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            if (bits >= 32)
            {
                return false;
            }

            var bytes = address.GetAddressBytes();
            var ip =
                ((uint)bytes[0] << 24) |
                ((uint)bytes[1] << 16) |
                ((uint)bytes[2] << 8) |
                bytes[3];

            var hostMask = (bits == 0) ? 0xFFFFFFFFu : ((1u << (32 - bits)) - 1u);
            return (ip & hostMask) == 0;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (bits >= 128)
            {
                return false;
            }

            var bytes = address.GetAddressBytes();
            var fullBytes = bits / 8;
            var remainingBits = bits % 8;

            if (fullBytes < 16)
            {
                if (remainingBits == 0)
                {
                    for (var i = fullBytes; i < 16; i++)
                    {
                        if (bytes[i] != 0)
                        {
                            return false;
                        }
                    }

                    return true;
                }

                var mask = (byte)(0xFF >> remainingBits);
                if ((bytes[fullBytes] & mask) != 0)
                {
                    return false;
                }

                for (var i = fullBytes + 1; i < 16; i++)
                {
                    if (bytes[i] != 0)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        return false;
    }
}

