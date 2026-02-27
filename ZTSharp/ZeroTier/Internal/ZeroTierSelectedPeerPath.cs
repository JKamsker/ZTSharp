using System.Net;

namespace ZTSharp.ZeroTier.Internal;

internal readonly record struct ZeroTierSelectedPeerPath(int LocalSocketId, IPEndPoint RemoteEndPoint);

