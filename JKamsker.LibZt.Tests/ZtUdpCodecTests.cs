using System.Net;
using JKamsker.LibZt.ZeroTier.Net;

namespace JKamsker.LibZt.Tests;

public sealed class ZtUdpCodecTests
{
    [Fact]
    public void Encode_Parse_RoundTrips()
    {
        var src = IPAddress.Parse("10.0.0.1");
        var dst = IPAddress.Parse("10.0.0.2");
        var payload = new byte[] { 1, 2, 3, 4 };

        var segment = ZtUdpCodec.Encode(
            src,
            dst,
            sourcePort: 12345,
            destinationPort: 9999,
            payload);

        Assert.True(ZtUdpCodec.TryParse(segment, out var srcPort, out var dstPort, out var parsedPayload));
        Assert.Equal((ushort)12345, srcPort);
        Assert.Equal((ushort)9999, dstPort);
        Assert.Equal(payload, parsedPayload.ToArray());
    }
}

