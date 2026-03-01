using System.Net;
using ZTSharp.ZeroTier.Net;

namespace ZTSharp.Tests;

public sealed class Ipv6CodecAhHeaderTests
{
    [Fact]
    public void TryParseTransportPayload_RejectsInvalidAhHeaderLength()
    {
        var src = IPAddress.Parse("2001:db8::1");
        var dst = IPAddress.Parse("2001:db8::2");

        var payload = new byte[8 + 8];
        payload[0] = UdpCodec.ProtocolNumber; // next header
        payload[1] = 0; // AH payload len (0 => 8 bytes total, invalid; must be >= 12 bytes)

        var packet = new byte[40 + payload.Length];
        packet[0] = 0x60; // version
        packet[4] = (byte)(payload.Length >> 8);
        packet[5] = (byte)(payload.Length & 0xFF);
        packet[6] = 51; // AH
        packet[7] = 64; // hop limit

        src.GetAddressBytes().CopyTo(packet, 8);
        dst.GetAddressBytes().CopyTo(packet, 24);
        payload.CopyTo(packet, 40);

        Assert.False(Ipv6Codec.TryParseTransportPayload(packet, out _, out _, out _, out _, out _));
    }
}

