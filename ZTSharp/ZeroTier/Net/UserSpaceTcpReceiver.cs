using System.IO;
using System.Threading.Channels;

namespace ZTSharp.ZeroTier.Net;

internal sealed class UserSpaceTcpReceiver
{
    private const int MaxReceiveBufferBytes = 256 * 1024;

    private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    private readonly Dictionary<uint, byte[]> _outOfOrder = new();

    private uint _recvNext;
    private uint? _pendingFinSeq;
    private long _receiveBufferedBytes;
    private bool _remoteClosed;

    public bool RemoteClosed => _remoteClosed;

    public uint RecvNext => _recvNext;

    public void Initialize(uint initialRecvNext)
    {
        _recvNext = initialRecvNext;
    }

    public void Complete(Exception? exception = null)
        => _incoming.Writer.TryComplete(exception);

    public void MarkRemoteClosed(Exception? exception = null)
    {
        _remoteClosed = true;
        _incoming.Writer.TryComplete(exception);
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
            if (_incoming.Reader.TryRead(out var readSegment))
            {
                var bytesRead = CopyFromSegmentAndRequeueRemainder(readSegment, buffer);
                ConsumeReceiveBuffer(bytesRead);
                return bytesRead;
            }

            if (_remoteClosed)
            {
                return 0;
            }

            try
            {
                var segmentFromChannel = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var bytesRead = CopyFromSegmentAndRequeueRemainder(segmentFromChannel, buffer);
                ConsumeReceiveBuffer(bytesRead);
                return bytesRead;
            }
            catch (ChannelClosedException)
            {
                return 0;
            }
        }
    }

    public bool ProcessSegment(uint seq, ReadOnlySpan<byte> tcpPayload, bool hasFin, out bool shouldAck)
    {
        var originalPayloadLength = tcpPayload.Length;
        shouldAck = hasFin || originalPayloadLength != 0;

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
                        var bytes = tcpPayload.ToArray();
                        if (_incoming.Writer.TryWrite(bytes))
                        {
                            _recvNext = unchecked(_recvNext + (uint)tcpPayload.Length);
                            accepted = true;
                        }
                        else
                        {
                            ReleaseReceiveBuffer(bytes.Length);
                        }
                    }

                    while (accepted && _outOfOrder.Remove(_recvNext, out var buffered))
                    {
                        if (_incoming.Writer.TryWrite(buffered))
                        {
                            _recvNext = unchecked(_recvNext + (uint)buffered.Length);
                        }
                        else
                        {
                            ReleaseReceiveBuffer(buffered.Length);
                            break;
                        }
                    }

                    if (accepted && _pendingFinSeq is { } pending && pending == _recvNext)
                    {
                        _pendingFinSeq = null;
                        _recvNext = unchecked(_recvNext + 1);
                        MarkRemoteClosed(new IOException("Remote has closed the connection."));
                        return true;
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
                MarkRemoteClosed(new IOException("Remote has closed the connection."));
                return true;
            }

            if (UserSpaceTcpSequenceNumbers.GreaterThan(finSeq, _recvNext))
            {
                _pendingFinSeq = finSeq;
            }
        }

        return false;
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

    private int CopyFromSegmentAndRequeueRemainder(ReadOnlyMemory<byte> segment, Memory<byte> destination)
    {
        var toCopy = Math.Min(segment.Length, destination.Length);
        segment.Span.Slice(0, toCopy).CopyTo(destination.Span);
        if (toCopy < segment.Length)
        {
            var remainder = segment.Slice(toCopy);
            if (!_incoming.Writer.TryWrite(remainder))
            {
                ReleaseReceiveBuffer(remainder.Length);
            }
        }

        return toCopy;
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
}
