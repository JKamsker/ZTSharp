using System.Net;
using ZTSharp.ZeroTier.Net;

namespace ZTSharp.Tests;

public sealed class ZeroTierFlowIdTests
{
    [Fact]
    public void Derive_Ipv4Udp_IsDirectionIndependent()
    {
        var srcIp = IPAddress.Parse("10.0.0.2");
        var dstIp = IPAddress.Parse("10.0.0.3");
        const ushort srcPort = 12345;
        const ushort dstPort = 54321;

        var udp = UdpCodec.Encode(srcIp, dstIp, srcPort, dstPort, payload: new byte[] { 1, 2, 3 });
        var ip = Ipv4Codec.Encode(srcIp, dstIp, UdpCodec.ProtocolNumber, udp, identification: 1);

        var udpReverse = UdpCodec.Encode(dstIp, srcIp, dstPort, srcPort, payload: new byte[] { 9, 8, 7 });
        var ipReverse = Ipv4Codec.Encode(dstIp, srcIp, UdpCodec.ProtocolNumber, udpReverse, identification: 2);

        Assert.Equal(ZeroTierFlowId.Derive(ip), ZeroTierFlowId.Derive(ipReverse));
    }

    [Fact]
    public void Derive_Ipv6Udp_IsDirectionIndependent()
    {
        var srcIp = IPAddress.Parse("fd00::2");
        var dstIp = IPAddress.Parse("fd00::3");
        const ushort srcPort = 12345;
        const ushort dstPort = 54321;

        var udp = UdpCodec.Encode(srcIp, dstIp, srcPort, dstPort, payload: new byte[] { 1, 2, 3 });
        var ip = Ipv6Codec.Encode(srcIp, dstIp, UdpCodec.ProtocolNumber, udp, hopLimit: 64);

        var udpReverse = UdpCodec.Encode(dstIp, srcIp, dstPort, srcPort, payload: new byte[] { 9, 8, 7 });
        var ipReverse = Ipv6Codec.Encode(dstIp, srcIp, UdpCodec.ProtocolNumber, udpReverse, hopLimit: 64);

        Assert.Equal(ZeroTierFlowId.Derive(ip), ZeroTierFlowId.Derive(ipReverse));
    }
}

