using System.Net;
using ZTSharp.ZeroTier.Internal;

namespace ZTSharp.ZeroTier.Protocol;

internal sealed record ZeroTierWorldRoot(
    ZeroTierIdentity Identity,
    IReadOnlyList<IPEndPoint> StableEndpoints);

