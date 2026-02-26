using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierSocketRuntimeBootstrapper
{
    internal static IZeroTierUdpTransport CreateUdpTransport(ZeroTierMultipathOptions multipath, bool enableIpv6)
    {
        ArgumentNullException.ThrowIfNull(multipath);

        if (!multipath.Enabled || multipath.UdpSocketCount == 1)
        {
            return new ZeroTierUdpTransport(localPort: 0, enableIpv6: enableIpv6, localSocketId: 0);
        }

        var ports = multipath.LocalUdpPorts;
        if (ports is null)
        {
            ports = Enumerable.Repeat(0, multipath.UdpSocketCount).ToArray();
        }

        if (ports.Count != multipath.UdpSocketCount)
        {
            throw new ArgumentOutOfRangeException(nameof(multipath), "LocalUdpPorts length must match UdpSocketCount.");
        }

        var sockets = new List<ZeroTierUdpTransport>(multipath.UdpSocketCount);
        var success = false;
        try
        {
            for (var i = 0; i < multipath.UdpSocketCount; i++)
            {
                sockets.Add(new ZeroTierUdpTransport(localPort: ports[i], enableIpv6: enableIpv6, localSocketId: i));
            }

            var transport = new ZeroTierUdpMultiTransport(sockets);
            success = true;
            return transport;
        }
        finally
        {
            if (!success)
            {
                foreach (var socket in sockets)
                {
                    try
                    {
                        socket.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                    catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or SocketException or InvalidOperationException)
                    {
                    }
                }
            }
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "UDP transport ownership transfers to ZeroTierDataplaneRuntime, which is disposed by ZeroTierSocket.DisposeAsync.")]
    public static async Task<(ZeroTierDataplaneRuntime Runtime, ZeroTierHelloOk HelloOk, byte[] RootKey)> CreateAsync(
        ZeroTierMultipathOptions multipath,
        ZeroTierIdentity localIdentity,
        ZeroTierWorld planet,
        ulong networkId,
        IReadOnlyList<IPAddress> managedIps,
        byte[] inlineCom,
        ZeroTierHelloOk? cachedRoot,
        byte[]? cachedRootKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localIdentity);
        ArgumentNullException.ThrowIfNull(planet);
        ArgumentNullException.ThrowIfNull(managedIps);
        ArgumentNullException.ThrowIfNull(inlineCom);

        var udp = CreateUdpTransport(multipath, enableIpv6: true);
        try
        {
            var localManagedIpsV6 = managedIps
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetworkV6)
                .ToArray();

            var localManagedIpsV4 = managedIps
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                .ToArray();

            return await ZeroTierDataplaneRuntimeFactory
                .CreateAsync(
                    udp,
                    localIdentity: localIdentity,
                    planet: planet,
                    networkId: networkId,
                    localManagedIpsV4: localManagedIpsV4,
                    localManagedIpsV6: localManagedIpsV6,
                    inlineCom: inlineCom,
                    multipath: multipath,
                    cachedRoot: cachedRoot,
                    cachedRootKey: cachedRootKey,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await udp.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
