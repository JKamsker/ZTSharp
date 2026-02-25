using System.Buffers;
using System.Diagnostics;

namespace ZTSharp;

internal sealed class EventLoopTimerQueue : IDisposable
{
    internal struct TimerItem
    {
        public long Id;
        public long DueTimestamp;
        public long PeriodTicks;
        public EventLoop.WorkItemCallback Callback;
        public object? State;
    }

    private TimerItem[] _heap;
    private int _count;
    private long _nextId;
    private HashSet<long>? _cancelledTimers;

    public EventLoopTimerQueue(int initialCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        _heap = ArrayPool<TimerItem>.Shared.Rent(initialCapacity);
    }

    public long Schedule(long dueTimestamp, long periodTicks, EventLoop.WorkItemCallback callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var id = ++_nextId;
        Insert(new TimerItem
        {
            Id = id,
            DueTimestamp = dueTimestamp,
            PeriodTicks = periodTicks,
            Callback = callback,
            State = state
        });
        return id;
    }

    public bool Cancel(long id)
    {
        _cancelledTimers ??= new HashSet<long>();
        _cancelledTimers.Add(id);
        return Remove(id);
    }

    public bool TryDequeueDue(long nowTimestamp, out TimerItem timer)
    {
        while (_count > 0)
        {
            var next = _heap[0];
            if (_cancelledTimers is not null && _cancelledTimers.Remove(next.Id))
            {
                Pop();
                continue;
            }

            if (next.DueTimestamp > nowTimestamp)
            {
                timer = default;
                return false;
            }

            timer = Pop();
            return true;
        }

        timer = default;
        return false;
    }

    public bool TryReschedulePeriodic(in TimerItem timer, long nowTimestamp)
    {
        if (timer.PeriodTicks <= 0)
        {
            return false;
        }

        if (_cancelledTimers is not null && _cancelledTimers.Remove(timer.Id))
        {
            return false;
        }

        var rescheduled = timer;
        rescheduled.DueTimestamp = nowTimestamp + timer.PeriodTicks;
        Insert(rescheduled);
        return true;
    }

    public int GetWaitMilliseconds(long nowTimestamp, int pollIntervalMs, bool hasPendingWork)
    {
        if (pollIntervalMs == 0)
        {
            return 0;
        }

        if (hasPendingWork)
        {
            return 0;
        }

        while (_count > 0)
        {
            var next = _heap[0];
            if (_cancelledTimers is null || !_cancelledTimers.Remove(next.Id))
            {
                var delta = next.DueTimestamp - nowTimestamp;
                if (delta <= 0)
                {
                    return 0;
                }

                var deltaMs = (int)Math.Min(delta * 1000 / Stopwatch.Frequency, int.MaxValue);
                return Math.Min(pollIntervalMs, Math.Max(1, deltaMs));
            }

            Pop();
        }

        return pollIntervalMs;
    }

    public void Dispose()
    {
        Array.Clear(_heap, 0, _heap.Length);
        ArrayPool<TimerItem>.Shared.Return(_heap);
        _heap = Array.Empty<TimerItem>();
        _count = 0;
        _cancelledTimers?.Clear();
    }

    private void Insert(in TimerItem timer)
    {
        if (_count == _heap.Length)
        {
            Grow(_heap.Length * 2);
        }

        var index = _count++;
        _heap[index] = timer;
        while (index > 0)
        {
            var parent = (index - 1) >> 1;
            if (_heap[parent].DueTimestamp <= _heap[index].DueTimestamp)
            {
                break;
            }

            (_heap[parent], _heap[index]) = (_heap[index], _heap[parent]);
            index = parent;
        }
    }

    private TimerItem Pop()
    {
        var timer = _heap[0];
        var lastIndex = --_count;
        if (_count == 0)
        {
            _heap[0] = default;
            return timer;
        }

        _heap[0] = _heap[lastIndex];
        _heap[lastIndex] = default;
        HeapifyDown(0);
        return timer;
    }

    private bool Remove(long id)
    {
        for (var i = 0; i < _count; i++)
        {
            if (_heap[i].Id != id)
            {
                continue;
            }

            var lastIndex = --_count;
            if (i == lastIndex)
            {
                _heap[i] = default;
                return true;
            }

            _heap[i] = _heap[lastIndex];
            _heap[lastIndex] = default;
            HeapifyDown(i);
            return true;
        }

        return false;
    }

    private void HeapifyDown(int index)
    {
        while (true)
        {
            var left = (index << 1) + 1;
            if (left >= _count)
            {
                break;
            }

            var right = left + 1;
            var smallest = right < _count && _heap[right].DueTimestamp < _heap[left].DueTimestamp
                ? right
                : left;

            if (_heap[index].DueTimestamp <= _heap[smallest].DueTimestamp)
            {
                break;
            }

            (_heap[index], _heap[smallest]) = (_heap[smallest], _heap[index]);
            index = smallest;
        }
    }

    private void Grow(int newSize)
    {
        var rented = ArrayPool<TimerItem>.Shared.Rent(newSize);
        Array.Copy(_heap, rented, _count);
        Array.Clear(_heap, 0, _heap.Length);
        ArrayPool<TimerItem>.Shared.Return(_heap);
        _heap = rented;
    }
}
