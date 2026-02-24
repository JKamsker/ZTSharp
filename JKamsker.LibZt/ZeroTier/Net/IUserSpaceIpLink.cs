namespace JKamsker.LibZt.ZeroTier.Net;

internal interface IUserSpaceIpLink : IAsyncDisposable
{
    ValueTask SendAsync(ReadOnlyMemory<byte> ipPacket, CancellationToken cancellationToken = default);

    ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default);
}
