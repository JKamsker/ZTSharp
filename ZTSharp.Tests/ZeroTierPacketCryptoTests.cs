using System.Security.Cryptography;
using ZTSharp.ZeroTier.Protocol;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;

namespace ZTSharp.Tests;

public sealed class ZeroTierPacketCryptoTests
{
    private static readonly byte[] S2012Tv0Key =
    [
        0x0f, 0x62, 0xb5, 0x08, 0x5b, 0xae, 0x01, 0x54, 0xa7, 0xfa, 0x4d, 0xa0, 0xf3, 0x46, 0x99, 0xec,
        0x3f, 0x92, 0xe5, 0x38, 0x8b, 0xde, 0x31, 0x84, 0xd7, 0x2a, 0x7d, 0xd0, 0x23, 0x76, 0xc9, 0x1c
    ];

    private static readonly byte[] S2012Tv0Iv = [0x28, 0x8f, 0xf6, 0x5d, 0xc4, 0x2b, 0x92, 0xf9];

    private static readonly byte[] S2012Tv0Keystream =
    [
        0x99, 0xDB, 0x33, 0xAD, 0x11, 0xCE, 0x0C, 0xCB, 0x3B, 0xFD, 0xBF, 0x8D, 0x0C, 0x18, 0x16, 0x04,
        0x52, 0xD0, 0x14, 0xCD, 0xE9, 0x89, 0xB4, 0xC4, 0x11, 0xA5, 0x59, 0xFF, 0x7C, 0x20, 0xA1, 0x69,
        0xE6, 0xDC, 0x99, 0x09, 0xD8, 0x16, 0xBE, 0xCE, 0xDC, 0x40, 0x63, 0xCE, 0x07, 0xCE, 0xA8, 0x28,
        0xF4, 0x4B, 0xF9, 0xB6, 0xC9, 0xA0, 0xA0, 0xB2, 0x00, 0xE1, 0xB5, 0x2A, 0xF4, 0x18, 0x59, 0xC5
    ];

    private static readonly byte[] Poly1305Tv0Key =
    [
        0x74, 0x68, 0x69, 0x73, 0x20, 0x69, 0x73, 0x20, 0x33, 0x32, 0x2d, 0x62, 0x79, 0x74, 0x65, 0x20,
        0x6b, 0x65, 0x79, 0x20, 0x66, 0x6f, 0x72, 0x20, 0x50, 0x6f, 0x6c, 0x79, 0x31, 0x33, 0x30, 0x35
    ];

    private static readonly byte[] Poly1305Tv0Input = new byte[32];

    private static readonly byte[] Poly1305Tv0Tag =
    [
        0x49, 0xec, 0x78, 0x09, 0x0e, 0x48, 0x1e, 0xc6, 0xc2, 0x6b, 0x33, 0xb9, 0x1c, 0xcc, 0x03, 0x07
    ];

    private static readonly byte[] Poly1305Tv1Input = "Hello world!"u8.ToArray();

    private static readonly byte[] Poly1305Tv1Tag =
    [
        0xa6, 0xf7, 0x45, 0x00, 0x8f, 0x81, 0xc9, 0x16, 0xa2, 0x0d, 0xcc, 0x74, 0xee, 0xf2, 0xb2, 0xf0
    ];

    private static readonly byte[] C25519Tv0Priv1 =
    [
        0xe5, 0xf3, 0x7b, 0xd4, 0x0e, 0xc9, 0xdc, 0x77, 0x50, 0x86, 0xdc, 0xf4, 0x2e, 0xbc, 0xdb, 0x27,
        0xf0, 0x73, 0xd4, 0x58, 0x73, 0xc4, 0x4b, 0x71, 0x8b, 0x3c, 0xc5, 0x4f, 0xa8, 0x7c, 0xa4, 0x84,
        0xd9, 0x96, 0x23, 0x73, 0xb4, 0x03, 0x16, 0xbf, 0x1e, 0xa1, 0x2d, 0xd8, 0xc4, 0x8a, 0xe7, 0x82,
        0x10, 0xda, 0xc9, 0xe5, 0x45, 0x9b, 0x01, 0xdc, 0x73, 0xa6, 0xc9, 0x17, 0xa8, 0x15, 0x31, 0x6d
    ];

