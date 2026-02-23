namespace JKamsker.LibZt.ZeroTier.Protocol;

internal readonly record struct ZtZeroTierPacketView(ReadOnlyMemory<byte> Raw, ZtZeroTierPacketHeader Header)
{
    public ReadOnlyMemory<byte> Payload => Raw.Slice(ZtZeroTierPacketHeader.Length);
}

