using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace JKamsker.LibZt.ZeroTier.Net;

internal sealed class ZtUserSpaceTcpServerConnection : IAsyncDisposable
{
    private const ushort DefaultMss = 1200;
    private const int MaxReceiveBufferBytes = 256 * 1024;

    private readonly IZtUserSpaceIpLink _link;
    private readonly IPAddress _localAddress;
    private readonly IPAddress _remoteAddress;
    private readonly ushort _localPort;
    private readonly ushort _remotePort;
    private readonly ushort _mss;
    private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private Task? _receiveLoop;
    private TaskCompletionSource<bool>? _acceptTcs;
    private TaskCompletionSource<bool>? _ackTcs;
    private uint _sendUna;
    private uint _sendNext;
    private uint _recvNext;
    private readonly Dictionary<uint, byte[]> _outOfOrder = new();
    private uint? _pendingFinSeq;
    private long _receiveBufferedBytes;
    private ushort _lastAdvertisedWindow = ushort.MaxValue;
    private int _windowUpdatePending;
    private double? _srttMs;
    private double _rttvarMs;
    private TimeSpan _rto = TimeSpan.FromSeconds(1);
    private bool _handshakeStarted;
    private bool _connected;
    private bool _remoteClosed;
    private bool _disposed;

    public ZtUserSpaceTcpServerConnection(
        IZtUserSpaceIpLink link,
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
        _localAddress = localAddress;
        _remoteAddress = remoteAddress;
        _localPort = localPort;
        _remotePort = remotePort;
        _mss = mss;
    }

    public bool Connected => _connected && !_disposed && !_remoteClosed;

    public Stream GetStream()
        => new ZtTcpStream(this);

