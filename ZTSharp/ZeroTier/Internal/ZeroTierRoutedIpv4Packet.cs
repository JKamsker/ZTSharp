namespace ZTSharp.ZeroTier.Internal;

internal readonly record struct ZeroTierRoutedIpPacket(NodeId PeerNodeId, ReadOnlyMemory<byte> Packet);
