using System.Buffers.Binary;
using System.Net;

namespace ZTSharp.ZeroTier.Internal;

internal readonly record struct ZeroTierTcpRouteKey(
    uint LocalIp,
    ushort LocalPort,
    uint RemoteIp,
    ushort RemotePort)
{
    public static ZeroTierTcpRouteKey FromEndpoints(IPEndPoint local, IPEndPoint remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);

        if (local.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            remote.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new NotSupportedException("Only IPv4 is supported in the TCP MVP.");
        }

        return new ZeroTierTcpRouteKey(
            LocalIp: BinaryPrimitives.ReadUInt32BigEndian(local.Address.GetAddressBytes()),
            LocalPort: (ushort)local.Port,
            RemoteIp: BinaryPrimitives.ReadUInt32BigEndian(remote.Address.GetAddressBytes()),
            RemotePort: (ushort)remote.Port);
    }

    public override string ToString()
        => $"{FormatIp(LocalIp)}:{LocalPort} <- {FormatIp(RemoteIp)}:{RemotePort}";

    private static string FormatIp(uint ipv4)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, ipv4);
        return new IPAddress(bytes).ToString();
    }
}

