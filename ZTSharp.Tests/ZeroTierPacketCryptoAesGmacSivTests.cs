using System.Security.Cryptography;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.Tests;

public sealed class ZeroTierPacketCryptoAesGmacSivTests
{
    [Fact]
    public void Dearmor_WhenAuthenticationFails_DoesNotMutatePacketBytes()
    {
        var key = RandomNumberGenerator.GetBytes(ZeroTierPacketCrypto.SymmetricKeyLength);

        var payload = RandomNumberGenerator.GetBytes(64);
        var packet = ZeroTierPacketCodec.Encode(
            new ZeroTierPacketHeader(
                PacketId: 1,
                Destination: new NodeId(0x1111111111),
                Source: new NodeId(0x2222222222),
                Flags: 0,
                Mac: 0,
                VerbRaw: (byte)ZeroTierVerb.Frame),
            payload);

        ZeroTierPacketCrypto.Armor(packet, key, encryptPayload: true);

        var tampered = (byte[])packet.Clone();
        tampered[ZeroTierPacketHeader.IndexVerb + 1] ^= 0x01;

        var before = (byte[])tampered.Clone();
        Assert.False(ZeroTierPacketCrypto.Dearmor(tampered, key));

        Assert.True(before.AsSpan().SequenceEqual(tampered));
    }
}

