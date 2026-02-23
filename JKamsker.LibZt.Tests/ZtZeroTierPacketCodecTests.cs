using JKamsker.LibZt.ZeroTier.Protocol;

namespace JKamsker.LibZt.Tests;

public sealed class ZtZeroTierPacketCodecTests
{
    [Fact]
    public void CanEncodeAndDecodePacket()
    {
        var header = new ZtZeroTierPacketHeader(
            PacketId: 0x0102030405060708UL,
            Destination: new ZtNodeId(0x1122334455UL),
            Source: new ZtNodeId(0x66778899AAUL),
            Flags: (byte)(ZtZeroTierPacketHeader.FlagFragmented | (3 << 3) | 5),
            Mac: 0x0A0B0C0D0E0F1011UL,
            VerbRaw: (byte)((byte)ZtZeroTierVerb.Hello | ZtZeroTierPacketHeader.VerbFlagCompressed));

        var payload = "hello-payload"u8.ToArray();

        var encoded = ZtZeroTierPacketCodec.Encode(header, payload);

        Assert.Equal(ZtZeroTierPacketHeader.Length + payload.Length, encoded.Length);
        Assert.Equal(0x01, encoded[0]);
        Assert.Equal(0x11, encoded[8]);
        Assert.Equal(0x66, encoded[13]);
        Assert.Equal(0x0A, encoded[19]);

        Assert.True(ZtZeroTierPacketCodec.TryDecode(encoded, out var decoded));
        Assert.Equal(header, decoded.Header);
        Assert.True(decoded.Payload.Span.SequenceEqual(payload));

        Assert.True(decoded.Header.IsFragmented);
        Assert.True(decoded.Header.IsCompressed);
        Assert.Equal((byte)5, decoded.Header.HopCount);
        Assert.Equal((byte)3, decoded.Header.CipherSuite);
        Assert.Equal(ZtZeroTierVerb.Hello, decoded.Header.Verb);
    }

    [Fact]
    public void DecodeRejectsShortPackets()
    {
        Assert.False(ZtZeroTierPacketCodec.TryDecode(new byte[ZtZeroTierPacketHeader.Length - 1], out _));
    }
}

