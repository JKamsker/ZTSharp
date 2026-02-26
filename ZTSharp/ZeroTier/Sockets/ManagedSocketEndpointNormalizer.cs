using System.Net;
using System.Net.Sockets;
using System.Linq;
using ZTSharp.ZeroTier;

namespace ZTSharp.ZeroTier.Sockets;

internal static class ManagedSocketEndpointNormalizer
{
    public static async ValueTask<IPEndPoint> NormalizeLocalEndpointAsync(
        ZeroTierSocket zeroTierSocket,
        AddressFamily addressFamily,
        IPEndPoint localEndPoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zeroTierSocket);
        ArgumentNullException.ThrowIfNull(localEndPoint);

        var address = localEndPoint.Address;

        if (addressFamily == AddressFamily.InterNetwork && address.Equals(IPAddress.Any))
        {
            await zeroTierSocket.JoinAsync(cancellationToken).ConfigureAwait(false);
            address = zeroTierSocket.ManagedIps.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                      ?? throw new InvalidOperationException("No IPv4 managed IP assigned for this network.");
        }

        if (addressFamily == AddressFamily.InterNetworkV6 && address.Equals(IPAddress.IPv6Any))
        {
            await zeroTierSocket.JoinAsync(cancellationToken).ConfigureAwait(false);
            address = zeroTierSocket.ManagedIps.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetworkV6)
                      ?? throw new InvalidOperationException("No IPv6 managed IP assigned for this network.");
        }

        return new IPEndPoint(address, localEndPoint.Port);
    }
}
