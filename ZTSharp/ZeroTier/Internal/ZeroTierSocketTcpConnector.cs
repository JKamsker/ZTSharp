using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using ZTSharp.ZeroTier.Net;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierSocketTcpConnector
{
    public static async ValueTask<Stream> ConnectAsync(
        Func<CancellationToken, Task> ensureJoinedAsync,
        Func<IReadOnlyList<IPAddress>> getManagedIps,
        Func<(IPAddress? LocalManagedIpV4, byte[] InlineCom)> getLocalManagedIpv4AndInlineCom,
        Func<IPAddress?, byte[], CancellationToken, Task<ZeroTierDataplaneRuntime>> getOrCreateRuntimeAsync,
        IPEndPoint? local,
        IPEndPoint remote,
        CancellationToken cancellationToken)
    {
        var (stream, _) = await ConnectWithLocalEndpointAsync(
                ensureJoinedAsync,
                getManagedIps,
                getLocalManagedIpv4AndInlineCom,
                getOrCreateRuntimeAsync,
                local,
                remote,
                cancellationToken)
            .ConfigureAwait(false);

        return stream;
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership transfers to the returned Stream (disposes UserSpaceTcpClient, link, and UDP transport).")]
    public static async ValueTask<(Stream Stream, IPEndPoint LocalEndpoint)> ConnectWithLocalEndpointAsync(
        Func<CancellationToken, Task> ensureJoinedAsync,
        Func<IReadOnlyList<IPAddress>> getManagedIps,
        Func<(IPAddress? LocalManagedIpV4, byte[] InlineCom)> getLocalManagedIpv4AndInlineCom,
        Func<IPAddress?, byte[], CancellationToken, Task<ZeroTierDataplaneRuntime>> getOrCreateRuntimeAsync,
        IPEndPoint? local,
        IPEndPoint remote,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ensureJoinedAsync);
        ArgumentNullException.ThrowIfNull(getManagedIps);
        ArgumentNullException.ThrowIfNull(getLocalManagedIpv4AndInlineCom);
        ArgumentNullException.ThrowIfNull(getOrCreateRuntimeAsync);
        ArgumentNullException.ThrowIfNull(remote);

        cancellationToken.ThrowIfCancellationRequested();

        if (remote.Port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(remote), "Remote port must be between 1 and 65535.");
        }

        if (remote.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork &&
            remote.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            throw new NotSupportedException($"Unsupported address family: {remote.Address.AddressFamily}.");
        }

        if (remote.Address.Equals(IPAddress.Any) || remote.Address.Equals(IPAddress.IPv6Any))
        {
            throw new ArgumentOutOfRangeException(nameof(remote), "Remote address must not be unspecified (Any/IPv6Any).");
        }

        if (remote.Address.Equals(IPAddress.Broadcast))
        {
            throw new ArgumentOutOfRangeException(nameof(remote), "Remote address must not be broadcast.");
        }

        if (IsMulticast(remote.Address))
        {
            throw new ArgumentOutOfRangeException(nameof(remote), "Remote address must not be multicast.");
        }

        if (IPAddress.IsLoopback(remote.Address))
        {
            throw new NotSupportedException("Loopback addresses are not supported in the ZeroTier managed stack.");
        }

        await ensureJoinedAsync(cancellationToken).ConfigureAwait(false);

        var managedIps = getManagedIps();
        var (localManagedIpV4, inlineCom) = getLocalManagedIpv4AndInlineCom();

        if (local is not null && local.Address.AddressFamily != remote.Address.AddressFamily)
        {
            throw new NotSupportedException("Local and remote address families must match.");
        }

        if (local is not null && (local.Port < 0 || local.Port > ushort.MaxValue))
        {
            throw new ArgumentOutOfRangeException(nameof(local), "Local port must be between 0 and 65535.");
        }

        var localAddress = local?.Address ?? (remote.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? localManagedIpV4 ?? throw new InvalidOperationException("No IPv4 managed IP assigned for this network.")
            : managedIps.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
              ?? throw new InvalidOperationException("No IPv6 managed IP assigned for this network."));

        if (!managedIps.Contains(localAddress))
        {
            throw new InvalidOperationException($"Local address '{localAddress}' is not one of this node's managed IPs.");
        }

        var runtime = await getOrCreateRuntimeAsync(localManagedIpV4, inlineCom, cancellationToken).ConfigureAwait(false);
        var remoteNodeId = await runtime.ResolveNodeIdAsync(remote.Address, cancellationToken).ConfigureAwait(false);

        var fixedPort = local is not null && local.Port != 0;
        var fixedLocalPort = fixedPort ? (ushort)local!.Port : (ushort)0;

        IUserSpaceIpLink? link = null;
        ushort localPort = 0;
        for (var attempt = 0; attempt < 32; attempt++)
        {
            localPort = fixedPort ? fixedLocalPort : ZeroTierEphemeralPorts.Generate();
            var localEndpoint = new IPEndPoint(localAddress, localPort);

            try
            {
                link = runtime.RegisterTcpRoute(remoteNodeId, localEndpoint, remote);
                break;
            }
            catch (InvalidOperationException) when (!fixedPort && attempt < 31)
            {
            }
        }

        if (link is null)
        {
            throw new InvalidOperationException("Failed to bind TCP to an ephemeral port (too many collisions).");
        }

        var tcp = new UserSpaceTcpClient(
            link,
            localAddress,
            remote.Address,
            remotePort: (ushort)remote.Port,
            localPort: localPort);

        try
        {
            await tcp.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tcp.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return (tcp.GetStream(), new IPEndPoint(localAddress, localPort));
    }

    private static bool IsMulticast(IPAddress address)
    {
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6Multicast;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes.Length == 4 && bytes[0] is >= 224 and <= 239;
        }

        return false;
    }
}
