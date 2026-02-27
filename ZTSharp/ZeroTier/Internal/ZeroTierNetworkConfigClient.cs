using System.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed record ZeroTierNetworkConfigResult(
    ZeroTierHelloOk UpstreamRoot,
    byte[] UpstreamRootKey,
    ZeroTierIdentity ControllerIdentity,
    byte[] DictionaryBytes,
    IPAddress[] ManagedIps);

internal static class ZeroTierNetworkConfigClient
{
    public static async Task<ZeroTierNetworkConfigResult> FetchAsync(
        ZeroTierIdentity localIdentity,
        ZeroTierWorld planet,
        ulong networkId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localIdentity);
        ArgumentNullException.ThrowIfNull(planet);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");
        }

        if (localIdentity.PrivateKey is null)
        {
            throw new InvalidOperationException("Local identity must contain a private key.");
        }

        var rootKeys = ZeroTierNetworkConfigProtocol.BuildRootKeys(localIdentity, planet);

        var deadline = DateTimeOffset.UtcNow + timeout;
        static TimeSpan GetRemainingTimeout(DateTimeOffset deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("Timed out while joining the network.");
            }

            return remaining;
        }

        var udp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: true);
        try
        {
            var helloOk = await ZeroTierHelloClient
                .HelloRootsAsync(udp, localIdentity, planet, GetRemainingTimeout(deadline), cancellationToken)
                .ConfigureAwait(false);

            if (!rootKeys.TryGetValue(helloOk.RootNodeId, out var upstreamRootKey))
            {
                throw new InvalidOperationException($"No root key available for {helloOk.RootNodeId}.");
            }

            var controllerNodeId = ZeroTierNetworkConfigProtocol.GetControllerNodeId(networkId);

            var controllerIdentity = await ZeroTierWhoisClient.WhoisAsync(
                    udp,
                    rootNodeId: helloOk.RootNodeId,
                    rootEndpoint: helloOk.RootEndpoint,
                    rootKey: upstreamRootKey,
                    rootProtocolVersion: helloOk.RemoteProtocolVersion,
                    localNodeId: localIdentity.NodeId,
                    controllerNodeId,
                    GetRemainingTimeout(deadline),
                    cancellationToken)
                .ConfigureAwait(false);

            var controllerKey = new byte[48];
            ZeroTierC25519.Agree(localIdentity.PrivateKey, controllerIdentity.PublicKey, controllerKey);

            var keys = new Dictionary<NodeId, byte[]>(capacity: planet.Roots.Count + 1);
            foreach (var pair in rootKeys)
            {
                keys[pair.Key] = pair.Value;
            }

            keys[controllerNodeId] = controllerKey;

            var controllerProtocolVersion = await ZeroTierHelloClient.HelloAsync(
                    udp,
                    localIdentity,
                    planet,
                    destination: controllerNodeId,
                    physicalDestination: helloOk.RootEndpoint,
                    sharedKey: controllerKey,
                    timeout: GetRemainingTimeout(deadline),
                    cancellationToken)
                .ConfigureAwait(false);

            var (dictBytes, ips) = await ZeroTierNetworkConfigProtocol.RequestNetworkConfigAsync(
                    udp,
                    keys,
                    rootEndpoint: helloOk.RootEndpoint,
                    localNodeId: localIdentity.NodeId,
                    controllerIdentity,
                    controllerProtocolVersion,
                    networkId,
                    GetRemainingTimeout(deadline),
                    allowLegacyUnsignedConfig: false,
                    cancellationToken)
                .ConfigureAwait(false);

            return new ZeroTierNetworkConfigResult(helloOk, upstreamRootKey, controllerIdentity, dictBytes, ips);
        }
        finally
        {
            await udp.DisposeAsync().ConfigureAwait(false);
        }
    }
}
