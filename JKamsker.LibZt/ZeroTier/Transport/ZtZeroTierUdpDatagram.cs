using System.Net;

namespace JKamsker.LibZt.ZeroTier.Transport;

internal readonly record struct ZtZeroTierUdpDatagram(IPEndPoint RemoteEndPoint, ReadOnlyMemory<byte> Payload);

