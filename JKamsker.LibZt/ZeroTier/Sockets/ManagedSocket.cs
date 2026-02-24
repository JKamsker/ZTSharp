using System.Net;
using System.Net.Sockets;
using JKamsker.LibZt.ZeroTier;

namespace JKamsker.LibZt.ZeroTier.Sockets;

[global::System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Usage",
    "CA2213:Disposable fields should be disposed",
    Justification = "ManagedSocket does not own the ZeroTierSocket instance.")]
public sealed class ManagedSocket : IAsyncDisposable, IDisposable
{
    private readonly ZeroTierSocket _zt;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _disposed;

    private IPEndPoint? _localEndPoint;
    private IPEndPoint? _remoteEndPoint;

    private Stream? _stream;
    private ZeroTierTcpListener? _listener;
    private ZeroTierUdpSocket? _udp;

    public ManagedSocket(ZeroTierSocket zeroTier, AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType)
    {
        ArgumentNullException.ThrowIfNull(zeroTier);

        if (addressFamily != AddressFamily.InterNetwork && addressFamily != AddressFamily.InterNetworkV6)
        {
            throw new NotSupportedException($"Unsupported address family: {addressFamily}.");
        }

        if (socketType == SocketType.Stream && protocolType != ProtocolType.Tcp)
        {
            throw new NotSupportedException("Stream sockets only support TCP.");
        }

        if (socketType == SocketType.Dgram && protocolType != ProtocolType.Udp)
        {
            throw new NotSupportedException("Datagram sockets only support UDP.");
        }

        if (socketType != SocketType.Stream && socketType != SocketType.Dgram)
        {
            throw new NotSupportedException($"Unsupported socket type: {socketType}.");
        }

        _zt = zeroTier;
        AddressFamily = addressFamily;
        SocketType = socketType;
        ProtocolType = protocolType;
    }

    private ManagedSocket(ZeroTierSocket zeroTier, IPEndPoint localEndPoint, IPEndPoint? remoteEndPoint, Stream connectedStream)
    {
        _zt = zeroTier;
        AddressFamily = localEndPoint.AddressFamily;
        SocketType = SocketType.Stream;
        ProtocolType = ProtocolType.Tcp;
        _localEndPoint = localEndPoint;
        _remoteEndPoint = remoteEndPoint;
        _stream = connectedStream;
    }

    public AddressFamily AddressFamily { get; }

    public SocketType SocketType { get; }

    public ProtocolType ProtocolType { get; }

    public EndPoint? LocalEndPoint => _localEndPoint;

    public EndPoint? RemoteEndPoint => _remoteEndPoint;

    public bool Connected => _stream is not null && !_disposed;

    public void Bind(EndPoint localEndPoint)
        => BindAsync(localEndPoint).AsTask().GetAwaiter().GetResult();

    public async ValueTask BindAsync(EndPoint localEndPoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (localEndPoint is not IPEndPoint ip)
        {
            throw new NotSupportedException("Only IPEndPoint is supported.");
        }

        if (ip.AddressFamily != AddressFamily)
        {
            throw new NotSupportedException("Local address family must match the socket address family.");
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_stream is not null || _listener is not null || _udp is not null)
            {
                throw new InvalidOperationException("Socket is already initialized.");
            }

            var normalized = await NormalizeLocalEndpointAsync(ip, cancellationToken).ConfigureAwait(false);

            if (SocketType == SocketType.Dgram)
            {
                var udp = await _zt
                    .BindUdpAsync(normalized.Address, normalized.Port, cancellationToken)
                    .ConfigureAwait(false);

                _udp = udp;
                _localEndPoint = udp.LocalEndpoint;
                return;
            }

            _localEndPoint = normalized;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public void Listen(int backlog = 128)
        => ListenAsync(backlog).AsTask().GetAwaiter().GetResult();

    public async ValueTask ListenAsync(int backlog = 128, CancellationToken cancellationToken = default)
    {
        _ = backlog;
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        EnsureTcp();

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

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

            var listener = await _zt
                .ListenTcpAsync(_localEndPoint.Address, _localEndPoint.Port, cancellationToken)
                .ConfigureAwait(false);

            _listener = listener;
            _localEndPoint = listener.LocalEndpoint;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public ManagedSocket Accept()
        => AcceptAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask<ManagedSocket> AcceptAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureTcp();

        var listener = _listener ?? throw new InvalidOperationException("Socket is not listening.");
        var stream = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);

        var local = listener.LocalEndpoint;
        return new ManagedSocket(_zt, local, remoteEndPoint: null, connectedStream: stream);
    }

    public void Connect(EndPoint remoteEndPoint)
        => ConnectAsync(remoteEndPoint).AsTask().GetAwaiter().GetResult();

