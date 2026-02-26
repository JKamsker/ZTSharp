using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierWhoisClientTests
{
    [Fact]
    public async Task WhoisAsync_ToleratesTrailingMalformedIdentityData()
    {
        var localIdentity = ZeroTierTestIdentities.CreateFastIdentity(0x2222222222);
        var rootIdentity = ZeroTierTestIdentities.CreateFastIdentity(0x1111111111);

        var rootKey = new byte[48];
        ZeroTierC25519.Agree(localIdentity.PrivateKey!, rootIdentity.PublicKey, rootKey);

        await using var rootUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        await using var clientUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);

        var controllerIdentity = ZeroTierTestIdentities.CreateFastIdentity(0xaaaaaaaaaa);
        var otherIdentity = ZeroTierTestIdentities.CreateFastIdentity(0xbbbbbbbbbb);

        const byte rootProtocolVersion = 12;

        var serverTask = RunWhoisOkServerOnceAsync(
            rootUdp,
            rootIdentity.NodeId,
            rootKey,
            rootProtocolVersion,
            expectedLocalNodeId: localIdentity.NodeId,
            identities: new[] { otherIdentity, controllerIdentity },
            trailingGarbage: new byte[] { 1, 2, 3 });

        var resolved = await ZeroTierWhoisClient.WhoisAsync(
            clientUdp,
            rootNodeId: rootIdentity.NodeId,
            rootEndpoint: rootUdp.LocalEndpoint,
            rootKey: rootKey,
            rootProtocolVersion: rootProtocolVersion,
            localNodeId: localIdentity.NodeId,
            controllerNodeId: controllerIdentity.NodeId,
            timeout: TimeSpan.FromSeconds(2),
            cancellationToken: CancellationToken.None);

        Assert.Equal(controllerIdentity.NodeId, resolved.NodeId);

        await serverTask;
    }

    private static async Task RunWhoisOkServerOnceAsync(
        ZeroTierUdpTransport rootUdp,
        NodeId rootNodeId,
        byte[] rootKey,
        byte rootProtocolVersion,
        NodeId expectedLocalNodeId,
        ZeroTierIdentity[] identities,
        byte[] trailingGarbage)
    {
        var request = await rootUdp.ReceiveAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var requestPacketBytes = request.Payload;
        Assert.True(ZeroTierPacketCodec.TryDecode(requestPacketBytes, out var decoded));
        Assert.Equal(rootNodeId, decoded.Header.Destination);
        Assert.Equal(expectedLocalNodeId, decoded.Header.Source);

        var inRePacketId = decoded.Header.PacketId;

        var payloadBytes = BuildWhoisOkPayload(inRePacketId, identities, trailingGarbage);
        var okHeader = new ZeroTierPacketHeader(
            PacketId: 2,
            Destination: expectedLocalNodeId,
            Source: rootNodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.Ok);

        var okPacket = ZeroTierPacketCodec.Encode(okHeader, payloadBytes);
        ZeroTierPacketCrypto.Armor(okPacket, ZeroTierPacketCrypto.SelectOutboundKey(rootKey, rootProtocolVersion), encryptPayload: true);

        await rootUdp.SendAsync(request.RemoteEndPoint, okPacket).ConfigureAwait(false);
    }

    private static byte[] BuildWhoisOkPayload(ulong inRePacketId, ZeroTierIdentity[] identities, byte[] trailingGarbage)
    {
        var identityBytes = new List<byte>(identities.Length * 80);
        foreach (var identity in identities)
        {
            identityBytes.AddRange(ZeroTierIdentityCodec.Serialize(identity));
        }

        var payload = new byte[1 + 8 + identityBytes.Count + trailingGarbage.Length];
        payload[0] = (byte)ZeroTierVerb.Whois;
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(1, 8), inRePacketId);
        identityBytes.CopyTo(payload, 1 + 8);
        trailingGarbage.CopyTo(payload.AsSpan(1 + 8 + identityBytes.Count));
        return payload;
    }
}
