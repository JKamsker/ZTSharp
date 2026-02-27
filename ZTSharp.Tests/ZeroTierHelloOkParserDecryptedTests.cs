using System.Net;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.Tests;

public sealed class ZeroTierHelloOkParserDecryptedTests
{
    [Fact]
    public void TryParseDecryptedOkHello_ParsesTimestampAndSurface()
    {
        var sharedKey = Enumerable.Repeat((byte)9, 48).ToArray();

        var packet = ZeroTierHelloOkPacketBuilder.BuildPacket(
            packetId: 1,
            destination: new NodeId(0x1111111111),
            source: new NodeId(0x2222222222),
            inRePacketId: 123,
            helloTimestampEcho: 456,
            externalSurfaceAddress: new IPEndPoint(IPAddress.Parse("203.0.113.1"), 9999),
            sharedKey: sharedKey);

        Assert.True(ZeroTierPacketCrypto.Dearmor(packet, sharedKey));

        Assert.True(ZeroTierHelloOkParser.TryParseDecryptedOkHello(packet, out var payload));
        Assert.Equal(123UL, payload.InRePacketId);
        Assert.Equal(456UL, payload.TimestampEcho);
        Assert.NotNull(payload.ExternalSurfaceAddress);
        Assert.Equal(IPAddress.Parse("203.0.113.1"), payload.ExternalSurfaceAddress!.Address);
        Assert.Equal(9999, payload.ExternalSurfaceAddress.Port);
    }
}

