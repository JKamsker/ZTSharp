using System.Buffers.Binary;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.Tests;

public sealed class ZeroTierFrameCodecTests
{
    [Fact]
    public void CertificateOfMembershipCodec_ReturnsExpectedLength()
    {
        var com = CreateMinimalSignedCom();

        Assert.True(ZeroTierCertificateOfMembershipCodec.TryGetSerializedLength(com, out var length));
        Assert.Equal(com.Length, length);
    }

    [Fact]
    public void ExtFrame_RoundTrips_WithInlineCom()
    {
        const ulong networkId = 0x1122334455667788UL;
        var com = CreateMinimalSignedCom();
        var to = new ZeroTierMac(0x001122334455UL);
        var from = new ZeroTierMac(0xaabbccddeeffUL);
        var frame = new byte[] { 1, 2, 3, 4 };

        var payload = ZeroTierFrameCodec.EncodeExtFramePayload(
            networkId,
            flags: 0x01,
            inlineCom: com,
            to,
            from,
            ZeroTierFrameCodec.EtherTypeIpv4,
            frame);

        Assert.True(ZeroTierFrameCodec.TryParseExtFramePayload(
            payload,
            out var parsedNetworkId,
            out var flags,
            out var parsedCom,
            out var parsedTo,
            out var parsedFrom,
            out var etherType,
            out var parsedFrame));

        Assert.Equal(networkId, parsedNetworkId);
        Assert.Equal(0x01, flags);
        Assert.Equal(com, parsedCom.ToArray());
        Assert.Equal(to, parsedTo);
        Assert.Equal(from, parsedFrom);
        Assert.Equal(ZeroTierFrameCodec.EtherTypeIpv4, etherType);
        Assert.Equal(frame, parsedFrame.ToArray());
    }

    [Fact]
    public void Frame_RoundTrips()
    {
        const ulong networkId = 0x1122334455667788UL;
        var frame = new byte[] { 1, 2, 3, 4 };

        var payload = ZeroTierFrameCodec.EncodeFramePayload(networkId, ZeroTierFrameCodec.EtherTypeIpv4, frame);

        Assert.True(ZeroTierFrameCodec.TryParseFramePayload(payload, out var parsedNetworkId, out var etherType, out var parsedFrame));
        Assert.Equal(networkId, parsedNetworkId);
        Assert.Equal(ZeroTierFrameCodec.EtherTypeIpv4, etherType);
        Assert.Equal(frame, parsedFrame.ToArray());
    }

    private static byte[] CreateMinimalSignedCom()
    {
        var com = new byte[1 + 2 + 5 + 96];
        com[0] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(com.AsSpan(1, 2), 0);
        com[3] = 0;
        com[4] = 0;
        com[5] = 0;
        com[6] = 0;
        com[7] = 1;
        return com;
    }
}

