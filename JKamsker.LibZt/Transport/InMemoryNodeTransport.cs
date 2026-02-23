using System.Collections.Concurrent;
using System.Net;

namespace JKamsker.LibZt.Transport;

/// <summary>
/// In-process transport used as M2 offline implementation backbone.
/// </summary>
internal sealed class InMemoryNodeTransport : IZtNodeTransport, IDisposable
{
    private sealed record Subscriber(
        ulong NodeId,
        Func<ulong, ulong, ReadOnlyMemory<byte>, CancellationToken, Task> OnFrameReceived);

    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<Guid, Subscriber>> _networkSubscribers = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<Guid> JoinNetworkAsync(
        ulong networkId,
        ulong nodeId,
        Func<ulong, ulong, ReadOnlyMemory<byte>, CancellationToken, Task> onFrameReceived,
        IPEndPoint? localEndpoint = null,
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
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sourceNodeId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_networkSubscribers.TryGetValue(networkId, out var subscribers))
        {
            return;
        }

        Task? firstTask = null;
        List<Task>? frameTasks = null;
        foreach (var subscriber in subscribers)
        {
            if (subscriber.Value.NodeId == sourceNodeId)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var callbackTask = subscriber.Value.OnFrameReceived(sourceNodeId, networkId, payload, cancellationToken);
            if (firstTask is null)
            {
                firstTask = callbackTask;
                continue;
            }

            frameTasks ??= new List<Task>(1 + subscribers.Count);
            if (frameTasks.Count == 0)
            {
                frameTasks.Add(firstTask);
            }
            frameTasks.Add(callbackTask);
        }

        if (frameTasks is not null)
        {
            await Task.WhenAll(frameTasks).ConfigureAwait(false);
            return;
        }

        if (firstTask is not null)
        {
            await firstTask.ConfigureAwait(false);
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _lock.Dispose();
    }
}
