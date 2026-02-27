using System.Net;
using ZTSharp.ZeroTier.Net;

namespace ZTSharp.Tests;

public sealed class TcpCodecEncodeBoundsTests
{
    [Fact]
    public void Encode_RejectsOversizedPayload()
    {
        var src = IPAddress.Parse("10.0.0.1");
        var dst = IPAddress.Parse("10.0.0.2");

        var payload = new byte[ushort.MaxValue];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TcpCodec.Encode(
                sourceIp: src,
                destinationIp: dst,
                sourcePort: 1,
                destinationPort: 2,
                sequenceNumber: 1,
                acknowledgmentNumber: 0,
                flags: TcpCodec.Flags.Ack,
                windowSize: 65535,
                options: ReadOnlySpan<byte>.Empty,
                payload: payload));
    }
}

