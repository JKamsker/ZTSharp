using System.Net;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.Tests;

public sealed class ZeroTierPeerPhysicalPathTrackerTests
{
    [Fact]
    public void ObserveHop0_TracksAndExpiresPaths()
    {
        var now = 1_000L;
        var tracker = new ZeroTierPeerPhysicalPathTracker(ttl: TimeSpan.FromMilliseconds(100), nowUnixMs: () => now);

        var peer = new NodeId(0x123456789a);
        var path = new IPEndPoint(IPAddress.Loopback, 9999);

        tracker.ObserveHop0(peer, localSocketId: 2, path);
        Assert.Single(tracker.GetSnapshot(peer));

        now += 2_000;
        Assert.Empty(tracker.GetSnapshot(peer));
    }

    [Fact]
    public void ObserveHop0_TracksMultipleSocketsAndEndpoints()
    {
        var now = 1_000L;
        var tracker = new ZeroTierPeerPhysicalPathTracker(ttl: TimeSpan.FromSeconds(10), nowUnixMs: () => now);

        var peer = new NodeId(0x1111111111);
        tracker.ObserveHop0(peer, localSocketId: 0, new IPEndPoint(IPAddress.Loopback, 10000));
        tracker.ObserveHop0(peer, localSocketId: 1, new IPEndPoint(IPAddress.Loopback, 10001));

        var snapshot = tracker.GetSnapshot(peer);
        Assert.Equal(2, snapshot.Length);
        Assert.Contains(snapshot, p => p.LocalSocketId == 0 && p.RemoteEndPoint.Port == 10000);
        Assert.Contains(snapshot, p => p.LocalSocketId == 1 && p.RemoteEndPoint.Port == 10001);
    }
}
