using System.Net;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierSizeCapHardeningTests
{
    [Fact]
    public void PacketCrypto_Dearmor_RejectsOversizedPackets()
    {
        var packet = new byte[ZeroTierProtocolLimits.MaxPacketBytes + 1];
        var key = new byte[ZeroTierPacketCrypto.KeyLength];

        Assert.False(ZeroTierPacketCrypto.Dearmor(packet, key));
    }

    [Fact]
    public void PacketCompression_TryUncompress_RejectsOversizedPackets()
    {
        var packet = new byte[ZeroTierProtocolLimits.MaxPacketBytes + 1];

        Assert.False(ZeroTierPacketCompression.TryUncompress(packet, out var uncompressed));
        Assert.Empty(uncompressed);
    }

    [Fact]
    public void PacketCompression_TryUncompress_InvalidCompressedPayload_ReturnsFalse()
    {
        var packet = new byte[ZeroTierPacketHeader.IndexPayload + 4];
        packet[ZeroTierPacketHeader.IndexVerb] = ZeroTierPacketHeader.VerbFlagCompressed;
        packet[ZeroTierPacketHeader.IndexPayload] = 0xFF;

        Assert.False(ZeroTierPacketCompression.TryUncompress(packet, out var uncompressed));
        Assert.Empty(uncompressed);
    }

    [Fact]
    public async Task PeerSecurity_DropsOversizedHello_WithoutCachingKeys()
    {
        await using var udp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);

        var localIdentity = ZeroTierTestIdentities.CreateFastIdentity(0x2222222222);
        var rootClient = new ZeroTierDataplaneRootClient(
            udp,
            rootNodeId: new NodeId(0x1111111111),
            rootEndpoint: new IPEndPoint(IPAddress.Loopback, 9999),
            rootKey: new byte[48],
            rootProtocolVersion: 12,
            localNodeId: localIdentity.NodeId,
            networkId: 1,
            inlineCom: Array.Empty<byte>());

        using var peerSecurity = new ZeroTierDataplanePeerSecurity(udp, rootClient, localIdentity);

        var packetBytes = new byte[ZeroTierProtocolLimits.MaxPacketBytes + 1];
        await peerSecurity.HandleHelloAsync(
            peerNodeId: new NodeId(0xaaaaaaaaaa),
            helloPacketId: 1,
            packetBytes: packetBytes,
            remoteEndPoint: new IPEndPoint(IPAddress.Loopback, 12345),
            cancellationToken: CancellationToken.None);

        Assert.False(peerSecurity.TryGetPeerKey(new NodeId(0xaaaaaaaaaa), out _));
    }
}
