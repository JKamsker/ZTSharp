using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierDataplaneRuntimeDirectPathTests
{
    [Fact]
    public async Task DataplaneRuntime_HandlesRendezvous_AndSendsHolePunch()
    {
        await using var udp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        await using var rootUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        await using var punchReceiver = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);

        var localIdentity = ZeroTierTestIdentities.CreateFastIdentity(0x2222222222);
        var rootNodeId = new NodeId(0x1111111111);
        var peerNodeId = new NodeId(0x3333333333);

        var rootKey = new byte[48];
        RandomNumberGenerator.Fill(rootKey);

        await using var runtime = new ZeroTierDataplaneRuntime(
            udp,
            rootNodeId: rootNodeId,
            rootEndpoint: TestUdpEndpoints.ToLoopback(rootUdp.LocalEndpoint),
            rootKey: rootKey,
            rootProtocolVersion: 12,
            localIdentity: localIdentity,
            networkId: 0x9ad07d01093a69e3UL,
            localManagedIpsV4: new[] { IPAddress.Parse("10.0.0.1") },
            localManagedIpsV6: Array.Empty<IPAddress>(),
            inlineCom: Array.Empty<byte>());

        var runtimeEndpoint = TestUdpEndpoints.ToLoopback(runtime.LocalUdp);
        var rendezvousPayload = BuildRendezvousPayload(peerNodeId, TestUdpEndpoints.ToLoopback(punchReceiver.LocalEndpoint));
        var packet = ZeroTierPacketCodec.Encode(
            new ZeroTierPacketHeader(
                PacketId: 1,
                Destination: localIdentity.NodeId,
                Source: rootNodeId,
                Flags: 0,
                Mac: 0,
                VerbRaw: (byte)ZeroTierVerb.Rendezvous),
            rendezvousPayload);

        ZeroTierPacketCrypto.Armor(packet, ZeroTierPacketCrypto.SelectOutboundKey(rootKey, remoteProtocolVersion: 12), encryptPayload: true);

        await rootUdp.SendAsync(runtimeEndpoint, packet);

        var holePunch = await punchReceiver.ReceiveAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(4, holePunch.Payload.Length);
    }

    [Fact]
    public async Task DataplaneRuntime_HandlesPushDirectPaths_AndSendsHolePunch()
    {
        await using var udp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        await using var rootUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        await using var punchReceiver = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);

        var localIdentity = ZeroTierTestIdentities.CreateFastIdentity(0x2222222222);
        Assert.True(ZeroTierIdentity.TryParse(ZeroTierTestIdentities.KnownGoodIdentity, out var peerIdentity));
        Assert.True(peerIdentity.HasPrivateKey);
        Assert.True(peerIdentity.LocallyValidate());

        var rootNodeId = new NodeId(0x1111111111);
        var rootKey = new byte[48];
        RandomNumberGenerator.Fill(rootKey);

        await using var runtime = new ZeroTierDataplaneRuntime(
            udp,
            rootNodeId: rootNodeId,
            rootEndpoint: TestUdpEndpoints.ToLoopback(rootUdp.LocalEndpoint),
            rootKey: rootKey,
            rootProtocolVersion: 12,
            localIdentity: localIdentity,
            networkId: 0x9ad07d01093a69e3UL,
            localManagedIpsV4: new[] { IPAddress.Parse("10.0.0.1") },
            localManagedIpsV6: Array.Empty<IPAddress>(),
            inlineCom: Array.Empty<byte>());

        var runtimeEndpoint = TestUdpEndpoints.ToLoopback(runtime.LocalUdp);

        var sharedKey = new byte[48];
        ZeroTierC25519.Agree(peerIdentity.PrivateKey!, localIdentity.PublicKey, sharedKey);

        var planet = new ZeroTierWorld(
            ZeroTierWorldType.Planet,
            id: 1,
            timestamp: 1,
            updatesMustBeSignedBy: new byte[ZeroTierWorld.C25519PublicKeyLength],
            signature: new byte[ZeroTierWorld.C25519SignatureLength],
            roots: Array.Empty<ZeroTierWorldRoot>());

        var helloPacket = ZeroTierHelloPacketBuilder.BuildPacket(
            peerIdentity,
            destination: localIdentity.NodeId,
            physicalDestination: runtimeEndpoint,
            planet,
            timestamp: (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            sharedKey,
            advertisedProtocolVersion: ZeroTierHelloClient.AdvertisedProtocolVersion,
            advertisedMajorVersion: ZeroTierHelloClient.AdvertisedMajorVersion,
            advertisedMinorVersion: ZeroTierHelloClient.AdvertisedMinorVersion,
            advertisedRevision: ZeroTierHelloClient.AdvertisedRevision,
            out _);

        await rootUdp.SendAsync(runtimeEndpoint, helloPacket);
        _ = await rootUdp.ReceiveAsync(TimeSpan.FromSeconds(2));

        var pushPayload = BuildPushDirectPathsPayload(TestUdpEndpoints.ToLoopback(punchReceiver.LocalEndpoint));
        var pushPacket = ZeroTierPacketCodec.Encode(
            new ZeroTierPacketHeader(
                PacketId: 2,
                Destination: localIdentity.NodeId,
                Source: peerIdentity.NodeId,
                Flags: 0,
                Mac: 0,
                VerbRaw: (byte)ZeroTierVerb.PushDirectPaths),
            pushPayload);

        ZeroTierPacketCrypto.Armor(pushPacket, ZeroTierPacketCrypto.SelectOutboundKey(sharedKey, remoteProtocolVersion: 12), encryptPayload: true);

        await rootUdp.SendAsync(runtimeEndpoint, pushPacket);

        var holePunch = await punchReceiver.ReceiveAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(4, holePunch.Payload.Length);
    }

    private static byte[] BuildRendezvousPayload(NodeId with, IPEndPoint endpoint)
    {
        var addressBytes = endpoint.Address.GetAddressBytes();
        var payload = new byte[1 + 5 + 2 + 1 + addressBytes.Length];

        payload[0] = 0;
        ZeroTierBinaryPrimitives.WriteUInt40BigEndian(payload.AsSpan(1, 5), with.Value);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1 + 5, 2), (ushort)endpoint.Port);
        payload[1 + 5 + 2] = (byte)addressBytes.Length;
        addressBytes.CopyTo(payload.AsSpan(1 + 5 + 2 + 1));

        return payload;
    }

    private static byte[] BuildPushDirectPathsPayload(IPEndPoint endpoint)
    {
        if (endpoint.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new ArgumentOutOfRangeException(nameof(endpoint), "Test helper supports IPv4 only.");
        }

        var addressBytes = endpoint.Address.GetAddressBytes();
        if (addressBytes.Length != 4)
        {
            throw new ArgumentOutOfRangeException(nameof(endpoint), "Invalid IPv4 address bytes.");
        }

        var payload = new byte[2 + 1 + 2 + 1 + 1 + 6];
        var span = payload.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(0, 2), 1);

        var ptr = 2;
        span[ptr++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(ptr, 2), 0);
        ptr += 2;

        span[ptr++] = 4;
        span[ptr++] = 6;

        addressBytes.CopyTo(span.Slice(ptr, 4));
        ptr += 4;
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(ptr, 2), (ushort)endpoint.Port);

        return payload;
    }
}
