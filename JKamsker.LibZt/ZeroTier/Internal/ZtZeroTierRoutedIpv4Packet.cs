namespace JKamsker.LibZt.ZeroTier.Internal;

internal readonly record struct ZtZeroTierRoutedIpv4Packet(ZtNodeId PeerNodeId, ReadOnlyMemory<byte> Packet);
