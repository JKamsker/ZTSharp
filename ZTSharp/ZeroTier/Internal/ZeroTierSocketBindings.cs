using System.Net;
using System.Net.Sockets;
using System.Linq;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierSocketBindings
{
    public static async ValueTask<ZeroTierTcpListener> ListenTcpAsync(
        Func<CancellationToken, Task> ensureJoinedAsync,
        Func<IReadOnlyList<IPAddress>> getManagedIps,
        Func<byte[]> getInlineCom,
        Func<byte[], CancellationToken, Task<ZeroTierDataplaneRuntime>> getOrCreateRuntimeAsync,
        IPAddress localAddress,
        int port,
        CancellationToken cancellationToken,
        int acceptQueueCapacity = 64)
    {
        ArgumentNullException.ThrowIfNull(ensureJoinedAsync);
        ArgumentNullException.ThrowIfNull(getManagedIps);
        ArgumentNullException.ThrowIfNull(getInlineCom);
        ArgumentNullException.ThrowIfNull(getOrCreateRuntimeAsync);
        ArgumentNullException.ThrowIfNull(localAddress);

        cancellationToken.ThrowIfCancellationRequested();

        if (port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 1 and 65535.");
        }

        if (localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork &&
            localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            throw new NotSupportedException($"Unsupported address family: {localAddress.AddressFamily}.");
        }

        await ensureJoinedAsync(cancellationToken).ConfigureAwait(false);
        var managedIps = getManagedIps();

        if (localAddress.Equals(IPAddress.Any))
        {
            if (!managedIps.Any(ip => ip.AddressFamily == AddressFamily.InterNetwork))
            {
                throw new InvalidOperationException("No IPv4 managed IP assigned for this network.");
            }
        }
        else if (localAddress.Equals(IPAddress.IPv6Any))
        {
            if (!managedIps.Any(ip => ip.AddressFamily == AddressFamily.InterNetworkV6))
            {
                throw new InvalidOperationException("No IPv6 managed IP assigned for this network.");
            }
        }
        else if (!ContainsManagedIp(managedIps, localAddress))
        {
            throw new InvalidOperationException($"Local address '{localAddress}' is not one of this node's managed IPs.");
        }

        if (acceptQueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(acceptQueueCapacity), acceptQueueCapacity, "Accept queue capacity must be greater than zero.");
        }

        var comBytes = getInlineCom();
        var runtime = await getOrCreateRuntimeAsync(comBytes, cancellationToken).ConfigureAwait(false);
        return new ZeroTierTcpListener(runtime, localAddress, (ushort)port, acceptQueueCapacity: acceptQueueCapacity);
    }

    public static async ValueTask<ZeroTierUdpSocket> BindUdpAsync(
        Func<CancellationToken, Task> ensureJoinedAsync,
        Func<IReadOnlyList<IPAddress>> getManagedIps,
        Func<byte[]> getInlineCom,
        Func<byte[], CancellationToken, Task<ZeroTierDataplaneRuntime>> getOrCreateRuntimeAsync,
        IPAddress localAddress,
        int port,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ensureJoinedAsync);
        ArgumentNullException.ThrowIfNull(getManagedIps);
        ArgumentNullException.ThrowIfNull(getInlineCom);
        ArgumentNullException.ThrowIfNull(getOrCreateRuntimeAsync);
        ArgumentNullException.ThrowIfNull(localAddress);

        cancellationToken.ThrowIfCancellationRequested();

        if (port is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 0 and 65535.");
        }

        if (localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork &&
            localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            throw new NotSupportedException($"Unsupported address family: {localAddress.AddressFamily}.");
        }

        await ensureJoinedAsync(cancellationToken).ConfigureAwait(false);
        var managedIps = getManagedIps();
        if (localAddress.Equals(IPAddress.Any))
        {
            localAddress = managedIps.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                           ?? throw new InvalidOperationException("No IPv4 managed IP assigned for this network.");
        }
        else if (localAddress.Equals(IPAddress.IPv6Any))
        {
            localAddress = managedIps.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetworkV6)
                           ?? throw new InvalidOperationException("No IPv6 managed IP assigned for this network.");
        }

        if (!ContainsManagedIp(managedIps, localAddress))
        {
            throw new InvalidOperationException($"Local address '{localAddress}' is not one of this node's managed IPs.");
        }

        var comBytes = getInlineCom();
        var runtime = await getOrCreateRuntimeAsync(comBytes, cancellationToken).ConfigureAwait(false);

        if (port != 0)
        {
            return new ZeroTierUdpSocket(runtime, localAddress, (ushort)port);
        }

        for (var attempt = 0; attempt < 32; attempt++)
        {
            var localPort = ZeroTierEphemeralPorts.Generate();
            var socket = TryBindUdpSocket(runtime, localAddress, localPort);
            if (socket is not null)
            {
                return socket;
            }
        }

        throw new InvalidOperationException("Failed to bind UDP to an ephemeral port (too many collisions).");
    }

    private static ZeroTierUdpSocket? TryBindUdpSocket(ZeroTierDataplaneRuntime runtime, IPAddress localAddress, ushort localPort)
    {
        try
        {
            return new ZeroTierUdpSocket(runtime, localAddress, localPort);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool ContainsManagedIp(IReadOnlyList<IPAddress> managedIps, IPAddress candidate)
    {
        var canonicalCandidate = ZeroTierIpAddressCanonicalization.CanonicalizeForManagedIpComparison(candidate);
        for (var i = 0; i < managedIps.Count; i++)
        {
            var managedIp = ZeroTierIpAddressCanonicalization.CanonicalizeForManagedIpComparison(managedIps[i]);
            if (managedIp.Equals(canonicalCandidate))
            {
                return true;
            }
        }

        return false;
    }
}
