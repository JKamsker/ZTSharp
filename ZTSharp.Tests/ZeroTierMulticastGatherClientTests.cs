using System.Buffers.Binary;
using System.Net;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierMulticastGatherClientTests
{
    // From external/libzt/ext/ZeroTierOne/selftest.cpp
    private const string KnownGoodIdentity =
        "8e4df28b72:0:ac3d46abe0c21f3cfe7a6c8d6a85cfcffcb82fbd55af6a4d6350657c68200843fa2e16f9418bbd9702cae365f2af5fb4c420908b803a681d4daef6114d78a2d7:bd8dd6e4ce7022d2f812797a80c6ee8ad180dc4ebf301dec8b06d1be08832bddd63a2f1cfa7b2c504474c75bdc8898ba476ef92e8e2d0509f8441985171ff16e";

    [Fact]
    public async Task GatherAsync_IncludesInlineCom_AndReturnsMembers()
    {
        Assert.True(ZeroTierIdentity.TryParse(KnownGoodIdentity, out var localIdentity));
        Assert.NotNull(localIdentity.PrivateKey);

        var rootIdentity = new ZeroTierIdentity(
            new NodeId(0x1122334455),
            (byte[])localIdentity.PublicKey.Clone(),
            (byte[])localIdentity.PrivateKey.Clone());

        await using var rootUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: true);
        var rootEndpoint = rootUdp.LocalEndpoint;

        await using var udp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: true);

        var rootKey = new byte[48];
        ZeroTierC25519.Agree(localIdentity.PrivateKey!, rootIdentity.PublicKey, rootKey);

        const ulong networkId = 0x9ad07d01093a69e3UL;
        var ip = IPAddress.Parse("10.121.15.99");
        var group = ZeroTierMulticastGroup.DeriveForAddressResolution(ip);
        var inlineCom = "com-bytes-for-test"u8.ToArray();

        var remoteNodeId = new NodeId(0xaaaaaaaaaa);

        var serverTask = RunGatherOkServerOnceAsync(
            rootUdp,
            rootIdentity.NodeId,
            localIdentity.NodeId,
            rootKey,
            networkId,
            group,
            expectedInlineCom: inlineCom,
            members: new[] { remoteNodeId });

        var (totalKnown, members) = await ZeroTierMulticastGatherClient.GatherAsync(
            udp,
            rootIdentity.NodeId,
            rootEndpoint,
            rootKey,
            rootProtocolVersion: 12,
            localIdentity.NodeId,
            networkId,
            group,
            gatherLimit: 32,
            inlineCom: inlineCom,
            timeout: TimeSpan.FromSeconds(2),
            cancellationToken: CancellationToken.None);

        Assert.Equal(1u, totalKnown);
        Assert.Single(members);
        Assert.Equal(remoteNodeId, members[0]);

        await serverTask;
    }

    [Fact]
    public async Task GatherAsync_ThrowsOnErrorResponse()
    {
        Assert.True(ZeroTierIdentity.TryParse(KnownGoodIdentity, out var localIdentity));
        Assert.NotNull(localIdentity.PrivateKey);

        var rootIdentity = new ZeroTierIdentity(
            new NodeId(0x1122334455),
            (byte[])localIdentity.PublicKey.Clone(),
            (byte[])localIdentity.PrivateKey.Clone());

        await using var rootUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: true);
        var rootEndpoint = rootUdp.LocalEndpoint;

        await using var udp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: true);

        var rootKey = new byte[48];
        ZeroTierC25519.Agree(localIdentity.PrivateKey!, rootIdentity.PublicKey, rootKey);

        const ulong networkId = 0x9ad07d01093a69e3UL;
        var ip = IPAddress.Parse("10.121.15.99");
        var group = ZeroTierMulticastGroup.DeriveForAddressResolution(ip);

        var serverTask = RunGatherErrorServerOnceAsync(
            rootUdp,
            rootIdentity.NodeId,
            localIdentity.NodeId,
            rootKey,
            errorCode: 0x06,
            networkId: networkId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            _ = await ZeroTierMulticastGatherClient.GatherAsync(
                udp,
                rootIdentity.NodeId,
                rootEndpoint,
                rootKey,
                rootProtocolVersion: 12,
                localIdentity.NodeId,
                networkId,
                group,
                gatherLimit: 32,
                inlineCom: ReadOnlyMemory<byte>.Empty,
                timeout: TimeSpan.FromSeconds(2),
                cancellationToken: CancellationToken.None);
        });

        Assert.Contains("Network membership certificate required", ex.Message, StringComparison.Ordinal);
        Assert.Contains("COM update needed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0x9ad07d01093a69e3", ex.Message, StringComparison.OrdinalIgnoreCase);

        await serverTask;
    }

    private static async Task RunGatherOkServerOnceAsync(
        ZeroTierUdpTransport transport,
        NodeId rootNodeId,
        NodeId localNodeId,
        byte[] rootKey,
        ulong networkId,
        ZeroTierMulticastGroup group,
        byte[] expectedInlineCom,
        NodeId[] members)
    {
        var datagram = await transport.ReceiveAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var packetBytes = datagram.Payload.ToArray();

        Assert.True(ZeroTierPacketCodec.TryDecode(packetBytes, out var decoded));
        Assert.Equal(rootNodeId, decoded.Header.Destination);
        Assert.Equal(localNodeId, decoded.Header.Source);

        Assert.True(ZeroTierPacketCrypto.Dearmor(packetBytes, rootKey));

        var verb = (ZeroTierVerb)(packetBytes[27] & 0x1F);
        Assert.Equal(ZeroTierVerb.MulticastGather, verb);

        var payload = packetBytes.AsSpan(ZeroTierPacketHeader.Length);
        Assert.True(payload.Length >= 23);
        Assert.Equal(networkId, BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(0, 8)));

        var flags = payload[8];
        Assert.True((flags & 0x01) != 0);
        Assert.True(payload.Length >= 23 + expectedInlineCom.Length);
        Assert.True(payload.Slice(23, expectedInlineCom.Length).SequenceEqual(expectedInlineCom));

        var requestPacketId = decoded.Header.PacketId;

        var totalKnown = (uint)members.Length;
        var added = (ushort)members.Length;

        var okVerbHeaderLength = 1 + 8;
        var okPayloadLength = okVerbHeaderLength + 8 + 6 + 4 + 4 + 2 + (members.Length * 5);
        var okPayload = new byte[okPayloadLength];

        okPayload[0] = (byte)ZeroTierVerb.MulticastGather;
        BinaryPrimitives.WriteUInt64BigEndian(okPayload.AsSpan(1, 8), requestPacketId);

        var ptr = okVerbHeaderLength;
        BinaryPrimitives.WriteUInt64BigEndian(okPayload.AsSpan(ptr, 8), networkId);
        ptr += 8;

        group.Mac.CopyTo(okPayload.AsSpan(ptr, 6));
        ptr += 6;

        BinaryPrimitives.WriteUInt32BigEndian(okPayload.AsSpan(ptr, 4), group.Adi);
        ptr += 4;

        BinaryPrimitives.WriteUInt32BigEndian(okPayload.AsSpan(ptr, 4), totalKnown);
        ptr += 4;

        BinaryPrimitives.WriteUInt16BigEndian(okPayload.AsSpan(ptr, 2), added);
        ptr += 2;

        foreach (var member in members)
        {
            var value = member.Value;
            okPayload[ptr++] = (byte)((value >> 32) & 0xFF);
            okPayload[ptr++] = (byte)((value >> 24) & 0xFF);
            okPayload[ptr++] = (byte)((value >> 16) & 0xFF);
            okPayload[ptr++] = (byte)((value >> 8) & 0xFF);
            okPayload[ptr++] = (byte)(value & 0xFF);
        }

        Assert.Equal(okPayloadLength, ptr);

        var okHeader = new ZeroTierPacketHeader(
            PacketId: 2,
            Destination: localNodeId,
            Source: rootNodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.Ok);

        var okPacket = ZeroTierPacketCodec.Encode(okHeader, okPayload);
        ZeroTierPacketCrypto.Armor(okPacket, rootKey, encryptPayload: true);

        await transport.SendAsync(datagram.RemoteEndPoint, okPacket).ConfigureAwait(false);
    }

    private static async Task RunGatherErrorServerOnceAsync(
        ZeroTierUdpTransport transport,
        NodeId rootNodeId,
        NodeId localNodeId,
        byte[] rootKey,
        byte errorCode,
        ulong networkId)
    {
        var datagram = await transport.ReceiveAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var packetBytes = datagram.Payload.ToArray();

        Assert.True(ZeroTierPacketCodec.TryDecode(packetBytes, out var decoded));
        Assert.Equal(rootNodeId, decoded.Header.Destination);
        Assert.Equal(localNodeId, decoded.Header.Source);

        Assert.True(ZeroTierPacketCrypto.Dearmor(packetBytes, rootKey));

        var requestPacketId = decoded.Header.PacketId;

        var payload = new byte[1 + 8 + 1 + 8];
        payload[0] = (byte)ZeroTierVerb.MulticastGather;
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(1, 8), requestPacketId);
        payload[9] = errorCode;
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(10, 8), networkId);

        var header = new ZeroTierPacketHeader(
            PacketId: 3,
            Destination: localNodeId,
            Source: rootNodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.Error);

        var errorPacket = ZeroTierPacketCodec.Encode(header, payload);
        ZeroTierPacketCrypto.Armor(errorPacket, rootKey, encryptPayload: true);

        await transport.SendAsync(datagram.RemoteEndPoint, errorPacket).ConfigureAwait(false);
    }
}
