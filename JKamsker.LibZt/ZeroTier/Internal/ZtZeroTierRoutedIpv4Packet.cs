namespace JKamsker.LibZt.ZeroTier.Internal;

internal readonly record struct ZtZeroTierRoutedIpPacket(ZtNodeId PeerNodeId, ReadOnlyMemory<byte> Packet);
