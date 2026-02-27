using System.Net;
using ZTSharp.Transport.Internal;

namespace ZTSharp.Tests;

[Collection("OsUdpPeerRegistry")]
public sealed class OsUdpPeerRegistryNetworkTrimTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    [Fact]
    public void SweepNetworkPeers_DoesNotEvictNetworks_WithLocalRegistration()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new OsUdpPeerRegistry(enablePeerDiscovery: false, UdpEndpointNormalization.Normalize, timeProvider: time);

        const int extraNetworks = 16;
        var totalNetworks = 256 + extraNetworks;

        for (var i = 0; i < totalNetworks; i++)
        {
            var networkId = (ulong)(0x9000 + i);
            registry.SetLocalNodeId(networkId, nodeId: 1);
            registry.AddOrUpdatePeer(networkId, nodeId: (ulong)(0xA000 + i), endpoint: new IPEndPoint(IPAddress.Loopback, 10000 + i));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        var firstNetworkId = 0x9000UL;
        Assert.True(registry.TryGetPeers(firstNetworkId, out _));
    }
}

