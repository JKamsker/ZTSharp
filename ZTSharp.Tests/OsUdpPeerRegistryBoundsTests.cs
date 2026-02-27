using System.Net;
using ZTSharp.Transport.Internal;

namespace ZTSharp.Tests;

[Collection("OsUdpPeerRegistry")]
public sealed class OsUdpPeerRegistryBoundsTests
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
    public void DirectoryPeers_AreCappedPerNetwork()
    {
        OsUdpPeerRegistry.ClearDirectoryForTests();
        try
        {
            var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            var registry = new OsUdpPeerRegistry(enablePeerDiscovery: true, UdpEndpointNormalization.Normalize, timeProvider: time);
            var networkId = 0x1234UL;

            for (var i = 0; i < OsUdpPeerRegistry.DirectoryMaxPeersPerNetwork + 50; i++)
            {
                _ = registry.RegisterLocalAndGetKnownPeers(
                    networkId,
                    localNodeId: (ulong)(i + 1),
                    advertisedEndpoint: new IPEndPoint(IPAddress.Loopback, 9999));
            }

            Assert.True(OsUdpPeerRegistry.GetDirectoryPeerCountForTests(networkId) <= OsUdpPeerRegistry.DirectoryMaxPeersPerNetwork);
        }
        finally
        {
            OsUdpPeerRegistry.ClearDirectoryForTests();
        }
    }

    [Fact]
    public void DirectoryPeers_ExpireOnTtlSweep()
    {
        OsUdpPeerRegistry.ClearDirectoryForTests();
        try
        {
            var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            var registry = new OsUdpPeerRegistry(enablePeerDiscovery: true, UdpEndpointNormalization.Normalize, timeProvider: time);
            var networkId = 0x5678UL;

            _ = registry.RegisterLocalAndGetKnownPeers(networkId, localNodeId: 1, advertisedEndpoint: new IPEndPoint(IPAddress.Loopback, 9999));
            Assert.Equal(1, OsUdpPeerRegistry.GetDirectoryPeerCountForTests(networkId));

            time.Advance(OsUdpPeerRegistry.DirectoryPeerTtl + TimeSpan.FromSeconds(1));
            _ = registry.RegisterLocalAndGetKnownPeers(networkId: networkId + 1, localNodeId: 2, advertisedEndpoint: new IPEndPoint(IPAddress.Loopback, 9998));

            Assert.Equal(0, OsUdpPeerRegistry.GetDirectoryPeerCountForTests(networkId));
        }
        finally
        {
            OsUdpPeerRegistry.ClearDirectoryForTests();
        }
    }
}

