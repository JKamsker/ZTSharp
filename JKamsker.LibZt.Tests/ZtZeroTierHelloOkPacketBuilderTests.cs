using System.Buffers.Binary;
using System.Net;
using JKamsker.LibZt.ZeroTier.Internal;
using JKamsker.LibZt.ZeroTier.Protocol;

namespace JKamsker.LibZt.Tests;

public sealed class ZtZeroTierHelloOkPacketBuilderTests
{
    // From external/libzt/ext/ZeroTierOne/selftest.cpp
    private const string KnownGoodIdentity =
        "8e4df28b72:0:ac3d46abe0c21f3cfe7a6c8d6a85cfcffcb82fbd55af6a4d6350657c68200843fa2e16f9418bbd9702cae365f2af5fb4c420908b803a681d4daef6114d78a2d7:bd8dd6e4ce7022d2f812797a80c6ee8ad180dc4ebf301dec8b06d1be08832bddd63a2f1cfa7b2c504474c75bdc8898ba476ef92e8e2d0509f8441985171ff16e";

    [Fact]
    public void BuildPacket_EncodesOkHello()
    {
        Assert.True(ZtZeroTierIdentity.TryParse(KnownGoodIdentity, out var identity));
        Assert.NotNull(identity.PrivateKey);

        var sharedKey = new byte[48];
        ZtZeroTierC25519.Agree(identity.PrivateKey!, identity.PublicKey, sharedKey);

        var destination = new ZtNodeId(0x0102030405);
        var source = identity.NodeId;
        const ulong inRePacketId = 0x1122334455667788;
        const ulong timestampEcho = 123456789;
        var surface = new IPEndPoint(IPAddress.Loopback, 9993);

        var packetBytes = ZtZeroTierHelloOkPacketBuilder.BuildPacket(
            packetId: 2,
            destination,
            source,
            inRePacketId,
            helloTimestampEcho: timestampEcho,
            externalSurfaceAddress: surface,
            sharedKey: sharedKey);

        Assert.True(ZtZeroTierPacketCodec.TryDecode(packetBytes, out var decoded));
        Assert.Equal(destination, decoded.Header.Destination);
        Assert.Equal(source, decoded.Header.Source);

        Assert.True(ZtZeroTierPacketCrypto.Dearmor(packetBytes, sharedKey));
        Assert.Equal(ZtZeroTierVerb.Ok, (ZtZeroTierVerb)(packetBytes[27] & 0x1F));

        var payload = packetBytes.AsSpan(ZtZeroTierPacketHeader.Length);

        Assert.Equal(ZtZeroTierVerb.Hello, (ZtZeroTierVerb)(payload[0] & 0x1F));
        Assert.Equal(inRePacketId, BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(1, 8)));
        Assert.Equal(timestampEcho, BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(9, 8)));
        Assert.Equal(ZtZeroTierHelloClient.AdvertisedProtocolVersion, payload[17]);
        Assert.Equal(ZtZeroTierHelloClient.AdvertisedMajorVersion, payload[18]);
        Assert.Equal(ZtZeroTierHelloClient.AdvertisedMinorVersion, payload[19]);
        Assert.Equal(ZtZeroTierHelloClient.AdvertisedRevision, BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(20, 2)));

        Assert.True(ZtZeroTierInetAddressCodec.TryDeserialize(payload.Slice(22), out var parsedSurface, out var read));
        Assert.Equal(surface, parsedSurface);
        Assert.Equal(payload.Length - 22, read);
    }
}

