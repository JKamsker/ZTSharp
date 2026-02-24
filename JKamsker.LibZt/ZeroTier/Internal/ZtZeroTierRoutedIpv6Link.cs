using System.Threading.Channels;
using JKamsker.LibZt.ZeroTier.Protocol;

namespace JKamsker.LibZt.ZeroTier.Internal;

internal sealed class ZtZeroTierRoutedIpv6Link : IZtZeroTierRoutedIpLink
{
    private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    private readonly ZtZeroTierDataplaneRuntime _runtime;
    private readonly ZtZeroTierTcpRouteKeyV6 _routeKey;
    private readonly ZtNodeId _peerNodeId;
    private bool _disposed;

    public ZtZeroTierRoutedIpv6Link(ZtZeroTierDataplaneRuntime runtime, ZtZeroTierTcpRouteKeyV6 routeKey, ZtNodeId peerNodeId)
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
        return _runtime.SendEthernetFrameAsync(_peerNodeId, ZtZeroTierFrameCodec.EtherTypeIpv6, ipPacket, cancellationToken);
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
