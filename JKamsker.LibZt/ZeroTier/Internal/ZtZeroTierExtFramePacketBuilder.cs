using JKamsker.LibZt.ZeroTier.Protocol;

namespace JKamsker.LibZt.ZeroTier.Internal;

internal static class ZtZeroTierExtFramePacketBuilder
{
    public static byte[] BuildIpv4Packet(
        ulong packetId,
        ZtNodeId destination,
        ZtNodeId source,
        ulong networkId,
        ReadOnlySpan<byte> inlineCom,
        ZtZeroTierMac to,
        ZtZeroTierMac from,
        ReadOnlySpan<byte> ipv4Packet,
        ReadOnlySpan<byte> sharedKey)
    {
        var extFrameFlags = (byte)(0x01 | (ZtZeroTierTrace.Enabled ? 0x10 : 0x00));
        var payload = ZtZeroTierFrameCodec.EncodeExtFramePayload(
            networkId,
            flags: extFrameFlags,
            inlineCom: inlineCom,
            to,
            from,
            ZtZeroTierFrameCodec.EtherTypeIpv4,
            ipv4Packet);

        var header = new ZtZeroTierPacketHeader(
            PacketId: packetId,
            Destination: destination,
            Source: source,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZtZeroTierVerb.ExtFrame);

        var packet = ZtZeroTierPacketCodec.Encode(header, payload);
        ZtZeroTierPacketCrypto.Armor(packet, sharedKey, encryptPayload: true);
        return packet;
    }
}
