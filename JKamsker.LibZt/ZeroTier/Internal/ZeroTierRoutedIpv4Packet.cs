namespace JKamsker.LibZt.ZeroTier.Internal;

internal readonly record struct ZeroTierRoutedIpPacket(NodeId PeerNodeId, ReadOnlyMemory<byte> Packet);
