using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ZTSharp.ZeroTier;

namespace ZTSharp.ZeroTier.Sockets;

[global::System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Usage",
    "CA2213:Disposable fields should be disposed",
    Justification = "ManagedSocket does not own the ZeroTierSocket instance.")]
public sealed class ManagedSocket : IAsyncDisposable, IDisposable
{
    private readonly ZeroTierSocket _zt;
    private readonly ManagedSocketBackend _backend;
    private int _disposed;

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
        _backend = socketType switch
        {
            SocketType.Stream => new ManagedTcpSocketBackend(_zt, addressFamily),
            SocketType.Dgram => new ManagedUdpSocketBackend(_zt, addressFamily),
            _ => throw new NotSupportedException($"Unsupported socket type: {socketType}."),
        };
    }

    private ManagedSocket(ZeroTierSocket zeroTier, ManagedSocketBackend backend)
    {
        ArgumentNullException.ThrowIfNull(zeroTier);
        ArgumentNullException.ThrowIfNull(backend);

        _zt = zeroTier;
        _backend = backend;
    }

    public AddressFamily AddressFamily => _backend.AddressFamily;

    public SocketType SocketType => _backend.SocketType;

    public ProtocolType ProtocolType => _backend.ProtocolType;

    public EndPoint? LocalEndPoint => _backend.LocalEndPoint;

    public EndPoint? RemoteEndPoint => _backend.RemoteEndPoint;

    public bool Connected => _backend.Connected && !IsDisposed;

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Bind(EndPoint localEndPoint)
        => BindAsync(localEndPoint).AsTask().GetAwaiter().GetResult();

    public async ValueTask BindAsync(EndPoint localEndPoint, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _backend.BindAsync(localEndPoint, cancellationToken).ConfigureAwait(false);
    }

    public void Listen(int backlog = 128)
        => ListenAsync(backlog).AsTask().GetAwaiter().GetResult();

    public async ValueTask ListenAsync(int backlog = 128, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _backend.ListenAsync(backlog, cancellationToken).ConfigureAwait(false);
    }

    public ManagedSocket Accept()
        => AcceptAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask<ManagedSocket> AcceptAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var accepted = await _backend.AcceptAsync(cancellationToken).ConfigureAwait(false);
        return new ManagedSocket(_zt, accepted);
    }

    public void Connect(EndPoint remoteEndPoint)
        => ConnectAsync(remoteEndPoint).AsTask().GetAwaiter().GetResult();

    public async ValueTask ConnectAsync(EndPoint remoteEndPoint, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _backend.ConnectAsync(remoteEndPoint, cancellationToken).ConfigureAwait(false);
    }

    public int Send(byte[] buffer)
        => SendAsync(buffer).AsTask().GetAwaiter().GetResult();

    public async ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await _backend.SendAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public int Receive(byte[] buffer)
        => ReceiveAsync(buffer).AsTask().GetAwaiter().GetResult();

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await _backend.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public int SendTo(byte[] buffer, EndPoint remoteEndPoint)
        => SendToAsync(buffer, remoteEndPoint).AsTask().GetAwaiter().GetResult();

    public async ValueTask<int> SendToAsync(ReadOnlyMemory<byte> buffer, EndPoint remoteEndPoint, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await _backend.SendToAsync(buffer, remoteEndPoint, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<(int ReceivedBytes, EndPoint RemoteEndPoint)> ReceiveFromAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await _backend.ReceiveFromAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1024:Use properties where appropriate",
        Justification = "API is intentionally socket-like; throws when not connected.")]
    public Stream GetStream()
        => _backend.GetStream();

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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _backend.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
