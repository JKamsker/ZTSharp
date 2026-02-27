using System.Net;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.Tests;

public sealed class ZeroTierPeerBondPolicyEngineTests
{
    [Fact]
    public void BalanceXor_SelectsStableIndex()
    {
        var peer = new NodeId(0x1111111111);
        var epA = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 1001);
        var epB = new IPEndPoint(IPAddress.Parse("203.0.113.2"), 1002);

        var paths = new[]
        {
            new ZeroTierPeerPhysicalPath(LocalSocketId: 2, epA, LastSeenUnixMs: 2),
            new ZeroTierPeerPhysicalPath(LocalSocketId: 1, epB, LastSeenUnixMs: 1),
        };

        var engine = new ZeroTierPeerBondPolicyEngine(
            getLatencyMs: (_, _, _) => null,
            getRemoteUtility: (_, _, _) => 0);

        Assert.True(engine.TrySelectSinglePath(peer, (ZeroTierPeerPhysicalPath[])paths.Clone(), flowId: 0, ZeroTierBondPolicy.BalanceXor, out var sel0));
        Assert.Equal(1, sel0.LocalSocketId);
        Assert.Equal(epB, sel0.RemoteEndPoint);

        Assert.True(engine.TrySelectSinglePath(peer, (ZeroTierPeerPhysicalPath[])paths.Clone(), flowId: 1, ZeroTierBondPolicy.BalanceXor, out var sel1));
        Assert.Equal(2, sel1.LocalSocketId);
        Assert.Equal(epA, sel1.RemoteEndPoint);
    }

    [Fact]
    public void BalanceRoundRobin_RotatesAcrossPaths()
    {
        var peer = new NodeId(0x1111111111);
        var epA = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 1001);
        var epB = new IPEndPoint(IPAddress.Parse("203.0.113.2"), 1002);

        var paths = new[]
        {
            new ZeroTierPeerPhysicalPath(LocalSocketId: 1, epA, LastSeenUnixMs: 1),
            new ZeroTierPeerPhysicalPath(LocalSocketId: 2, epB, LastSeenUnixMs: 1),
        };

        var engine = new ZeroTierPeerBondPolicyEngine(
            getLatencyMs: (_, _, _) => null,
            getRemoteUtility: (_, _, _) => 0);

        Assert.True(engine.TrySelectSinglePath(peer, (ZeroTierPeerPhysicalPath[])paths.Clone(), flowId: 0, ZeroTierBondPolicy.BalanceRoundRobin, out var s0));
        Assert.True(engine.TrySelectSinglePath(peer, (ZeroTierPeerPhysicalPath[])paths.Clone(), flowId: 0, ZeroTierBondPolicy.BalanceRoundRobin, out var s1));
        Assert.NotEqual(s0, s1);
    }

    [Fact]
    public void ActiveBackup_PrefersLowestLatency()
    {
        var peer = new NodeId(0x1111111111);
        var epFast = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 1001);
        var epSlow = new IPEndPoint(IPAddress.Parse("203.0.113.2"), 1002);

        var paths = new[]
        {
            new ZeroTierPeerPhysicalPath(LocalSocketId: 1, epSlow, LastSeenUnixMs: 1),
            new ZeroTierPeerPhysicalPath(LocalSocketId: 2, epFast, LastSeenUnixMs: 1),
        };

        var latency = new Dictionary<IPEndPoint, int>
        {
            [epFast] = 10,
            [epSlow] = 100
        };

        var engine = new ZeroTierPeerBondPolicyEngine(
            getLatencyMs: (_, _, ep) => latency.TryGetValue(ep, out var l) ? l : null,
            getRemoteUtility: (_, _, _) => 0);

        Assert.True(engine.TrySelectSinglePath(peer, (ZeroTierPeerPhysicalPath[])paths.Clone(), flowId: 0, ZeroTierBondPolicy.ActiveBackup, out var selected));
        Assert.Equal(epFast, selected.RemoteEndPoint);
    }

    [Fact]
    public void ActiveBackup_IsSticky_AndSwitchesAfterHold()
    {
        var peer = new NodeId(0x1111111111);
        var epA = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 1001);
        var epB = new IPEndPoint(IPAddress.Parse("203.0.113.2"), 1002);

        var paths = new[]
        {
            new ZeroTierPeerPhysicalPath(LocalSocketId: 1, epA, LastSeenUnixMs: 1),
            new ZeroTierPeerPhysicalPath(LocalSocketId: 2, epB, LastSeenUnixMs: 1),
        };

        var now = 1_000L;
        var latency = new Dictionary<IPEndPoint, int>
        {
            [epA] = 10,
            [epB] = 100
        };

        var engine = new ZeroTierPeerBondPolicyEngine(
            getLatencyMs: (_, _, ep) => latency.TryGetValue(ep, out var l) ? l : null,
            getRemoteUtility: (_, _, _) => 0,
            nowMs: () => now);

        Assert.True(engine.TrySelectSinglePath(peer, (ZeroTierPeerPhysicalPath[])paths.Clone(), flowId: 0, ZeroTierBondPolicy.ActiveBackup, out var s0));
        Assert.Equal(epA, s0.RemoteEndPoint);

        latency[epA] = 200;
        latency[epB] = 10;
        now += 1_000;

        Assert.True(engine.TrySelectSinglePath(peer, (ZeroTierPeerPhysicalPath[])paths.Clone(), flowId: 0, ZeroTierBondPolicy.ActiveBackup, out var s1));
        Assert.Equal(epA, s1.RemoteEndPoint);

        now += 20_000;
        Assert.True(engine.TrySelectSinglePath(peer, (ZeroTierPeerPhysicalPath[])paths.Clone(), flowId: 0, ZeroTierBondPolicy.ActiveBackup, out var s2));
        Assert.Equal(epB, s2.RemoteEndPoint);
    }

    [Fact]
    public void BalanceAware_ChoosesOnlyWithinSlack()
    {
        var peer = new NodeId(0x1111111111);
        var epFast = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 1001);
        var epSlow = new IPEndPoint(IPAddress.Parse("203.0.113.2"), 1002);

        var paths = new[]
        {
            new ZeroTierPeerPhysicalPath(LocalSocketId: 1, epFast, LastSeenUnixMs: 1),
            new ZeroTierPeerPhysicalPath(LocalSocketId: 2, epSlow, LastSeenUnixMs: 1),
        };

        var latency = new Dictionary<IPEndPoint, int>
        {
            [epFast] = 10,
            [epSlow] = 100
        };

        var engine = new ZeroTierPeerBondPolicyEngine(
            getLatencyMs: (_, _, ep) => latency.TryGetValue(ep, out var l) ? l : null,
            getRemoteUtility: (_, _, _) => 0,
            nowMs: () => 1_000);

        Assert.True(engine.TrySelectSinglePath(peer, (ZeroTierPeerPhysicalPath[])paths.Clone(), flowId: 0, ZeroTierBondPolicy.BalanceAware, out var s0));
        Assert.True(engine.TrySelectSinglePath(peer, (ZeroTierPeerPhysicalPath[])paths.Clone(), flowId: 123, ZeroTierBondPolicy.BalanceAware, out var s1));
        Assert.Equal(epFast, s0.RemoteEndPoint);
        Assert.Equal(epFast, s1.RemoteEndPoint);
    }

    [Fact]
    public void Broadcast_ReturnsStableSortedPaths()
    {
        var epA = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 1001);
        var epB = new IPEndPoint(IPAddress.Parse("203.0.113.2"), 1002);
        var paths = new[]
        {
            new ZeroTierPeerPhysicalPath(LocalSocketId: 2, epB, LastSeenUnixMs: 1),
            new ZeroTierPeerPhysicalPath(LocalSocketId: 1, epA, LastSeenUnixMs: 1),
        };

        var engine = new ZeroTierPeerBondPolicyEngine(
            getLatencyMs: (_, _, _) => null,
            getRemoteUtility: (_, _, _) => 0);

        var broadcast = ZeroTierPeerBondPolicyEngine.GetBroadcastPaths((ZeroTierPeerPhysicalPath[])paths.Clone());
        Assert.Equal(2, broadcast.Length);
        Assert.Equal(1, broadcast[0].LocalSocketId);
        Assert.Equal(epA, broadcast[0].RemoteEndPoint);
        Assert.Equal(2, broadcast[1].LocalSocketId);
        Assert.Equal(epB, broadcast[1].RemoteEndPoint);
    }
}
