using System.IO;
using System.Net;

namespace ZTSharp.ZeroTier.Net;

internal sealed class UserSpaceTcpServerConnection : IAsyncDisposable
{
    private const ushort DefaultMss = 1200;

    private readonly IUserSpaceIpLink _link;
    private readonly ushort _mss;

    private readonly UserSpaceTcpAcceptSignals _signals = new();
    private readonly UserSpaceTcpReceiver _receiver = new();
    private readonly UserSpaceTcpSender _sender;
    private readonly UserSpaceTcpServerReceiveLoop _receiveLoop;

    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private Task? _receiveLoopTask;
    private bool _disposed;
    private int _disposeState;

    public UserSpaceTcpServerConnection(
        IUserSpaceIpLink link,
        IPAddress localAddress,
        ushort localPort,
        IPAddress remoteAddress,
        ushort remotePort,
        ushort mss = DefaultMss)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(localAddress);
        ArgumentNullException.ThrowIfNull(remoteAddress);

        if (localAddress.AddressFamily != remoteAddress.AddressFamily)
        {
            throw new ArgumentOutOfRangeException(nameof(remoteAddress), "Local and remote address families must match.");
        }

        if (localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork &&
            localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            throw new ArgumentOutOfRangeException(nameof(localAddress), "Only IPv4 and IPv6 are supported.");
        }

        if (localPort == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(localPort));
        }

        if (remotePort == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(remotePort));
        }

        _link = link;
        _mss = mss;

        _sender = new UserSpaceTcpSender(
            link,
            localAddress,
            remoteAddress,
            localPort,
            remotePort,
            mss,
            _receiver);

        _receiveLoop = new UserSpaceTcpServerReceiveLoop(
            link,
            localAddress,
            localPort,
            remoteAddress,
            remotePort,
            mss,
            _sender,
            _receiver,
            _signals);
    }

    public bool Connected => _signals.Connected && !_disposed && !_receiver.RemoteClosed;

    public Stream GetStream()
        => new UserSpaceTcpServerStream(this);

    public async Task AcceptAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_signals.Connected)
        {
            return;
        }

        _receiveLoopTask ??= Task.Run(() => _receiveLoop.RunAsync(_cts.Token), CancellationToken.None);

        _signals.AcceptTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _signals.AcceptTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        _signals.Connected = true;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var bytesRead = await _receiver.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _sender.MaybeSendWindowUpdate(_signals.Connected, _receiver.RemoteClosed);
        return bytesRead;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_signals.Connected)
        {
            throw new InvalidOperationException("TCP server connection is not established.");
        }

        if (_receiver.RemoteClosed)
        {
            throw new IOException("Remote has closed the connection.");
        }

        await _sender.WriteAsync(buffer, getAckNumber: () => _receiver.RecvNext, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await _disposeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_signals.Connected)
            {
                try
                {
                    using var finCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _sender.SendFinWithRetriesAsync(_receiver.RecvNext, finCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or ObjectDisposedException or InvalidOperationException or IOException)
                {
                }
            }

            var disposed = new ObjectDisposedException(nameof(UserSpaceTcpServerConnection));
            _signals.AcceptTcs?.TrySetException(disposed);
            _receiver.Complete(disposed);
            _sender.FailPendingOperations(disposed);

            await _cts.CancelAsync().ConfigureAwait(false);
            if (_receiveLoopTask is not null)
            {
                try
                {
                    await _receiveLoopTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                }
            }

            await _link.DisposeAsync().ConfigureAwait(false);
            await _sender.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _disposeLock.Release();
            _disposeLock.Dispose();
            _cts.Dispose();
        }
    }
}
