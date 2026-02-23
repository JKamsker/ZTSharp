using System.Net.Sockets;

namespace JKamsker.LibZt.Libzt.Sockets;

/// <summary>
/// <see cref="Stream"/> implementation on top of <see cref="global::ZeroTier.Sockets.Socket"/>.
/// </summary>
public sealed class ZtLibztSocketStream : Stream
{
    private const int PollDelayMs = 10;

    private readonly global::ZeroTier.Sockets.Socket _socket;
    private readonly bool _ownsSocket;
    private readonly byte[] _receiveBuffer = new byte[64 * 1024];

    private int _receiveOffset;
    private int _receiveCount;
    private bool _disposed;

    private int _readTimeoutMs = Timeout.Infinite;
    private int _writeTimeoutMs = Timeout.Infinite;

    public ZtLibztSocketStream(global::ZeroTier.Sockets.Socket socket, bool ownsSocket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;
        _ownsSocket = ownsSocket;

        // ZeroTier.Sockets.Socket.Send/Receive are not compatible with .NET's offset/size semantics
        // (they ignore the size parameter), so we always call them with offset=0 and a buffer whose
        // length matches the intended I/O size.
        //
        // Use non-blocking mode and implement cancellation/timeout semantics in managed code.
        _socket.Blocking = false;
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => false;

    public override bool CanWrite => !_disposed;

    public override bool CanTimeout => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int ReadTimeout
    {
        get => _readTimeoutMs;
        set => _readTimeoutMs = NormalizeTimeoutMs(value);
    }

    public override int WriteTimeout
    {
        get => _writeTimeoutMs;
        set => _writeTimeoutMs = NormalizeTimeoutMs(value);
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if ((uint)offset > (uint)buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if ((uint)count > (uint)(buffer.Length - offset))
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (count == 0)
        {
            return 0;
        }

        return ReadTo(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0)
        {
            return 0;
        }

        return ReadTo(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<int>(cancellationToken);
        }

        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<int>(cancellationToken);
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0)
        {
            return ValueTask.FromResult(0);
        }

        return ReadToAsync(buffer, cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if ((uint)offset > (uint)buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if ((uint)count > (uint)(buffer.Length - offset))
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (count == 0)
        {
            return;
        }

        SendAll(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0)
        {
            return;
        }

        SendAll(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        ArgumentNullException.ThrowIfNull(buffer);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
        {
            return ValueTask.CompletedTask;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        return SendAllAsync(buffer, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override ValueTask DisposeAsync()
    {
        Dispose(disposing: true);
        return base.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            base.Dispose(disposing);
            return;
        }

        _disposed = true;
        if (disposing && _ownsSocket)
        {
#pragma warning disable CA1031
            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
            }

            try
            {
                _socket.Close();
            }
            catch
            {
            }
#pragma warning restore CA1031
        }

        base.Dispose(disposing);
    }

    private int ReadTo(Span<byte> destination)
    {
        if (_receiveCount != 0)
        {
            return CopyFromReceiveBuffer(destination);
        }

        var timeoutMs = _readTimeoutMs;
        var startTick = Environment.TickCount64;

        while (true)
        {
            // Wait for readability in small increments so we can honor ReadTimeout.
            if (!Poll(SelectMode.SelectRead, PollDelayMs))
            {
                ThrowIfTimedOut(timeoutMs, startTick, "Read");
                continue;
            }

            var received = _socket.Receive(_receiveBuffer);
            if (received > 0)
            {
                _receiveOffset = 0;
                _receiveCount = received;
                return CopyFromReceiveBuffer(destination);
            }

            if (received == 0)
            {
                return 0;
            }

            if (received == global::ZeroTier.Sockets.Socket.ZTS_ERR_NO_RESULT)
            {
                ThrowIfTimedOut(timeoutMs, startTick, "Read");
                continue;
            }

            var errno = global::ZeroTier.Core.Node.ErrNo;
            if (errno == global::ZeroTier.Constants.EINTR)
            {
                continue;
            }

            if (IsWouldBlock(errno) || errno == global::ZeroTier.Constants.ENOTCONN)
            {
                ThrowIfTimedOut(timeoutMs, startTick, "Read");
                continue;
            }

            if (errno == global::ZeroTier.Constants.ETIMEDOUT)
            {
                throw new IOException("Read timed out.");
            }

            throw new IOException($"libzt receive failed (code {received}, errno {errno}).");
        }
    }

    private async ValueTask<int> ReadToAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (_receiveCount != 0)
        {
            return CopyFromReceiveBuffer(destination.Span);
        }

        var timeoutMs = _readTimeoutMs;
        var startTick = Environment.TickCount64;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Poll(SelectMode.SelectRead, timeoutMs: 0))
            {
                ThrowIfTimedOut(timeoutMs, startTick, "Read");
                await Task.Delay(PollDelayMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var received = _socket.Receive(_receiveBuffer);
            if (received > 0)
            {
                _receiveOffset = 0;
                _receiveCount = received;
                return CopyFromReceiveBuffer(destination.Span);
            }

            if (received == 0)
            {
                return 0;
            }

            if (received == global::ZeroTier.Sockets.Socket.ZTS_ERR_NO_RESULT)
            {
                ThrowIfTimedOut(timeoutMs, startTick, "Read");
                await Task.Delay(PollDelayMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var errno = global::ZeroTier.Core.Node.ErrNo;
            if (errno == global::ZeroTier.Constants.EINTR)
            {
                continue;
            }

            if (IsWouldBlock(errno) || errno == global::ZeroTier.Constants.ENOTCONN)
            {
                ThrowIfTimedOut(timeoutMs, startTick, "Read");
                await Task.Delay(PollDelayMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (errno == global::ZeroTier.Constants.ETIMEDOUT)
            {
                throw new IOException("Read timed out.");
            }

            throw new IOException($"libzt receive failed (code {received}, errno {errno}).");
        }
    }

    private int CopyFromReceiveBuffer(Span<byte> destination)
    {
        var toCopy = Math.Min(destination.Length, _receiveCount);
        _receiveBuffer.AsSpan(_receiveOffset, toCopy).CopyTo(destination);

        _receiveOffset += toCopy;
        _receiveCount -= toCopy;
        if (_receiveCount == 0)
        {
            _receiveOffset = 0;
        }

        return toCopy;
    }

    private void SendAll(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return;
        }

        // Always call Send with offset=0 and buffer.Length == intended send length.
        var bytes = payload.ToArray();
        var timeoutMs = _writeTimeoutMs;
        var startTick = Environment.TickCount64;

        while (bytes.Length != 0)
        {
            if (!Poll(SelectMode.SelectWrite, PollDelayMs))
            {
                ThrowIfTimedOut(timeoutMs, startTick, "Write");
                continue;
            }

            var sent = _socket.Send(bytes);
            if (sent > 0)
            {
                if (sent == bytes.Length)
                {
                    return;
                }

                bytes = SliceToNewArray(bytes, sent);
                continue;
            }

            if (sent == 0)
            {
                ThrowIfTimedOut(timeoutMs, startTick, "Write");
                continue;
            }

            if (sent == global::ZeroTier.Sockets.Socket.ZTS_ERR_NO_RESULT)
            {
                ThrowIfTimedOut(timeoutMs, startTick, "Write");
                continue;
            }

            var errno = global::ZeroTier.Core.Node.ErrNo;
            if (errno == global::ZeroTier.Constants.EINTR)
            {
                continue;
            }

            if (IsWouldBlock(errno) || errno == global::ZeroTier.Constants.ENOTCONN)
            {
                ThrowIfTimedOut(timeoutMs, startTick, "Write");
                continue;
            }

            if (errno == global::ZeroTier.Constants.ETIMEDOUT)
            {
                throw new IOException("Write timed out.");
            }

            throw new IOException($"libzt send failed (code {sent}, errno {errno}).");
        }
    }

    private async ValueTask SendAllAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length == 0)
        {
            return;
        }

        // Always call Send with offset=0 and buffer.Length == intended send length.
        var bytes = payload.ToArray();
        var timeoutMs = _writeTimeoutMs;
        var startTick = Environment.TickCount64;

        while (bytes.Length != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Poll(SelectMode.SelectWrite, timeoutMs: 0))
            {
                ThrowIfTimedOut(timeoutMs, startTick, "Write");
                await Task.Delay(PollDelayMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var sent = _socket.Send(bytes);
            if (sent > 0)
            {
                if (sent == bytes.Length)
                {
                    return;
                }

                bytes = SliceToNewArray(bytes, sent);
                continue;
            }

            if (sent == 0)
            {
                ThrowIfTimedOut(timeoutMs, startTick, "Write");
                await Task.Delay(PollDelayMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (sent == global::ZeroTier.Sockets.Socket.ZTS_ERR_NO_RESULT)
            {
                ThrowIfTimedOut(timeoutMs, startTick, "Write");
                await Task.Delay(PollDelayMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var errno = global::ZeroTier.Core.Node.ErrNo;
            if (errno == global::ZeroTier.Constants.EINTR)
            {
                continue;
            }

            if (IsWouldBlock(errno) || errno == global::ZeroTier.Constants.ENOTCONN)
            {
                ThrowIfTimedOut(timeoutMs, startTick, "Write");
                await Task.Delay(PollDelayMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (errno == global::ZeroTier.Constants.ETIMEDOUT)
            {
                throw new IOException("Write timed out.");
            }

            throw new IOException($"libzt send failed (code {sent}, errno {errno}).");
        }
    }

    private bool Poll(SelectMode mode, int timeoutMs)
    {
        try
        {
            return _socket.Poll(microSeconds: timeoutMs * 1000, mode);
        }
        catch (global::ZeroTier.Sockets.SocketException ex)
        {
            throw new IOException(
                $"libzt poll failed (service {ex.ServiceErrorCode}, socket {ex.SocketErrorCode}).",
                ex);
        }
    }

    private static bool IsWouldBlock(int errno)
        => errno == global::ZeroTier.Constants.EAGAIN || errno == global::ZeroTier.Constants.EWOULDBLOCK;

    private static int NormalizeTimeoutMs(int value)
    {
        // Be tolerant: some callers set 0/-1. Treat all <= 0 as infinite.
        return value <= 0 ? Timeout.Infinite : value;
    }

    private static void ThrowIfTimedOut(int timeoutMs, long startTick, string operation)
    {
        if (timeoutMs == Timeout.Infinite)
        {
            return;
        }

        var elapsed = Environment.TickCount64 - startTick;
        if (elapsed >= timeoutMs)
        {
            throw new IOException($"{operation} timed out.");
        }
    }

    private static byte[] SliceToNewArray(byte[] buffer, int offset)
    {
        if ((uint)offset > (uint)buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var remainingLength = buffer.Length - offset;
        if (remainingLength == 0)
        {
            return Array.Empty<byte>();
        }

        var remaining = new byte[remainingLength];
        Buffer.BlockCopy(buffer, offset, remaining, 0, remainingLength);
        return remaining;
    }
}
