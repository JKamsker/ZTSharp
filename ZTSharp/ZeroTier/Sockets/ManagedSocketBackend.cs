using System.Net;
using System.Net.Sockets;
using ZTSharp.ZeroTier;

namespace ZTSharp.ZeroTier.Sockets;

internal abstract class ManagedSocketBackend : IAsyncDisposable
{
    protected ManagedSocketBackend(ZeroTierSocket zeroTier)
    {
        ArgumentNullException.ThrowIfNull(zeroTier);
        Zt = zeroTier;
    }

    protected ZeroTierSocket Zt { get; }

    protected SemaphoreSlim InitLock { get; } = new(1, 1);

    protected bool Disposed { get; private set; }

    public abstract AddressFamily AddressFamily { get; }

    public abstract SocketType SocketType { get; }

    public abstract ProtocolType ProtocolType { get; }

    public abstract EndPoint? LocalEndPoint { get; }

    public abstract EndPoint? RemoteEndPoint { get; }

    public abstract bool Connected { get; }

    public abstract ValueTask BindAsync(EndPoint localEndPoint, CancellationToken cancellationToken);

    public virtual ValueTask ListenAsync(int backlog, CancellationToken cancellationToken)
        => throw new NotSupportedException("Operation is only supported on TCP stream sockets.");

    public virtual ValueTask<ManagedSocketBackend> AcceptAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("Operation is only supported on TCP stream sockets.");

    public virtual ValueTask ConnectAsync(EndPoint remoteEndPoint, CancellationToken cancellationToken)
        => throw new NotSupportedException("Operation is only supported on TCP stream sockets.");

    public virtual ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        => throw new NotSupportedException("Operation is only supported on TCP stream sockets.");

    public virtual ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => throw new NotSupportedException("Operation is only supported on TCP stream sockets.");

    public virtual ValueTask<int> SendToAsync(ReadOnlyMemory<byte> buffer, EndPoint remoteEndPoint, CancellationToken cancellationToken)
        => throw new NotSupportedException("Operation is only supported on UDP datagram sockets.");

    public virtual ValueTask<(int ReceivedBytes, EndPoint RemoteEndPoint)> ReceiveFromAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => throw new NotSupportedException("Operation is only supported on UDP datagram sockets.");

    public virtual Stream GetStream()
        => throw new InvalidOperationException("Socket is not connected.");

    public async ValueTask DisposeAsync()
    {
        if (Disposed)
        {
            return;
        }

        await InitLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Disposed)
            {
                return;
            }

            Disposed = true;
        }
        finally
        {
            InitLock.Release();
        }

        await DisposeResourcesAsync().ConfigureAwait(false);
        InitLock.Dispose();
    }

    protected abstract ValueTask DisposeResourcesAsync();
}

