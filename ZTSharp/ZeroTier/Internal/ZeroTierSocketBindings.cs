using System.Net;
using System.Linq;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierSocketBindings
{
    public static async ValueTask<ZeroTierTcpListener> ListenTcpAsync(
        Func<CancellationToken, Task> ensureJoinedAsync,
        Func<IReadOnlyList<IPAddress>> getManagedIps,
        Func<(IPAddress? LocalManagedIpV4, byte[] InlineCom)> getLocalManagedIpv4AndInlineCom,
        Func<IPAddress?, byte[], CancellationToken, Task<ZeroTierDataplaneRuntime>> getOrCreateRuntimeAsync,
        IPAddress localAddress,
        int port,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ensureJoinedAsync);
        ArgumentNullException.ThrowIfNull(getManagedIps);
        ArgumentNullException.ThrowIfNull(getLocalManagedIpv4AndInlineCom);
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
        if (!managedIps.Contains(localAddress))
        {
            throw new InvalidOperationException($"Local address '{localAddress}' is not one of this node's managed IPs.");
        }

        var (localManagedIpV4, comBytes) = getLocalManagedIpv4AndInlineCom();
        var runtime = await getOrCreateRuntimeAsync(localManagedIpV4, comBytes, cancellationToken).ConfigureAwait(false);
        return new ZeroTierTcpListener(runtime, localAddress, (ushort)port);
    }

    public static async ValueTask<ZeroTierUdpSocket> BindUdpAsync(
        Func<CancellationToken, Task> ensureJoinedAsync,
        Func<IReadOnlyList<IPAddress>> getManagedIps,
        Func<(IPAddress? LocalManagedIpV4, byte[] InlineCom)> getLocalManagedIpv4AndInlineCom,
        Func<IPAddress?, byte[], CancellationToken, Task<ZeroTierDataplaneRuntime>> getOrCreateRuntimeAsync,
        IPAddress localAddress,
        int port,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ensureJoinedAsync);
        ArgumentNullException.ThrowIfNull(getManagedIps);
        ArgumentNullException.ThrowIfNull(getLocalManagedIpv4AndInlineCom);
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
        if (!managedIps.Contains(localAddress))
        {
            throw new InvalidOperationException($"Local address '{localAddress}' is not one of this node's managed IPs.");
        }

        var (localManagedIpV4, comBytes) = getLocalManagedIpv4AndInlineCom();
        var runtime = await getOrCreateRuntimeAsync(localManagedIpV4, comBytes, cancellationToken).ConfigureAwait(false);

        if (port != 0)
        {
            return new ZeroTierUdpSocket(runtime, localAddress, (ushort)port);
        }

        for (var attempt = 0; attempt < 32; attempt++)
        {
            var localPort = ZeroTierEphemeralPorts.Generate();
            try
            {
                return new ZeroTierUdpSocket(runtime, localAddress, localPort);
            }
            catch (InvalidOperationException)
            {
            }
        }

        throw new InvalidOperationException("Failed to bind UDP to an ephemeral port (too many collisions).");
    }
}
