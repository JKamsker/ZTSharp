using System.Collections.Concurrent;
using System.Net;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ManagedIpToNodeIdCache
{
    private readonly int _capacity;
    private readonly long _resolvedTtlMs;
    private readonly long _learnedTtlMs;
    private readonly Func<long> _getNowMs;

    private readonly ConcurrentDictionary<IPAddress, Entry> _entries = new();
    private readonly ConcurrentQueue<IPAddress> _evictionQueue = new();

    public ManagedIpToNodeIdCache(
        int capacity = 1024,
        TimeSpan? resolvedTtl = null,
        TimeSpan? learnedTtl = null,
        Func<long>? getNowMs = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be greater than zero.");
        }

        _capacity = capacity;
        _resolvedTtlMs = (long)(resolvedTtl ?? TimeSpan.FromMinutes(5)).TotalMilliseconds;
        _learnedTtlMs = (long)(learnedTtl ?? TimeSpan.FromMinutes(1)).TotalMilliseconds;
        _getNowMs = getNowMs ?? (() => Environment.TickCount64);
    }

    public int Count => _entries.Count;

    public bool TryGet(IPAddress managedIp, out NodeId nodeId)
    {
        ArgumentNullException.ThrowIfNull(managedIp);

        nodeId = default;
        if (!_entries.TryGetValue(managedIp, out var entry))
        {
            return false;
        }

        if (IsExpired(entry))
        {
            _entries.TryRemove(managedIp, out _);
            return false;
        }

        nodeId = entry.NodeId;
        return true;
    }

    public void SetResolved(IPAddress managedIp, NodeId nodeId)
        => Set(managedIp, nodeId, isAuthoritative: true);

    public void LearnFromNeighbor(IPAddress managedIp, NodeId nodeId)
        => Set(managedIp, nodeId, isAuthoritative: false);

    private void Set(IPAddress managedIp, NodeId nodeId, bool isAuthoritative)
    {
        ArgumentNullException.ThrowIfNull(managedIp);

        var now = _getNowMs();
        var ttl = isAuthoritative ? _resolvedTtlMs : _learnedTtlMs;
        var expiresAt = unchecked(now + ttl);

        _entries.AddOrUpdate(
            managedIp,
            _ =>
            {
                EnqueueForEviction(managedIp);
                return new Entry(nodeId, expiresAt, isAuthoritative);
            },
            (_, existing) =>
            {
                if (IsExpired(existing))
                {
                    return new Entry(nodeId, expiresAt, isAuthoritative);
                }

                if (!isAuthoritative && existing.IsAuthoritative)
                {
                    return existing;
                }

                return new Entry(nodeId, expiresAt, isAuthoritative);
            });

        EnforceCapacity();
    }

    private void EnqueueForEviction(IPAddress managedIp)
        => _evictionQueue.Enqueue(managedIp);

    private void EnforceCapacity()
    {
        while (_entries.Count > _capacity && _evictionQueue.TryDequeue(out var key))
        {
            _entries.TryRemove(key, out _);
        }
    }

    private bool IsExpired(Entry entry)
    {
        var now = _getNowMs();
        return unchecked(now - entry.ExpiresAtMs) >= 0;
    }

    private readonly record struct Entry(NodeId NodeId, long ExpiresAtMs, bool IsAuthoritative);
}

