using System.Threading.Channels;

namespace ZTSharp.Sockets;

internal sealed class OverlayTcpIncomingBuffer
{
    private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    private ReadOnlyMemory<byte> _currentSegment;
    private int _currentSegmentOffset;
    private bool _remoteClosed;

    public bool RemoteClosed => _remoteClosed;

    public bool TryWrite(ReadOnlyMemory<byte> segment)
        => segment.Length > 0 && _incoming.Writer.TryWrite(segment);

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
}
