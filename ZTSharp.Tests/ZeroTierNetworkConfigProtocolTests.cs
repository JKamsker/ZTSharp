using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierNetworkConfigProtocolTests
{
    [Fact]
    public async Task RequestNetworkConfigAsync_RejectsOverlappingChunks_FailsFast()
    {
        await using var controllerUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        await using var clientUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);

        var controllerIdentity = ZeroTierTestIdentities.CreateFastIdentity(0x1111111111);
        Assert.NotNull(controllerIdentity.PrivateKey);

        var controllerKey = new byte[48];
        RandomNumberGenerator.Fill(controllerKey);

        var keys = new Dictionary<NodeId, byte[]>
        {
            [controllerIdentity.NodeId] = controllerKey
        };

        const byte controllerProtocolVersion = 12;
        const ulong networkId = 1;
        var localNodeId = new NodeId(0x2222222222);

        var serverTask = RunConfigServerOnceAsync(
            controllerUdp,
            controllerIdentity,
            controllerKey,
            controllerProtocolVersion,
            localNodeId,
            networkId,
            configUpdateId: 7,
            totalLength: 8,
            chunks: new[]
            {
                (ChunkIndex: 0u, Data: new byte[] { 1, 2, 3, 4, 5, 6 }),
                (ChunkIndex: 4u, Data: new byte[] { 9, 9, 9, 9 }),
            });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            _ = await ZeroTierNetworkConfigProtocol.RequestNetworkConfigAsync(
                clientUdp,
                keys,
                rootEndpoint: controllerUdp.LocalEndpoint,
                localNodeId: localNodeId,
                controllerIdentity: controllerIdentity,
                controllerProtocolVersion: controllerProtocolVersion,
                networkId: networkId,
                timeout: TimeSpan.FromSeconds(2),
                allowLegacyUnsignedConfig: false,
                cancellationToken: CancellationToken.None);
        });

        await serverTask;
    }

    [Fact]
    public async Task RequestNetworkConfigAsync_RejectsConflictingDuplicateChunkStarts_FailsFast()
    {
        await using var controllerUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        await using var clientUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);

        var controllerIdentity = ZeroTierTestIdentities.CreateFastIdentity(0x1111111111);
        Assert.NotNull(controllerIdentity.PrivateKey);

        var controllerKey = new byte[48];
        RandomNumberGenerator.Fill(controllerKey);

        var keys = new Dictionary<NodeId, byte[]>
        {
            [controllerIdentity.NodeId] = controllerKey
        };

        const byte controllerProtocolVersion = 12;
        const ulong networkId = 1;
        var localNodeId = new NodeId(0x2222222222);

        var serverTask = RunConfigServerOnceAsync(
            controllerUdp,
            controllerIdentity,
            controllerKey,
            controllerProtocolVersion,
            localNodeId,
            networkId,
            configUpdateId: 7,
            totalLength: 6,
            chunks: new[]
            {
                (ChunkIndex: 0u, Data: new byte[] { 1, 2, 3, 4 }),
                (ChunkIndex: 0u, Data: new byte[] { 9, 9, 9 }),
            });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            _ = await ZeroTierNetworkConfigProtocol.RequestNetworkConfigAsync(
                clientUdp,
                keys,
                rootEndpoint: controllerUdp.LocalEndpoint,
                localNodeId: localNodeId,
                controllerIdentity: controllerIdentity,
                controllerProtocolVersion: controllerProtocolVersion,
                networkId: networkId,
                timeout: TimeSpan.FromSeconds(2),
                allowLegacyUnsignedConfig: false,
                cancellationToken: CancellationToken.None);
        });

        await serverTask;
    }

    [Fact]
    public async Task RequestNetworkConfigAsync_RejectsLegacyUnsignedConfig_ByDefault()
    {
        await using var controllerUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        await using var clientUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);

        var controllerIdentity = ZeroTierTestIdentities.CreateFastIdentity(0x1111111111);
        Assert.NotNull(controllerIdentity.PrivateKey);

        var controllerKey = new byte[48];
        RandomNumberGenerator.Fill(controllerKey);

        var keys = new Dictionary<NodeId, byte[]>
        {
            [controllerIdentity.NodeId] = controllerKey
        };

        const byte controllerProtocolVersion = 12;
        const ulong networkId = 1;
        var localNodeId = new NodeId(0x2222222222);

        var serverTask = RunUnsignedConfigServerOnceAsync(
            controllerUdp,
            controllerIdentity.NodeId,
            controllerKey,
            controllerProtocolVersion,
            localNodeId,
            networkId,
            chunkData: new byte[] { 1, 2, 3, 4 });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            _ = await ZeroTierNetworkConfigProtocol.RequestNetworkConfigAsync(
                clientUdp,
                keys,
                rootEndpoint: controllerUdp.LocalEndpoint,
                localNodeId: localNodeId,
                controllerIdentity: controllerIdentity,
                controllerProtocolVersion: controllerProtocolVersion,
                networkId: networkId,
                timeout: TimeSpan.FromSeconds(2),
                allowLegacyUnsignedConfig: false,
                cancellationToken: CancellationToken.None);
        });

        await serverTask;
    }

    [Fact]
    public async Task RequestNetworkConfigAsync_AllowsLegacyUnsignedConfig_WhenEnabled()
    {
        await using var controllerUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        await using var clientUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);

        var controllerIdentity = ZeroTierTestIdentities.CreateFastIdentity(0x1111111111);
        Assert.NotNull(controllerIdentity.PrivateKey);

        var controllerKey = new byte[48];
        RandomNumberGenerator.Fill(controllerKey);

        var keys = new Dictionary<NodeId, byte[]>
        {
            [controllerIdentity.NodeId] = controllerKey
        };

        const byte controllerProtocolVersion = 12;
        const ulong networkId = 1;
        var localNodeId = new NodeId(0x2222222222);

        var chunkData = new byte[] { 1, 2, 3, 4 };
        var serverTask = RunUnsignedConfigServerOnceAsync(
            controllerUdp,
            controllerIdentity.NodeId,
            controllerKey,
            controllerProtocolVersion,
            localNodeId,
            networkId,
            chunkData);

        var (dictionary, _) = await ZeroTierNetworkConfigProtocol.RequestNetworkConfigAsync(
            clientUdp,
            keys,
            rootEndpoint: controllerUdp.LocalEndpoint,
            localNodeId: localNodeId,
            controllerIdentity: controllerIdentity,
            controllerProtocolVersion: controllerProtocolVersion,
            networkId: networkId,
            timeout: TimeSpan.FromSeconds(2),
            allowLegacyUnsignedConfig: true,
            cancellationToken: CancellationToken.None);

        Assert.True(dictionary.SequenceEqual(chunkData));

        await serverTask;
    }

    private static async Task RunConfigServerOnceAsync(
        ZeroTierUdpTransport controllerUdp,
        ZeroTierIdentity controllerIdentity,
        byte[] controllerKey,
        byte controllerProtocolVersion,
        NodeId expectedLocalNodeId,
        ulong networkId,
        ulong configUpdateId,
        uint totalLength,
        (uint ChunkIndex, byte[] Data)[] chunks)
    {
        var datagram = await controllerUdp.ReceiveAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        Assert.True(ZeroTierPacketCodec.TryDecode(datagram.Payload, out var decoded));
        Assert.Equal(expectedLocalNodeId, decoded.Header.Source);
        Assert.Equal(controllerIdentity.NodeId, decoded.Header.Destination);

        foreach (var (chunkIndex, data) in chunks)
        {
            var packet = BuildSignedNetworkConfigPacket(
                controllerNodeId: controllerIdentity.NodeId,
                localNodeId: expectedLocalNodeId,
                controllerKey: controllerKey,
                controllerProtocolVersion: controllerProtocolVersion,
                networkId: networkId,
                configUpdateId: configUpdateId,
                totalLength: totalLength,
                chunkIndex: chunkIndex,
                chunkData: data,
                controllerPrivateKey: controllerIdentity.PrivateKey!);

            await controllerUdp.SendAsync(datagram.RemoteEndPoint, packet).ConfigureAwait(false);
        }
    }

    private static async Task RunUnsignedConfigServerOnceAsync(
        ZeroTierUdpTransport controllerUdp,
        NodeId controllerNodeId,
        byte[] controllerKey,
        byte controllerProtocolVersion,
        NodeId expectedLocalNodeId,
        ulong networkId,
        byte[] chunkData)
    {
        var datagram = await controllerUdp.ReceiveAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        Assert.True(ZeroTierPacketCodec.TryDecode(datagram.Payload, out var decoded));
        Assert.Equal(expectedLocalNodeId, decoded.Header.Source);
        Assert.Equal(controllerNodeId, decoded.Header.Destination);

        var payload = new byte[8 + 2 + chunkData.Length];
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(0, 8), networkId);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(8, 2), checked((ushort)chunkData.Length));
        chunkData.CopyTo(payload.AsSpan(10));

        var header = new ZeroTierPacketHeader(
            PacketId: 1,
            Destination: expectedLocalNodeId,
            Source: controllerNodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.NetworkConfig);

        var packet = ZeroTierPacketCodec.Encode(header, payload);
        ZeroTierPacketCrypto.Armor(packet, ZeroTierPacketCrypto.SelectOutboundKey(controllerKey, controllerProtocolVersion), encryptPayload: true);

        await controllerUdp.SendAsync(datagram.RemoteEndPoint, packet).ConfigureAwait(false);
    }

    private static byte[] BuildSignedNetworkConfigPacket(
        NodeId controllerNodeId,
        NodeId localNodeId,
        byte[] controllerKey,
        byte controllerProtocolVersion,
        ulong networkId,
        ulong configUpdateId,
        uint totalLength,
        uint chunkIndex,
        byte[] chunkData,
        byte[] controllerPrivateKey)
    {
        var flags = (byte)0x00;

        var messageLen = 8 + 2 + chunkData.Length + 1 + 8 + 4 + 4;
        var message = new byte[messageLen];
        var span = message.AsSpan();

        BinaryPrimitives.WriteUInt64BigEndian(span.Slice(0, 8), networkId);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(8, 2), checked((ushort)chunkData.Length));
        chunkData.CopyTo(span.Slice(10, chunkData.Length));

        var ptr = 10 + chunkData.Length;
        span[ptr++] = flags;
        BinaryPrimitives.WriteUInt64BigEndian(span.Slice(ptr, 8), configUpdateId);
        ptr += 8;
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(ptr, 4), totalLength);
        ptr += 4;
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(ptr, 4), chunkIndex);

        var signature = ZeroTierC25519.Sign(controllerPrivateKey, message);

        var payload = new byte[messageLen + 1 + 2 + signature.Length];
        message.CopyTo(payload.AsSpan(0, messageLen));
        payload[messageLen] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(messageLen + 1, 2), checked((ushort)signature.Length));
        signature.CopyTo(payload.AsSpan(messageLen + 1 + 2));

        var header = new ZeroTierPacketHeader(
            PacketId: 1,
            Destination: localNodeId,
            Source: controllerNodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.NetworkConfig);

        var packet = ZeroTierPacketCodec.Encode(header, payload);
        ZeroTierPacketCrypto.Armor(packet, ZeroTierPacketCrypto.SelectOutboundKey(controllerKey, controllerProtocolVersion), encryptPayload: true);
        return packet;
    }
}
