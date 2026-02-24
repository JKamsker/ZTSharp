namespace JKamsker.LibZt.ZeroTier.Net;

internal interface IZtUserSpaceIpv4Link : IAsyncDisposable
{
    ValueTask SendAsync(ReadOnlyMemory<byte> ipv4Packet, CancellationToken cancellationToken = default);

    ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default);
}

