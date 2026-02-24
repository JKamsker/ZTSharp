using System.Net;
using ZTSharp.ZeroTier.Net;

namespace ZTSharp.Tests;

public sealed class Ipv6CodecTests
{
    [Fact]
    public void Encode_Parse_RoundTrips()
    {
        var src = IPAddress.Parse("fd00::1");
        var dst = IPAddress.Parse("fd00::2");
        var payload = new byte[] { 1, 2, 3, 4 };

        var packet = Ipv6Codec.Encode(
            src,
            dst,
            nextHeader: 0x3A,
            payload,
            hopLimit: 64,
            trafficClass: 0x12,
            flowLabel: 0x34567);

        Assert.True(Ipv6Codec.TryParse(packet, out var parsedSrc, out var parsedDst, out var nextHeader, out var hopLimit, out var parsedPayload));
        Assert.Equal(src, parsedSrc);
        Assert.Equal(dst, parsedDst);
        Assert.Equal((byte)0x3A, nextHeader);
        Assert.Equal((byte)64, hopLimit);
        Assert.Equal(payload, parsedPayload.ToArray());
    }
}

