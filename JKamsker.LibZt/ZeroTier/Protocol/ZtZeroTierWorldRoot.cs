using System.Net;
using JKamsker.LibZt.ZeroTier.Internal;

namespace JKamsker.LibZt.ZeroTier.Protocol;

internal sealed record ZtZeroTierWorldRoot(
    ZtZeroTierIdentity Identity,
    IReadOnlyList<IPEndPoint> StableEndpoints);

