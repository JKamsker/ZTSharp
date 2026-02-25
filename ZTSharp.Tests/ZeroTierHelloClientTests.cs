using System.Buffers.Binary;
using System.Net;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierHelloClientTests
{
    // From external/libzt/ext/ZeroTierOne/selftest.cpp
    private const string KnownGoodIdentity =
        "8e4df28b72:0:ac3d46abe0c21f3cfe7a6c8d6a85cfcffcb82fbd55af6a4d6350657c68200843fa2e16f9418bbd9702cae365f2af5fb4c420908b803a681d4daef6114d78a2d7:bd8dd6e4ce7022d2f812797a80c6ee8ad180dc4ebf301dec8b06d1be08832bddd63a2f1cfa7b2c504474c75bdc8898ba476ef92e8e2d0509f8441985171ff16e";

    [Fact]
    public async Task HelloRootsAsync_SendsHello_And_ParsesOk()
    {
        Assert.True(ZeroTierIdentity.TryParse(KnownGoodIdentity, out var localIdentity));
        Assert.NotNull(localIdentity.PrivateKey);

        var rootIdentity = new ZeroTierIdentity(
            new NodeId(0x1122334455),
            (byte[])localIdentity.PublicKey.Clone(),
            (byte[])localIdentity.PrivateKey.Clone());

        await using var rootUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: true);
        var rootEndpoint = rootUdp.LocalEndpoint;

        var planet = new ZeroTierWorld(
            ZeroTierWorldType.Planet,
            id: 1,
            timestamp: 1,
            updatesMustBeSignedBy: new byte[ZeroTierWorld.C25519PublicKeyLength],
            signature: new byte[ZeroTierWorld.C25519SignatureLength],
            roots: new[]
            {
                new ZeroTierWorldRoot(rootIdentity, new[] { rootEndpoint })
            });

        var serverTask = RunHelloServerOnceAsync(rootUdp, rootIdentity, localIdentity);

        var ok = await ZeroTierHelloClient.HelloRootsAsync(
            localIdentity,
            planet,
            timeout: TimeSpan.FromSeconds(2),
            cancellationToken: CancellationToken.None);

        Assert.Equal(rootIdentity.NodeId, ok.RootNodeId);
        Assert.Equal(rootEndpoint, ok.RootEndpoint);
        Assert.Equal(11, ok.RemoteProtocolVersion);
        Assert.Equal(1, ok.RemoteMajorVersion);
        Assert.Equal(12, ok.RemoteMinorVersion);
        Assert.Equal(0, ok.RemoteRevision);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 9993), ok.ExternalSurfaceAddress);

        await serverTask;
    }

    [Fact]
    public async Task HelloAsync_SendsHello_AndCompletesOnOk()
    {
        Assert.True(ZeroTierIdentity.TryParse(KnownGoodIdentity, out var localIdentity));
        Assert.NotNull(localIdentity.PrivateKey);

        var remoteIdentity = new ZeroTierIdentity(
            new NodeId(0x1122334455),
            (byte[])localIdentity.PublicKey.Clone(),
            (byte[])localIdentity.PrivateKey.Clone());

        await using var remoteUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: true);
        var remoteEndpoint = remoteUdp.LocalEndpoint;

        await using var udp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: true);

        var planet = new ZeroTierWorld(
            ZeroTierWorldType.Planet,
            id: 1,
            timestamp: 1,
            updatesMustBeSignedBy: new byte[ZeroTierWorld.C25519PublicKeyLength],
            signature: new byte[ZeroTierWorld.C25519SignatureLength],
            roots: Array.Empty<ZeroTierWorldRoot>());

        var sharedKey = new byte[48];
        ZeroTierC25519.Agree(localIdentity.PrivateKey!, remoteIdentity.PublicKey, sharedKey);

        var serverTask = RunHelloServerOnceAsync(remoteUdp, remoteIdentity, localIdentity);

        var remoteProtocolVersion = await ZeroTierHelloClient.HelloAsync(
            udp,
            localIdentity,
            planet,
            destination: remoteIdentity.NodeId,
            physicalDestination: remoteEndpoint,
            sharedKey: sharedKey,
            timeout: TimeSpan.FromSeconds(2),
            cancellationToken: CancellationToken.None);

        Assert.Equal(11, remoteProtocolVersion);

        await serverTask;
    }

    private static async Task RunHelloServerOnceAsync(
        ZeroTierUdpTransport transport,
        ZeroTierIdentity rootIdentity,
        ZeroTierIdentity localIdentity)
    {
        var helloDatagram = await transport.ReceiveAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var helloPacketBytes = helloDatagram.Payload.ToArray();
        Assert.True(ZeroTierPacketCodec.TryDecode(helloPacketBytes, out var hello));
        Assert.Equal(ZeroTierVerb.Hello, hello.Header.Verb);

        var sharedKey = new byte[48];
        ZeroTierC25519.Agree(rootIdentity.PrivateKey!, localIdentity.PublicKey, sharedKey);

        Assert.True(ZeroTierPacketCrypto.Dearmor(helloPacketBytes, sharedKey));

        var helloTimestamp = BinaryPrimitives.ReadUInt64BigEndian(
            helloPacketBytes.AsSpan(ZeroTierPacketHeader.Length + 5, 8));

        var surface = new IPEndPoint(IPAddress.Loopback, 9993);
        var surfaceLength = ZeroTierInetAddressCodec.GetSerializedLength(surface);

        var okPayloadLength = 1 + 8 + 8 + 1 + 1 + 1 + 2 + surfaceLength;
        var okPayload = new byte[okPayloadLength];
        okPayload[0] = (byte)ZeroTierVerb.Hello;
        BinaryPrimitives.WriteUInt64BigEndian(okPayload.AsSpan(1, 8), hello.Header.PacketId);
        BinaryPrimitives.WriteUInt64BigEndian(okPayload.AsSpan(9, 8), helloTimestamp);
        okPayload[17] = 11;
        okPayload[18] = 1;
        okPayload[19] = 12;
        BinaryPrimitives.WriteUInt16BigEndian(okPayload.AsSpan(20, 2), 0);
        _ = ZeroTierInetAddressCodec.Serialize(surface, okPayload.AsSpan(22));

        var okHeader = new ZeroTierPacketHeader(
            PacketId: 2,
            Destination: localIdentity.NodeId,
            Source: rootIdentity.NodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.Ok);

        var okPacket = ZeroTierPacketCodec.Encode(okHeader, okPayload);
        ZeroTierPacketCrypto.Armor(okPacket, sharedKey, encryptPayload: true);

        await transport.SendAsync(helloDatagram.RemoteEndPoint, okPacket).ConfigureAwait(false);
    }
}
