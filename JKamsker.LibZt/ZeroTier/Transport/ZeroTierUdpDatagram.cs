using System.Net;

namespace JKamsker.LibZt.ZeroTier.Transport;

internal readonly record struct ZeroTierUdpDatagram(IPEndPoint RemoteEndPoint, ReadOnlyMemory<byte> Payload);

