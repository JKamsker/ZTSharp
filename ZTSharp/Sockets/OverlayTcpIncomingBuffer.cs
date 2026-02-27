using System.IO;
using System.Threading;
using System.Threading.Channels;

namespace ZTSharp.Sockets;

internal sealed class OverlayTcpIncomingBuffer
{
    private const int MaxQueuedSegments = 1024;
    private const int MaxSegmentLength = 1024;
    private const int FinProbeGracePeriodMs = 50;
    private const int FinLateDataGracePeriodMs = 200;

    private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(MaxQueuedSegments)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleWriter = false,
        SingleReader = true
    });
    private readonly TaskCompletionSource<bool> _finArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ReadOnlyMemory<byte> _currentSegment;
    private int _currentSegmentOffset;
    private int _remoteFinReceived;
    private long _remoteFinReceivedAtMs;
    private int _remoteClosed;
    private long _lastActivityMs;
    private IOException? _fault;

    public bool RemoteClosed => Volatile.Read(ref _remoteClosed) != 0;

    public bool RemoteFinReceived => Volatile.Read(ref _remoteFinReceived) != 0;

    public bool TryWrite(ReadOnlyMemory<byte> segment)
    {
        if (Volatile.Read(ref _fault) is not null)
        {
            return false;
        }

        if (segment.Length <= 0)
        {
            return true;
        }

        if (segment.Length > MaxSegmentLength)
        {
            Fault(new IOException($"Overlay TCP segment exceeds maximum size of {MaxSegmentLength} bytes."));
            return false;
        }

        if (Volatile.Read(ref _remoteClosed) != 0)
        {
            return false;
        }

        if (_incoming.Writer.TryWrite(segment))
        {
            Volatile.Write(ref _lastActivityMs, Environment.TickCount64);
            return true;
        }

        if (Volatile.Read(ref _remoteClosed) != 0)
        {
            return false;
        }

        Fault(new IOException("Overlay TCP receive buffer overflowed; closing connection to avoid silent data loss."));
        return false;
    }

    public void MarkRemoteFinReceived()
    {
        if (Interlocked.CompareExchange(ref _remoteFinReceived, 1, 0) == 0)
        {
            var finReceivedAtMs = Environment.TickCount64;
            Volatile.Write(ref _remoteFinReceivedAtMs, finReceivedAtMs);

            while (true)
            {
                var lastActivityMs = Volatile.Read(ref _lastActivityMs);
                if (lastActivityMs >= finReceivedAtMs)
                {
                    break;
                }

                if (Interlocked.CompareExchange(ref _lastActivityMs, finReceivedAtMs, lastActivityMs) == lastActivityMs)
                {
                    break;
                }
            }

            _finArrived.TrySetResult(true);
        }
    }

    public void MarkRemoteClosed()
    {
        Volatile.Write(ref _remoteClosed, 1);
        _incoming.Writer.TryComplete();
    }

    public void Complete()
        => _incoming.Writer.TryComplete();

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fault = Volatile.Read(ref _fault);
        if (fault is not null)
        {
            throw fault;
        }

        if (_currentSegment.Length == 0 || _currentSegmentOffset >= _currentSegment.Length)
        {
            while (true)
            {
                if (_incoming.Reader.TryRead(out var nextSegment))
                {
                    _currentSegment = nextSegment;
                    _currentSegmentOffset = 0;
                    break;
                }

                if (Volatile.Read(ref _remoteClosed) != 0)
                {
                    fault = Volatile.Read(ref _fault);
                    if (fault is not null)
                    {
                        throw fault;
                    }

                    if (_incoming.Reader.Completion.IsFaulted &&
                        _incoming.Reader.Completion.Exception?.InnerException is IOException ioException)
                    {
                        throw ioException;
                    }

                    return 0;
                }

                try
                {
                    ReadOnlyMemory<byte> segment;
                    if (Volatile.Read(ref _remoteFinReceived) != 0)
                    {
                        var now = Environment.TickCount64;
                        var finReceivedAtMs = Volatile.Read(ref _remoteFinReceivedAtMs);
                        var lastActivityMs = Volatile.Read(ref _lastActivityMs);
                        var last = Math.Max(lastActivityMs, finReceivedAtMs);
                        var gracePeriodMs = last > finReceivedAtMs ? FinLateDataGracePeriodMs : FinProbeGracePeriodMs;
                        var elapsedMs = now - last;
                        if (elapsedMs < 0)
                        {
                            elapsedMs = 0;
                        }

                        var remainingGraceMs = gracePeriodMs - elapsedMs;
                        if (remainingGraceMs <= 0)
                        {
                            MarkRemoteClosed();
                            return 0;
                        }

                        using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        remainingGraceMs = Math.Min(remainingGraceMs, gracePeriodMs);
                        graceCts.CancelAfter(TimeSpan.FromMilliseconds(remainingGraceMs));
                        try
                        {
                            while (await _incoming.Reader.WaitToReadAsync(graceCts.Token).ConfigureAwait(false))
                            {
                                if (_incoming.Reader.TryRead(out segment))
                                {
                                    _currentSegment = segment;
                                    _currentSegmentOffset = 0;
                                    break;
                                }
                            }

                            if (_currentSegment.Length != 0)
                            {
                                break;
                            }

                            MarkRemoteClosed();
                            return 0;
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && graceCts.IsCancellationRequested)
                        {
                            MarkRemoteClosed();
                            return 0;
                        }
                    }

                    var waitToReadTask = _incoming.Reader.WaitToReadAsync(cancellationToken).AsTask();
                    var completed = await Task.WhenAny(waitToReadTask, _finArrived.Task).ConfigureAwait(false);
                    if (completed != waitToReadTask)
                    {
                        continue;
                    }

                    if (!await waitToReadTask.ConfigureAwait(false))
                    {
                        throw new ChannelClosedException();
                    }

                    if (_incoming.Reader.TryRead(out segment))
                    {
                        _currentSegment = segment;
                        _currentSegmentOffset = 0;
                        break;
                    }
                }
                catch (ChannelClosedException)
                {
                    if (_incoming.Reader.Completion.IsFaulted &&
                        _incoming.Reader.Completion.Exception?.InnerException is IOException ioException)
                    {
                        throw ioException;
                    }

                    return 0;
                }
            }
        }

        var remaining = _currentSegment.Length - _currentSegmentOffset;
        var toCopy = Math.Min(buffer.Length, remaining);
        _currentSegment.Span.Slice(_currentSegmentOffset, toCopy).CopyTo(buffer.Span);
        _currentSegmentOffset += toCopy;
        return toCopy;
    }

    private void Fault(IOException exception)
    {
        if (Interlocked.CompareExchange(ref _fault, exception, null) is not null)
        {
            return;
        }

        Volatile.Write(ref _remoteClosed, 1);
        _incoming.Writer.TryComplete(exception);
    }
}
