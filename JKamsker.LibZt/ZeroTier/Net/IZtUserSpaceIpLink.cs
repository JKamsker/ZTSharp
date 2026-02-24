namespace JKamsker.LibZt.ZeroTier.Net;

internal interface IZtUserSpaceIpLink : IAsyncDisposable
{
    ValueTask SendAsync(ReadOnlyMemory<byte> ipPacket, CancellationToken cancellationToken = default);

    ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default);
}
