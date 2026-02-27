using System.IO;
using System.Threading.Channels;

namespace ZTSharp.Sockets;

internal sealed class OverlayTcpIncomingBuffer
{
    private const int MaxQueuedSegments = 1024;
    private const int MaxSegmentLength = 1024;

    private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(MaxQueuedSegments)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleWriter = false,
        SingleReader = true
    });
    private ReadOnlyMemory<byte> _currentSegment;
    private int _currentSegmentOffset;
    private bool _remoteClosed;
    private IOException? _fault;

    public bool RemoteClosed => _remoteClosed;

    public bool TryWrite(ReadOnlyMemory<byte> segment)
    {
        if (_fault is not null)
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

        if (_incoming.Writer.TryWrite(segment))
        {
            return true;
        }

        Fault(new IOException("Overlay TCP receive buffer overflowed; closing connection to avoid silent data loss."));
        return false;
    }

    public void MarkRemoteClosed()
    {
        _remoteClosed = true;
        _incoming.Writer.TryComplete();
    }

    public void Complete()
        => _incoming.Writer.TryComplete();

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_fault is not null)
        {
            throw _fault;
        }

        if (_currentSegment.Length == 0 || _currentSegmentOffset >= _currentSegment.Length)
        {
            while (true)
            {
                if (_incoming.Reader.TryRead(out var segment))
                {
                    _currentSegment = segment;
                    _currentSegmentOffset = 0;
                    break;
                }

                if (_remoteClosed)
                {
                    return 0;
                }

                try
                {
                    _currentSegment = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    _currentSegmentOffset = 0;
                    break;
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
        if (_fault is not null)
        {
            return;
        }

        _fault = exception;
        _remoteClosed = true;
        _incoming.Writer.TryComplete(exception);
    }
}
