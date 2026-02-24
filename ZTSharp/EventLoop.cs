using System.Buffers;
using System.Diagnostics;
using System.Threading;

namespace ZTSharp;

internal sealed class EventLoop : IDisposable
{
    internal delegate ValueTask WorkItemCallback(object? state, CancellationToken cancellationToken);

    private readonly struct WorkItem
    {
        public readonly WorkItemCallback Callback;
        public readonly object? State;

        public WorkItem(WorkItemCallback callback, object? state)
        {
            Callback = callback;
            State = state;
        }
    }

    private struct TimerItem
    {
        public long Id;
        public long DueTimestamp;
        public long PeriodTicks;
        public WorkItemCallback Callback;
        public object? State;
    }

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

    private WorkItem[] _workBuffer;
    private int _workHead;
    private int _workCount;

    private TimerItem[] _timerHeap;
    private int _timerCount;

    private long _nextTimerId;
    private HashSet<long>? _cancelledTimers;
    private bool _disposed;

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

        _workBuffer = ArrayPool<WorkItem>.Shared.Rent(initialWorkItemCapacity);
        _timerHeap = ArrayPool<TimerItem>.Shared.Rent(initialTimerCapacity);

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
            EnqueueWorkNoLock(new WorkItem(callback, state));
        }

        _signal.Set();
    }

    public TimerHandle Schedule(TimeSpan dueTime, WorkItemCallback callback, object? state = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfLessThan(dueTime, TimeSpan.Zero);

        var id = Interlocked.Increment(ref _nextTimerId);
        var dueTimestamp = Stopwatch.GetTimestamp() + ToStopwatchTicks(dueTime);
        lock (_gate)
        {
            ThrowIfDisposed();
            InsertTimerNoLock(new TimerItem
            {
                Id = id,
                DueTimestamp = dueTimestamp,
                PeriodTicks = 0,
                Callback = callback,
                State = state
            });
        }

        _signal.Set();
        return new TimerHandle(this, id);
    }

    public TimerHandle SchedulePeriodic(TimeSpan period, WorkItemCallback callback, object? state = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);

        var periodTicks = ToStopwatchTicks(period);
        var id = Interlocked.Increment(ref _nextTimerId);
        var dueTimestamp = Stopwatch.GetTimestamp() + periodTicks;
        lock (_gate)
        {
            ThrowIfDisposed();
            InsertTimerNoLock(new TimerItem
            {
                Id = id,
                DueTimestamp = dueTimestamp,
                PeriodTicks = periodTicks,
                Callback = callback,
                State = state
            });
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

            _cancelledTimers ??= new HashSet<long>();
            _cancelledTimers.Add(id);
            return RemoveTimerNoLock(id);
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
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
            _cts.Cancel();
        }
    }

    private static void ExecuteWorkItem(in WorkItem work, CancellationToken cancellationToken)
    {
        try
        {
            var task = work.Callback(work.State, cancellationToken);
            if (!task.IsCompletedSuccessfully)
            {
                task.AsTask().GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Event loop callback threw an exception.", ex);
        }
    }

    private void ExecuteTimerItem(in TimerItem timer, CancellationToken cancellationToken)
    {
        try
        {
            var task = timer.Callback(timer.State, cancellationToken);
            if (!task.IsCompletedSuccessfully)
            {
                task.AsTask().GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Event loop callback threw an exception.", ex);
        }

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

            if (_cancelledTimers is not null && _cancelledTimers.Remove(timer.Id))
            {
                return;
            }

            var rescheduled = timer;
            rescheduled.DueTimestamp = Stopwatch.GetTimestamp() + timer.PeriodTicks;
            InsertTimerNoLock(rescheduled);
        }

        _signal.Set();
    }

    private int GetWaitMilliseconds(long nowTimestamp)
    {
        if (_pollIntervalMs == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            if (_disposed || _workCount > 0)
            {
                return 0;
            }

            while (_timerCount > 0)
            {
                var next = _timerHeap[0];
                if (_cancelledTimers is null || !_cancelledTimers.Remove(next.Id))
                {
                    var delta = next.DueTimestamp - nowTimestamp;
                    if (delta <= 0)
                    {
                        return 0;
                    }

                    var deltaMs = (int)Math.Min(delta * 1000 / Stopwatch.Frequency, int.MaxValue);
                    return Math.Min(_pollIntervalMs, Math.Max(1, deltaMs));
                }

                PopTimerNoLock();
            }

            return _pollIntervalMs;
        }
    }

    private bool TryDequeueWork(out WorkItem work)
    {
        lock (_gate)
        {
            if (_disposed || _workCount == 0)
            {
                work = default;
                return false;
            }

            work = _workBuffer[_workHead];
            _workBuffer[_workHead] = default;
            _workHead++;
            if (_workHead == _workBuffer.Length)
            {
                _workHead = 0;
            }

            _workCount--;
            return true;
        }
    }

    private bool TryDequeueDueTimer(long nowTimestamp, out TimerItem timer)
    {
        lock (_gate)
        {
            if (_disposed || _timerCount == 0)
            {
                timer = default;
                return false;
            }

            while (_timerCount > 0)
            {
                var next = _timerHeap[0];
                if (_cancelledTimers is not null && _cancelledTimers.Remove(next.Id))
                {
                    PopTimerNoLock();
                    continue;
                }

                if (next.DueTimestamp > nowTimestamp)
                {
                    timer = default;
                    return false;
                }

                timer = PopTimerNoLock();
                return true;
            }

            timer = default;
            return false;
        }
    }

    private void EnqueueWorkNoLock(in WorkItem work)
    {
        if (_workCount == _workBuffer.Length)
        {
            GrowWorkBufferNoLock(_workBuffer.Length * 2);
        }

        var index = _workHead + _workCount;
        if (index >= _workBuffer.Length)
        {
            index -= _workBuffer.Length;
        }

        _workBuffer[index] = work;
        _workCount++;
    }

    private void GrowWorkBufferNoLock(int newSize)
    {
        var rented = ArrayPool<WorkItem>.Shared.Rent(newSize);
        for (var i = 0; i < _workCount; i++)
        {
            var index = _workHead + i;
            if (index >= _workBuffer.Length)
            {
                index -= _workBuffer.Length;
            }

            rented[i] = _workBuffer[index];
        }

        Array.Clear(_workBuffer, 0, _workBuffer.Length);
        ArrayPool<WorkItem>.Shared.Return(_workBuffer);
        _workBuffer = rented;
        _workHead = 0;
    }

    private void InsertTimerNoLock(in TimerItem timer)
    {
        if (_timerCount == _timerHeap.Length)
        {
            GrowTimerHeapNoLock(_timerHeap.Length * 2);
        }

        var index = _timerCount++;
        _timerHeap[index] = timer;
        while (index > 0)
        {
            var parent = (index - 1) >> 1;
            if (_timerHeap[parent].DueTimestamp <= _timerHeap[index].DueTimestamp)
            {
                break;
            }

            (_timerHeap[parent], _timerHeap[index]) = (_timerHeap[index], _timerHeap[parent]);
            index = parent;
        }
    }

    private TimerItem PopTimerNoLock()
    {
        var timer = _timerHeap[0];
        var lastIndex = --_timerCount;
        if (_timerCount == 0)
        {
            _timerHeap[0] = default;
            return timer;
        }

        _timerHeap[0] = _timerHeap[lastIndex];
        _timerHeap[lastIndex] = default;
        HeapifyDownNoLock(0);
        return timer;
    }

    private bool RemoveTimerNoLock(long id)
    {
        for (var i = 0; i < _timerCount; i++)
        {
            if (_timerHeap[i].Id != id)
            {
                continue;
            }

            var lastIndex = --_timerCount;
            if (i == lastIndex)
            {
                _timerHeap[i] = default;
                return true;
            }

            _timerHeap[i] = _timerHeap[lastIndex];
            _timerHeap[lastIndex] = default;
            HeapifyDownNoLock(i);
            return true;
        }

        return false;
    }

    private void HeapifyDownNoLock(int index)
    {
        while (true)
        {
            var left = (index << 1) + 1;
            if (left >= _timerCount)
            {
                break;
            }

            var right = left + 1;
            var smallest = right < _timerCount && _timerHeap[right].DueTimestamp < _timerHeap[left].DueTimestamp
                ? right
                : left;

            if (_timerHeap[index].DueTimestamp <= _timerHeap[smallest].DueTimestamp)
            {
                break;
            }

            (_timerHeap[index], _timerHeap[smallest]) = (_timerHeap[smallest], _timerHeap[index]);
            index = smallest;
        }
    }

    private void GrowTimerHeapNoLock(int newSize)
    {
        var rented = ArrayPool<TimerItem>.Shared.Rent(newSize);
        Array.Copy(_timerHeap, rented, _timerCount);
        Array.Clear(_timerHeap, 0, _timerHeap.Length);
        ArrayPool<TimerItem>.Shared.Return(_timerHeap);
        _timerHeap = rented;
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

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _signal.Set();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join();
        }

        _signal.Dispose();
        _cts.Dispose();

        Array.Clear(_workBuffer, 0, _workBuffer.Length);
        ArrayPool<WorkItem>.Shared.Return(_workBuffer);
        _workBuffer = Array.Empty<WorkItem>();

        Array.Clear(_timerHeap, 0, _timerHeap.Length);
        ArrayPool<TimerItem>.Shared.Return(_timerHeap);
        _timerHeap = Array.Empty<TimerItem>();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
