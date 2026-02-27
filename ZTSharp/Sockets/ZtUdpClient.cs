using System.Buffers.Binary;
using System.Buffers;
using System.Threading.Channels;

namespace ZTSharp.Sockets;

/// <summary>
/// Managed UDP-like client backed by the node transport.
/// </summary>
public sealed class ZtUdpClient : IAsyncDisposable
{
    private const byte UdpFrameVersion1 = 1;
    private const byte UdpFrameVersion2 = 2;
    private const byte UdpFrameType = 1;
    private const int UdpFrameHeaderV1Length = 6;
    private const int UdpFrameHeaderV2Length = 14;

    private readonly Channel<UdpDatagram> _incoming;
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private readonly ulong _networkId;
    private readonly int _localPort;
    private readonly Node _node;

    private ulong _connectedNode;
    private int _connectedPort;
    private bool _disposed;

    public ZtUdpClient(Node node, ulong networkId, int localPort, bool ownsConnection = true)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (localPort is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(localPort));
        }
        _node = node;
        _networkId = networkId;
        _localPort = localPort;
        _incoming = Channel.CreateBounded<UdpDatagram>(new BoundedChannelOptions(capacity: 1024)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleWriter = false,
            SingleReader = true
        });
        _ = ownsConnection;

        _node.RawFrameReceived += OnFrameReceived;
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

    public async Task<int> SendToAsync(
        ReadOnlyMemory<byte> datagram,
        ulong remoteNodeId,
        int remotePort,
        CancellationToken cancellationToken = default)
    {
        if (remotePort is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(remotePort));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var frameLength = UdpFrameHeaderV2Length + datagram.Length;
        var usesPool = _node.LocalTransportEndpoint is not null;
        var frame = usesPool
            ? ArrayPool<byte>.Shared.Rent(frameLength)
            : new byte[frameLength];
        try
        {
            BuildFrameV2(_localPort, remotePort, remoteNodeId, datagram.Span, frame.AsSpan(0, frameLength));
            await _node.SendFrameAsync(_networkId, frame.AsMemory(0, frameLength), cancellationToken).ConfigureAwait(false);
            return datagram.Length;
        }
        finally
        {
            if (usesPool)
            {
                ArrayPool<byte>.Shared.Return(frame);
            }
        }
    }

    public ValueTask<UdpDatagram> ReceiveAsync(CancellationToken cancellationToken = default)
        => _incoming.Reader.ReadAsync(cancellationToken);

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
            _node.RawFrameReceived -= OnFrameReceived;

            _incoming.Writer.TryComplete();
        }
        finally
        {
            _disposeLock.Release();
            _disposeLock.Dispose();
        }
    }

    private void OnFrameReceived(in RawFrame frame)
    {
        if (_disposed || frame.NetworkId != _networkId || frame.Payload.Length < UdpFrameHeaderV1Length)
        {
            return;
        }

        if (!TryParseUdpFrame(frame.Payload.Span, out var version, out var sourcePort, out var destinationPort, out var destinationNodeId, out var payloadOffset, out var payloadLength))
        {
            return;
        }

        if (destinationPort != _localPort)
        {
            return;
        }

        var localNodeId = _node.NodeId.Value;
        if (frame.SourceNodeId == localNodeId)
        {
            return;
        }

        if (version == UdpFrameVersion2 && (localNodeId == 0 || destinationNodeId != localNodeId))
        {
            return;
        }

        if (_connectedNode != 0 && _connectedPort != 0 &&
            (frame.SourceNodeId != _connectedNode || sourcePort != _connectedPort))
        {
            return;
        }

        // Drop frames silently if the consumer cannot keep up.
        _incoming.Writer.TryWrite(new UdpDatagram(
            frame.SourceNodeId,
            sourcePort,
            frame.Payload.Slice(payloadOffset, payloadLength),
            DateTimeOffset.UtcNow));
    }

    private static bool TryParseUdpFrame(
        ReadOnlySpan<byte> payload,
        out byte version,
        out int sourcePort,
        out int destinationPort,
        out ulong destinationNodeId,
        out int dataOffset,
        out int dataLength)
    {
        version = 0;
        sourcePort = 0;
        destinationPort = 0;
        destinationNodeId = 0;
        dataOffset = 0;
        dataLength = 0;

        if (payload.Length < UdpFrameHeaderV1Length || payload[1] != UdpFrameType)
        {
            return false;
        }

        version = payload[0];
        if (version == UdpFrameVersion1)
        {
            sourcePort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2, 2));
            destinationPort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2));
            dataOffset = UdpFrameHeaderV1Length;
            dataLength = payload.Length - UdpFrameHeaderV1Length;
            return true;
        }

        if (version == UdpFrameVersion2)
        {
            if (payload.Length < UdpFrameHeaderV2Length)
            {
                return false;
            }

            sourcePort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2, 2));
            destinationPort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2));
            destinationNodeId = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(6, 8));
            dataOffset = UdpFrameHeaderV2Length;
            dataLength = payload.Length - UdpFrameHeaderV2Length;
            return true;
        }

        return false;
    }

    private static void BuildFrameV2(
        int sourcePort,
        int destinationPort,
        ulong destinationNodeId,
        ReadOnlySpan<byte> payload,
        Span<byte> destination)
    {
        destination[0] = UdpFrameVersion2;
        destination[1] = UdpFrameType;
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), (ushort)sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(4, 2), (ushort)destinationPort);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(6, 8), destinationNodeId);
        payload.CopyTo(destination.Slice(UdpFrameHeaderV2Length));
    }
}
