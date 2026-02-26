using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierSocketRuntimeBootstrapper
{
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "UDP transport ownership transfers to ZeroTierDataplaneRuntime, which is disposed by ZeroTierSocket.DisposeAsync.")]
    public static async Task<(ZeroTierDataplaneRuntime Runtime, ZeroTierHelloOk HelloOk, byte[] RootKey)> CreateAsync(
        ZeroTierIdentity localIdentity,
        ZeroTierWorld planet,
        ulong networkId,
        IReadOnlyList<IPAddress> managedIps,
        IPAddress? localManagedIpV4,
        byte[] inlineCom,
        ZeroTierHelloOk? cachedRoot,
        byte[]? cachedRootKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localIdentity);
        ArgumentNullException.ThrowIfNull(planet);
        ArgumentNullException.ThrowIfNull(managedIps);
        ArgumentNullException.ThrowIfNull(inlineCom);

        var udp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: true);
        try
        {
            var localManagedIpsV6 = managedIps
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetworkV6)
                .ToArray();

            return await ZeroTierDataplaneRuntimeFactory
                .CreateAsync(
                    udp,
                    localIdentity: localIdentity,
                    planet: planet,
                    networkId: networkId,
                    localManagedIpV4: localManagedIpV4,
                    localManagedIpsV6: localManagedIpsV6,
                    inlineCom: inlineCom,
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
