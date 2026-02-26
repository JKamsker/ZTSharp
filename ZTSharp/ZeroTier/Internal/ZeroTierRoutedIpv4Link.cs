using System.Threading.Channels;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierRoutedIpv4Link : IZeroTierRoutedIpLink
{
    private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(capacity: 256)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });
    private readonly ZeroTierDataplaneRuntime _runtime;
    private readonly ZeroTierTcpRouteKey _routeKey;
    private readonly NodeId _peerNodeId;
    private bool _disposed;

    public ZeroTierRoutedIpv4Link(ZeroTierDataplaneRuntime runtime, ZeroTierTcpRouteKey routeKey, NodeId peerNodeId)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        _runtime = runtime;
        _routeKey = routeKey;
        _peerNodeId = peerNodeId;
    }

    public ChannelWriter<ReadOnlyMemory<byte>> IncomingWriter => _incoming.Writer;

    public ValueTask SendAsync(ReadOnlyMemory<byte> ipPacket, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _runtime.SendIpv4Async(_peerNodeId, ipPacket, cancellationToken);
    }

    public ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _incoming.Reader.ReadAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _runtime.UnregisterRoute(_routeKey);
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
