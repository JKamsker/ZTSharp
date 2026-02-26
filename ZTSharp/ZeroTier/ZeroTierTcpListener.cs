using System.Net;
using System.Threading.Channels;
using ZTSharp.Internal;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier;

public sealed class ZeroTierTcpListener : IAsyncDisposable
{
    private const int DefaultAcceptQueueCapacity = 64;

    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ActiveTaskSet _connectionTasks = new();
    private readonly Channel<Stream> _acceptQueue;
    private readonly ZeroTierDataplaneRuntime _runtime;
    private readonly IPAddress _localAddress;
    private readonly IPAddress _localAddressMatch;
    private readonly ushort _localPort;
    private readonly int _acceptQueueCapacity;
    private int _pendingAcceptCount;
    private long _droppedAcceptCount;
    private int _disposeState;
    private readonly bool _isWildcardBind;

    internal ZeroTierTcpListener(ZeroTierDataplaneRuntime runtime, IPAddress localAddress, ushort localPort, int acceptQueueCapacity = DefaultAcceptQueueCapacity)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(localAddress);
        if (localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork &&
            localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            throw new NotSupportedException("Only IPv4 and IPv6 are supported.");
        }

        if (acceptQueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(acceptQueueCapacity), acceptQueueCapacity, "Accept queue capacity must be greater than zero.");
        }

        _runtime = runtime;
        _localAddress = localAddress;
        _localAddressMatch = ZeroTierIpAddressCanonicalization.CanonicalizeForManagedIpComparison(localAddress);
        _localPort = localPort;
        _acceptQueueCapacity = acceptQueueCapacity;
        _isWildcardBind = localAddress.Equals(IPAddress.Any) || localAddress.Equals(IPAddress.IPv6Any);
        _acceptQueue = Channel.CreateBounded<Stream>(new BoundedChannelOptions(_acceptQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        if (!_runtime.TryRegisterTcpListener(localAddress, localPort, OnSynAsync))
        {
            throw new InvalidOperationException($"A TCP listener is already registered for {localAddress}:{localPort} (or a wildcard listener already binds this port).");
        }
    }

    public IPEndPoint LocalEndpoint => new(_localAddress, _localPort);

    public int AcceptQueueCapacity => _acceptQueueCapacity;

    public int PendingAcceptCount => Volatile.Read(ref _pendingAcceptCount);

    public long DroppedAcceptCount => Interlocked.Read(ref _droppedAcceptCount);

    public ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return ReadFromQueueAsync(cancellationToken);

        async ValueTask<Stream> ReadFromQueueAsync(CancellationToken token)
        {
            try
            {
                var stream = await _acceptQueue.Reader.ReadAsync(token).ConfigureAwait(false);
                Interlocked.Decrement(ref _pendingAcceptCount);
                return stream;
            }
            catch (ChannelClosedException)
            {
                throw new ObjectDisposedException(typeof(ZeroTierTcpListener).FullName);
            }
        }
    }

    public async ValueTask<Stream> AcceptAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return await ZeroTierTimeouts
            .RunWithTimeoutAsync(timeout, operation: "TCP accept", AcceptAsync, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await _disposeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _runtime.UnregisterTcpListener(_localAddress, _localPort);
            await _shutdown.CancelAsync().ConfigureAwait(false);
            _acceptQueue.Writer.TryComplete();

            while (_acceptQueue.Reader.TryRead(out var accepted))
            {
                Interlocked.Decrement(ref _pendingAcceptCount);
                await accepted.DisposeAsync().ConfigureAwait(false);
            }

            using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _connectionTasks.WaitAsync(drainCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (drainCts.IsCancellationRequested)
            {
            }
        }
        finally
        {
            _disposeLock.Release();
            _disposeLock.Dispose();
            _shutdown.Dispose();
        }
    }

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of accepted connections transfers to the accept queue consumer (via the returned Stream).")]
    private Task OnSynAsync(NodeId peerNodeId, ReadOnlyMemory<byte> ipPacket, CancellationToken cancellationToken)
    {
        if (IsDisposed)
        {
            return Task.CompletedTask;
        }

        IPAddress src;
        IPAddress dst;
        ReadOnlySpan<byte> ipPayload;
        if (_localAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            if (!Ipv4Codec.TryParse(ipPacket.Span, out src, out dst, out var protocol, out ipPayload))
            {
                return Task.CompletedTask;
            }

            if ((!_isWildcardBind && !dst.Equals(_localAddressMatch)) || protocol != TcpCodec.ProtocolNumber)
            {
                return Task.CompletedTask;
            }
        }
        else
        {
            if (!Ipv6Codec.TryParse(ipPacket.Span, out src, out dst, out var nextHeader, out _, out ipPayload))
            {
                return Task.CompletedTask;
            }

            if ((!_isWildcardBind && !ZeroTierIpAddressCanonicalization.EqualsForManagedIpComparison(dst, _localAddressMatch)) || nextHeader != TcpCodec.ProtocolNumber)
            {
                return Task.CompletedTask;
            }
        }

        if (!TcpCodec.TryParse(ipPayload, out var srcPort, out var dstPort, out _, out _, out var flags, out _, out _))
        {
            return Task.CompletedTask;
        }

        if (dstPort != _localPort || (flags & TcpCodec.Flags.Syn) == 0 || (flags & TcpCodec.Flags.Ack) != 0)
        {
            return Task.CompletedTask;
        }

        var remoteEndpoint = new IPEndPoint(src, srcPort);
        var localEndpoint = new IPEndPoint(dst, dstPort);

        var link = _runtime.RegisterTcpRoute(peerNodeId, localEndpoint, remoteEndpoint);
        var connection = new UserSpaceTcpServerConnection(
            link,
            localAddress: dst,
            localPort: dstPort,
            remoteAddress: src,
            remotePort: srcPort);

        link.IncomingWriter.TryWrite(ipPacket);

        _connectionTasks.Track(HandleAcceptedConnectionAsync(connection, cancellationToken));
        return Task.CompletedTask;
    }

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of accepted connections transfers to the accept queue consumer (via the returned Stream).")]
    private async Task HandleAcceptedConnectionAsync(UserSpaceTcpServerConnection connection, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        var token = linkedCts.Token;

        Stream? stream = null;
        var handedOff = false;
        var disposed = false;

        try
        {
            await connection.AcceptAsync(token).ConfigureAwait(false);
            stream = connection.GetStream();

            if (!_acceptQueue.Writer.TryWrite(stream))
            {
                Interlocked.Increment(ref _droppedAcceptCount);
                if (ZeroTierTrace.Enabled)
                {
                    ZeroTierTrace.WriteLine($"[zerotier] Drop accept: backlog full ({_acceptQueueCapacity}).");
                }

                await stream.DisposeAsync().ConfigureAwait(false);
                disposed = true;
                stream = null;
                return;
            }

            Interlocked.Increment(ref _pendingAcceptCount);
            handedOff = true;
            stream = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            if (!handedOff && !disposed)
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposeState) != 0;
}
