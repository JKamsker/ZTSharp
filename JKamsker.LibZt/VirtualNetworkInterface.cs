using System.Buffers;
using System.Buffers.Binary;
using System.Threading.Channels;

namespace JKamsker.LibZt;

/// <summary>
/// Minimal virtual network interface abstraction for future TUN/TAP parity.
/// </summary>
public sealed class VirtualNetworkInterface : IAsyncDisposable
{
    private const byte FrameVersion = 1;
    private const byte FrameType = 0x10;
    private const int HeaderLength = 1 + 1 + sizeof(ulong);

    private readonly Channel<IpPacket> _incoming;
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private readonly Node _node;
    private readonly ulong _networkId;
    private readonly ulong _localNodeId;
    private bool _disposed;

    public VirtualNetworkInterface(Node node, ulong networkId)
    {
        ArgumentNullException.ThrowIfNull(node);
        _node = node;
        _networkId = networkId;
        _localNodeId = node.NodeId.Value;
        _incoming = Channel.CreateUnbounded<IpPacket>();

        _node.RawFrameReceived += OnFrameReceived;
    }

    public Task<int> SendPacketAsync(
        ulong destinationNodeId,
        ReadOnlyMemory<byte> ipPacket,
        CancellationToken cancellationToken = default)
        => SendPacketInternalAsync(destinationNodeId, ipPacket, cancellationToken);

    public ValueTask<IpPacket> ReceivePacketAsync(CancellationToken cancellationToken = default)
        => _incoming.Reader.ReadAsync(cancellationToken);

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

    private async Task<int> SendPacketInternalAsync(
        ulong destinationNodeId,
        ReadOnlyMemory<byte> ipPacket,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frameLength = HeaderLength + ipPacket.Length;
        var usesPool = _node.LocalTransportEndpoint is not null;
        var frame = usesPool ? ArrayPool<byte>.Shared.Rent(frameLength) : new byte[frameLength];
        try
        {
            var span = frame.AsSpan(0, frameLength);
            span[0] = FrameVersion;
            span[1] = FrameType;
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(2, sizeof(ulong)), destinationNodeId);
            ipPacket.Span.CopyTo(span.Slice(HeaderLength));
            await _node.SendFrameAsync(_networkId, frame.AsMemory(0, frameLength), cancellationToken).ConfigureAwait(false);
            return ipPacket.Length;
        }
        finally
        {
            if (usesPool)
            {
                ArrayPool<byte>.Shared.Return(frame);
            }
        }
    }

    private void OnFrameReceived(in RawFrame frame)
    {
        if (_disposed || frame.NetworkId != _networkId || frame.Payload.Length < HeaderLength)
        {
            return;
        }

        var payload = frame.Payload.Span;
        if (payload[0] != FrameVersion || payload[1] != FrameType)
        {
            return;
        }

        var destinationNodeId = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(2, sizeof(ulong)));
        if (destinationNodeId != _localNodeId || frame.SourceNodeId == _localNodeId)
        {
            return;
        }

        _incoming.Writer.TryWrite(new IpPacket(
            frame.SourceNodeId,
            frame.Payload.Slice(HeaderLength),
            DateTimeOffset.UtcNow));
    }
}

