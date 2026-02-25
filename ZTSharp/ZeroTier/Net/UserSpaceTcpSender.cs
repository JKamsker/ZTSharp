using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;

namespace ZTSharp.ZeroTier.Net;

internal sealed class UserSpaceTcpSender : IAsyncDisposable
{
    private readonly IUserSpaceIpLink _link;
    private readonly IPAddress _localAddress;
    private readonly IPAddress _remoteAddress;
    private readonly ushort _remotePort;
    private readonly ushort _localPort;
    private readonly ushort _mss;
    private readonly UserSpaceTcpReceiver _receiver;

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private TaskCompletionSource<bool>? _ackTcs;
    private uint _sendUna;
    private uint _sendNext;

    private ushort _lastAdvertisedWindow = ushort.MaxValue;
    private int _windowUpdatePending;

    private double? _srttMs;
    private double _rttvarMs;
    private TimeSpan _rto = TimeSpan.FromSeconds(1);

    private ushort _remoteWindow = ushort.MaxValue;
    private TaskCompletionSource<bool>? _remoteWindowTcs;
    private readonly object _remoteWindowLock = new();

    private bool _disposed;

    public UserSpaceTcpSender(
        IUserSpaceIpLink link,
        IPAddress localAddress,
        IPAddress remoteAddress,
        ushort localPort,
        ushort remotePort,
        ushort mss,
        UserSpaceTcpReceiver receiver)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(localAddress);
        ArgumentNullException.ThrowIfNull(remoteAddress);
        ArgumentNullException.ThrowIfNull(receiver);

