using System.Net;
using JKamsker.LibZt.ZeroTier.Internal;

namespace JKamsker.LibZt.ZeroTier.Protocol;

internal sealed record ZeroTierWorldRoot(
    ZeroTierIdentity Identity,
    IReadOnlyList<IPEndPoint> StableEndpoints);

