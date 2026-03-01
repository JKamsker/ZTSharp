using System.Net;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.Tests;

public sealed class ZeroTierPeerPathNegotiationManagerTests
{
    [Fact]
    public void HandleInboundRequest_StoresUtilityPerPath()
    {
        var now = 1_000L;
        var mgr = new ZeroTierPeerPathNegotiationManager(nowMs: () => now);

        var peerNodeId = new NodeId(0x1111111111);
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 9999);

        mgr.HandleInboundRequest(peerNodeId, localSocketId: 2, endpoint, remoteUtility: 123);

        Assert.True(mgr.TryGetRemoteUtility(peerNodeId, localSocketId: 2, endpoint, out var utility));
        Assert.Equal((short)123, utility);
    }

    [Fact]
    public void TryGetRemoteUtility_DoesNotReturnStaleUtilityBeyondTtl()
    {
        var now = 1_000L;
        var mgr = new ZeroTierPeerPathNegotiationManager(nowMs: () => now);

        var peerNodeId = new NodeId(0x1111111111);
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 9999);

        mgr.HandleInboundRequest(peerNodeId, localSocketId: 2, endpoint, remoteUtility: 123);

        now += 300_001;

        Assert.False(mgr.TryGetRemoteUtility(peerNodeId, localSocketId: 2, endpoint, out _));
    }

    [Fact]
    public void TryMarkSent_RateLimitsPerPath()
    {
        var now = 1_000L;
        var mgr = new ZeroTierPeerPathNegotiationManager(nowMs: () => now);

        var peerNodeId = new NodeId(0x1111111111);
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 9999);

        Assert.True(mgr.TryMarkSent(peerNodeId, localSocketId: 2, endpoint));
        Assert.False(mgr.TryMarkSent(peerNodeId, localSocketId: 2, endpoint));

        now += 10_000;
        Assert.True(mgr.TryMarkSent(peerNodeId, localSocketId: 2, endpoint));
    }
}

