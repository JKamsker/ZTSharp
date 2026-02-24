using JKamsker.LibZt.ZeroTier.Internal;
using JKamsker.LibZt.ZeroTier.Protocol;

namespace JKamsker.LibZt.Tests;

public sealed class ZeroTierExtFramePacketBuilderTests
{
    [Fact]
    public void BuildIpv4Packet_CanBeDecrypted_AndParsed()
    {
        const ulong packetId = 0x0102030405060708UL;
        const ulong networkId = 0x9ad07d01093a69e3UL;

        var localNodeId = new NodeId(0x17e81f3f59UL);
        var remoteNodeId = new NodeId(0x12abcdef01UL);

        var sharedKey = new byte[48];
        for (var i = 0; i < sharedKey.Length; i++)
        {
            sharedKey[i] = (byte)(i + 1);
        }

        var com = new byte[1 + 2 + 5 + 96];
        com[0] = 1;
        com[7] = 1;

        var to = ZeroTierMac.FromAddress(remoteNodeId, networkId);
        var from = ZeroTierMac.FromAddress(localNodeId, networkId);

        var ipv4Packet = new byte[] { 0x45, 0x00, 0x00, 0x14, 0x00, 0x00, 0x00, 0x00, 0x40, 0x11, 0x00, 0x00, 0x0a, 0x00, 0x00, 0x01, 0x0a, 0x00, 0x00, 0x02 };

        var packet = ZeroTierExtFramePacketBuilder.BuildIpv4Packet(
            packetId,
            destination: remoteNodeId,
            source: localNodeId,
            networkId,
            com,
            to,
            from,
            ipv4Packet,
            sharedKey);

        Assert.True(ZeroTierPacketCrypto.Dearmor(packet, sharedKey));
        Assert.Equal(ZeroTierVerb.ExtFrame, (ZeroTierVerb)(packet[27] & 0x1F));

        Assert.True(ZeroTierFrameCodec.TryParseExtFramePayload(
            packet.AsSpan(ZeroTierPacketHeader.Length),
            out var parsedNetworkId,
            out var flags,
            out var parsedCom,
            out var parsedTo,
            out var parsedFrom,
            out var etherType,
            out var frame));

        Assert.Equal(networkId, parsedNetworkId);
        Assert.Equal(0x01, flags);
        Assert.Equal(com, parsedCom.ToArray());
        Assert.Equal(to, parsedTo);
        Assert.Equal(from, parsedFrom);
        Assert.Equal(ZeroTierFrameCodec.EtherTypeIpv4, etherType);
        Assert.Equal(ipv4Packet, frame.ToArray());
    }
}

