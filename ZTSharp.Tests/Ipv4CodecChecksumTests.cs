using System.Net;
using ZTSharp.ZeroTier.Net;

namespace ZTSharp.Tests;

public sealed class Ipv4CodecChecksumTests
{
    [Fact]
    public void TryParse_RejectsInvalidHeaderChecksum()
    {
        var src = IPAddress.Parse("10.0.0.1");
        var dst = IPAddress.Parse("10.0.0.2");
        var packet = Ipv4Codec.Encode(src, dst, protocol: UdpCodec.ProtocolNumber, payload: new byte[] { 1, 2, 3 }, identification: 1);

        packet[8] ^= 0x01; // TTL

        Assert.False(Ipv4Codec.TryParse(packet, out _, out _, out _, out _));
    }

    [Fact]
    public void TryParse_AcceptsValidHeaderChecksum()
    {
        var src = IPAddress.Parse("10.0.0.1");
        var dst = IPAddress.Parse("10.0.0.2");
        var packet = Ipv4Codec.Encode(src, dst, protocol: UdpCodec.ProtocolNumber, payload: new byte[] { 1, 2, 3 }, identification: 1);

        Assert.True(Ipv4Codec.TryParse(packet, out var parsedSrc, out var parsedDst, out var protocol, out var payload));
        Assert.Equal(src, parsedSrc);
        Assert.Equal(dst, parsedDst);
        Assert.Equal(UdpCodec.ProtocolNumber, protocol);
        Assert.Equal(new byte[] { 1, 2, 3 }, payload.ToArray());
    }
}

