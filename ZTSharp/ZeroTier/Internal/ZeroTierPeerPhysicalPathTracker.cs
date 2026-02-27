using System.Collections.Concurrent;
using System.Net;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierPeerPhysicalPathTracker
{
    private readonly TimeSpan _ttl;
    private readonly Func<long> _nowUnixMs;
    private readonly ConcurrentDictionary<NodeId, PeerState> _peers = new();
    private long _lastCleanupUnixMs;

    public ZeroTierPeerPhysicalPathTracker(TimeSpan ttl, Func<long>? nowUnixMs = null)
    {
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl, "TTL must be positive.");
        }

        _ttl = ttl;
        _nowUnixMs = nowUnixMs ?? (() => Environment.TickCount64);
    }

    public void ObserveHop0(NodeId peerNodeId, int localSocketId, IPEndPoint remoteEndPoint)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);

        var now = _nowUnixMs();
        var peer = _peers.GetOrAdd(peerNodeId, _ => new PeerState());
        peer.Paths[new ZeroTierPeerPhysicalPathKey(localSocketId, remoteEndPoint)] = now;

        CleanupIfNeeded(now);
    }

    public ZeroTierPeerPhysicalPath[] GetSnapshot(NodeId peerNodeId)
    {
        var now = _nowUnixMs();
        CleanupIfNeeded(now);

        if (!_peers.TryGetValue(peerNodeId, out var peer))
        {
            return Array.Empty<ZeroTierPeerPhysicalPath>();
        }

        return peer.Paths
            .Select(pair => new ZeroTierPeerPhysicalPath(pair.Key.LocalSocketId, pair.Key.RemoteEndPoint, pair.Value))
            .ToArray();
    }

    public NodeId[] GetPeersSnapshot()
    {
        var now = _nowUnixMs();
        CleanupIfNeeded(now);
        return _peers.Keys.ToArray();
    }

    private void CleanupIfNeeded(long nowUnixMs)
    {
        var last = Volatile.Read(ref _lastCleanupUnixMs);
        if (unchecked(nowUnixMs - last) < 1000)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastCleanupUnixMs, nowUnixMs, last) != last)
        {
            return;
        }

        var expiresBefore = nowUnixMs - (long)_ttl.TotalMilliseconds;
        foreach (var (peerNodeId, peer) in _peers)
        {
            foreach (var (key, lastSeenUnixMs) in peer.Paths)
            {
                if (lastSeenUnixMs <= expiresBefore)
                {
                    peer.Paths.TryRemove(key, out _);
                }
            }

            if (peer.Paths.IsEmpty)
            {
                _peers.TryRemove(peerNodeId, out _);
            }
        }
    }

    private sealed class PeerState
    {
        public ConcurrentDictionary<ZeroTierPeerPhysicalPathKey, long> Paths { get; } = new();
    }
}

internal readonly record struct ZeroTierPeerPhysicalPathKey(int LocalSocketId, IPEndPoint RemoteEndPoint);

internal readonly record struct ZeroTierPeerPhysicalPath(int LocalSocketId, IPEndPoint RemoteEndPoint, long LastSeenUnixMs);