    public async ValueTask ConnectAsync(EndPoint remoteEndPoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureTcp();

        if (remoteEndPoint is not IPEndPoint remoteIp)
        {
            throw new NotSupportedException("Only IPEndPoint is supported.");
        }

        if (remoteIp.AddressFamily != AddressFamily)
        {
            throw new NotSupportedException("Remote address family must match the socket address family.");
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_stream is not null)
            {
                throw new InvalidOperationException("Socket is already connected.");
            }

            if (_listener is not null)
            {
                throw new InvalidOperationException("Socket is listening. Create a new socket for outgoing connections.");
            }

            if (_udp is not null)
            {
                throw new InvalidOperationException("Socket is already initialized for UDP.");
            }

            var local = _localEndPoint;
            if (local is not null)
            {
                local = await NormalizeLocalEndpointAsync(local, cancellationToken).ConfigureAwait(false);
            }

            Stream connected;
            if (local is null)
            {
                connected = await _zt.ConnectTcpAsync(remoteIp, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                connected = await _zt.ConnectTcpAsync(local, remoteIp, cancellationToken).ConfigureAwait(false);
            }

            _stream = connected;
            _remoteEndPoint = remoteIp;
            _localEndPoint = local;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public int Send(byte[] buffer)
        => SendAsync(buffer).AsTask().GetAwaiter().GetResult();

    public async ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureTcp();

        var stream = _stream ?? throw new InvalidOperationException("Socket is not connected.");
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.Length;
    }

    public int Receive(byte[] buffer)
        => ReceiveAsync(buffer).AsTask().GetAwaiter().GetResult();

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureTcp();

        var stream = _stream ?? throw new InvalidOperationException("Socket is not connected.");
        return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public int SendTo(byte[] buffer, EndPoint remoteEndPoint)
        => SendToAsync(buffer, remoteEndPoint).AsTask().GetAwaiter().GetResult();

    public async ValueTask<int> SendToAsync(ReadOnlyMemory<byte> buffer, EndPoint remoteEndPoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureUdp();

        if (remoteEndPoint is not IPEndPoint remote)
        {
            throw new NotSupportedException("Only IPEndPoint is supported.");
        }

        var udp = _udp ?? throw new InvalidOperationException("Socket is not bound.");
        return await udp.SendToAsync(buffer, remote, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<(int ReceivedBytes, EndPoint RemoteEndPoint)> ReceiveFromAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureUdp();

        var udp = _udp ?? throw new InvalidOperationException("Socket is not bound.");
        var result = await udp.ReceiveFromAsync(buffer, cancellationToken).ConfigureAwait(false);
        return (result.ReceivedBytes, result.RemoteEndPoint);
    }

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1024:Use properties where appropriate",
        Justification = "API is intentionally socket-like; throws when not connected.")]
    public Stream GetStream()
        => _stream ?? throw new InvalidOperationException("Socket is not connected.");

    public void Shutdown(SocketShutdown how)
    {
        _ = how;
        Close();
    }

    public void Close()
        => Dispose();

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }
        finally
        {
            _initLock.Release();
        }

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

        if (_udp is not null)
        {
            await _udp.DisposeAsync().ConfigureAwait(false);
            _udp = null;
        }

        _initLock.Dispose();
    }

    private void EnsureTcp()
    {
        if (SocketType != SocketType.Stream || ProtocolType != ProtocolType.Tcp)
        {
            throw new NotSupportedException("Operation is only supported on TCP stream sockets.");
        }
    }

    private void EnsureUdp()
    {
        if (SocketType != SocketType.Dgram || ProtocolType != ProtocolType.Udp)
        {
            throw new NotSupportedException("Operation is only supported on UDP datagram sockets.");
        }
    }

    private async ValueTask<IPEndPoint> NormalizeLocalEndpointAsync(IPEndPoint localEndPoint, CancellationToken cancellationToken)
    {
        var address = localEndPoint.Address;

        if (AddressFamily == AddressFamily.InterNetwork && address.Equals(IPAddress.Any))
        {
            await _zt.JoinAsync(cancellationToken).ConfigureAwait(false);
            address = _zt.ManagedIps.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                      ?? throw new InvalidOperationException("No IPv4 managed IP assigned for this network.");
        }

        if (AddressFamily == AddressFamily.InterNetworkV6 && address.Equals(IPAddress.IPv6Any))
        {
            await _zt.JoinAsync(cancellationToken).ConfigureAwait(false);
            address = _zt.ManagedIps.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetworkV6)
                      ?? throw new InvalidOperationException("No IPv6 managed IP assigned for this network.");
        }

        return new IPEndPoint(address, localEndPoint.Port);
    }
}
