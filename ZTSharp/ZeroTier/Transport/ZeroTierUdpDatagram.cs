using System.Net;

namespace ZTSharp.ZeroTier.Transport;

internal readonly record struct ZeroTierUdpDatagram(int LocalSocketId, IPEndPoint RemoteEndPoint, byte[] Payload);