    public async Task AcceptAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_connected)
        {
            return;
        }

        _receiveLoop ??= Task.Run(ReceiveLoopAsync, CancellationToken.None);

        _acceptTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _acceptTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        _connected = true;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.Length == 0)
        {
            return 0;
        }

        while (true)
        {
            if (_incoming.Reader.TryRead(out var readSegment))
            {
                var toCopy = Math.Min(readSegment.Length, buffer.Length);
                readSegment.Span.Slice(0, toCopy).CopyTo(buffer.Span);
                if (toCopy < readSegment.Length)
                {
                    _incoming.Writer.TryWrite(readSegment.Slice(toCopy));
                }

                ConsumeReceiveBuffer(toCopy);
                return toCopy;
            }

            if (_remoteClosed)
            {
                return 0;
            }

            try
            {
                var segmentFromChannel = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var toCopy = Math.Min(segmentFromChannel.Length, buffer.Length);
                segmentFromChannel.Span.Slice(0, toCopy).CopyTo(buffer.Span);
                if (toCopy < segmentFromChannel.Length)
                {
                    _incoming.Writer.TryWrite(segmentFromChannel.Slice(toCopy));
                }

                ConsumeReceiveBuffer(toCopy);
                return toCopy;
            }
            catch (ChannelClosedException)
            {
                return 0;
            }
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.IsEmpty)
        {
            return;
        }

        if (!_connected)
        {
            throw new InvalidOperationException("TCP connection is not established.");
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_remoteClosed)
            {
                throw new IOException("Remote closed the connection.");
            }

            var offset = 0;
            while (offset < buffer.Length)
            {
                var segmentLength = Math.Min(_mss, buffer.Length - offset);
                var segment = buffer.Slice(offset, segmentLength);

                var seq = _sendNext;
                _sendNext = unchecked(_sendNext + (uint)segmentLength);
                var expectedAck = _sendNext;

                await SendTcpWithRetriesAsync(
                        seq: seq,
                        ack: _recvNext,
                        flags: ZtTcpCodec.Flags.Psh | ZtTcpCodec.Flags.Ack,
                        options: ReadOnlyMemory<byte>.Empty,
                        payload: segment,
                        expectedAck: expectedAck,
                        cancellationToken)
                    .ConfigureAwait(false);

                offset += segmentLength;
            }
        }
        finally
        {
            _sendLock.Release();
        }
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
            await _cts.CancelAsync().ConfigureAwait(false);
            _incoming.Writer.TryComplete();

            if (_connected && !_remoteClosed)
            {
                try
                {
                    await SendTcpAsync(
                            seq: _sendNext,
                            ack: _recvNext,
                            flags: ZtTcpCodec.Flags.Fin | ZtTcpCodec.Flags.Ack,
                            options: ReadOnlyMemory<byte>.Empty,
                            payload: ReadOnlyMemory<byte>.Empty,
                            cancellationToken: CancellationToken.None)
                        .ConfigureAwait(false);
                    _sendNext += 1;
                }
                catch (OperationCanceledException)
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

            if (_receiveLoop is not null)
            {
                try
                {
                    await _receiveLoop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            await _link.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _disposeLock.Release();
            _disposeLock.Dispose();
            _sendLock.Dispose();
            _cts.Dispose();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            ReadOnlyMemory<byte> ipPacket;
            try
            {
                ipPacket = await _link.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (InvalidOperationException ex)
            {
                _remoteClosed = true;
                _incoming.Writer.TryComplete(ex);
                _acceptTcs?.TrySetException(ex);
                _ackTcs?.TrySetException(ex);
                return;
            }
            catch (IOException ex)
            {
                _remoteClosed = true;
                _incoming.Writer.TryComplete(ex);
                _acceptTcs?.TrySetException(ex);
                _ackTcs?.TrySetException(ex);
                return;
            }

            IPAddress src;
            IPAddress dst;
            ReadOnlySpan<byte> ipPayload;
            if (_localAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                if (!ZtIpv4Codec.TryParse(ipPacket.Span, out src, out dst, out var protocol, out ipPayload))
                {
                    continue;
                }

                if (!src.Equals(_remoteAddress) || !dst.Equals(_localAddress) || protocol != ZtTcpCodec.ProtocolNumber)
                {
                    continue;
                }
            }
            else
            {
                if (!ZtIpv6Codec.TryParse(ipPacket.Span, out src, out dst, out var nextHeader, out _, out ipPayload))
                {
                    continue;
                }

                if (!src.Equals(_remoteAddress) || !dst.Equals(_localAddress) || nextHeader != ZtTcpCodec.ProtocolNumber)
                {
                    continue;
                }
            }

            if (!ZtTcpCodec.TryParse(ipPayload, out var srcPort, out var dstPort, out var seq, out var ack, out var flags, out _, out var tcpPayload))
            {
                continue;
            }

            if (srcPort != _remotePort || dstPort != _localPort)
            {
                continue;
            }

            if ((flags & ZtTcpCodec.Flags.Rst) != 0)
            {
                _remoteClosed = true;
                _incoming.Writer.TryComplete();
                _acceptTcs?.TrySetException(new IOException("Remote reset the connection."));
                _ackTcs?.TrySetException(new IOException("Remote reset the connection."));
                continue;
            }

            if (!_connected)
            {
                if ((flags & ZtTcpCodec.Flags.Syn) != 0 && (flags & ZtTcpCodec.Flags.Ack) == 0)
                {
                    if (!_handshakeStarted)
                    {
                        _handshakeStarted = true;
                        _acceptTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _sendUna = GenerateInitialSequenceNumber();
                        _sendNext = unchecked(_sendUna + 1);
                    }

                    _recvNext = unchecked(seq + 1);

                    var synAckOptions = ZtTcpCodec.EncodeMssOption(_mss);
                    await SendTcpAsync(
                            seq: _sendUna,
                            ack: _recvNext,
                            flags: ZtTcpCodec.Flags.Syn | ZtTcpCodec.Flags.Ack,
                            options: synAckOptions,
                            payload: ReadOnlyMemory<byte>.Empty,
                            cancellationToken: token)
                        .ConfigureAwait(false);
                    continue;
                }

                if (_handshakeStarted &&
                    (flags & ZtTcpCodec.Flags.Ack) != 0 &&
                    ack == _sendNext)
                {
                    _connected = true;
                    _acceptTcs?.TrySetResult(true);
                }
            }

            if (!_connected)
            {
                continue;
            }

            if ((flags & ZtTcpCodec.Flags.Ack) != 0)
            {
                if (ack != 0 && SequenceGreaterThan(ack, _sendUna))
                {
                    _sendUna = ack;
                    _ackTcs?.TrySetResult(true);
                }
            }

            var hasFin = (flags & ZtTcpCodec.Flags.Fin) != 0;
            var shouldAck = hasFin || !tcpPayload.IsEmpty;
            var originalPayloadLength = tcpPayload.Length;

            if (!tcpPayload.IsEmpty)
            {
                var segmentSeq = seq;

                if (SequenceGreaterThanOrEqual(_recvNext, segmentSeq))
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
                            _remoteClosed = true;
                            _incoming.Writer.TryComplete();
                        }
                    }
                    else if (SequenceGreaterThan(segmentSeq, _recvNext))
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
                    _remoteClosed = true;
                    _incoming.Writer.TryComplete();
                }
                else if (SequenceGreaterThan(finSeq, _recvNext))
                {
                    _pendingFinSeq = finSeq;
                }
            }

            if (shouldAck)
            {
                await SendTcpAsync(
                        seq: _sendNext,
                        ack: _recvNext,
                        flags: ZtTcpCodec.Flags.Ack,
                        options: ReadOnlyMemory<byte>.Empty,
                        payload: ReadOnlyMemory<byte>.Empty,
                        cancellationToken: token)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task SendTcpWithRetriesAsync(
        uint seq,
        uint ack,
        ZtTcpCodec.Flags flags,
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
                if (SequenceGreaterThanOrEqual(_sendUna, expectedAck))
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
        ZtTcpCodec.Flags flags,
        ReadOnlyMemory<byte> options,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var window = GetReceiveWindow();
        _lastAdvertisedWindow = window;

        var tcp = ZtTcpCodec.Encode(
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
            ip = ZtIpv4Codec.Encode(
                _localAddress,
                _remoteAddress,
                protocol: ZtTcpCodec.ProtocolNumber,
                payload: tcp,
                identification: GenerateIpIdentification());
        }
        else
        {
            ip = ZtIpv6Codec.Encode(
                _localAddress,
                _remoteAddress,
                nextHeader: ZtTcpCodec.ProtocolNumber,
                payload: tcp,
                hopLimit: 64);
        }

        await _link.SendAsync(ip, cancellationToken).ConfigureAwait(false);
    }

    private ushort GetReceiveWindow()
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
        TrySendWindowUpdate();
    }

    private void TrySendWindowUpdate()
    {
        if (!_connected || _disposed || _remoteClosed)
        {
            return;
        }

        if (_lastAdvertisedWindow != 0)
        {
            return;
        }

        var window = GetReceiveWindow();
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
                await SendTcpAsync(
                        seq: _sendNext,
                        ack: _recvNext,
                        flags: ZtTcpCodec.Flags.Ack,
                        options: ReadOnlyMemory<byte>.Empty,
                        payload: ReadOnlyMemory<byte>.Empty,
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
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

    private static uint GenerateInitialSequenceNumber()
    {
        Span<byte> buffer = stackalloc byte[4];
        RandomNumberGenerator.Fill(buffer);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static ushort GenerateIpIdentification()
    {
        Span<byte> buffer = stackalloc byte[2];
        RandomNumberGenerator.Fill(buffer);
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
    }

    private static bool SequenceGreaterThan(uint a, uint b) => (int)(a - b) > 0;

    private static bool SequenceGreaterThanOrEqual(uint a, uint b) => (int)(a - b) >= 0;

    private sealed class ZtTcpStream : Stream
    {
        private readonly ZtUserSpaceTcpServerConnection _client;

        public ZtTcpStream(ZtUserSpaceTcpServerConnection client)
        {
            _client = client;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
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

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => await _client.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => await _client.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
