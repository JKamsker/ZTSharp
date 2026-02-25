using System.Buffers;

namespace ZTSharp;

internal readonly struct EventLoopWorkItem
{
    public readonly EventLoop.WorkItemCallback Callback;
    public readonly object? State;

    public EventLoopWorkItem(EventLoop.WorkItemCallback callback, object? state)
    {
        Callback = callback;
        State = state;
    }
}

internal sealed class EventLoopWorkQueue
{
    private EventLoopWorkItem[] _buffer;
    private int _head;
    private int _count;

    public EventLoopWorkQueue(int initialCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        _buffer = ArrayPool<EventLoopWorkItem>.Shared.Rent(initialCapacity);
    }

    public bool HasPendingWork => _count > 0;

    public void Enqueue(in EventLoopWorkItem work)
    {
        if (_count == _buffer.Length)
        {
            Grow(_buffer.Length * 2);
        }

        var index = _head + _count;
        if (index >= _buffer.Length)
        {
            index -= _buffer.Length;
        }

        _buffer[index] = work;
        _count++;
    }

    public bool TryDequeue(out EventLoopWorkItem work)
    {
        if (_count == 0)
        {
            work = default;
            return false;
        }

        work = _buffer[_head];
        _buffer[_head] = default;
        _head++;
        if (_head == _buffer.Length)
        {
            _head = 0;
        }

        _count--;
        return true;
    }

    public void Dispose()
    {
        Array.Clear(_buffer, 0, _buffer.Length);
        ArrayPool<EventLoopWorkItem>.Shared.Return(_buffer);
        _buffer = Array.Empty<EventLoopWorkItem>();
        _head = 0;
        _count = 0;
    }

    private void Grow(int newSize)
    {
        var rented = ArrayPool<EventLoopWorkItem>.Shared.Rent(newSize);
        for (var i = 0; i < _count; i++)
        {
            var index = _head + i;
            if (index >= _buffer.Length)
            {
                index -= _buffer.Length;
            }

            rented[i] = _buffer[index];
        }

        Array.Clear(_buffer, 0, _buffer.Length);
        ArrayPool<EventLoopWorkItem>.Shared.Return(_buffer);
        _buffer = rented;
        _head = 0;
    }
}

