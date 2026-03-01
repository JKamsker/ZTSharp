using System.Collections.Concurrent;

namespace ZTSharp.Internal;

internal sealed class ActiveTaskSet
{
    private readonly ConcurrentDictionary<int, Task> _tasks = new();
    private int _nextId;

    public void Track(Task task)
    {
        var id = Interlocked.Increment(ref _nextId);
        _tasks.TryAdd(id, task);

        _ = task.ContinueWith(
            t => _tasks.TryRemove(id, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_tasks.IsEmpty)
            {
                return;
            }

            var snapshot = new List<KeyValuePair<int, Task>>(_tasks.Count);
            foreach (var pair in _tasks)
            {
                snapshot.Add(pair);
            }

            if (snapshot.Count == 0)
            {
                await Task.Yield();
                continue;
            }

            try
            {
                var tasks = new Task[snapshot.Count];
                for (var i = 0; i < snapshot.Count; i++)
                {
                    tasks[i] = snapshot[i].Value;
                }

                await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // ActiveTaskSet is primarily used for shutdown coordination; faults are observed but not surfaced here.
            catch
#pragma warning restore CA1031
            {
                foreach (var pair in snapshot)
                {
                    if (pair.Value.IsCompleted)
                    {
                        _tasks.TryRemove(pair.Key, out _);
                    }
                }

                await Task.Yield();
            }
        }
    }
}
