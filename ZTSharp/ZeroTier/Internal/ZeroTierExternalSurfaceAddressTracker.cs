using System.Collections.Concurrent;
using System.Net;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierExternalSurfaceAddressTracker
{
    private readonly TimeSpan _ttl;
    private readonly Func<long> _nowUnixMs;
    private readonly ConcurrentDictionary<ZeroTierExternalSurfaceKey, Entry> _entries = new();
    private long _lastCleanupUnixMs;

    public ZeroTierExternalSurfaceAddressTracker(TimeSpan ttl, Func<long>? nowUnixMs = null)
    {
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl, "TTL must be positive.");
        }

        _ttl = ttl;
        _nowUnixMs = nowUnixMs ?? (() => Environment.TickCount64);
    }

    public void Observe(NodeId reportingPeerNodeId, int localSocketId, IPEndPoint surfaceAddress)
    {
        ArgumentNullException.ThrowIfNull(surfaceAddress);

        var now = _nowUnixMs();
        var key = new ZeroTierExternalSurfaceKey(localSocketId, reportingPeerNodeId);
        var stored = new IPEndPoint(surfaceAddress.Address, surfaceAddress.Port);
        _entries[key] = new Entry(stored, now);
        CleanupIfNeeded(now);
    }

    public IPEndPoint[] GetSnapshot(int localSocketId)
    {
        var now = _nowUnixMs();
        CleanupIfNeeded(now);

        var list = new List<IPEndPoint>();
        foreach (var (key, entry) in _entries)
        {
            if (key.LocalSocketId == localSocketId)
            {
                list.Add(new IPEndPoint(entry.SurfaceAddress.Address, entry.SurfaceAddress.Port));
            }
        }

        return list
            .Distinct()
            .ToArray();
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
        foreach (var (key, entry) in _entries)
        {
            if (entry.LastSeenUnixMs <= expiresBefore)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private readonly record struct Entry(IPEndPoint SurfaceAddress, long LastSeenUnixMs);
}

internal readonly record struct ZeroTierExternalSurfaceKey(int LocalSocketId, NodeId ReportingPeerNodeId);

