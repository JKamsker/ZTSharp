using System.Diagnostics;
using System.Threading;

namespace ZTSharp;

internal sealed class EventLoop : IDisposable
{
    internal delegate ValueTask WorkItemCallback(object? state, CancellationToken cancellationToken);

    public readonly struct TimerHandle
    {
        private readonly EventLoop? _owner;

        internal TimerHandle(EventLoop owner, long id)
        {
            _owner = owner;
            Id = id;
        }

        public long Id { get; }

        public bool Cancel() => _owner is not null && _owner.CancelTimer(Id);
    }

    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(initialState: false);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly int _pollIntervalMs;
    private readonly EventLoopTimerQueue _timers;
    private readonly EventLoopWorkQueue _work;

    private bool _disposed;
    private Exception? _fault;

    public EventLoop(
        TimeSpan pollInterval,
        int initialWorkItemCapacity = 256,
        int initialTimerCapacity = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialWorkItemCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialTimerCapacity);

        _pollIntervalMs = pollInterval == TimeSpan.Zero
            ? 0
            : Math.Clamp((int)pollInterval.TotalMilliseconds, 1, int.MaxValue);

        _work = new EventLoopWorkQueue(initialWorkItemCapacity);
        _timers = new EventLoopTimerQueue(initialTimerCapacity);

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "EventLoop"
        };
        _thread.Start();
    }

    public void Post(WorkItemCallback callback, object? state = null)
    {
        ArgumentNullException.ThrowIfNull(callback);

        lock (_gate)
        {
            ThrowIfDisposed();
            _work.Enqueue(new EventLoopWorkItem(callback, state));
        }

        _signal.Set();
    }

    public TimerHandle Schedule(TimeSpan dueTime, WorkItemCallback callback, object? state = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfLessThan(dueTime, TimeSpan.Zero);

        var dueTimestamp = Stopwatch.GetTimestamp() + ToStopwatchTicks(dueTime);
        long id;
        lock (_gate)
        {
            ThrowIfDisposed();
            id = _timers.Schedule(dueTimestamp, periodTicks: 0, callback, state);
        }

        _signal.Set();
        return new TimerHandle(this, id);
    }

    public TimerHandle SchedulePeriodic(TimeSpan period, WorkItemCallback callback, object? state = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);

        var periodTicks = ToStopwatchTicks(period);
        var dueTimestamp = Stopwatch.GetTimestamp() + periodTicks;
        long id;
        lock (_gate)
        {
            ThrowIfDisposed();
            id = _timers.Schedule(dueTimestamp, periodTicks, callback, state);
        }

        _signal.Set();
        return new TimerHandle(this, id);
    }

    private bool CancelTimer(long id)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            return _timers.Cancel(id);
        }
    }

    private void Run()
    {
        var token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = Stopwatch.GetTimestamp();
                while (true)
                {
                    if (TryDequeueWork(out var work))
                    {
                        ExecuteWorkItem(work, token);
                        continue;
                    }

                    if (TryDequeueDueTimer(now, out var timer))
                    {
                        ExecuteTimerItem(timer, token);
                        now = Stopwatch.GetTimestamp();
                        continue;
                    }

                    break;
                }

                var waitMs = GetWaitMilliseconds(now);
                _signal.WaitOne(waitMs);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (InvalidOperationException ex)
        {
            lock (_gate)
            {
                _fault ??= ex;
            }

            _cts.Cancel();
        }
    }

    private static void ExecuteWorkItem(in EventLoopWorkItem work, CancellationToken cancellationToken)
        => ExecuteCallback(work.Callback, work.State, "Event loop work item", cancellationToken);

    private void ExecuteTimerItem(in EventLoopTimerQueue.TimerItem timer, CancellationToken cancellationToken)
    {
        ExecuteCallback(timer.Callback, timer.State, "Event loop timer", cancellationToken);

        if (timer.PeriodTicks <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _timers.TryReschedulePeriodic(timer, Stopwatch.GetTimestamp());
        }

        _signal.Set();
    }

    private static void ExecuteCallback(WorkItemCallback callback, object? state, string operation, CancellationToken cancellationToken)
    {
        try
        {
            var task = callback(state, cancellationToken);
            if (!task.IsCompletedSuccessfully)
            {
                task.AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{operation} callback threw an exception.", ex);
        }
    }

    private int GetWaitMilliseconds(long nowTimestamp)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return 0;
            }

            return _timers.GetWaitMilliseconds(nowTimestamp, _pollIntervalMs, hasPendingWork: _work.HasPendingWork);
        }
    }

    private bool TryDequeueWork(out EventLoopWorkItem work)
    {
        lock (_gate)
        {
            if (_disposed || !_work.TryDequeue(out var dequeued))
            {
                work = default;
                return false;
            }

            work = dequeued;
            return true;
        }
    }

    private bool TryDequeueDueTimer(long nowTimestamp, out EventLoopTimerQueue.TimerItem timer)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                timer = default;
                return false;
            }

            return _timers.TryDequeueDue(nowTimestamp, out timer);
        }
    }

    private static long ToStopwatchTicks(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return 0;
        }

        var ticks = duration.TotalSeconds * Stopwatch.Frequency;
        return ticks >= long.MaxValue ? long.MaxValue : (long)ticks;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _cts.Cancel();

        _signal.Set();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join();
        }

        _signal.Dispose();
        _cts.Dispose();
        _timers.Dispose();
        _work.Dispose();
    }

    private void ThrowIfDisposed()
    {
        var fault = Volatile.Read(ref _fault);
        if (fault is not null)
        {
            throw new InvalidOperationException("Event loop is faulted.", fault);
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
