using System.Buffers.Binary;
using System.Net;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

internal static class ZeroTierNetworkConfigTestHarness
{
    public static Task RunControllerAsync(
        ZeroTierUdpTransport controllerUdp,
        ZeroTierIdentity controllerIdentity,
        ZeroTierIdentity localIdentity,
        CancellationToken cancellationToken)
        => RunControllerAsync(
            controllerUdp,
            controllerIdentity,
            localIdentity,
            networkId =>
            {
                var dictBytes = ZeroTierNetworkConfigTestPayloads.BuildDictionaryWithStaticIp(IPAddress.Parse("10.121.15.99"), bits: 24);
                return ZeroTierNetworkConfigTestPayloads.BuildSignedConfigChunkPayload(networkId, dictBytes, controllerIdentity.PrivateKey!);
            },
            cancellationToken);

    public static async Task RunRootAsync(
        ZeroTierUdpTransport rootUdp,
        ZeroTierIdentity rootIdentity,
        ZeroTierIdentity controllerIdentity,
        IPEndPoint controllerEndpoint,
        CancellationToken cancellationToken)
    {
        Assert.True(ZeroTierIdentity.TryParse(ZeroTierTestIdentities.KnownGoodIdentity, out var localIdentity));
        Assert.NotNull(localIdentity.PrivateKey);

        var localNodeId = localIdentity.NodeId;

        var rootKey = new byte[48];
        ZeroTierC25519.Agree(rootIdentity.PrivateKey!, localIdentity.PublicKey, rootKey);

        IPEndPoint? localEndpoint = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            ZeroTierUdpDatagram datagram;
            try
            {
                datagram = await rootUdp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var packet = datagram.Payload;
            if (!ZeroTierPacketCodec.TryDecode(packet, out var decoded))
            {
                continue;
            }

            if (decoded.Header.Destination == rootIdentity.NodeId)
            {
                var authPacket = (byte[])packet.Clone();
                if (!ZeroTierPacketCrypto.Dearmor(authPacket, rootKey))
                {
                    continue;
                }

                var verb = (ZeroTierVerb)(authPacket[27] & 0x1F);
                if (verb == ZeroTierVerb.Hello)
                {
                    localEndpoint = datagram.RemoteEndPoint;
                    var helloTimestamp = BinaryPrimitives.ReadUInt64BigEndian(
                        authPacket.AsSpan(ZeroTierPacketHeader.Length + 5, 8));

                    var okPayload = ZeroTierNetworkConfigTestPayloads.BuildHelloOkPayload(decoded.Header.PacketId, helloTimestamp, localEndpoint);
                    var okHeader = new ZeroTierPacketHeader(
                        PacketId: 3,
                        Destination: decoded.Header.Source,
                        Source: rootIdentity.NodeId,
                        Flags: 0,
                        Mac: 0,
                        VerbRaw: (byte)ZeroTierVerb.Ok);

                    var okPacket = ZeroTierPacketCodec.Encode(okHeader, okPayload);
                    ZeroTierPacketCrypto.Armor(okPacket, rootKey, encryptPayload: true);
                    await rootUdp.SendAsync(localEndpoint, okPacket, cancellationToken).ConfigureAwait(false);
                }
                else if (verb == ZeroTierVerb.Whois && localEndpoint is not null)
                {
                    var identityBytes = ZeroTierIdentityCodec.Serialize(controllerIdentity, includePrivate: false);
                    var okPayload = new byte[1 + 8 + identityBytes.Length];
                    okPayload[0] = (byte)ZeroTierVerb.Whois;
                    BinaryPrimitives.WriteUInt64BigEndian(okPayload.AsSpan(1, 8), decoded.Header.PacketId);
                    identityBytes.CopyTo(okPayload.AsSpan(9));

                    var okHeader = new ZeroTierPacketHeader(
                        PacketId: 4,
                        Destination: decoded.Header.Source,
                        Source: rootIdentity.NodeId,
                        Flags: 0,
                        Mac: 0,
                        VerbRaw: (byte)ZeroTierVerb.Ok);

                    var okPacket = ZeroTierPacketCodec.Encode(okHeader, okPayload);
                    ZeroTierPacketCrypto.Armor(okPacket, rootKey, encryptPayload: true);
                    await rootUdp.SendAsync(localEndpoint, okPacket, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            if (decoded.Header.Destination == controllerIdentity.NodeId)
            {
                await rootUdp.SendAsync(controllerEndpoint, datagram.Payload, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (localEndpoint is not null && decoded.Header.Destination == localNodeId)
            {
                await rootUdp.SendAsync(localEndpoint, datagram.Payload, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static async Task RunControllerAsync(
        ZeroTierUdpTransport controllerUdp,
        ZeroTierIdentity controllerIdentity,
        ZeroTierIdentity localIdentity,
        Func<ulong, byte[]> chunkPayloadFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunkPayloadFactory);

        var key = new byte[48];
        ZeroTierC25519.Agree(controllerIdentity.PrivateKey!, localIdentity.PublicKey, key);

        while (!cancellationToken.IsCancellationRequested)
        {
            ZeroTierUdpDatagram datagram;
            try
            {
                datagram = await controllerUdp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var packet = datagram.Payload;
            if (!ZeroTierPacketCodec.TryDecode(packet, out var decoded))
            {
                continue;
            }

            var authPacket = (byte[])packet.Clone();
            if (!ZeroTierPacketCrypto.Dearmor(authPacket, key))
            {
                continue;
            }

            var verb = (ZeroTierVerb)(authPacket[27] & 0x1F);
            if (verb == ZeroTierVerb.Hello)
            {
                var helloTimestamp = BinaryPrimitives.ReadUInt64BigEndian(
                    authPacket.AsSpan(ZeroTierPacketHeader.Length + 5, 8));

                var helloOkPayload = ZeroTierNetworkConfigTestPayloads.BuildHelloOkPayload(decoded.Header.PacketId, helloTimestamp, datagram.RemoteEndPoint);
                var helloOkHeader = new ZeroTierPacketHeader(
                    PacketId: 6,
                    Destination: decoded.Header.Source,
                    Source: controllerIdentity.NodeId,
                    Flags: 0,
                    Mac: 0,
                    VerbRaw: (byte)ZeroTierVerb.Ok);

                var helloOkPacket = ZeroTierPacketCodec.Encode(helloOkHeader, helloOkPayload);
                ZeroTierPacketCrypto.Armor(helloOkPacket, key, encryptPayload: true);
                await controllerUdp.SendAsync(datagram.RemoteEndPoint, helloOkPacket, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (verb != ZeroTierVerb.NetworkConfigRequest)
            {
                continue;
            }

            var networkId = BinaryPrimitives.ReadUInt64BigEndian(authPacket.AsSpan(ZeroTierPacketHeader.Length, 8));

            var chunkPayload = chunkPayloadFactory(networkId);

            var okPayload = new byte[1 + 8 + chunkPayload.Length];
            okPayload[0] = (byte)ZeroTierVerb.NetworkConfigRequest;
            BinaryPrimitives.WriteUInt64BigEndian(okPayload.AsSpan(1, 8), decoded.Header.PacketId);
            chunkPayload.CopyTo(okPayload.AsSpan(9));

            var okHeader = new ZeroTierPacketHeader(
                PacketId: 5,
                Destination: decoded.Header.Source,
                Source: controllerIdentity.NodeId,
                Flags: 0,
                Mac: 0,
                VerbRaw: (byte)ZeroTierVerb.Ok);

            var okPacket = ZeroTierPacketCodec.Encode(okHeader, okPayload);
            ZeroTierPacketCrypto.Armor(okPacket, key, encryptPayload: true);

            await controllerUdp.SendAsync(datagram.RemoteEndPoint, okPacket, cancellationToken).ConfigureAwait(false);
        }
    }

}
