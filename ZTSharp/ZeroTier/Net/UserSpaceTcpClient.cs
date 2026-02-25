using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Security.Cryptography;

namespace ZTSharp.ZeroTier.Net;

internal sealed class UserSpaceTcpClient : IAsyncDisposable
{
    private const ushort DefaultMss = 1200;

    private readonly IUserSpaceIpLink _link;
    private readonly IPAddress _localAddress;
    private readonly IPAddress _remoteAddress;
    private readonly ushort _remotePort;
    private readonly ushort _localPort;
    private readonly ushort _mss;

    private readonly UserSpaceTcpConnectionSignals _signals = new();
    private readonly UserSpaceTcpReceiver _receiver = new();
    private readonly UserSpaceTcpSender _sender;
    private readonly UserSpaceTcpReceiveLoop _receiveLoop;

    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private Task? _receiveLoopTask;
    private bool _disposed;

    public UserSpaceTcpClient(
        IUserSpaceIpLink link,
        IPAddress localAddress,
        IPAddress remoteAddress,
        ushort remotePort,
        ushort? localPort = null,
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

        if (remotePort == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(remotePort));
        }

        _link = link;
        _localAddress = localAddress;
        _remoteAddress = remoteAddress;
        _remotePort = remotePort;
        _localPort = localPort ?? GenerateEphemeralPort();
        _mss = mss;

        _sender = new UserSpaceTcpSender(
            link,
            localAddress,
            remoteAddress,
            localPort: _localPort,
            remotePort,
            mss,
            _receiver);

        _receiveLoop = new UserSpaceTcpReceiveLoop(
            link,
            localAddress,
            remoteAddress,
            localPort: _localPort,
            remotePort,
            _sender,
            _receiver,
            _signals);
    }

    public bool Connected => _signals.Connected && !_disposed && !_receiver.RemoteClosed;

    public Stream GetStream()
        => new UserSpaceTcpStream(this);

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_signals.Connected)
        {
            return;
        }

        _receiveLoopTask ??= Task.Run(() => _receiveLoop.RunAsync(_cts.Token), CancellationToken.None);

        var iss = GenerateInitialSequenceNumber();
        _sender.InitializeSendState(iss);

        _signals.ConnectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var synOptions = TcpCodec.EncodeMssOption(_mss);
        var synSeq = _sender.AllocateNextSequence(bytes: 1);

        const int synRetries = 5;
        var delay = TimeSpan.FromMilliseconds(250);

        for (var attempt = 0; attempt <= synRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _sender
                .SendSegmentAsync(
                    seq: synSeq,
                    ack: 0,
                    flags: TcpCodec.Flags.Syn,
                    options: synOptions,
                    payload: ReadOnlyMemory<byte>.Empty,
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await _signals.ConnectTcs.Task.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
                _signals.Connected = true;
                return;
            }
            catch (TimeoutException)
            {
                if (attempt >= synRetries)
                {
                    break;
                }

                delay = delay < TimeSpan.FromSeconds(2)
                    ? delay + delay
                    : TimeSpan.FromSeconds(2);
            }
        }

        throw new TimeoutException("Timed out waiting for TCP SYN-ACK.");
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
            throw new InvalidOperationException("TCP client is not connected.");
        }

        if (_receiver.RemoteClosed)
        {
            throw new IOException("Remote has closed the connection.");
        }

        await _sender.WriteAsync(buffer, getAckNumber: () => _receiver.RecvNext, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
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

            var disposed = new ObjectDisposedException(nameof(UserSpaceTcpClient));
            _signals.ConnectTcs?.TrySetException(disposed);
            _receiver.Complete(disposed);
            _sender.FailPendingOperations(disposed);

            if (_signals.Connected && !_receiver.RemoteClosed)
            {
                try
                {
                    using var finCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _sender.SendFinWithRetriesAsync(_receiver.RecvNext, finCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (TimeoutException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                catch (IOException)
                {
                }
            }

            await _cts.CancelAsync().ConfigureAwait(false);
            if (_receiveLoopTask is not null)
            {
                try
                {
                    await _receiveLoopTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
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

    private static ushort GenerateEphemeralPort()
    {
        Span<byte> buffer = stackalloc byte[2];
        RandomNumberGenerator.Fill(buffer);
        var port = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        return (ushort)(49152 + (port % (ushort)(65535 - 49152)));
    }

    private static uint GenerateInitialSequenceNumber()
    {
        Span<byte> buffer = stackalloc byte[4];
        RandomNumberGenerator.Fill(buffer);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }
}
