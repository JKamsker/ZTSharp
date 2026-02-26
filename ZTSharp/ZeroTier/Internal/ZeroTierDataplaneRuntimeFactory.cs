using System.Net;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierDataplaneRuntimeFactory
{
    internal static async Task<(ZeroTierDataplaneRuntime Runtime, ZeroTierHelloOk HelloOk, byte[] RootKey)> CreateAsync(
        ZeroTierUdpTransport udp,
        ZeroTierIdentity localIdentity,
        ZeroTierWorld planet,
        ulong networkId,
        IPAddress[] localManagedIpsV4,
        IPAddress[] localManagedIpsV6,
        byte[] inlineCom,
        ZeroTierHelloOk? cachedRoot,
        byte[]? cachedRootKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(localIdentity);
        ArgumentNullException.ThrowIfNull(planet);
        ArgumentNullException.ThrowIfNull(localManagedIpsV4);
        ArgumentNullException.ThrowIfNull(localManagedIpsV6);
        ArgumentNullException.ThrowIfNull(inlineCom);

        ZeroTierHelloOk helloOk;
        byte[] rootKey;
        if (cachedRoot is { } cachedHelloOk && cachedRootKey is not null)
        {
            helloOk = cachedHelloOk;
            rootKey = cachedRootKey;
        }
        else
        {
            helloOk = await ZeroTierHelloClient
                .HelloRootsAsync(udp, localIdentity, planet, timeout: TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);

            rootKey = ComputeRootKey(localIdentity, planet, helloOk.RootNodeId);
        }

        var runtime = new ZeroTierDataplaneRuntime(
            udp,
            rootNodeId: helloOk.RootNodeId,
            rootEndpoint: helloOk.RootEndpoint,
            rootKey: rootKey,
            rootProtocolVersion: helloOk.RemoteProtocolVersion,
            localIdentity: localIdentity,
            networkId: networkId,
            localManagedIpsV4: localManagedIpsV4,
            localManagedIpsV6: localManagedIpsV6,
            inlineCom: inlineCom);

        await TrySubscribeForAddressResolutionAsync(
                udp,
                localIdentity.NodeId,
                networkId,
                localManagedIpsV4,
                localManagedIpsV6,
                helloOk,
                rootKey,
                cancellationToken)
            .ConfigureAwait(false);

        return (runtime, helloOk, rootKey);
    }

    private static byte[] ComputeRootKey(ZeroTierIdentity localIdentity, ZeroTierWorld planet, NodeId rootNodeId)
    {
        var root = planet.Roots.FirstOrDefault(r => r.Identity.NodeId == rootNodeId);
        if (root is null)
        {
            throw new InvalidOperationException($"Root identity not found for {rootNodeId}.");
        }

        var rootKey = new byte[48];
        ZeroTierC25519.Agree(localIdentity.PrivateKey!, root.Identity.PublicKey, rootKey);
        return rootKey;
    }

    private static async Task TrySubscribeForAddressResolutionAsync(
        ZeroTierUdpTransport udp,
        NodeId localNodeId,
        ulong networkId,
        IPAddress[] localManagedIpsV4,
        IPAddress[] localManagedIpsV6,
        ZeroTierHelloOk helloOk,
        byte[] rootKey,
        CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(localManagedIpsV4);
            var groups = new List<ZeroTierMulticastGroup>(localManagedIpsV4.Length + localManagedIpsV6.Length);

            for (var i = 0; i < localManagedIpsV4.Length; i++)
            {
                groups.Add(ZeroTierMulticastGroup.DeriveForAddressResolution(localManagedIpsV4[i]));
            }

            for (var i = 0; i < localManagedIpsV6.Length; i++)
            {
                groups.Add(ZeroTierMulticastGroup.DeriveForAddressResolution(localManagedIpsV6[i]));
            }

            await ZeroTierMulticastLikeClient
                .SendAsync(
                    udp,
                    helloOk.RootNodeId,
                    helloOk.RootEndpoint,
                    rootKey,
                    rootProtocolVersion: helloOk.RemoteProtocolVersion,
                    localNodeId,
                    networkId,
                    groups: groups,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (System.Net.Sockets.SocketException)
        {
            // Best-effort. Some environments restrict certain outbound paths (IPv6, captive portals, etc.).
        }
    }
}
