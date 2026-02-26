using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace ZTSharp.ZeroTier.Internal;

internal readonly record struct ZeroTierTcpRouteKeyV6(
    ulong LocalIpHigh,
    ulong LocalIpLow,
    ushort LocalPort,
    ulong RemoteIpHigh,
    ulong RemoteIpLow,
    ushort RemotePort)
{
    public static ZeroTierTcpRouteKeyV6 FromEndpoints(IPEndPoint local, IPEndPoint remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);

        if (local.Address.AddressFamily != AddressFamily.InterNetworkV6 ||
            remote.Address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            throw new NotSupportedException("Only IPv6 is supported by this route key.");
        }

        return FromAddresses(local.Address, (ushort)local.Port, remote.Address, (ushort)remote.Port);
    }

    public static ZeroTierTcpRouteKeyV6 FromAddresses(IPAddress localAddress, ushort localPort, IPAddress remoteAddress, ushort remotePort)
    {
        ArgumentNullException.ThrowIfNull(localAddress);
        ArgumentNullException.ThrowIfNull(remoteAddress);

        if (localAddress.ScopeId != 0 || remoteAddress.ScopeId != 0)
        {
            throw new NotSupportedException("Scoped IPv6 addresses are not supported by this route key.");
        }

        Span<byte> localBytes = stackalloc byte[16];
        if (!localAddress.TryWriteBytes(localBytes, out var localWritten) || localWritten != 16)
        {
            throw new ArgumentOutOfRangeException(nameof(localAddress), "Local address must be IPv6.");
        }

        Span<byte> remoteBytes = stackalloc byte[16];
        if (!remoteAddress.TryWriteBytes(remoteBytes, out var remoteWritten) || remoteWritten != 16)
        {
            throw new ArgumentOutOfRangeException(nameof(remoteAddress), "Remote address must be IPv6.");
        }

        return new ZeroTierTcpRouteKeyV6(
            LocalIpHigh: BinaryPrimitives.ReadUInt64BigEndian(localBytes.Slice(0, 8)),
            LocalIpLow: BinaryPrimitives.ReadUInt64BigEndian(localBytes.Slice(8, 8)),
            LocalPort: localPort,
            RemoteIpHigh: BinaryPrimitives.ReadUInt64BigEndian(remoteBytes.Slice(0, 8)),
            RemoteIpLow: BinaryPrimitives.ReadUInt64BigEndian(remoteBytes.Slice(8, 8)),
            RemotePort: remotePort);
    }

    public override string ToString()
        => $"{FormatIp(LocalIpHigh, LocalIpLow)}:{LocalPort} <- {FormatIp(RemoteIpHigh, RemoteIpLow)}:{RemotePort}";

    private static string FormatIp(ulong high, ulong low)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes.Slice(0, 8), high);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.Slice(8, 8), low);
        return new IPAddress(bytes).ToString();
    }
}
