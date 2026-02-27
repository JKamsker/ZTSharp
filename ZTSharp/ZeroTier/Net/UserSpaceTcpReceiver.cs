using System.Buffers;
using System.IO.Pipelines;

namespace ZTSharp.ZeroTier.Net;

internal sealed class UserSpaceTcpReceiver
{
    private const int MaxReceiveBufferBytes = 256 * 1024;

    private readonly Pipe _incoming = new(new PipeOptions(pauseWriterThreshold: long.MaxValue, resumeWriterThreshold: long.MaxValue));
    private readonly Dictionary<uint, byte[]> _outOfOrder = new();

    private uint _recvNext;
    private uint? _pendingFinSeq;
    private long _receiveBufferedBytes;
    private bool _remoteClosed;
    private Exception? _terminalException;
    private int _incomingCompleted;

    public bool RemoteClosed => _remoteClosed;

    public uint RecvNext => _recvNext;

    public void Initialize(uint initialRecvNext)
    {
        _recvNext = initialRecvNext;
    }

    public void Complete(Exception? exception = null)
    {
        _terminalException = exception;
        _remoteClosed = true;
        CompleteIncoming();
    }

    public void MarkRemoteClosed(Exception? exception = null)
    {
        if (exception is not null)
        {
            _terminalException ??= exception;
        }

        _remoteClosed = true;
        CompleteIncoming();
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (buffer.Length == 0)
        {
            return 0;
        }

        while (true)
        {
            var result = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var source = result.Buffer;

            if (!source.IsEmpty)
            {
                var toCopy = (int)Math.Min(source.Length, buffer.Length);
                CopyFromSequence(source, buffer.Span, toCopy);
                _incoming.Reader.AdvanceTo(source.GetPosition(toCopy));
                ConsumeReceiveBuffer(toCopy);
                return toCopy;
            }

            _incoming.Reader.AdvanceTo(source.Start, source.End);
            if (result.IsCompleted)
            {
                if (_terminalException is not null)
                {
                    throw _terminalException;
                }

                return 0;
            }
        }
    }

    public ValueTask<ProcessSegmentResult> ProcessSegmentAsync(uint seq, ReadOnlySpan<byte> tcpPayload, bool hasFin)
    {
        var originalPayloadLength = tcpPayload.Length;
        var shouldAck = hasFin || originalPayloadLength != 0;
        var wroteToPipe = false;
        var markRemoteClosed = false;

        if (originalPayloadLength != 0)
        {
            var segmentSeq = seq;

            if (UserSpaceTcpSequenceNumbers.GreaterThanOrEqual(_recvNext, segmentSeq))
            {
                var alreadyReceived = (int)(_recvNext - segmentSeq);
                if (alreadyReceived < tcpPayload.Length)
                {
                    tcpPayload = tcpPayload.Slice(alreadyReceived);
                    segmentSeq = _recvNext;
                }
                else
                {
                    tcpPayload = ReadOnlySpan<byte>.Empty;
                }
            }

            if (!tcpPayload.IsEmpty)
            {
                if (segmentSeq == _recvNext)
                {
                    var accepted = false;
                    if (TryReserveReceiveBuffer(tcpPayload.Length))
                    {
                        try
                        {
                            WriteToPipe(_incoming.Writer, tcpPayload);

                            _recvNext = unchecked(_recvNext + (uint)tcpPayload.Length);
                            accepted = true;
                            wroteToPipe = true;
                        }
                        catch (ObjectDisposedException)
                        {
                            ReleaseReceiveBuffer(tcpPayload.Length);
                        }
                        catch (InvalidOperationException)
                        {
                            ReleaseReceiveBuffer(tcpPayload.Length);
                        }
                    }

                    if (accepted)
                    {
                        wroteToPipe |= DrainAndTrimOutOfOrder();
                    }

                    if (accepted && _pendingFinSeq is { } pending && pending == _recvNext)
                    {
                        _pendingFinSeq = null;
                        _recvNext = unchecked(_recvNext + 1);
                        markRemoteClosed = true;
                        return FlushAndMaybeCloseAsync(wroteToPipe, markRemoteClosed, new ProcessSegmentResult(ClosedNow: true, shouldAck));
                    }
                }
                else if (UserSpaceTcpSequenceNumbers.GreaterThan(segmentSeq, _recvNext))
                {
                    if (!_outOfOrder.ContainsKey(segmentSeq) && TryReserveReceiveBuffer(tcpPayload.Length))
                    {
                        var bytes = tcpPayload.ToArray();
                        if (!_outOfOrder.TryAdd(segmentSeq, bytes))
                        {
                            ReleaseReceiveBuffer(bytes.Length);
                        }
                    }
                }
            }
        }

        if (hasFin)
        {
            var finSeq = unchecked(seq + (uint)originalPayloadLength);
            if (finSeq == _recvNext)
            {
                _recvNext = unchecked(_recvNext + 1);
                markRemoteClosed = true;
                return FlushAndMaybeCloseAsync(wroteToPipe, markRemoteClosed, new ProcessSegmentResult(ClosedNow: true, shouldAck));
            }

            if (UserSpaceTcpSequenceNumbers.GreaterThan(finSeq, _recvNext))
            {
                _pendingFinSeq = finSeq;
            }
        }

        return FlushAndMaybeCloseAsync(wroteToPipe, markRemoteClosed, new ProcessSegmentResult(ClosedNow: false, shouldAck));
    }

    public ushort GetReceiveWindow()
    {
        var buffered = Volatile.Read(ref _receiveBufferedBytes);
        var available = MaxReceiveBufferBytes - buffered;
        if (available <= 0)
        {
            return 0;
        }

        if (available >= ushort.MaxValue)
        {
            return ushort.MaxValue;
        }

        return (ushort)available;
    }

