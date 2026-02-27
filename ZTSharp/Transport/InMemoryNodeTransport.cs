using System.Collections.Concurrent;
using System.Net;
using System.Threading;

namespace ZTSharp.Transport;

/// <summary>
/// In-process transport used as M2 offline implementation backbone.
/// </summary>
internal sealed class InMemoryNodeTransport : INodeTransport, IDisposable
{
    private sealed record Subscriber(
        Guid RegistrationId,
        ulong NodeId,
        Func<ulong, ulong, ReadOnlyMemory<byte>, CancellationToken, Task> OnFrameReceived);

    private sealed class NetworkSubscribers
    {
        public readonly object Gate = new();
        public Subscriber[] Subscribers = Array.Empty<Subscriber>();
    }

    private readonly ConcurrentDictionary<ulong, NetworkSubscribers> _networkSubscribers = new();

    public Task<Guid> JoinNetworkAsync(
        ulong networkId,
        ulong nodeId,
        Func<ulong, ulong, ReadOnlyMemory<byte>, CancellationToken, Task> onFrameReceived,
        IPEndPoint? localEndpoint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(nodeId);
        ArgumentNullException.ThrowIfNull(onFrameReceived);
        cancellationToken.ThrowIfCancellationRequested();

        var registrationId = Guid.NewGuid();
        var entry = _networkSubscribers.GetOrAdd(networkId, _ => new NetworkSubscribers());
        lock (entry.Gate)
        {
            var existing = entry.Subscribers;
            var updated = new Subscriber[existing.Length + 1];
            Array.Copy(existing, updated, existing.Length);
            updated[^1] = new Subscriber(registrationId, nodeId, onFrameReceived);
            entry.Subscribers = updated;
        }

        return Task.FromResult(registrationId);
    }

    public Task LeaveNetworkAsync(ulong networkId, Guid registrationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_networkSubscribers.TryGetValue(networkId, out var entry))
        {
            return Task.CompletedTask;
        }

        lock (entry.Gate)
        {
            var existing = entry.Subscribers;
            if (existing.Length == 0)
            {
                return Task.CompletedTask;
            }

            var index = -1;
            for (var i = 0; i < existing.Length; i++)
            {
                if (existing[i].RegistrationId == registrationId)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                return Task.CompletedTask;
            }

            if (existing.Length == 1)
            {
                entry.Subscribers = Array.Empty<Subscriber>();
                return Task.CompletedTask;
            }

            var updated = new Subscriber[existing.Length - 1];
            if (index > 0)
            {
                Array.Copy(existing, 0, updated, 0, index);
            }

            if (index < existing.Length - 1)
            {
                Array.Copy(existing, index + 1, updated, index, existing.Length - index - 1);
            }

            entry.Subscribers = updated;
            return Task.CompletedTask;
        }
    }

    public Task SendFrameAsync(
        ulong networkId,
        ulong sourceNodeId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sourceNodeId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_networkSubscribers.TryGetValue(networkId, out var entry))
        {
            return Task.CompletedTask;
        }

        var subscribers = Volatile.Read(ref entry.Subscribers);
        var dispatchToken = CancellationToken.None;
        for (var i = 0; i < subscribers.Length; i++)
        {
            var subscriber = subscribers[i];
            if (subscriber.NodeId == sourceNodeId)
            {
                continue;
            }

            var task = subscriber.OnFrameReceived(sourceNodeId, networkId, payload, dispatchToken);
            if (!task.IsCompletedSuccessfully)
            {
                return SendFrameSlowAsync(subscribers, i + 1, task, sourceNodeId, networkId, payload, dispatchToken);
            }
        }

        return Task.CompletedTask;
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static async Task SendFrameSlowAsync(
        Subscriber[] subscribers,
        int nextIndex,
        Task currentTask,
        ulong sourceNodeId,
        ulong networkId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        await currentTask.ConfigureAwait(false);
        for (var i = nextIndex; i < subscribers.Length; i++)
        {
            var subscriber = subscribers[i];
            if (subscriber.NodeId == sourceNodeId)
            {
                continue;
            }

            await subscriber.OnFrameReceived(sourceNodeId, networkId, payload, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _networkSubscribers.Clear();
    }
}