        _link = link;
        _localAddress = localAddress;
        _remoteAddress = remoteAddress;
        _localPort = localPort;
        _remotePort = remotePort;
        _mss = mss;
        _receiver = receiver;
    }

    public uint SendNext => _sendNext;

    public void InitializeSendState(uint iss)
    {
        _sendUna = iss;
        _sendNext = iss;
    }

    public uint AllocateNextSequence(uint bytes)
    {
        var current = _sendNext;
        _sendNext = unchecked(_sendNext + bytes);
        return current;
    }

    public void UpdateRemoteSendWindow(ushort windowSize)
    {
        TaskCompletionSource<bool>? toRelease = null;
        lock (_remoteWindowLock)
        {
            _remoteWindow = windowSize;
            if (windowSize != 0 && _remoteWindowTcs is not null)
            {
                toRelease = _remoteWindowTcs;
                _remoteWindowTcs = null;
            }
        }

        toRelease?.TrySetResult(true);
    }

    public void FailPendingOperations(Exception exception)
    {
        _ackTcs?.TrySetException(exception);
        SignalRemoteSendWindowWaiters(exception);
    }

    public void OnAckReceived(uint ack)
    {
        if (ack == 0)
        {
            return;
        }

        if (UserSpaceTcpSequenceNumbers.GreaterThan(ack, _sendUna))
        {
            _sendUna = ack;
            _ackTcs?.TrySetResult(true);
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, Func<uint> getAckNumber, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.Length == 0)
        {
            return;
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var remaining = buffer;
            while (!remaining.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await WaitForRemoteSendWindowAsync(cancellationToken).ConfigureAwait(false);

                var remoteWindow = _remoteWindow;
                var maxChunkSize = Math.Min((int)_mss, (int)remoteWindow);
                if (maxChunkSize == 0)
                {
                    continue;
                }

                var chunk = remaining.Length > maxChunkSize ? remaining.Slice(0, maxChunkSize) : remaining;
                remaining = remaining.Slice(chunk.Length);

                var seq = _sendNext;
                var expectedAck = unchecked(_sendNext + (uint)chunk.Length);

                await SendTcpWithRetriesAsync(
                        seq,
                        ack: getAckNumber(),
                        flags: TcpCodec.Flags.Ack | TcpCodec.Flags.Psh,
                        options: ReadOnlyMemory<byte>.Empty,
                        payload: chunk,
                        expectedAck,
                        cancellationToken)
                    .ConfigureAwait(false);

                _sendNext = expectedAck;
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SendPureAckAsync(uint ack, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await SendTcpAsync(
                seq: _sendNext,
                ack,
                flags: TcpCodec.Flags.Ack,
                options: ReadOnlyMemory<byte>.Empty,
                payload: ReadOnlyMemory<byte>.Empty,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task SendSegmentAsync(
        uint seq,
        uint ack,
        TcpCodec.Flags flags,
        ReadOnlyMemory<byte> options,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SendTcpAsync(seq, ack, flags, options, payload, cancellationToken);
    }

    public async Task SendFinWithRetriesAsync(uint ack, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var finSeq = _sendNext;
            var expectedAck = unchecked(finSeq + 1);

            await SendTcpWithRetriesAsync(
                    seq: finSeq,
                    ack,
                    flags: TcpCodec.Flags.Fin | TcpCodec.Flags.Ack,
                    options: ReadOnlyMemory<byte>.Empty,
                    payload: ReadOnlyMemory<byte>.Empty,
                    expectedAck,
                    cancellationToken)
                .ConfigureAwait(false);

            _sendNext = expectedAck;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void MaybeSendWindowUpdate(bool isConnected, bool isRemoteClosed)
    {
        if (!isConnected || _disposed || isRemoteClosed)
        {
            return;
        }

        if (_lastAdvertisedWindow != 0)
        {
            return;
        }

        var window = _receiver.GetReceiveWindow();
        if (window == 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _windowUpdatePending, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await SendPureAckAsync(ack: _receiver.RecvNext, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }
            finally
            {
                Interlocked.Exchange(ref _windowUpdatePending, 0);
            }
        });
    }

    private async Task SendTcpWithRetriesAsync(
        uint seq,
        uint ack,
        TcpCodec.Flags flags,
        ReadOnlyMemory<byte> options,
        ReadOnlyMemory<byte> payload,
        uint expectedAck,
        CancellationToken cancellationToken)
    {
        const int retries = 8;
        var delay = _rto;
        var maxDelay = TimeSpan.FromSeconds(30);

        for (var attempt = 0; attempt < retries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _ackTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sentAt = Stopwatch.GetTimestamp();
            await SendTcpAsync(seq, ack, flags, options, payload, cancellationToken).ConfigureAwait(false);

            try
            {
                await _ackTcs.Task.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
                if (UserSpaceTcpSequenceNumbers.GreaterThanOrEqual(_sendUna, expectedAck))
                {
                    if (attempt == 0)
                    {
                        UpdateRto(Stopwatch.GetElapsedTime(sentAt));
                    }

                    return;
                }
            }
            catch (TimeoutException)
            {
                delay = delay < maxDelay ? delay + delay : maxDelay;
                _rto = delay;
            }
        }

        throw new IOException($"TCP send timed out waiting for ACK after {retries} attempts.");
    }

    private void UpdateRto(TimeSpan rtt)
    {
        var r = rtt.TotalMilliseconds;
        if (r <= 0 || double.IsNaN(r) || double.IsInfinity(r))
        {
            return;
        }

        const double alpha = 1.0 / 8.0;
        const double beta = 1.0 / 4.0;

        if (_srttMs is null)
        {
            _srttMs = r;
            _rttvarMs = r / 2.0;
        }
        else
        {
            var srtt = _srttMs.Value;
            _rttvarMs = (1.0 - beta) * _rttvarMs + beta * Math.Abs(srtt - r);
            _srttMs = (1.0 - alpha) * srtt + alpha * r;
        }

        var rtoMs = _srttMs.Value + Math.Max(1.0, 4.0 * _rttvarMs);
        rtoMs = Math.Clamp(rtoMs, 200.0, 60_000.0);
        _rto = TimeSpan.FromMilliseconds(rtoMs);
    }

    private async Task SendTcpAsync(
        uint seq,
        uint ack,
        TcpCodec.Flags flags,
        ReadOnlyMemory<byte> options,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var window = _receiver.GetReceiveWindow();
        _lastAdvertisedWindow = window;

        var tcp = TcpCodec.Encode(
            _localAddress,
            _remoteAddress,
            sourcePort: _localPort,
            destinationPort: _remotePort,
            sequenceNumber: seq,
            acknowledgmentNumber: ack,
            flags,
            windowSize: window,
            options: options.Span,
            payload.Span);

        byte[] ip;
        if (_localAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            ip = Ipv4Codec.Encode(
                _localAddress,
                _remoteAddress,
                protocol: TcpCodec.ProtocolNumber,
                payload: tcp,
                identification: GenerateIpIdentification());
        }
        else
        {
            ip = Ipv6Codec.Encode(
                _localAddress,
                _remoteAddress,
                nextHeader: TcpCodec.ProtocolNumber,
                payload: tcp,
                hopLimit: 64);
        }

        await _link.SendAsync(ip, cancellationToken).ConfigureAwait(false);
    }

    private void SignalRemoteSendWindowWaiters(Exception exception)
    {
        TaskCompletionSource<bool>? toRelease = null;
        lock (_remoteWindowLock)
        {
            if (_remoteWindowTcs is not null)
            {
                toRelease = _remoteWindowTcs;
                _remoteWindowTcs = null;
            }
        }

        toRelease?.TrySetException(exception);
    }

    private async Task WaitForRemoteSendWindowAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_receiver.RemoteClosed)
            {
                throw new IOException("Remote has closed the connection.");
            }

            TaskCompletionSource<bool> tcs;
            lock (_remoteWindowLock)
            {
                if (_remoteWindow != 0)
                {
                    return;
                }

                tcs = _remoteWindowTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static ushort GenerateIpIdentification()
    {
        Span<byte> buffer = stackalloc byte[2];
        RandomNumberGenerator.Fill(buffer);
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
