using System.Net;

namespace ZTSharp.ZeroTier.Transport;

internal readonly record struct ZeroTierUdpDatagram(IPEndPoint RemoteEndPoint, byte[] Payload);
