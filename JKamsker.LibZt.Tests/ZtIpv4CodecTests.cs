using System.Net;
using JKamsker.LibZt.ZeroTier.Net;

namespace JKamsker.LibZt.Tests;

public sealed class ZtIpv4CodecTests
{
    [Fact]
    public void Encode_Parse_RoundTrips()
    {
        var src = IPAddress.Parse("10.0.0.1");
        var dst = IPAddress.Parse("10.0.0.2");
        var payload = new byte[] { 1, 2, 3, 4 };

        var packet = ZtIpv4Codec.Encode(src, dst, protocol: 0x11, payload, identification: 0x1234);

        Assert.True(ZtIpv4Codec.TryParse(packet, out var parsedSrc, out var parsedDst, out var proto, out var parsedPayload));
        Assert.Equal(src, parsedSrc);
        Assert.Equal(dst, parsedDst);
        Assert.Equal(0x11, proto);
        Assert.Equal(payload, parsedPayload.ToArray());
    }
}

