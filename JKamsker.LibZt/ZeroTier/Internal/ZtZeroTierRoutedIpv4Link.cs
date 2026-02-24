using System.Threading.Channels;
using JKamsker.LibZt.ZeroTier.Net;
using JKamsker.LibZt.ZeroTier.Protocol;

namespace JKamsker.LibZt.ZeroTier.Internal;

internal sealed class ZtZeroTierRoutedIpv4Link : IZtUserSpaceIpv4Link
{
    private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    private readonly ZtZeroTierDataplaneRuntime _runtime;
    private readonly ZtZeroTierTcpRouteKey _routeKey;
    private readonly ZtNodeId _peerNodeId;
    private bool _disposed;

    public ZtZeroTierRoutedIpv4Link(ZtZeroTierDataplaneRuntime runtime, ZtZeroTierTcpRouteKey routeKey, ZtNodeId peerNodeId)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        _runtime = runtime;
        _routeKey = routeKey;
        _peerNodeId = peerNodeId;
    }

    public ChannelWriter<ReadOnlyMemory<byte>> IncomingWriter => _incoming.Writer;

    public ValueTask SendAsync(ReadOnlyMemory<byte> ipv4Packet, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _runtime.SendIpv4Async(_peerNodeId, ipv4Packet, cancellationToken);
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

