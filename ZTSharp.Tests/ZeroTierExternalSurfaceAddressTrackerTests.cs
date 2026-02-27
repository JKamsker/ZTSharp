using System.Net;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.Tests;

public sealed class ZeroTierExternalSurfaceAddressTrackerTests
{
    [Fact]
    public void Observe_TracksDistinctSurfaceAddressesPerLocalSocket()
    {
        var now = 1_000L;
        var tracker = new ZeroTierExternalSurfaceAddressTracker(ttl: TimeSpan.FromSeconds(10), nowUnixMs: () => now);

        var localSocketId = 2;
        tracker.Observe(new NodeId(0x1111111111), localSocketId, new IPEndPoint(IPAddress.Parse("198.51.100.1"), 10000));
        tracker.Observe(new NodeId(0x2222222222), localSocketId, new IPEndPoint(IPAddress.Parse("198.51.100.1"), 10000));
        tracker.Observe(new NodeId(0x3333333333), localSocketId, new IPEndPoint(IPAddress.Parse("198.51.100.2"), 10001));

        var snapshot = tracker.GetSnapshot(localSocketId);
        Assert.Equal(2, snapshot.Length);
    }

    [Fact]
    public void Observe_ExpiresEntries()
    {
        var now = 1_000L;
        var tracker = new ZeroTierExternalSurfaceAddressTracker(ttl: TimeSpan.FromMilliseconds(100), nowUnixMs: () => now);

        var localSocketId = 2;
        tracker.Observe(new NodeId(0x1111111111), localSocketId, new IPEndPoint(IPAddress.Parse("198.51.100.1"), 10000));
        Assert.Single(tracker.GetSnapshot(localSocketId));

        now += 2_000;
        Assert.Empty(tracker.GetSnapshot(localSocketId));
    }
}

