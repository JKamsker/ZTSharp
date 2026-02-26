using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal interface IZeroTierDataplanePeerDatagramProcessor
{
    Task ProcessAsync(ZeroTierUdpDatagram datagram, CancellationToken cancellationToken);
}

