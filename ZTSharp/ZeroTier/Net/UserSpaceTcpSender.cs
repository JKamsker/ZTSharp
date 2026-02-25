using System.Diagnostics;
using System.IO;
using System.Net;

namespace ZTSharp.ZeroTier.Net;

internal sealed class UserSpaceTcpSender : IAsyncDisposable
{
    private readonly ushort _mss;
    private readonly UserSpaceTcpReceiver _receiver;
    private readonly UserSpaceTcpSegmentTransmitter _transmitter;
    private readonly UserSpaceTcpRemoteSendWindow _remoteSendWindow = new();
    private readonly UserSpaceTcpRtoEstimator _rtoEstimator = new();

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private TaskCompletionSource<bool>? _ackTcs;
    private uint _sendUna;
    private uint _sendNext;

    private ushort _lastAdvertisedWindow = ushort.MaxValue;
    private readonly UserSpaceTcpWindowUpdateTrigger _windowUpdateTrigger;

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

        _mss = mss;
        _receiver = receiver;
        _transmitter = new UserSpaceTcpSegmentTransmitter(link, localAddress, remoteAddress, localPort, remotePort);
        _windowUpdateTrigger = new UserSpaceTcpWindowUpdateTrigger(
            receiver,
            sendPureAckAsync: SendPureAckAsync,
            getLastAdvertisedWindow: () => _lastAdvertisedWindow,
            isDisposed: () => _disposed);
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
        => _remoteSendWindow.Update(windowSize);

    public void FailPendingOperations(Exception exception)
    {
        _ackTcs?.TrySetException(exception);
        _remoteSendWindow.SignalWaiters(exception);
    }

    public void OnAckReceived(uint ack)
    {
        if (ack == 0)
        {
            return;
        }

        if (UserSpaceTcpSequenceNumbers.GreaterThan(ack, _sendUna) &&
            UserSpaceTcpSequenceNumbers.GreaterThanOrEqual(_sendNext, ack))
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

                var remoteWindow = _remoteSendWindow.Window;
                var maxChunkSize = Math.Min((int)_mss, (int)remoteWindow);
                if (maxChunkSize == 0)
                {
                    continue;
                }

                var chunk = remaining.Length > maxChunkSize ? remaining.Slice(0, maxChunkSize) : remaining;
                remaining = remaining.Slice(chunk.Length);

                var seq = AllocateNextSequence((uint)chunk.Length);
                var expectedAck = _sendNext;

                await SendTcpWithRetriesAsync(
                        seq,
                        ack: getAckNumber(),
                        flags: TcpCodec.Flags.Ack | TcpCodec.Flags.Psh,
                        options: ReadOnlyMemory<byte>.Empty,
                        payload: chunk,
                        expectedAck,
                        cancellationToken)
                    .ConfigureAwait(false);
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
            var finSeq = AllocateNextSequence(bytes: 1);
            var expectedAck = _sendNext;

            await SendTcpWithRetriesAsync(
                    seq: finSeq,
                    ack,
                    flags: TcpCodec.Flags.Fin | TcpCodec.Flags.Ack,
                    options: ReadOnlyMemory<byte>.Empty,
                    payload: ReadOnlyMemory<byte>.Empty,
                    expectedAck,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void MaybeSendWindowUpdate(bool isConnected, bool isRemoteClosed)
        => _windowUpdateTrigger.MaybeSendWindowUpdate(isConnected, isRemoteClosed);

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
        var delay = _rtoEstimator.Rto;
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
                        _rtoEstimator.Update(Stopwatch.GetElapsedTime(sentAt));
                    }

                    return;
                }
            }
            catch (TimeoutException)
            {
                delay = delay < maxDelay ? delay + delay : maxDelay;
                _rtoEstimator.Rto = delay;
            }
        }

        throw new IOException($"TCP send timed out waiting for ACK after {retries} attempts.");
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
        await _transmitter
            .SendAsync(seq, ack, flags, window, options, payload, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WaitForRemoteSendWindowAsync(CancellationToken cancellationToken)
    {
        await _remoteSendWindow.WaitForNonZeroAsync(cancellationToken).ConfigureAwait(false);

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_receiver.RemoteClosed)
        {
            throw new IOException("Remote has closed the connection.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
