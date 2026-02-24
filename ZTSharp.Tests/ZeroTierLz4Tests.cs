using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.Tests;

public sealed class ZeroTierLz4Tests
{
    [Fact]
    public void Decompress_LiteralsOnly_Works()
    {
        // token: litlen=3, matchlen=0 => 0x30, then "abc"
        ReadOnlySpan<byte> compressed = [(byte)0x30, (byte)'a', (byte)'b', (byte)'c'];
        Span<byte> output = stackalloc byte[16];

        Assert.True(ZeroTierLz4.TryDecompress(compressed, output, out var written));
        Assert.Equal(3, written);
        Assert.Equal("abc", System.Text.Encoding.ASCII.GetString(output.Slice(0, written)));
    }

    [Fact]
    public void Decompress_WithMatch_Works()
    {
        // Produces "abcdabcd":
        // token: litlen=4 (0x4), matchlen=0 => 0x40
        // literals: "abcd"
        // offset: 4
        ReadOnlySpan<byte> compressed = [(byte)0x40, (byte)'a', (byte)'b', (byte)'c', (byte)'d', 0x04, 0x00];
        Span<byte> output = stackalloc byte[16];

        Assert.True(ZeroTierLz4.TryDecompress(compressed, output, out var written));
        Assert.Equal(8, written);
        Assert.Equal("abcdabcd", System.Text.Encoding.ASCII.GetString(output.Slice(0, written)));
    }

    [Fact]
    public void PacketCompression_UncompressesPayload_AndClearsFlag()
    {
        // Build a fake packet: header (28 bytes), compressed payload is "abc".
        var packet = new byte[28 + 4];
        packet[27] = (byte)(ZeroTierPacketHeader.VerbFlagCompressed | (byte)ZeroTierVerb.Nop);
        packet[28] = 0x30;
        packet[29] = (byte)'a';
        packet[30] = (byte)'b';
        packet[31] = (byte)'c';

        Assert.True(ZeroTierPacketCompression.TryUncompress(packet, out var uncompressed));
        Assert.Equal(28 + 3, uncompressed.Length);
        Assert.Equal((byte)ZeroTierVerb.Nop, (byte)(uncompressed[27] & 0x1F));
        Assert.Equal(0, uncompressed[27] & ZeroTierPacketHeader.VerbFlagCompressed);
        Assert.Equal("abc", System.Text.Encoding.ASCII.GetString(uncompressed.AsSpan(28)));
    }
}

