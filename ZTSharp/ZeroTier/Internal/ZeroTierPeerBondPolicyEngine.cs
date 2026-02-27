using System.Collections.Concurrent;
using System.Net;
using ZTSharp.ZeroTier;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierPeerBondPolicyEngine
{
    private const long AwareFlowTtlMs = 120_000;
    private const int AwareLatencySlackMs = 25;
    private const long ActiveBackupMinHoldMs = 10_000;
    private const int ActiveBackupLatencySlackMs = 25;
    private const long PeerStateTtlMs = 600_000;

    private readonly Func<NodeId, int, IPEndPoint, int?> _getLatencyMs;
    private readonly Func<NodeId, int, IPEndPoint, short> _getRemoteUtility;
    private readonly Func<long> _nowMs;

    private readonly ConcurrentDictionary<NodeId, PeerState> _peerStates = new();

    public ZeroTierPeerBondPolicyEngine(
        Func<NodeId, int, IPEndPoint, int?> getLatencyMs,
        Func<NodeId, int, IPEndPoint, short> getRemoteUtility,
        Func<long>? nowMs = null)
    {
        _getLatencyMs = getLatencyMs ?? throw new ArgumentNullException(nameof(getLatencyMs));
        _getRemoteUtility = getRemoteUtility ?? throw new ArgumentNullException(nameof(getRemoteUtility));
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    public bool TrySelectSinglePath(
        NodeId peerNodeId,
        ZeroTierPeerPhysicalPath[] observedPaths,
        uint flowId,
        ZeroTierBondPolicy policy,
        out ZeroTierSelectedPeerPath selected)
    {
        if (observedPaths.Length == 0)
        {
            selected = default;
            return false;
        }

        if (observedPaths.Length == 1)
        {
            selected = new ZeroTierSelectedPeerPath(observedPaths[0].LocalSocketId, observedPaths[0].RemoteEndPoint);
            return true;
        }

        switch (policy)
        {
            case ZeroTierBondPolicy.BalanceXor:
                StableSort(observedPaths);
                return SelectByIndex(observedPaths, index: (int)(flowId % (uint)observedPaths.Length), out selected);

            case ZeroTierBondPolicy.BalanceRoundRobin:
                StableSort(observedPaths);
                var state = _peerStates.GetOrAdd(peerNodeId, static _ => new PeerState());
                Volatile.Write(ref state.LastUsedMs, _nowMs());
                var rr = Interlocked.Increment(ref state.RoundRobinCounter);
                return SelectByIndex(observedPaths, index: (int)((uint)rr % (uint)observedPaths.Length), out selected);

            case ZeroTierBondPolicy.BalanceAware:
                StableSort(observedPaths);
                return SelectBalanceAware(peerNodeId, observedPaths, flowId, out selected);

            case ZeroTierBondPolicy.ActiveBackup:
                StableSort(observedPaths);
                return SelectActiveBackup(peerNodeId, observedPaths, out selected);

            case ZeroTierBondPolicy.Off:
            default:
                return SelectBest(peerNodeId, observedPaths, out selected);
        }
    }

    public void MaintenanceTick()
    {
        var now = _nowMs();
        foreach (var pair in _peerStates)
        {
            var state = pair.Value;
            CleanupFlowsIfNeeded(state, now);

            var lastUsed = Volatile.Read(ref state.LastUsedMs);
            if (lastUsed != 0 &&
                unchecked(now - lastUsed) > PeerStateTtlMs &&
                state.Flows.IsEmpty)
            {
                _peerStates.TryRemove(pair.Key, out _);
            }
        }
    }

    public static ZeroTierSelectedPeerPath[] GetBroadcastPaths(ZeroTierPeerPhysicalPath[] observedPaths)
    {
        if (observedPaths.Length == 0)
        {
            return Array.Empty<ZeroTierSelectedPeerPath>();
        }

        StableSort(observedPaths);
        var paths = new ZeroTierSelectedPeerPath[observedPaths.Length];
        for (var i = 0; i < observedPaths.Length; i++)
        {
            paths[i] = new ZeroTierSelectedPeerPath(observedPaths[i].LocalSocketId, observedPaths[i].RemoteEndPoint);
        }

        return paths;
    }

    private static bool SelectByIndex(
        ZeroTierPeerPhysicalPath[] observedPaths,
        int index,
        out ZeroTierSelectedPeerPath selected)
    {
        if ((uint)index >= (uint)observedPaths.Length)
        {
            selected = default;
            return false;
        }

        selected = new ZeroTierSelectedPeerPath(observedPaths[index].LocalSocketId, observedPaths[index].RemoteEndPoint);
        return true;
    }

    private bool SelectBest(NodeId peerNodeId, ZeroTierPeerPhysicalPath[] observedPaths, out ZeroTierSelectedPeerPath selected)
    {
        var bestIndex = -1;
        var bestHasLatency = false;
        var bestLatency = int.MaxValue;
        var bestUtility = short.MinValue;

        for (var i = 0; i < observedPaths.Length; i++)
        {
            var path = observedPaths[i];
            var latency = _getLatencyMs(peerNodeId, path.LocalSocketId, path.RemoteEndPoint);
            var remoteUtility = _getRemoteUtility(peerNodeId, path.LocalSocketId, path.RemoteEndPoint);

            if (latency is int latencyMs)
            {
                var better =
                    (!bestHasLatency) ||
                    (latencyMs < bestLatency) ||
                    (latencyMs == bestLatency && remoteUtility > bestUtility) ||
                    (latencyMs == bestLatency && remoteUtility == bestUtility && (bestIndex < 0 || StableComparer.Instance.Compare(path, observedPaths[bestIndex]) < 0));

                if (better)
                {
                    bestIndex = i;
                    bestHasLatency = true;
                    bestLatency = latencyMs;
                    bestUtility = remoteUtility;
                }
            }
            else if (!bestHasLatency)
            {
                var better =
                    (bestIndex < 0) ||
                    (remoteUtility > bestUtility) ||
                    (remoteUtility == bestUtility && (bestIndex < 0 || StableComparer.Instance.Compare(path, observedPaths[bestIndex]) < 0));

                if (better)
                {
                    bestIndex = i;
                    bestUtility = remoteUtility;
                }
            }
        }

        if (bestIndex < 0)
        {
            selected = default;
            return false;
        }

        selected = new ZeroTierSelectedPeerPath(observedPaths[bestIndex].LocalSocketId, observedPaths[bestIndex].RemoteEndPoint);
        return true;
    }

    private bool SelectActiveBackup(NodeId peerNodeId, ZeroTierPeerPhysicalPath[] observedPaths, out ZeroTierSelectedPeerPath selected)
    {
        var now = _nowMs();
        var state = _peerStates.GetOrAdd(peerNodeId, static _ => new PeerState());
        Volatile.Write(ref state.LastUsedMs, now);

        lock (state.Gate)
        {
            if (state.ActiveBackupPath is { } existing && TryFindPath(observedPaths, existing, out var existingPath))
            {
                selected = new ZeroTierSelectedPeerPath(existingPath.LocalSocketId, existingPath.RemoteEndPoint);

                if (state.ActiveBackupSelectedAtMs != 0 && unchecked(now - state.ActiveBackupSelectedAtMs) < ActiveBackupMinHoldMs)
                {
                    return true;
                }

                if (!SelectBest(peerNodeId, observedPaths, out var best) || best == selected)
                {
                    return true;
                }

                if (!ShouldSwitchActiveBackup(peerNodeId, existingPath, best))
                {
                    return true;
                }

                state.ActiveBackupPath = new ZeroTierPeerPhysicalPathKey(best.LocalSocketId, best.RemoteEndPoint);
                state.ActiveBackupSelectedAtMs = now;
                selected = best;
                return true;
            }

            if (!SelectBest(peerNodeId, observedPaths, out selected))
            {
                return false;
            }

            state.ActiveBackupPath = new ZeroTierPeerPhysicalPathKey(selected.LocalSocketId, selected.RemoteEndPoint);
            state.ActiveBackupSelectedAtMs = now;
            return true;
        }
    }

    private bool ShouldSwitchActiveBackup(NodeId peerNodeId, ZeroTierPeerPhysicalPath current, ZeroTierSelectedPeerPath best)
    {
        var bestLatency = _getLatencyMs(peerNodeId, best.LocalSocketId, best.RemoteEndPoint);
        var currentLatency = _getLatencyMs(peerNodeId, current.LocalSocketId, current.RemoteEndPoint);

        if (bestLatency is int bestLatencyMs)
        {
            if (currentLatency is not int currentLatencyMs)
            {
                return true;
            }

            if (bestLatencyMs + ActiveBackupLatencySlackMs < currentLatencyMs)
            {
                return true;
            }
        }
        else if (currentLatency is int)
        {
            return false;
        }

        var bestUtility = _getRemoteUtility(peerNodeId, best.LocalSocketId, best.RemoteEndPoint);
        var currentUtility = _getRemoteUtility(peerNodeId, current.LocalSocketId, current.RemoteEndPoint);
        return bestUtility > currentUtility;
    }

    private static bool TryFindPath(ZeroTierPeerPhysicalPath[] observedPaths, ZeroTierPeerPhysicalPathKey key, out ZeroTierPeerPhysicalPath path)
    {
        for (var i = 0; i < observedPaths.Length; i++)
        {
            if (observedPaths[i].LocalSocketId == key.LocalSocketId && observedPaths[i].RemoteEndPoint.Equals(key.RemoteEndPoint))
            {
                path = observedPaths[i];
                return true;
            }
        }

        path = default;
        return false;
    }

    private bool SelectBalanceAware(
        NodeId peerNodeId,
        ZeroTierPeerPhysicalPath[] observedPaths,
        uint flowId,
        out ZeroTierSelectedPeerPath selected)
    {
        var now = _nowMs();
        var state = _peerStates.GetOrAdd(peerNodeId, static _ => new PeerState());
        Volatile.Write(ref state.LastUsedMs, now);

        CleanupFlowsIfNeeded(state, now);

        if (state.Flows.TryGetValue(flowId, out var existing))
        {
            if (unchecked(now - existing.LastUsedMs) <= AwareFlowTtlMs)
            {
                for (var i = 0; i < observedPaths.Length; i++)
                {
                    var path = observedPaths[i];
                    if (path.LocalSocketId == existing.Path.LocalSocketId && path.RemoteEndPoint.Equals(existing.Path.RemoteEndPoint))
                    {
                        state.Flows[flowId] = existing with { LastUsedMs = now };
                        selected = new ZeroTierSelectedPeerPath(path.LocalSocketId, path.RemoteEndPoint);
                        return true;
                    }
                }
            }
        }

        var bestLatency = int.MaxValue;
        for (var i = 0; i < observedPaths.Length; i++)
        {
            var path = observedPaths[i];
            if (_getLatencyMs(peerNodeId, path.LocalSocketId, path.RemoteEndPoint) is int latency && latency < bestLatency)
            {
                bestLatency = latency;
            }
        }

        if (bestLatency == int.MaxValue)
        {
            return SelectByIndex(observedPaths, index: (int)(flowId % (uint)observedPaths.Length), out selected);
        }

        Span<int> eligible = stackalloc int[Math.Min(observedPaths.Length, 64)];
        var eligibleCount = 0;
        var cutoff = bestLatency + AwareLatencySlackMs;

        for (var i = 0; i < observedPaths.Length && eligibleCount < eligible.Length; i++)
        {
            var path = observedPaths[i];
            if (_getLatencyMs(peerNodeId, path.LocalSocketId, path.RemoteEndPoint) is int latency && latency <= cutoff)
            {
                eligible[eligibleCount++] = i;
            }
        }

        if (eligibleCount <= 0)
        {
            return SelectByIndex(observedPaths, index: (int)(flowId % (uint)observedPaths.Length), out selected);
        }

        var index = eligible[eligibleCount == 1 ? 0 : (int)(flowId % (uint)eligibleCount)];
        if (!SelectByIndex(observedPaths, index, out selected))
        {
            return false;
        }

        state.Flows[flowId] = new FlowAssignment(new ZeroTierPeerPhysicalPathKey(selected.LocalSocketId, selected.RemoteEndPoint), LastUsedMs: now);
        return true;
    }

    private static void CleanupFlowsIfNeeded(PeerState state, long now)
    {
        var last = Volatile.Read(ref state.LastFlowCleanupMs);
        if (last != 0 && unchecked(now - last) < 10_000)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref state.LastFlowCleanupMs, now, last) != last)
        {
            return;
        }

        foreach (var pair in state.Flows)
        {
            if (unchecked(now - pair.Value.LastUsedMs) > AwareFlowTtlMs)
            {
                state.Flows.TryRemove(pair.Key, out _);
            }
        }
    }

    private static void StableSort(ZeroTierPeerPhysicalPath[] observedPaths)
        => Array.Sort(observedPaths, StableComparer.Instance);

    private sealed class PeerState
    {
        public object Gate { get; } = new();
        public int RoundRobinCounter;
        public ConcurrentDictionary<uint, FlowAssignment> Flows { get; } = new();
        public long LastFlowCleanupMs;
        public ZeroTierPeerPhysicalPathKey? ActiveBackupPath;
        public long ActiveBackupSelectedAtMs;
        public long LastUsedMs;
    }

    private readonly record struct FlowAssignment(ZeroTierPeerPhysicalPathKey Path, long LastUsedMs);

    private sealed class StableComparer : IComparer<ZeroTierPeerPhysicalPath>
    {
        public static StableComparer Instance { get; } = new();

        public int Compare(ZeroTierPeerPhysicalPath x, ZeroTierPeerPhysicalPath y)
        {
            var c = x.LocalSocketId.CompareTo(y.LocalSocketId);
            if (c != 0)
            {
                return c;
            }

            c = CompareEndpoints(x.RemoteEndPoint, y.RemoteEndPoint);
            if (c != 0)
            {
                return c;
            }

            return 0;
        }

        private static int CompareEndpoints(IPEndPoint x, IPEndPoint y)
        {
            var familyCompare = x.AddressFamily.CompareTo(y.AddressFamily);
            if (familyCompare != 0)
            {
                return familyCompare;
            }

            var xb = x.Address.GetAddressBytes();
            var yb = y.Address.GetAddressBytes();
            var len = Math.Min(xb.Length, yb.Length);
            for (var i = 0; i < len; i++)
            {
                var b = xb[i].CompareTo(yb[i]);
                if (b != 0)
                {
                    return b;
                }
            }

            if (xb.Length != yb.Length)
            {
                return xb.Length.CompareTo(yb.Length);
            }

            if (x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                var scopeCompare = x.Address.ScopeId.CompareTo(y.Address.ScopeId);
                if (scopeCompare != 0)
                {
                    return scopeCompare;
                }
            }

            return x.Port.CompareTo(y.Port);
        }
    }
}
