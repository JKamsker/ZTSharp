using System.Collections.Concurrent;

namespace JKamsker.LibZt.Transport;

/// <summary>
/// In-process transport used as M2 offline implementation backbone.
/// </summary>
internal sealed class InMemoryNodeTransport : IZtNodeTransport
{
    private sealed record Subscriber(
        ulong NodeId,
        Func<ulong, ulong, byte[], CancellationToken, Task> OnFrameReceived);

    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<Guid, Subscriber>> _networkSubscribers = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<Guid> JoinNetworkAsync(
        ulong networkId,
        ulong nodeId,
        Func<ulong, ulong, byte[], CancellationToken, Task> onFrameReceived,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(nodeId);
        ArgumentNullException.ThrowIfNull(onFrameReceived);
        cancellationToken.ThrowIfCancellationRequested();

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var registrationId = Guid.NewGuid();
            var subscribers = _networkSubscribers.GetOrAdd(
                networkId,
                _ => new ConcurrentDictionary<Guid, Subscriber>());

            subscribers[registrationId] = new Subscriber(nodeId, onFrameReceived);
            return registrationId;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task LeaveNetworkAsync(ulong networkId, Guid registrationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_networkSubscribers.TryGetValue(networkId, out var subscribers))
            {
                return;
            }

            subscribers.TryRemove(registrationId, out _);
            if (subscribers.IsEmpty)
            {
                _networkSubscribers.TryRemove(networkId, out _);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SendFrameAsync(
        ulong networkId,
        ulong sourceNodeId,
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sourceNodeId);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(payload);

        if (!_networkSubscribers.TryGetValue(networkId, out var subscribers))
        {
            return;
        }

        var frameTasks = new List<Task>();
        var copy = subscribers.ToArray();
        foreach (var subscriber in copy)
        {
            if (subscriber.Value.NodeId == sourceNodeId)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            frameTasks.Add(subscriber.Value.OnFrameReceived(sourceNodeId, networkId, payload.ToArray(), cancellationToken));
        }

        await Task.WhenAll(frameTasks).ConfigureAwait(false);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
