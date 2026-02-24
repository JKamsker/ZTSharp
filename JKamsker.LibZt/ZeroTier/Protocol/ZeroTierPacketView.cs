namespace JKamsker.LibZt.ZeroTier.Protocol;

internal readonly record struct ZeroTierPacketView(ReadOnlyMemory<byte> Raw, ZeroTierPacketHeader Header)
{
    public ReadOnlyMemory<byte> Payload => Raw.Slice(ZeroTierPacketHeader.Length);
}

