using System.Buffers.Binary;
using System.Threading.Channels;

namespace JKamsker.LibZt.Sockets;

/// <summary>
/// Managed UDP-like client backed by the in-memory node transport.
/// </summary>
public sealed class ZtUdpClient : IAsyncDisposable
{
    private const byte UdpFrameVersion = 1;
    private const byte UdpFrameType = 1;
    private readonly Channel<ZtUdpDatagram> _incoming;
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private readonly ulong _localNodeId;
    private readonly ulong _networkId;
    private readonly int _localPort;
    private readonly ZtNode _node;
    private readonly bool _ownsConnection;

    private ulong _connectedNode;
    private int _connectedPort;
    private bool _disposed;

    public ZtUdpClient(ZtNode node, ulong networkId, int localPort, bool ownsConnection = true)
    {
        if (localPort is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(localPort));
        }
        _node = node;
        _networkId = networkId;
        _localPort = localPort;
        _localNodeId = node.NodeId.Value;
        _incoming = Channel.CreateUnbounded<ZtUdpDatagram>();
        _ownsConnection = ownsConnection;

        _node.FrameReceived += OnFrameReceived;
    }

    public bool IsDisposed => _disposed;

    public ulong NetworkId => _networkId;

    public int LocalPort => _localPort;

    public Task ConnectAsync(ulong remoteNodeId, int remotePort, CancellationToken cancellationToken = default)
    {
        if (remotePort is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(remotePort));
        }

        cancellationToken.ThrowIfCancellationRequested();
        _connectedNode = remoteNodeId;
        _connectedPort = remotePort;
        return Task.CompletedTask;
    }

    public async Task<int> SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default)
    {
        if (_connectedNode == 0 || _connectedPort == 0)
        {
            throw new InvalidOperationException("No remote endpoint configured. Use SendToAsync or ConnectAsync first.");
        }

        await SendToAsync(datagram, _connectedNode, _connectedPort, cancellationToken).ConfigureAwait(false);
        return datagram.Length;
    }

    public Task SendToAsync(ReadOnlyMemory<byte> datagram, ulong remoteNodeId, int remotePort, CancellationToken cancellationToken = default)
    {
        if (remotePort is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(remotePort));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var frame = BuildFrame(_localPort, remotePort, datagram.Span);
        return _node.SendFrameAsync(_networkId, frame, cancellationToken);
    }

    public async Task<ZtUdpDatagram> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        return await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Close()
    {
        _ = DisposeAsync().AsTask();
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
            if (_ownsConnection)
            {
                _node.FrameReceived -= OnFrameReceived;
            }

            _incoming.Writer.TryComplete();
        }
        finally
        {
            _disposeLock.Release();
        }
    }

    private void OnFrameReceived(object? sender, ZtNetworkFrame frame)
    {
        if (_disposed || frame.NetworkId != _networkId || frame.Payload.Length < 6)
        {
            return;
        }

        if (!_tryParseUdpFrame(frame.Payload.AsSpan(), out var sourcePort, out var destinationPort, out var payloadOffset, out var payloadLength))
        {
            return;
        }

        if (destinationPort != _localPort || frame.SourceNodeId == _localNodeId)
        {
            return;
        }

        // Drop frames silently if the consumer cannot keep up.
        _incoming.Writer.TryWrite(new ZtUdpDatagram(
            frame.SourceNodeId,
            sourcePort,
            frame.Payload.AsMemory(payloadOffset, payloadLength),
            DateTimeOffset.UtcNow));
    }

    private static bool _tryParseUdpFrame(
        ReadOnlySpan<byte> payload,
        out int sourcePort,
        out int destinationPort,
        out int dataOffset,
        out int dataLength)
    {
        sourcePort = 0;
        destinationPort = 0;
        dataOffset = 0;
        dataLength = 0;

        if (payload.Length < 6 || payload[0] != UdpFrameVersion || payload[1] != UdpFrameType)
        {
            return false;
        }

        sourcePort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2, 2));
        destinationPort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2));
        dataOffset = 6;
        dataLength = payload.Length - 6;
        return true;
    }

    private static byte[] BuildFrame(int sourcePort, int destinationPort, ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[6 + payload.Length];
        buffer[0] = UdpFrameVersion;
        buffer[1] = UdpFrameType;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), (ushort)sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4, 2), (ushort)destinationPort);
        payload.CopyTo(buffer, 6);
        return buffer;
    }
}
