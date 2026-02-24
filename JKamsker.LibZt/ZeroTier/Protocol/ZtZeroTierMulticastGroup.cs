using System.Buffers.Binary;
using System.Net;

namespace JKamsker.LibZt.ZeroTier.Protocol;

internal readonly record struct ZtZeroTierMulticastGroup(ZtZeroTierMac Mac, uint Adi)
{
    public static ZtZeroTierMulticastGroup DeriveForAddressResolution(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            var adi = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            return new ZtZeroTierMulticastGroup(ZtZeroTierMac.Broadcast, adi);
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            var macValue =
                ((ulong)0x33 << 40) |
                ((ulong)0x33 << 32) |
                ((ulong)0xFF << 24) |
                ((ulong)bytes[13] << 16) |
                ((ulong)bytes[14] << 8) |
                bytes[15];
            return new ZtZeroTierMulticastGroup(new ZtZeroTierMac(macValue), 0);
        }

        throw new ArgumentOutOfRangeException(nameof(ip), $"Unsupported address family: {ip.AddressFamily}.");
    }
}

