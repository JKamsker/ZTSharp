using System.Buffers.Binary;
using System.Net;

namespace JKamsker.LibZt.ZeroTier.Protocol;

internal readonly record struct ZeroTierPushedDirectPath(
    byte Flags,
    IPEndPoint Endpoint);

internal static class ZeroTierPushDirectPathsCodec
{
    public static bool TryParse(ReadOnlySpan<byte> payload, out ZeroTierPushedDirectPath[] paths)
    {
        if (payload.Length < 2)
        {
            paths = Array.Empty<ZeroTierPushedDirectPath>();
            return false;
        }

        var count = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(0, 2));
        var ptr = 2;

        if (count == 0)
        {
            paths = Array.Empty<ZeroTierPushedDirectPath>();
            return true;
        }

        var list = new List<ZeroTierPushedDirectPath>(Math.Min((int)count, 32));

        for (var i = 0; i < count; i++)
        {
            if (ptr + 1 + 2 + 1 + 1 > payload.Length)
            {
                paths = Array.Empty<ZeroTierPushedDirectPath>();
                return false;
            }

            var flags = payload[ptr++];
            var extLen = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(ptr, 2));
            ptr += 2;

            if (ptr + extLen + 1 + 1 > payload.Length)
            {
                paths = Array.Empty<ZeroTierPushedDirectPath>();
                return false;
            }

            ptr += extLen;

            var addrType = payload[ptr++];
            var addrLen = payload[ptr++];

            if (ptr + addrLen > payload.Length)
            {
                paths = Array.Empty<ZeroTierPushedDirectPath>();
                return false;
            }

            try
            {
                if (addrType == 4 && addrLen >= 6)
                {
                    var address = new IPAddress(payload.Slice(ptr, 4).ToArray());
                    var port = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(ptr + 4, 2));
                    if (port != 0)
                    {
                        list.Add(new ZeroTierPushedDirectPath(flags, new IPEndPoint(address, port)));
                    }
                }
                else if (addrType == 6 && addrLen >= 18)
                {
                    var address = new IPAddress(payload.Slice(ptr, 16).ToArray());
                    var port = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(ptr + 16, 2));
                    if (port != 0)
                    {
                        list.Add(new ZeroTierPushedDirectPath(flags, new IPEndPoint(address, port)));
                    }
                }
            }
            catch (ArgumentException)
            {
                // Ignore invalid address bytes.
            }

            ptr += addrLen;
        }

        paths = list.ToArray();
        return true;
    }
}
