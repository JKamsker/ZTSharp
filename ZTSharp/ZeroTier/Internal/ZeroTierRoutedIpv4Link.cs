using System.IO;
using System.Threading.Channels;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal sealed class ZeroTierRoutedIpv4Link : IZeroTierRoutedIpLink
{
    private const int IncomingCapacity = 256;

    private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(capacity: IncomingCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = true
    });
    private readonly ZeroTierDataplaneRuntime _runtime;
    private readonly ZeroTierTcpRouteKey _routeKey;
    private readonly NodeId _peerNodeId;
    private IOException? _terminalException;
    private long _incomingDropCount;
    private bool _disposed;

    public ZeroTierRoutedIpv4Link(ZeroTierDataplaneRuntime runtime, ZeroTierTcpRouteKey routeKey, NodeId peerNodeId)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        _runtime = runtime;
        _routeKey = routeKey;
        _peerNodeId = peerNodeId;
    }

    public ChannelWriter<ReadOnlyMemory<byte>> IncomingWriter => _incoming.Writer;

    public bool TryEnqueueIncoming(ReadOnlyMemory<byte> ipv4Packet)
    {
        if (_disposed)
        {
            return false;
        }

        if (Volatile.Read(ref _terminalException) is not null)
        {
            return false;
        }

        if (_incoming.Writer.TryWrite(ipv4Packet))
        {
            return true;
        }

        Interlocked.Increment(ref _incomingDropCount);
        var ex = new IOException($"TCP route backlog overflow (capacity {IncomingCapacity}).");
        if (Interlocked.CompareExchange(ref _terminalException, ex, null) is null)
        {
            _runtime.UnregisterRoute(_routeKey);
            _incoming.Writer.TryComplete(ex);
        }

        return false;
    }

    public long IncomingDropCount => Interlocked.Read(ref _incomingDropCount);

    public ValueTask SendAsync(ReadOnlyMemory<byte> ipPacket, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var terminal = Volatile.Read(ref _terminalException);
        if (terminal is not null)
        {
            throw terminal;
        }

        return _runtime.SendIpv4Async(_peerNodeId, ipPacket, cancellationToken);
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var terminal = Volatile.Read(ref _terminalException);
        if (terminal is not null)
        {
            throw terminal;
        }

        try
        {
            return await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            throw new IOException("TCP route was closed.", ex);
        }
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
