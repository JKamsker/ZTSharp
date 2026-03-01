using System.Net;
using ZTSharp.Transport.Internal;

namespace ZTSharp.Tests;

[Collection("OsUdpPeerRegistry")]
public sealed class OsUdpPeerRegistryLastSeenTests
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
    public void RefreshPeerLastSeen_KeepsActivePeerFromExpiring()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new OsUdpPeerRegistry(enablePeerDiscovery: false, UdpEndpointNormalization.Normalize, timeProvider: time);
        var networkId = 0x1111UL;
        var peerNodeId = 0x2222UL;
        var endpoint = new IPEndPoint(IPAddress.Loopback, 9999);

        registry.AddOrUpdatePeer(networkId, peerNodeId, endpoint);

        time.Advance(TimeSpan.FromMinutes(4));
        registry.RefreshPeerLastSeen(networkId, peerNodeId);

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(registry.TryGetPeers(networkId, out var peers));
        Assert.True(peers.ContainsKey(peerNodeId));

        time.Advance(TimeSpan.FromMinutes(10));
        Assert.True(registry.TryGetPeers(networkId, out peers));
        Assert.False(peers.ContainsKey(peerNodeId));
    }
}

