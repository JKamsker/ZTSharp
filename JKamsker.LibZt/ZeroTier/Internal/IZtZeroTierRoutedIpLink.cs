using System.Threading.Channels;
using JKamsker.LibZt.ZeroTier.Net;

namespace JKamsker.LibZt.ZeroTier.Internal;

internal interface IZtZeroTierRoutedIpLink : IZtUserSpaceIpLink
{
    ChannelWriter<ReadOnlyMemory<byte>> IncomingWriter { get; }
}

