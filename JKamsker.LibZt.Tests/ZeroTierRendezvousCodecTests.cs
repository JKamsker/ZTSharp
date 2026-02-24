using System.Net;
using JKamsker.LibZt.ZeroTier.Protocol;

namespace JKamsker.LibZt.Tests;

public sealed class ZeroTierRendezvousCodecTests
{
    [Fact]
    public void TryParse_ParsesIpv4()
    {
        var payload = new byte[]
        {
            0x00, // flags
            0x11, 0x22, 0x33, 0x44, 0x55, // with
            0x13, 0x88, // port 5000
            0x04, // addrlen
            0x01, 0x02, 0x03, 0x04 // ip
        };

        Assert.True(ZeroTierRendezvousCodec.TryParse(payload, out var rendezvous));
        Assert.Equal((byte)0x00, rendezvous.Flags);
        Assert.Equal(new NodeId(0x1122334455UL), rendezvous.With);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 5000), rendezvous.Endpoint);
    }

    [Fact]
    public void TryParse_ParsesIpv6()
    {
        var ipv6 = IPAddress.Parse("2001:db8::1").GetAddressBytes();
        Assert.Equal(16, ipv6.Length);

        var payload = new byte[1 + 5 + 2 + 1 + 16];
        payload[0] = 0x00; // flags
        payload[1] = 0xAA;
        payload[2] = 0xBB;
        payload[3] = 0xCC;
        payload[4] = 0xDD;
        payload[5] = 0xEE; // with
        payload[6] = 0x00;
        payload[7] = 0x50; // port 80
        payload[8] = 0x10; // addrlen 16
        ipv6.CopyTo(payload, 9);

        Assert.True(ZeroTierRendezvousCodec.TryParse(payload, out var rendezvous));
        Assert.Equal(new NodeId(0xAABBCCDDEEUL), rendezvous.With);
        Assert.Equal(80, rendezvous.Endpoint.Port);
        Assert.Equal(IPAddress.Parse("2001:db8::1"), rendezvous.Endpoint.Address);
    }

    [Fact]
    public void TryParse_RejectsShortPayload()
    {
        Assert.False(ZeroTierRendezvousCodec.TryParse(new byte[1 + 5 + 2], out _));
    }
}

