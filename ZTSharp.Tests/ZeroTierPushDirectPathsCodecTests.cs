using System.Buffers.Binary;
using System.Net;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.Tests;

public sealed class ZeroTierPushDirectPathsCodecTests
{
    [Fact]
    public void TryParse_ParsesIpv4Entries()
    {
        var payload = new byte[]
        {
            0x00, 0x01, // count
            0x00, // flags
            0x00, 0x00, // extLen
            0x04, // addrType (v4)
            0x06, // addrLen (4 + 2)
            0x01, 0x02, 0x03, 0x04, // ip
            0x13, 0x88 // port 5000
        };

        Assert.True(ZeroTierPushDirectPathsCodec.TryParse(payload, out var paths));
        Assert.Single(paths);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 5000), paths[0].Endpoint);
    }

    [Fact]
    public void TryParse_ParsesIpv6Entries()
    {
        var ipv6 = IPAddress.Parse("2001:db8::1").GetAddressBytes();
        Assert.Equal(16, ipv6.Length);

        var payload = new byte[2 + 1 + 2 + 1 + 1 + 18];
        payload[0] = 0x00;
        payload[1] = 0x01; // count
        payload[2] = 0x00; // flags
        payload[3] = 0x00;
        payload[4] = 0x00; // extLen
        payload[5] = 0x06; // addrType (v6)
        payload[6] = 0x12; // addrLen (16 + 2)
        ipv6.CopyTo(payload, 7);
        payload[23] = 0x00;
        payload[24] = 0x50; // port 80

        Assert.True(ZeroTierPushDirectPathsCodec.TryParse(payload, out var paths));
        Assert.Single(paths);
        Assert.Equal(IPAddress.Parse("2001:db8::1"), paths[0].Endpoint.Address);
        Assert.Equal(80, paths[0].Endpoint.Port);
    }

    [Fact]
    public void TryParse_Clamps_PathCount()
    {
        var count = ZeroTierProtocolLimits.MaxPushedDirectPaths + 1;
        var payload = new byte[2 + (count * 11)];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), (ushort)count);

        var ptr = 2;
        for (var i = 0; i < count; i++)
        {
            payload[ptr++] = 0x00; // flags
            payload[ptr++] = 0x00;
            payload[ptr++] = 0x00; // extLen
            payload[ptr++] = 0x04; // addrType (v4)
            payload[ptr++] = 0x06; // addrLen (4 + 2)
            payload[ptr++] = 10;
            payload[ptr++] = 0;
            payload[ptr++] = 0;
            payload[ptr++] = (byte)(i + 1);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(ptr, 2), 5000);
            ptr += 2;
        }

        Assert.True(ZeroTierPushDirectPathsCodec.TryParse(payload, out var paths));
        Assert.Equal(ZeroTierProtocolLimits.MaxPushedDirectPaths, paths.Length);
        var expectedLast = new IPEndPoint(
            new IPAddress(new byte[] { 10, 0, 0, (byte)ZeroTierProtocolLimits.MaxPushedDirectPaths }),
            5000);
        Assert.Equal(expectedLast, paths[^1].Endpoint);
    }
}
