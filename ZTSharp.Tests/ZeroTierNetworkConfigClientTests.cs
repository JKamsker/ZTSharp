using System.Buffers.Binary;
using System.Net;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierNetworkConfigClientTests
{
    [Fact]
    public async Task FetchAsync_CanObtainAssignedIps_FromSignedChunk()
    {
        Assert.True(ZeroTierIdentity.TryParse(ZeroTierTestIdentities.KnownGoodIdentity, out var localIdentity));
        Assert.NotNull(localIdentity.PrivateKey);

        var rootIdentity = ZeroTierTestIdentities.CreateFastIdentity(0x0102030405);
        var controllerIdentity = ZeroTierTestIdentities.CreateFastIdentity(0x0a0b0c0d0e);

        var networkId = (controllerIdentity.NodeId.Value << 24) | 0x000001UL;

        await using var rootUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: true);
        await using var controllerUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: true);

        var planet = new ZeroTierWorld(
            ZeroTierWorldType.Planet,
            id: 1,
            timestamp: 1,
            updatesMustBeSignedBy: new byte[ZeroTierWorld.C25519PublicKeyLength],
            signature: new byte[ZeroTierWorld.C25519SignatureLength],
            roots: new[]
            {
                new ZeroTierWorldRoot(rootIdentity, new[] { rootUdp.LocalEndpoint })
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var rootTask = ZeroTierNetworkConfigTestHarness.RunRootAsync(rootUdp, rootIdentity, controllerIdentity, controllerUdp.LocalEndpoint, cts.Token);
        var controllerTask = ZeroTierNetworkConfigTestHarness.RunControllerAsync(controllerUdp, controllerIdentity, localIdentity, cts.Token);

        var result = await ZeroTierNetworkConfigClient.FetchAsync(
            localIdentity,
            planet,
            networkId,
            timeout: TimeSpan.FromSeconds(2),
            cancellationToken: CancellationToken.None);

        Assert.Equal(controllerIdentity.NodeId, result.ControllerIdentity.NodeId);
        Assert.Contains(IPAddress.Parse("10.121.15.99"), result.ManagedIps);

        cts.Cancel();
        await Task.WhenAll(rootTask, controllerTask);
    }
}