    private static readonly byte[] C25519Tv0Pub2 =
    [
        0x3e, 0x49, 0xa4, 0x0e, 0x3a, 0xaf, 0xa3, 0x07, 0x3d, 0xf7, 0x2a, 0xec, 0x43, 0xb1, 0xd4, 0x09,
        0x1a, 0xcb, 0x8e, 0x92, 0xf9, 0x65, 0x95, 0x04, 0x6d, 0x2d, 0x9b, 0x34, 0xa3, 0xbf, 0x51, 0x00,
        0xe2, 0xee, 0x23, 0xf5, 0x28, 0x0a, 0xa9, 0xb1, 0x57, 0x0b, 0x96, 0x56, 0x62, 0xba, 0x12, 0x94,
        0xaf, 0xc6, 0x5f, 0xb5, 0x61, 0x43, 0x0f, 0xde, 0x0b, 0xab, 0xfa, 0x4f, 0xfe, 0xc5, 0xe7, 0x18
    ];

    private static readonly byte[] C25519Tv0Agreement =
    [
        0xab, 0xce, 0xd2, 0x24, 0xe8, 0x93, 0xb0, 0xe7, 0x72, 0x14, 0xdc, 0xbb, 0x7d, 0x0f, 0xd8, 0x94,
        0x16, 0x9e, 0xb5, 0x7f, 0xd7, 0x19, 0x5f, 0x3e, 0x2d, 0x45, 0xd5, 0xf7, 0x90, 0x0b, 0x3e, 0x05,
        0x18, 0x2e, 0x2b, 0xf4, 0xfa, 0xd4, 0xec, 0x62, 0x4a, 0x4f, 0x48, 0x50, 0xaf, 0x1c, 0xe8, 0x9f,
        0x1a, 0xe1, 0x3d, 0x70, 0x49, 0x00, 0xa7, 0xe3, 0x5b, 0x1e, 0xa1, 0x9b, 0x68, 0x1e, 0xa1, 0x73
    ];

    private static readonly byte[] AesGmacSivKatArmoredPacket =
    [
        0x6a, 0x9c, 0x24, 0x6a, 0x15, 0x94, 0xed, 0xc1, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
        0x99, 0xaa, 0x18, 0xb9, 0x39, 0x13, 0xb3, 0x2d, 0x34, 0x9e, 0xa0, 0x84, 0x65, 0xd3, 0x97, 0x43,
        0x8d, 0xf5, 0xd0, 0xad, 0xd3, 0x2b, 0x70, 0x47
    ];

    [Fact]
    public void Salsa20_12_Matches_Upstream_TestVector()
    {
        Span<byte> keystream = stackalloc byte[64];
        ZeroTierSalsa20.GenerateKeyStream12(S2012Tv0Key, S2012Tv0Iv, keystream);
        Assert.True(keystream.SequenceEqual(S2012Tv0Keystream));
    }

    [Fact]
    public void Poly1305_Matches_Upstream_TestVectors()
    {
        Assert.True(ComputePoly1305(Poly1305Tv0Key, Poly1305Tv0Input).SequenceEqual(Poly1305Tv0Tag));
        Assert.True(ComputePoly1305(Poly1305Tv0Key, Poly1305Tv1Input).SequenceEqual(Poly1305Tv1Tag));
    }

    [Fact]
    public void C25519_Agree_Matches_Upstream_TestVector()
    {
        Span<byte> agreement = stackalloc byte[64];
        ZeroTierC25519.Agree(C25519Tv0Priv1, C25519Tv0Pub2, agreement);
        Assert.True(agreement.SequenceEqual(C25519Tv0Agreement));
    }

