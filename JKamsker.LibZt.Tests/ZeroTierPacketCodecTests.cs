using JKamsker.LibZt.ZeroTier.Protocol;

namespace JKamsker.LibZt.Tests;

public sealed class ZeroTierPacketCodecTests
{
    [Fact]
    public void CanEncodeAndDecodePacket()
    {
        var header = new ZeroTierPacketHeader(
            PacketId: 0x0102030405060708UL,
            Destination: new NodeId(0x1122334455UL),
            Source: new NodeId(0x66778899AAUL),
            Flags: (byte)(ZeroTierPacketHeader.FlagFragmented | (3 << 3) | 5),
            Mac: 0x0A0B0C0D0E0F1011UL,
            VerbRaw: (byte)((byte)ZeroTierVerb.Hello | ZeroTierPacketHeader.VerbFlagCompressed));

        var payload = "hello-payload"u8.ToArray();

        var encoded = ZeroTierPacketCodec.Encode(header, payload);

        Assert.Equal(ZeroTierPacketHeader.Length + payload.Length, encoded.Length);
        Assert.Equal(0x01, encoded[0]);
        Assert.Equal(0x11, encoded[8]);
        Assert.Equal(0x66, encoded[13]);
        Assert.Equal(0x0A, encoded[19]);

        Assert.True(ZeroTierPacketCodec.TryDecode(encoded, out var decoded));
        Assert.Equal(header, decoded.Header);
        Assert.True(decoded.Payload.Span.SequenceEqual(payload));

        Assert.True(decoded.Header.IsFragmented);
        Assert.True(decoded.Header.IsCompressed);
        Assert.Equal((byte)5, decoded.Header.HopCount);
        Assert.Equal((byte)3, decoded.Header.CipherSuite);
        Assert.Equal(ZeroTierVerb.Hello, decoded.Header.Verb);
    }

    [Fact]
    public void DecodeRejectsShortPackets()
    {
        Assert.False(ZeroTierPacketCodec.TryDecode(new byte[ZeroTierPacketHeader.Length - 1], out _));
    }
}

