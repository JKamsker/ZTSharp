using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace JKamsker.LibZt.Sockets;

/// <summary>
/// Managed stream client built on top of the node transport (not OS TCP).
/// </summary>
public sealed class OverlayTcpClient : IAsyncDisposable
{
    private const byte TcpFrameVersion = 1;

    private enum TcpFrameType : byte
    {
        Syn = 1,
        SynAck = 2,
        Data = 3,
        Fin = 4
    }

    private const int HeaderLength = 1 + 1 + 2 + 2 + sizeof(ulong) + sizeof(ulong);
    private const int MaxDataPerFrame = 1024;

    private readonly Channel<ReadOnlyMemory<byte>> _incoming;
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Node _node;
    private readonly ulong _networkId;
    private readonly ulong _localNodeId;
    private readonly int _localPort;

    private ulong _remoteNodeId;
    private int _remotePort;
    private ulong _connectionId;

    private ReadOnlyMemory<byte> _currentSegment;
    private int _currentSegmentOffset;

    private TaskCompletionSource<bool>? _connectTcs;
    private bool _connected;
    private bool _remoteClosed;
    private bool _disposed;

    private OverlayTcpStream? _stream;

    public OverlayTcpClient(Node node, ulong networkId, int localPort)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (localPort is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(localPort));
        }

        _node = node;
        _networkId = networkId;
        _localPort = localPort;
        _localNodeId = node.NodeId.Value;
        _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        _node.RawFrameReceived += OnFrameReceived;
    }

    internal OverlayTcpClient(
        Node node,
        ulong networkId,
        int localPort,
        ulong remoteNodeId,
        int remotePort,
        ulong connectionId)
        : this(node, networkId, localPort)
    {
        _remoteNodeId = remoteNodeId;
        _remotePort = remotePort;
        _connectionId = connectionId;
        _connected = true;
    }

    public bool Connected => _connected && !_disposed && !_remoteClosed;

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1024:Use properties where appropriate",
        Justification = "Matches TcpClient-style API surface.")]
    public Stream GetStream()
    {
        if (_stream is not null)
        {
            return _stream;
        }

        var created = new OverlayTcpStream(this);
        _stream = created;
        return created;
    }

    public async Task ConnectAsync(ulong remoteNodeId, int remotePort, CancellationToken cancellationToken = default)
    {
        if (remotePort is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(remotePort));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_connected)
        {
            return;
        }

        _remoteNodeId = remoteNodeId;
        _remotePort = remotePort;
        _connectionId = GenerateConnectionId();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connectTcs = tcs;

        await SendControlFrameAsync(TcpFrameType.Syn, cancellationToken).ConfigureAwait(false);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        _connected = true;
    }

    internal async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

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

    internal async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_connected)
        {
            throw new InvalidOperationException("Client is not connected.");
        }

        if (_remoteClosed)
        {
            throw new IOException("Remote has closed the connection.");
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var remaining = buffer;
            while (!remaining.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = remaining.Slice(0, Math.Min(remaining.Length, MaxDataPerFrame));
                remaining = remaining.Slice(chunk.Length);

                await SendDataFrameAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _disposeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _node.RawFrameReceived -= OnFrameReceived;
            if (_connected && !_remoteClosed)
            {
                try
                {
                    await SendControlFrameAsync(TcpFrameType.Fin, CancellationToken.None).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                catch (OperationCanceledException)
                {
                }
            }

            _incoming.Writer.TryComplete();
            _stream?.Dispose();
        }
        finally
        {
            _disposeLock.Release();
            _disposeLock.Dispose();
            _sendLock.Dispose();
        }
    }

    private void OnFrameReceived(in RawFrame frame)
    {
        if (_disposed || frame.NetworkId != _networkId || frame.Payload.Length < HeaderLength)
        {
            return;
        }

        if (!TryParseHeader(frame.Payload.Span, out var type, out var sourcePort, out var destinationPort, out var destinationNodeId, out var connectionId))
        {
            return;
        }

        if (destinationNodeId != _localNodeId)
        {
            return;
        }

        if (_connected)
        {
            if (connectionId != _connectionId ||
                destinationPort != _localPort ||
                sourcePort != _remotePort ||
                frame.SourceNodeId != _remoteNodeId)
            {
                return;
            }

            if (type == TcpFrameType.Data)
            {
                _incoming.Writer.TryWrite(frame.Payload.Slice(HeaderLength));
                return;
            }

            if (type == TcpFrameType.Fin)
            {
                _remoteClosed = true;
                _incoming.Writer.TryComplete();
            }

            return;
        }

        if (type != TcpFrameType.SynAck ||
            destinationPort != _localPort ||
            sourcePort != _remotePort ||
            connectionId != _connectionId ||
            frame.SourceNodeId != _remoteNodeId)
        {
            return;
        }

        _connectTcs?.TrySetResult(true);
    }

    private async Task SendControlFrameAsync(TcpFrameType type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var usesPool = _node.LocalTransportEndpoint is not null;
        var frameLength = HeaderLength;
        var frame = usesPool ? ArrayPool<byte>.Shared.Rent(frameLength) : new byte[frameLength];
        try
        {
            BuildHeader(type, _localPort, _remotePort, _remoteNodeId, _connectionId, frame.AsSpan(0, frameLength));
            await _node.SendFrameAsync(_networkId, frame.AsMemory(0, frameLength), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (usesPool)
            {
                ArrayPool<byte>.Shared.Return(frame);
            }
        }
    }

    private async Task SendDataFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var usesPool = _node.LocalTransportEndpoint is not null;
        var frameLength = HeaderLength + payload.Length;
        var frame = usesPool ? ArrayPool<byte>.Shared.Rent(frameLength) : new byte[frameLength];
        try
        {
            var frameSpan = frame.AsSpan(0, frameLength);
            BuildHeader(TcpFrameType.Data, _localPort, _remotePort, _remoteNodeId, _connectionId, frameSpan);
            payload.Span.CopyTo(frameSpan.Slice(HeaderLength));
            await _node.SendFrameAsync(_networkId, frame.AsMemory(0, frameLength), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (usesPool)
            {
                ArrayPool<byte>.Shared.Return(frame);
            }
        }
    }

    private static void BuildHeader(
        TcpFrameType type,
        int sourcePort,
        int destinationPort,
        ulong destinationNodeId,
        ulong connectionId,
        Span<byte> destination)
    {
        destination[0] = TcpFrameVersion;
        destination[1] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), (ushort)sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(4, 2), (ushort)destinationPort);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(6, sizeof(ulong)), destinationNodeId);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(6 + sizeof(ulong), sizeof(ulong)), connectionId);
    }

    private static bool TryParseHeader(
        ReadOnlySpan<byte> payload,
        out TcpFrameType type,
        out int sourcePort,
        out int destinationPort,
        out ulong destinationNodeId,
        out ulong connectionId)
    {
        type = TcpFrameType.Syn;
        sourcePort = 0;
        destinationPort = 0;
        destinationNodeId = 0;
        connectionId = 0;

        if (payload.Length < HeaderLength || payload[0] != TcpFrameVersion)
        {
            return false;
        }

        type = (TcpFrameType)payload[1];
        if (type is not (TcpFrameType.Syn or TcpFrameType.SynAck or TcpFrameType.Data or TcpFrameType.Fin))
        {
            return false;
        }

        sourcePort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2, 2));
        destinationPort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2));
        destinationNodeId = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(6, sizeof(ulong)));
        connectionId = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(6 + sizeof(ulong), sizeof(ulong)));
        return true;
    }

    private static ulong GenerateConnectionId()
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(buffer);
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private sealed class OverlayTcpStream : Stream
    {
        private readonly OverlayTcpClient _client;

        public OverlayTcpStream(OverlayTcpClient client)
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
