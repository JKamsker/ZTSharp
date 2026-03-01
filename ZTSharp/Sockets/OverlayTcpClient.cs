using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Sockets;

namespace ZTSharp.Sockets;

/// <summary>
/// Managed stream client built on top of the node transport (not OS TCP).
/// </summary>
public sealed class OverlayTcpClient : IAsyncDisposable
{
    private const int HeaderLength = OverlayTcpFrameCodec.HeaderLength;
    private const int MaxDataPerFrame = 1024;

    private readonly OverlayTcpIncomingBuffer _incoming;
    [SuppressMessage(
        "Reliability",
        "CA2213:Disposable fields should be disposed",
        Justification = "DisposeAsync must be idempotent; disposing this lock can throw on subsequent/overlapping DisposeAsync calls.")]
    private readonly SemaphoreSlim _disposeLock = new(1, 1);

    [SuppressMessage(
        "Reliability",
        "CA2213:Disposable fields should be disposed",
        Justification = "DisposeAsync must be idempotent; disposing this lock can throw on subsequent/overlapping DisposeAsync calls.")]
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Node _node;
    private readonly ulong _networkId;
    private readonly int _localPort;

    private ulong _remoteNodeId;
    private int _remotePort;
    private ulong _connectionId;

    private TaskCompletionSource<bool>? _connectTcs;
    private bool _connected;
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
        _incoming = new OverlayTcpIncomingBuffer();

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

    public bool Connected => _connected && !_disposed && !_incoming.RemoteClosed;

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

        await SendControlFrameAsync(OverlayTcpFrameCodec.FrameType.Syn, cancellationToken).ConfigureAwait(false);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        _connected = true;
    }

    internal async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _incoming.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_connected)
        {
            throw new InvalidOperationException("Client is not connected.");
        }

        if (_incoming.RemoteClosed)
        {
            throw new IOException("Remote has closed the connection.");
        }

        if (_incoming.RemoteFinReceived)
        {
            throw new IOException("Remote has closed the connection.");
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_incoming.RemoteClosed || _incoming.RemoteFinReceived)
            {
                throw new IOException("Remote has closed the connection.");
            }

            var remaining = buffer;
            while (!remaining.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
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
            if (_connected && !_incoming.RemoteClosed)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await SendControlFrameAsync(OverlayTcpFrameCodec.FrameType.Fin, cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or OperationCanceledException or SocketException)
                {
                }
            }

            _incoming.Complete();
            _stream?.Dispose();
        }
        finally
        {
            _disposeLock.Release();
        }
    }

    private void OnFrameReceived(in RawFrame frame)
    {
        if (_disposed || frame.NetworkId != _networkId || frame.Payload.Length < HeaderLength)
        {
            return;
        }

        if (!OverlayTcpFrameCodec.TryParseHeader(frame.Payload.Span, out var type, out var sourcePort, out var destinationPort, out var destinationNodeId, out var connectionId))
        {
            return;
        }

        var localNodeId = _node.NodeId.Value;
        if (localNodeId == 0 || destinationNodeId != localNodeId)
        {
            return;
        }

        if (connectionId != _connectionId ||
            destinationPort != _localPort ||
            sourcePort != _remotePort ||
            frame.SourceNodeId != _remoteNodeId)
        {
            return;
        }

        if (type == OverlayTcpFrameCodec.FrameType.Data)
        {
            _incoming.TryWrite(frame.Payload.Slice(HeaderLength));
            return;
        }

        if (type == OverlayTcpFrameCodec.FrameType.Fin)
        {
            _incoming.MarkRemoteFinReceived();
            return;
        }

        if (_connected)
        {
            return;
        }

        if (type == OverlayTcpFrameCodec.FrameType.SynAck)
        {
            _connectTcs?.TrySetResult(true);
        }
    }

    private async Task SendControlFrameAsync(OverlayTcpFrameCodec.FrameType type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var usesPool = _node.LocalTransportEndpoint is not null;
        var frameLength = HeaderLength;
        var frame = usesPool ? ArrayPool<byte>.Shared.Rent(frameLength) : new byte[frameLength];
        try
        {
            OverlayTcpFrameCodec.BuildHeader(type, _localPort, _remotePort, _remoteNodeId, _connectionId, frame.AsSpan(0, frameLength));
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
            OverlayTcpFrameCodec.BuildHeader(OverlayTcpFrameCodec.FrameType.Data, _localPort, _remotePort, _remoteNodeId, _connectionId, frameSpan);
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

    private static ulong GenerateConnectionId()
        => OverlayTcpFrameCodec.GenerateConnectionId();

}
