using JKamsker.LibZt.ZeroTier.Protocol;

namespace JKamsker.LibZt.ZeroTier.Internal;

internal static class ZeroTierExtFramePacketBuilder
{
    public static byte[] BuildPacket(
        ulong packetId,
        NodeId destination,
        NodeId source,
        ulong networkId,
        ReadOnlySpan<byte> inlineCom,
        ZeroTierMac to,
        ZeroTierMac from,
        ushort etherType,
        ReadOnlySpan<byte> frame,
        ReadOnlySpan<byte> sharedKey)
    {
        var extFrameFlags = (byte)(0x01 | (ZeroTierTrace.Enabled ? 0x10 : 0x00));
        var payload = ZeroTierFrameCodec.EncodeExtFramePayload(
            networkId,
            flags: extFrameFlags,
            inlineCom: inlineCom,
            to,
            from,
            etherType,
            frame);

        var header = new ZeroTierPacketHeader(
            PacketId: packetId,
            Destination: destination,
            Source: source,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.ExtFrame);

        var packet = ZeroTierPacketCodec.Encode(header, payload);
        ZeroTierPacketCrypto.Armor(packet, sharedKey, encryptPayload: true);
        return packet;
    }

    public static byte[] BuildIpv4Packet(
        ulong packetId,
        NodeId destination,
        NodeId source,
        ulong networkId,
        ReadOnlySpan<byte> inlineCom,
        ZeroTierMac to,
        ZeroTierMac from,
        ReadOnlySpan<byte> ipv4Packet,
        ReadOnlySpan<byte> sharedKey)
        => BuildPacket(
            packetId,
            destination,
            source,
            networkId,
            inlineCom,
            to,
            from,
            ZeroTierFrameCodec.EtherTypeIpv4,
            ipv4Packet,
            sharedKey);
}
