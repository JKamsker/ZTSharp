using System.Buffers.Binary;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.Tests;

public sealed class ZeroTierMulticastGatherCodecTests
{
    [Fact]
    public void EncodeRequestPayload_WithCom_SetsFlagAndAppendsBytes()
    {
        const ulong networkId = 0x1122334455667788UL;
        var group = new ZeroTierMulticastGroup(ZeroTierMac.Broadcast, 0x0a0b0c0d);
        var com = new byte[] { 1, 2, 3 };

        var payload = ZeroTierMulticastGatherCodec.EncodeRequestPayload(networkId, group, gatherLimit: 16, inlineCom: com);

        Assert.Equal(8 + 1 + 6 + 4 + 4 + com.Length, payload.Length);
        Assert.Equal(0x01, payload[8]);
        Assert.Equal(com, payload.AsSpan(payload.Length - com.Length).ToArray());
    }

    [Fact]
    public void TryParseOkPayload_ParsesMembers()
    {
        const ulong networkId = 0x1122334455667788UL;
        var group = new ZeroTierMulticastGroup(ZeroTierMac.Broadcast, 0x0a0b0c0d);

        var payload = new byte[24 + (2 * 5)];
        var span = payload.AsSpan();
        BinaryPrimitives.WriteUInt64BigEndian(span.Slice(0, 8), networkId);
        group.Mac.CopyTo(span.Slice(8, 6));
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(14, 4), group.Adi);
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(18, 4), 5u);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(22, 2), 2);

        span[24] = 0x01;
        span[25] = 0x02;
        span[26] = 0x03;
        span[27] = 0x04;
        span[28] = 0x05;

        span[29] = 0x0a;
        span[30] = 0x0b;
        span[31] = 0x0c;
        span[32] = 0x0d;
        span[33] = 0x0e;

        Assert.True(ZeroTierMulticastGatherCodec.TryParseOkPayload(payload, out var parsedNwid, out var parsedGroup, out var totalKnown, out var members));
        Assert.Equal(networkId, parsedNwid);
        Assert.Equal(group, parsedGroup);
        Assert.Equal(5u, totalKnown);
        Assert.Equal(new[] { new NodeId(0x0102030405UL), new NodeId(0x0a0b0c0d0eUL) }, members);
    }
}

