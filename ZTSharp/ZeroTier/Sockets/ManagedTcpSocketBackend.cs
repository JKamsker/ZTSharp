using System.Net;
using System.Net.Sockets;
using ZTSharp.ZeroTier;

namespace ZTSharp.ZeroTier.Sockets;

internal sealed class ManagedTcpSocketBackend : ManagedSocketBackend
{
    private IPEndPoint? _localEndPoint;
    private IPEndPoint? _remoteEndPoint;
    private Stream? _stream;
    private ZeroTierTcpListener? _listener;
    private int _connectInProgress;

    public ManagedTcpSocketBackend(ZeroTierSocket zeroTier, AddressFamily addressFamily)
        : base(zeroTier)
    {
        AddressFamily = addressFamily;
    }

    private ManagedTcpSocketBackend(ZeroTierSocket zeroTier, IPEndPoint localEndPoint, IPEndPoint? remoteEndPoint, Stream connectedStream)
        : base(zeroTier)
    {
        AddressFamily = localEndPoint.AddressFamily;
        _localEndPoint = localEndPoint;
        _remoteEndPoint = remoteEndPoint;
        _stream = connectedStream;
    }

    public override AddressFamily AddressFamily { get; }

    public override SocketType SocketType => SocketType.Stream;

    public override ProtocolType ProtocolType => ProtocolType.Tcp;

    public override EndPoint? LocalEndPoint => _localEndPoint;

    public override EndPoint? RemoteEndPoint => _remoteEndPoint;

    public override bool Connected => _stream is not null && !Disposed;

    public override async ValueTask BindAsync(EndPoint localEndPoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Disposed, this);

        if (localEndPoint is not IPEndPoint ip)
        {
            throw new NotSupportedException("Only IPEndPoint is supported.");
        }

        if (ip.AddressFamily != AddressFamily)
        {
            throw new NotSupportedException("Local address family must match the socket address family.");
        }

        await InitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Disposed, this);

            if (_stream is not null || _listener is not null)
            {
                throw new InvalidOperationException("Socket is already initialized.");
            }

            _localEndPoint = await ManagedSocketEndpointNormalizer
                .NormalizeLocalEndpointAsync(Zt, AddressFamily, ip, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            InitLock.Release();
        }
    }

    public override async ValueTask ListenAsync(int backlog, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Disposed, this);

        await InitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Disposed, this);

            if (_listener is not null)
            {
                throw new InvalidOperationException("Socket is already listening.");
            }

            if (_stream is not null)
            {
                throw new InvalidOperationException("Socket is already connected.");
            }

            if (_localEndPoint is null)
            {
                throw new InvalidOperationException("Bind must be called before Listen.");
            }

            if (_localEndPoint.Port == 0)
            {
                throw new NotSupportedException("Listening on port 0 is not supported. Bind to a concrete port first.");
            }

            var acceptQueueCapacity = backlog <= 0 ? 128 : backlog;
            _listener = await Zt
                .ListenTcpAsync(_localEndPoint.Address, _localEndPoint.Port, acceptQueueCapacity, cancellationToken)
                .ConfigureAwait(false);

            _localEndPoint = _listener.LocalEndpoint;
        }
        finally
        {
            InitLock.Release();
        }
    }

    public override async ValueTask<ManagedSocketBackend> AcceptAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Disposed, this);

        var listener = _listener ?? throw new InvalidOperationException("Socket is not listening.");
        var (stream, local, remote) = await listener.AcceptWithEndpointsAsync(cancellationToken).ConfigureAwait(false);
        return new ManagedTcpSocketBackend(Zt, local, remote, connectedStream: stream);
    }

    public override async ValueTask ConnectAsync(EndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Disposed, this);

        if (remoteEndPoint is not IPEndPoint remoteIp)
        {
            throw new NotSupportedException("Only IPEndPoint is supported.");
        }

        if (remoteIp.AddressFamily != AddressFamily)
        {
            throw new NotSupportedException("Remote address family must match the socket address family.");
        }

        if (Interlocked.CompareExchange(ref _connectInProgress, 1, 0) != 0)
        {
            throw new InvalidOperationException("Socket connect is already in progress.");
        }

        IPEndPoint? local;
        try
        {
            await InitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Disposed, this);

                if (_stream is not null)
                {
                    throw new InvalidOperationException("Socket is already connected.");
                }

                if (_listener is not null)
                {
                    throw new InvalidOperationException("Socket is listening. Create a new socket for outgoing connections.");
                }

                local = _localEndPoint;
            }
            finally
            {
                InitLock.Release();
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ShutdownToken);
            var token = linkedCts.Token;

            if (local is not null)
            {
                local = await ManagedSocketEndpointNormalizer
                    .NormalizeLocalEndpointAsync(Zt, AddressFamily, local, token)
                    .ConfigureAwait(false);
            }

            (Stream stream, IPEndPoint localEndpoint) = local is null
                ? await Zt.ConnectTcpWithLocalEndpointAsync(remoteIp, token).ConfigureAwait(false)
                : await Zt.ConnectTcpWithLocalEndpointAsync(local, remoteIp, token).ConfigureAwait(false);

            await InitLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Disposed, this);
                _stream = stream;
                _remoteEndPoint = remoteIp;
                _localEndPoint = localEndpoint;
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                InitLock.Release();
            }
        }
        catch (OperationCanceledException ex)
        {
            if (ShutdownToken.IsCancellationRequested || Disposed)
            {
                throw new ObjectDisposedException(GetType().FullName, ex);
            }

            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _connectInProgress, 0);
        }
    }

    public override async ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Disposed, this);

        var stream = _stream ?? throw new InvalidOperationException("Socket is not connected.");
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.Length;
    }

    public override async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Disposed, this);

        var stream = _stream ?? throw new InvalidOperationException("Socket is not connected.");
        return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Stream GetStream()
        => _stream ?? throw new InvalidOperationException("Socket is not connected.");

    protected override async ValueTask DisposeResourcesAsync()
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
            _listener = null;
        }
    }
}