    [Fact]
    public void PacketCrypto_Roundtrips_With_And_Without_Encryption()
    {
        var header = new ZeroTierPacketHeader(
            PacketId: 0x0102030405060708UL,
            Destination: new NodeId(0x1122334455UL),
            Source: new NodeId(0x66778899AAUL),
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.Echo);

        var payload = "test-payload"u8.ToArray();
        var plain = ZeroTierPacketCodec.Encode(header, payload);

        // Encrypt + decrypt (Salsa20/12-Poly1305, 32-byte key)
        var key32 = new byte[32];
        RandomNumberGenerator.Fill(key32);

        var encrypted32 = (byte[])plain.Clone();
        ZeroTierPacketCrypto.Armor(encrypted32, key32, encryptPayload: true);
        Assert.Equal(1, (encrypted32[18] & 0x38) >> 3);
        Assert.True(ZeroTierPacketCrypto.Dearmor(encrypted32, key32));
        Assert.True(encrypted32.AsSpan(27).SequenceEqual(plain.AsSpan(27)));

        // Encrypt + decrypt (AES-GMAC-SIV, 48-byte key)
        var key48 = new byte[48];
        RandomNumberGenerator.Fill(key48);

        var encrypted48 = (byte[])plain.Clone();
        ZeroTierPacketCrypto.Armor(encrypted48, key48, encryptPayload: true);
        Assert.Equal(3, (encrypted48[18] & 0x38) >> 3);
        Assert.True(ZeroTierPacketCrypto.Dearmor(encrypted48, key48));
        Assert.True(encrypted48.AsSpan(27).SequenceEqual(plain.AsSpan(27)));

        // MAC-only + verify (cipher suite 0, key length doesn't matter beyond 32 bytes)
        var macOnly = (byte[])plain.Clone();
        ZeroTierPacketCrypto.Armor(macOnly, key48, encryptPayload: false);
        Assert.Equal(0, (macOnly[18] & 0x38) >> 3);
        Assert.True(ZeroTierPacketCrypto.Dearmor(macOnly, key48));
        Assert.True(macOnly.AsSpan(27).SequenceEqual(plain.AsSpan(27)));

        // Wrong key fails (Salsa)
        var wrongKey32 = new byte[32];
        RandomNumberGenerator.Fill(wrongKey32);
        var shouldFail32 = (byte[])plain.Clone();
        ZeroTierPacketCrypto.Armor(shouldFail32, key32, encryptPayload: true);
        Assert.False(ZeroTierPacketCrypto.Dearmor(shouldFail32, wrongKey32));

        // Wrong key fails (AES-GMAC-SIV)
        var wrongKey48 = new byte[48];
        RandomNumberGenerator.Fill(wrongKey48);
        var shouldFail48 = (byte[])plain.Clone();
        ZeroTierPacketCrypto.Armor(shouldFail48, key48, encryptPayload: true);
        Assert.False(ZeroTierPacketCrypto.Dearmor(shouldFail48, wrongKey48));
    }

    [Fact]
    public void AesGmacSiv_Armor_Matches_KnownAnswer()
    {
        var header = new ZeroTierPacketHeader(
            PacketId: 0x0102030405060708UL,
            Destination: new NodeId(0x1122334455UL),
            Source: new NodeId(0x66778899AAUL),
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.Echo);

        var payload = "test-payload"u8.ToArray();
        var plain = ZeroTierPacketCodec.Encode(header, payload);

        var key48 = new byte[48];
        for (var i = 0; i < key48.Length; i++)
        {
            key48[i] = (byte)i;
        }

        var armored = (byte[])plain.Clone();
        ZeroTierPacketCrypto.Armor(armored, key48, encryptPayload: true);

        Assert.True(armored.SequenceEqual(AesGmacSivKatArmoredPacket));

        Assert.True(ZeroTierPacketCrypto.Dearmor(armored, key48));
        Assert.True(armored.AsSpan(27).SequenceEqual(plain.AsSpan(27)));
    }

    private static byte[] ComputePoly1305(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        var mac = new Poly1305();
        mac.Init(new KeyParameter(key.ToArray()));
        mac.BlockUpdate(data.ToArray(), 0, data.Length);
        var tag = new byte[16];
        mac.DoFinal(tag, 0);
        return tag;
    }
}
