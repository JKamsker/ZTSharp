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
                registry.RegisterDiscoveredPeer(networkId, sourceNodeId: (ulong)(i + 1), new IPEndPoint(IPAddress.Loopback, 9999));
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

            registry.RegisterDiscoveredPeer(networkId, sourceNodeId: 1, new IPEndPoint(IPAddress.Loopback, 9999));
            Assert.Equal(1, OsUdpPeerRegistry.GetDirectoryPeerCountForTests(networkId));

            time.Advance(OsUdpPeerRegistry.DirectoryPeerTtl + TimeSpan.FromSeconds(1));
            registry.Cleanup();

            Assert.Equal(0, OsUdpPeerRegistry.GetDirectoryPeerCountForTests(networkId));
        }
        finally
        {
            OsUdpPeerRegistry.ClearDirectoryForTests();
        }
    }
}

