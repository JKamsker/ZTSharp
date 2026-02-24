using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using JKamsker.LibZt.ZeroTier.Internal;
using JKamsker.LibZt.ZeroTier.Protocol;
using JKamsker.LibZt.ZeroTier.Transport;
using Org.BouncyCastle.Crypto.Parameters;

namespace JKamsker.LibZt.Tests;

public sealed class ZeroTierNetworkConfigClientTests
{
    // From external/libzt/ext/ZeroTierOne/selftest.cpp
    private const string KnownGoodIdentity =
        "8e4df28b72:0:ac3d46abe0c21f3cfe7a6c8d6a85cfcffcb82fbd55af6a4d6350657c68200843fa2e16f9418bbd9702cae365f2af5fb4c420908b803a681d4daef6114d78a2d7:bd8dd6e4ce7022d2f812797a80c6ee8ad180dc4ebf301dec8b06d1be08832bddd63a2f1cfa7b2c504474c75bdc8898ba476ef92e8e2d0509f8441985171ff16e";

    [Fact]
    public async Task FetchAsync_CanObtainAssignedIps_FromSignedChunk()
    {
        Assert.True(ZeroTierIdentity.TryParse(KnownGoodIdentity, out var localIdentity));
        Assert.NotNull(localIdentity.PrivateKey);

        var rootIdentity = CreateFastIdentity(0x0102030405);
        var controllerIdentity = CreateFastIdentity(0x0a0b0c0d0e);

        var networkId = (controllerIdentity.NodeId.Value << 24) | 0x000001UL;

        await using var rootUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: true);
        await using var controllerUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: true);

        var planet = new ZeroTierWorld(
            ZeroTierWorldType.Planet,
            id: 1,
            timestamp: 1,
            updatesMustBeSignedBy: new byte[ZeroTierWorld.C25519PublicKeyLength],
            signature: new byte[ZeroTierWorld.C25519SignatureLength],
            roots: new[]
            {
                new ZeroTierWorldRoot(rootIdentity, new[] { rootUdp.LocalEndpoint })
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var rootTask = RunRootAsync(rootUdp, rootIdentity, controllerIdentity, controllerUdp.LocalEndpoint, cts.Token);
        var controllerTask = RunControllerAsync(controllerUdp, controllerIdentity, localIdentity, cts.Token);

        var result = await ZeroTierNetworkConfigClient.FetchAsync(
            localIdentity,
            planet,
            networkId,
            timeout: TimeSpan.FromSeconds(2),
            cancellationToken: CancellationToken.None);

        Assert.Equal(controllerIdentity.NodeId, result.ControllerIdentity.NodeId);
        Assert.Contains(IPAddress.Parse("10.121.15.99"), result.ManagedIps);

        cts.Cancel();
        await Task.WhenAll(rootTask, controllerTask);
    }

    private static async Task RunRootAsync(
        ZeroTierUdpTransport rootUdp,
        ZeroTierIdentity rootIdentity,
        ZeroTierIdentity controllerIdentity,
        IPEndPoint controllerEndpoint,
        CancellationToken cancellationToken)
    {
        Assert.True(ZeroTierIdentity.TryParse(KnownGoodIdentity, out var localIdentity));
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

            var packet = datagram.Payload.ToArray();
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

                    var okPayload = BuildHelloOkPayload(decoded.Header.PacketId, helloTimestamp, localEndpoint);
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

    private static async Task RunControllerAsync(
        ZeroTierUdpTransport controllerUdp,
        ZeroTierIdentity controllerIdentity,
        ZeroTierIdentity localIdentity,
        CancellationToken cancellationToken)
    {
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

            var packet = datagram.Payload.ToArray();
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

                var helloOkPayload = BuildHelloOkPayload(decoded.Header.PacketId, helloTimestamp, datagram.RemoteEndPoint);
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

            var dictBytes = BuildDictionaryWithStaticIp(IPAddress.Parse("10.121.15.99"), bits: 24);

            var chunkPayload = BuildSignedConfigChunkPayload(networkId, dictBytes, controllerIdentity.PrivateKey!);

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

    private static byte[] BuildHelloOkPayload(ulong helloPacketId, ulong helloTimestamp, IPEndPoint surface)
    {
        var surfaceLength = ZeroTierInetAddressCodec.GetSerializedLength(surface);
        var payload = new byte[1 + 8 + 8 + 1 + 1 + 1 + 2 + surfaceLength];
        payload[0] = (byte)ZeroTierVerb.Hello;
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(1, 8), helloPacketId);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(9, 8), helloTimestamp);
        payload[17] = 11;
        payload[18] = 1;
        payload[19] = 12;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(20, 2), 0);
        _ = ZeroTierInetAddressCodec.Serialize(surface, payload.AsSpan(22));
        return payload;
    }

    private static byte[] BuildDictionaryWithStaticIp(IPAddress address, int bits)
    {
        var endpoint = new IPEndPoint(address, bits);
        var inetLen = ZeroTierInetAddressCodec.GetSerializedLength(endpoint);
        var inet = new byte[inetLen];
        _ = ZeroTierInetAddressCodec.Serialize(endpoint, inet);

        var escaped = EscapeDictionaryValue(inet);

        var dict = new byte[2 + escaped.Length + 1];
        dict[0] = (byte)'I';
        dict[1] = (byte)'=';
        escaped.CopyTo(dict.AsSpan(2));
        dict[^1] = (byte)'\n';
        return dict;
    }

    private static byte[] BuildSignedConfigChunkPayload(ulong networkId, byte[] dictionaryBytes, byte[] controllerPrivateKey)
    {
        var flagsLength = 1;
        var updateIdLength = 8;
        var totalLengthLength = 4;
        var chunkIndexLength = 4;

        var chunkLen = (ushort)dictionaryBytes.Length;
        var signatureMessageLength = 8 + 2 + chunkLen + flagsLength + updateIdLength + totalLengthLength + chunkIndexLength;
        var signatureMessage = new byte[signatureMessageLength];

        var p = 0;
        BinaryPrimitives.WriteUInt64BigEndian(signatureMessage.AsSpan(p, 8), networkId);
        p += 8;
        BinaryPrimitives.WriteUInt16BigEndian(signatureMessage.AsSpan(p, 2), chunkLen);
        p += 2;
        dictionaryBytes.CopyTo(signatureMessage.AsSpan(p));
        p += dictionaryBytes.Length;

        signatureMessage[p++] = 0; // flags
        BinaryPrimitives.WriteUInt64BigEndian(signatureMessage.AsSpan(p, 8), 7);
        p += 8;
        BinaryPrimitives.WriteUInt32BigEndian(signatureMessage.AsSpan(p, 4), (uint)dictionaryBytes.Length);
        p += 4;
        BinaryPrimitives.WriteUInt32BigEndian(signatureMessage.AsSpan(p, 4), 0);
        p += 4;

        if (p != signatureMessage.Length)
        {
            throw new InvalidOperationException("Signature message size mismatch.");
        }

        var signature = ZeroTierC25519.Sign(controllerPrivateKey, signatureMessage);

        var payload = new byte[signatureMessageLength + 1 + 2 + signature.Length];
        signatureMessage.CopyTo(payload.AsSpan(0, signatureMessageLength));
        payload[signatureMessageLength] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(signatureMessageLength + 1, 2), (ushort)signature.Length);
        signature.CopyTo(payload.AsSpan(signatureMessageLength + 3));
        return payload;
    }

    private static byte[] EscapeDictionaryValue(ReadOnlySpan<byte> value)
    {
        var output = new List<byte>(value.Length * 2);
        foreach (var b in value)
        {
            switch (b)
            {
                case 0:
                    output.Add((byte)'\\');
                    output.Add((byte)'0');
                    break;
                case 13:
                    output.Add((byte)'\\');
                    output.Add((byte)'r');
                    break;
                case 10:
                    output.Add((byte)'\\');
                    output.Add((byte)'n');
                    break;
                case (byte)'\\':
                    output.Add((byte)'\\');
                    output.Add((byte)'\\');
                    break;
                case (byte)'=':
                    output.Add((byte)'\\');
                    output.Add((byte)'e');
                    break;
                default:
                    output.Add(b);
                    break;
            }
        }

        return output.ToArray();
    }

    private static ZeroTierIdentity CreateFastIdentity(ulong nodeId)
    {
        var privateKey = new byte[ZeroTierIdentity.PrivateKeyLength];
        RandomNumberGenerator.Fill(privateKey);

        var publicKey = new byte[ZeroTierIdentity.PublicKeyLength];

        var xPriv = new X25519PrivateKeyParameters(privateKey, 0);
        var xPub = xPriv.GeneratePublicKey().GetEncoded();
        Buffer.BlockCopy(xPub, 0, publicKey, 0, 32);

        var edPriv = new Ed25519PrivateKeyParameters(privateKey, 32);
        var edPub = edPriv.GeneratePublicKey().GetEncoded();
        Buffer.BlockCopy(edPub, 0, publicKey, 32, 32);

        return new ZeroTierIdentity(new NodeId(nodeId), publicKey, privateKey);
    }
}
