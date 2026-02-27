using System.Net;

namespace ZTSharp.ZeroTier.Transport;

internal interface IZeroTierUdpTransport : IAsyncDisposable
{
    IReadOnlyList<ZeroTierUdpLocalSocket> LocalSockets { get; }

    ValueTask<ZeroTierUdpDatagram> ReceiveAsync(CancellationToken cancellationToken = default);

    ValueTask<ZeroTierUdpDatagram> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    Task SendAsync(IPEndPoint remoteEndpoint, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    Task SendAsync(int localSocketId, IPEndPoint remoteEndpoint, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
}

