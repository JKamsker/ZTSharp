using System.Net;
using System.Net.Sockets;
using ZTSharp.ZeroTier;

namespace ZTSharp.ZeroTier.Sockets;

internal sealed class ManagedUdpSocketBackend : ManagedSocketBackend
{
    private IPEndPoint? _localEndPoint;
    private ZeroTierUdpSocket? _udp;

    public ManagedUdpSocketBackend(ZeroTierSocket zeroTier, AddressFamily addressFamily)
        : base(zeroTier)
    {
        AddressFamily = addressFamily;
    }

    public override AddressFamily AddressFamily { get; }

    public override SocketType SocketType => SocketType.Dgram;

    public override ProtocolType ProtocolType => ProtocolType.Udp;

    public override EndPoint? LocalEndPoint => _localEndPoint;

    public override EndPoint? RemoteEndPoint => null;

    public override bool Connected => false;

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

            if (_udp is not null)
            {
                throw new InvalidOperationException("Socket is already initialized.");
            }

            var normalized = await ManagedSocketEndpointNormalizer
                .NormalizeLocalEndpointAsync(Zt, AddressFamily, ip, cancellationToken)
                .ConfigureAwait(false);

            _udp = await Zt.BindUdpAsync(normalized.Address, normalized.Port, cancellationToken).ConfigureAwait(false);
            _localEndPoint = _udp.LocalEndpoint;
        }
        finally
        {
            InitLock.Release();
        }
    }

    public override async ValueTask<int> SendToAsync(ReadOnlyMemory<byte> buffer, EndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Disposed, this);

        if (remoteEndPoint is not IPEndPoint remote)
        {
            throw new NotSupportedException("Only IPEndPoint is supported.");
        }

        var udp = _udp ?? throw new InvalidOperationException("Socket is not bound.");
        return await udp.SendToAsync(buffer, remote, cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<(int ReceivedBytes, EndPoint RemoteEndPoint)> ReceiveFromAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Disposed, this);

        var udp = _udp ?? throw new InvalidOperationException("Socket is not bound.");
        var result = await udp.ReceiveFromAsync(buffer, cancellationToken).ConfigureAwait(false);
        return (result.ReceivedBytes, result.RemoteEndPoint);
    }

    protected override async ValueTask DisposeResourcesAsync()
    {
        if (_udp is not null)
        {
            await _udp.DisposeAsync().ConfigureAwait(false);
            _udp = null;
        }
    }
}

