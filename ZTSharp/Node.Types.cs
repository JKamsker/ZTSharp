using System.Collections;
using System.Collections.Concurrent;

namespace ZTSharp;

internal sealed class NetworkIdReadOnlyCollection : IReadOnlyCollection<ulong>
{
    private readonly ConcurrentDictionary<ulong, NetworkInfo> _source;

    public NetworkIdReadOnlyCollection(ConcurrentDictionary<ulong, NetworkInfo> source)
    {
        _source = source;
    }

    public int Count => _source.Count;

    public IEnumerator<ulong> GetEnumerator() => _source.Keys.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed record NetworkInfo(ulong NetworkId, DateTimeOffset JoinedAt);

public sealed record NetworkState(ulong NetworkId, DateTimeOffset JoinedAt, NetworkStateState State);

public enum NetworkStateState
{
    Joined
}

public enum NodeState
{
    Created,
    Starting,
    Running,
    Stopping,
    Stopped,
    Faulted
}

