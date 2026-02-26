namespace ZTSharp.ZeroTier.Protocol;

internal readonly record struct ZeroTierPacketView(ReadOnlyMemory<byte> Raw, ZeroTierPacketHeader Header)
{
    public ReadOnlyMemory<byte> Payload => Raw.Slice(ZeroTierPacketHeader.IndexPayload);
}