    private void CompleteIncoming()
    {
        if (Interlocked.Exchange(ref _incomingCompleted, 1) == 1)
        {
            return;
        }

        try
        {
            _incoming.Writer.Complete();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private bool DrainAndTrimOutOfOrder()
    {
        var wroteToPipe = false;
        while (true)
        {
            var progressed = DrainContiguousOutOfOrder();
            progressed |= TrimOutOfOrderSegments();
            if (!progressed)
            {
                return wroteToPipe;
            }

            wroteToPipe = true;
        }
    }

    private bool DrainContiguousOutOfOrder()
    {
        var progressed = false;
        while (_outOfOrder.Remove(_recvNext, out var buffered))
        {
            try
            {
                WriteToPipe(_incoming.Writer, buffered);

                _recvNext = unchecked(_recvNext + (uint)buffered.Length);
                progressed = true;
            }
            catch (ObjectDisposedException)
            {
                ReleaseReceiveBuffer(buffered.Length);
                break;
            }
            catch (InvalidOperationException)
            {
                ReleaseReceiveBuffer(buffered.Length);
                break;
            }
        }

        return progressed;
    }

    private ValueTask<ProcessSegmentResult> FlushAndMaybeCloseAsync(
        bool wroteToPipe,
        bool markRemoteClosed,
        ProcessSegmentResult result)
    {
        if (!wroteToPipe)
        {
            if (markRemoteClosed)
            {
                MarkRemoteClosed();
            }

            return new ValueTask<ProcessSegmentResult>(result);
        }

        ValueTask<FlushResult> flushTask;
        try
        {
            flushTask = _incoming.Writer.FlushAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            if (markRemoteClosed)
            {
                MarkRemoteClosed();
            }

            return new ValueTask<ProcessSegmentResult>(result);
        }

        if (flushTask.IsCompletedSuccessfully)
        {
            if (markRemoteClosed)
            {
                MarkRemoteClosed();
            }

            return new ValueTask<ProcessSegmentResult>(result);
        }

        return AwaitFlushAndMaybeCloseAsync(flushTask, result, markRemoteClosed);
    }

    private async ValueTask<ProcessSegmentResult> AwaitFlushAndMaybeCloseAsync(
        ValueTask<FlushResult> flushTask,
        ProcessSegmentResult result,
        bool markRemoteClosed)
    {
        try
        {
            await flushTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            if (markRemoteClosed)
            {
                MarkRemoteClosed();
            }

            return result;
        }

        if (markRemoteClosed)
        {
            MarkRemoteClosed();
        }

        return result;
    }

    private bool TrimOutOfOrderSegments()
    {
        if (_outOfOrder.Count == 0)
        {
            return false;
        }

        List<uint>? keysToRemove = null;
        List<byte[]>? segmentsToAdd = null;

        foreach (var (seq, bytes) in _outOfOrder)
        {
            if (!UserSpaceTcpSequenceNumbers.GreaterThanOrEqual(_recvNext, seq))
            {
                continue;
            }

            var alreadyReceived = (int)(_recvNext - seq);
            if (alreadyReceived <= 0)
            {
                continue;
            }

            keysToRemove ??= new List<uint>();
            keysToRemove.Add(seq);

            if (alreadyReceived >= bytes.Length)
            {
                ReleaseReceiveBuffer(bytes.Length);
                continue;
            }

            ReleaseReceiveBuffer(alreadyReceived);
            segmentsToAdd ??= new List<byte[]>();
            segmentsToAdd.Add(bytes.AsSpan(alreadyReceived).ToArray());
        }

        if (keysToRemove is null)
        {
            return false;
        }

        for (var i = 0; i < keysToRemove.Count; i++)
        {
            _outOfOrder.Remove(keysToRemove[i]);
        }

        if (segmentsToAdd is not null)
        {
            for (var i = 0; i < segmentsToAdd.Count; i++)
            {
                var bytes = segmentsToAdd[i];
                if (_outOfOrder.TryGetValue(_recvNext, out var existing))
                {
                    if (existing.Length >= bytes.Length)
                    {
                        ReleaseReceiveBuffer(bytes.Length);
                        continue;
                    }

                    _outOfOrder[_recvNext] = bytes;
                    ReleaseReceiveBuffer(existing.Length);
                }
                else
                {
                    _outOfOrder[_recvNext] = bytes;
                }
            }
        }

        return true;
    }

    private bool TryReserveReceiveBuffer(int bytes)
    {
        if (bytes <= 0)
        {
            return true;
        }

        while (true)
        {
            var current = Volatile.Read(ref _receiveBufferedBytes);
            var next = current + bytes;
            if (next > MaxReceiveBufferBytes)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _receiveBufferedBytes, next, current) == current)
            {
                return true;
            }
        }
    }

    private void ReleaseReceiveBuffer(int bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        Interlocked.Add(ref _receiveBufferedBytes, -bytes);
    }

    private void ConsumeReceiveBuffer(int bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        Interlocked.Add(ref _receiveBufferedBytes, -bytes);
    }

    private static void CopyFromSequence(ReadOnlySequence<byte> source, Span<byte> destination, int length)
    {
        var copied = 0;
        foreach (var memory in source)
        {
            var span = memory.Span;
            var remaining = length - copied;
            if (remaining <= 0)
            {
                break;
            }

            if (span.Length > remaining)
            {
                span = span.Slice(0, remaining);
            }

            span.CopyTo(destination.Slice(copied));
            copied += span.Length;
        }
    }

    private static void WriteToPipe(PipeWriter writer, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        var span = writer.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        writer.Advance(bytes.Length);
    }

    public readonly record struct ProcessSegmentResult(bool ClosedNow, bool ShouldAck);
}
