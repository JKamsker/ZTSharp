using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ZTSharp.Sockets;

namespace ZTSharp.Http;

internal sealed class OwnedOverlayTcpClientStream : Stream
{
    private readonly OverlayTcpClient _client;
    private readonly Stream _inner;
    private readonly Action? _onDispose;
    private int _disposed;
    private int _onDisposeInvoked;
    private Task? _backgroundDispose;

    public OwnedOverlayTcpClientStream(OverlayTcpClient client, Action? onDispose = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _inner = client.GetStream();
        _onDispose = onDispose;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(buffer, cancellationToken);

    [SuppressMessage(
        "Reliability",
        "CA1031:Do not catch general exception types",
        Justification = "Stream disposal is best-effort and must not throw (e.g., during HttpResponseMessage.Dispose()).")]
    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (disposing)
        {
            try
            {
                _inner.Dispose();
            }
            catch
            {
            }

            var disposeTask = _client.DisposeAsync();
            if (disposeTask.IsCompletedSuccessfully)
            {
                InvokeOnDispose();
            }
            else
            {
                _backgroundDispose = ObserveDisposeAsync(disposeTask);
            }
        }

        base.Dispose(disposing);
    }

    [SuppressMessage(
        "Reliability",
        "CA1031:Do not catch general exception types",
        Justification = "Stream disposal is best-effort and must not throw (e.g., during HttpResponseMessage.DisposeAsync()).")]
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        await base.DisposeAsync().ConfigureAwait(false);

        InvokeOnDispose();

        if (_backgroundDispose is not null)
        {
            try
            {
                await _backgroundDispose.ConfigureAwait(false);
            }
            catch
            {
            }
        }

    }

    [SuppressMessage(
        "Reliability",
        "CA1031:Do not catch general exception types",
        Justification = "Disposal must observe and ignore failures to prevent unobserved task exceptions.")]
    private async Task ObserveDisposeAsync(ValueTask disposeTask)
    {
        try
        {
            await disposeTask.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            InvokeOnDispose();
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA1031:Do not catch general exception types",
        Justification = "Dispose callbacks are best-effort and must not throw.")]
    private void InvokeOnDispose()
    {
        if (_onDispose is null || Interlocked.Exchange(ref _onDisposeInvoked, 1) == 1)
        {
            return;
        }

        try
        {
            _onDispose();
        }
        catch
        {
        }
    }
}

