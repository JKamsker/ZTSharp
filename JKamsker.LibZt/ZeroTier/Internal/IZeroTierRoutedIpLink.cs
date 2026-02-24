using System.Threading.Channels;
using JKamsker.LibZt.ZeroTier.Net;

namespace JKamsker.LibZt.ZeroTier.Internal;

internal interface IZeroTierRoutedIpLink : IUserSpaceIpLink
{
    ChannelWriter<ReadOnlyMemory<byte>> IncomingWriter { get; }
}

