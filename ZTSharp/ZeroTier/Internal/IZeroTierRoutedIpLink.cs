using System.Threading.Channels;
using ZTSharp.ZeroTier.Net;

namespace ZTSharp.ZeroTier.Internal;

internal interface IZeroTierRoutedIpLink : IUserSpaceIpLink
{
    ChannelWriter<ReadOnlyMemory<byte>> IncomingWriter { get; }
}

