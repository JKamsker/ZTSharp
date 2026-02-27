using System.Net;

namespace ZTSharp.ZeroTier.Transport;

internal readonly record struct ZeroTierUdpLocalSocket(int Id, IPEndPoint LocalEndpoint);

