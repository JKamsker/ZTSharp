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
        while (!_tasks.IsEmpty)
        {
            var snapshot = new List<Task>(_tasks.Count);
            foreach (var task in _tasks.Values)
            {
                snapshot.Add(task);
            }

            if (snapshot.Count == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(snapshot).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
