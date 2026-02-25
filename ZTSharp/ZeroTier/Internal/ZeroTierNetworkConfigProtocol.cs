using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierNetworkConfigProtocol
{
    private const int IndexVerb = 27;
    private const int IndexPayload = ZeroTierPacketHeader.Length;

    private const int OkIndexInReVerb = ZeroTierPacketHeader.Length;
    private const int OkIndexInRePacketId = OkIndexInReVerb + 1;
    private const int OkIndexPayload = OkIndexInRePacketId + 8;

    public static NodeId GetControllerNodeId(ulong networkId) => new(networkId >> 24);

    public static Dictionary<NodeId, byte[]> BuildRootKeys(ZeroTierIdentity localIdentity, ZeroTierWorld planet)
        => ZeroTierRootKeyDerivation.BuildRootKeys(localIdentity, planet);

    public static async Task<ZeroTierIdentity> WhoisAsync(
        ZeroTierUdpTransport udp,
        NodeId rootNodeId,
        IPEndPoint rootEndpoint,
        byte[] rootKey,
        byte rootProtocolVersion,
        NodeId localNodeId,
        NodeId controllerNodeId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var whoisPayload = new byte[5];
        WriteUInt40(whoisPayload, controllerNodeId.Value);

        var whoisPacketId = GeneratePacketId();
        var whoisHeader = new ZeroTierPacketHeader(
            PacketId: whoisPacketId,
            Destination: rootNodeId,
            Source: localNodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.Whois);

        var whoisPacket = ZeroTierPacketCodec.Encode(whoisHeader, whoisPayload);
        ZeroTierPacketCrypto.Armor(whoisPacket, ZeroTierPacketCrypto.SelectOutboundKey(rootKey, rootProtocolVersion), encryptPayload: true);
        whoisPacketId = BinaryPrimitives.ReadUInt64BigEndian(whoisPacket.AsSpan(0, 8));

        await udp.SendAsync(rootEndpoint, whoisPacket, cancellationToken).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (true)
        {
            (NodeId Source, IPEndPoint RemoteEndPoint, byte[] PacketBytes)? received;
            try
            {
                received = await ReceiveAndDecryptAsync(udp, rootNodeId, rootKey, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for OK(WHOIS) from root after {timeout}.");
            }

            if (received is null)
            {
                continue;
            }

            var packetBytes = received.Value.PacketBytes;
            if ((ZeroTierVerb)(packetBytes[IndexVerb] & 0x1F) != ZeroTierVerb.Ok)
            {
                continue;
            }

            var inReVerb = (ZeroTierVerb)(packetBytes[OkIndexInReVerb] & 0x1F);
            if (inReVerb != ZeroTierVerb.Whois)
            {
                continue;
            }

            var inRePacketId = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(OkIndexInRePacketId, 8));
            if (inRePacketId != whoisPacketId)
            {
                continue;
            }

            var ptr = OkIndexPayload;
            while (ptr < packetBytes.Length)
            {
                var identity = ZeroTierIdentityCodec.Deserialize(packetBytes.AsSpan(ptr), out var bytesRead);
                ptr += bytesRead;
                if (identity.NodeId == controllerNodeId)
                {
                    return identity;
                }
            }
        }
    }

    public static async Task<(byte[] DictionaryBytes, IPAddress[] ManagedIps)> RequestNetworkConfigAsync(
        ZeroTierUdpTransport udp,
        Dictionary<NodeId, byte[]> keys,
        IPEndPoint rootEndpoint,
        NodeId localNodeId,
        ZeroTierIdentity controllerIdentity,
        byte controllerProtocolVersion,
        ulong networkId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var controllerNodeId = controllerIdentity.NodeId;
        if (!keys.TryGetValue(controllerNodeId, out var controllerKey))
        {
            throw new InvalidOperationException("Missing controller key.");
        }

        var metaDataBytes = ZeroTierNetworkConfigRequestMetadata.BuildDictionaryBytes();
        if (metaDataBytes.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException("Network config request metadata dictionary is too large.");
        }

        var reqPayload = new byte[8 + 2 + metaDataBytes.Length + 16];
        BinaryPrimitives.WriteUInt64BigEndian(reqPayload.AsSpan(0, 8), networkId);
        BinaryPrimitives.WriteUInt16BigEndian(reqPayload.AsSpan(8, 2), (ushort)metaDataBytes.Length);
        metaDataBytes.CopyTo(reqPayload.AsSpan(10));

        var ptr = 10 + metaDataBytes.Length;
        BinaryPrimitives.WriteUInt64BigEndian(reqPayload.AsSpan(ptr, 8), 0);
        BinaryPrimitives.WriteUInt64BigEndian(reqPayload.AsSpan(ptr + 8, 8), 0);

        var reqPacketId = GeneratePacketId();
        var reqHeader = new ZeroTierPacketHeader(
            PacketId: reqPacketId,
            Destination: controllerNodeId,
            Source: localNodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.NetworkConfigRequest);

        var reqPacket = ZeroTierPacketCodec.Encode(reqHeader, reqPayload);
        ZeroTierPacketCrypto.Armor(reqPacket, ZeroTierPacketCrypto.SelectOutboundKey(controllerKey, controllerProtocolVersion), encryptPayload: true);
        reqPacketId = BinaryPrimitives.ReadUInt64BigEndian(reqPacket.AsSpan(0, 8));

        await udp.SendAsync(rootEndpoint, reqPacket, cancellationToken).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        byte[]? dictionary = null;
        var receivedLength = 0;
        var totalLength = 0u;
        var updateId = 0UL;
        var receivedOffsets = new HashSet<uint>();

        while (true)
        {
            (NodeId Source, IPEndPoint RemoteEndPoint, byte[] PacketBytes)? received;
            try
            {
                received = await ReceiveAndDecryptAsync(udp, controllerNodeId, controllerKey, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for config chunks after {timeout}.");
            }

            if (received is null)
            {
                continue;
            }

            var packetBytes = received.Value.PacketBytes;

            var verb = (ZeroTierVerb)(packetBytes[IndexVerb] & 0x1F);
            var payloadStart = -1;

            if (verb == ZeroTierVerb.Error)
            {
                if (packetBytes.Length < IndexPayload + 1 + 8 + 1)
                {
                    continue;
                }

                var inReVerb = (ZeroTierVerb)(packetBytes[IndexPayload] & 0x1F);
                if (inReVerb != ZeroTierVerb.NetworkConfigRequest)
                {
                    continue;
                }

                var inRePacketId = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(IndexPayload + 1, 8));
                if (inRePacketId != reqPacketId)
                {
                    continue;
                }

                var errorCode = packetBytes[IndexPayload + 1 + 8];
                ulong? errorNetworkId = null;
                if (packetBytes.Length >= IndexPayload + 1 + 8 + 1 + 8)
                {
                    errorNetworkId = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(IndexPayload + 1 + 8 + 1, 8));
                }

                throw new InvalidOperationException(FormatNetworkConfigRequestError(errorCode, errorNetworkId));
            }

            if (verb == ZeroTierVerb.Ok)
            {
                var inReVerb = (ZeroTierVerb)(packetBytes[OkIndexInReVerb] & 0x1F);
                if (inReVerb != ZeroTierVerb.NetworkConfigRequest)
                {
                    continue;
                }

                var inRePacketId = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(OkIndexInRePacketId, 8));
                if (inRePacketId != reqPacketId)
                {
                    continue;
                }

                payloadStart = OkIndexPayload;
            }
            else if (verb == ZeroTierVerb.NetworkConfig)
            {
                payloadStart = IndexPayload;
            }
            else
            {
                continue;
            }

            if (!ZeroTierNetworkConfigParsing.TryParseConfigChunk(
                    packetBytes,
                    payloadStart,
                    out var chunkNetworkId,
                    out var chunkData,
                    out var configUpdateId,
                    out var configTotalLength,
                    out var chunkIndex,
                    out var signatureData,
                    out var signatureMessage))
            {
                continue;
            }

            if (chunkNetworkId != networkId)
            {
                continue;
            }

            if (signatureData is not null)
            {
                if (!ZeroTierC25519.VerifySignature(controllerIdentity.PublicKey, signatureMessage, signatureData))
                {
                    continue;
                }
            }
            else
            {
                configUpdateId = reqPacketId;
                configTotalLength = (uint)chunkData.Length;
                chunkIndex = 0;
            }

            if (dictionary is null)
            {
                dictionary = new byte[configTotalLength];
                totalLength = configTotalLength;
                updateId = configUpdateId;
            }

            if (configUpdateId != updateId || configTotalLength != totalLength)
            {
                continue;
            }

            if ((ulong)chunkIndex + (ulong)chunkData.Length > totalLength)
            {
                continue;
            }

            if (!receivedOffsets.Add(chunkIndex))
            {
                continue;
            }

            chunkData.CopyTo(dictionary.AsSpan((int)chunkIndex, chunkData.Length));
            receivedLength += chunkData.Length;

            if ((uint)receivedLength == totalLength)
            {
                var managedIps = ZeroTierNetworkConfigParsing.ParseManagedIps(dictionary);
                return (dictionary, managedIps);
            }
        }
    }

    private static string FormatNetworkConfigRequestError(byte errorCode, ulong? networkId)
    {
        var message = errorCode switch
        {
            0x01 => "Invalid NETWORK_CONFIG_REQUEST.",
            0x02 => "Bad/unsupported protocol version for NETWORK_CONFIG_REQUEST.",
            0x03 => "Controller object not found for NETWORK_CONFIG_REQUEST.",
            0x04 => "Identity collision reported by controller.",
            0x05 => "Controller does not support NETWORK_CONFIG_REQUEST.",
            0x06 => "Network membership certificate required (COM update needed).",
            0x07 => "Network access denied (not authorized).",
            0x08 => "Unwanted multicast (unexpected for NETWORK_CONFIG_REQUEST).",
            0x09 => "Network authentication required (external/2FA).",
            _ => $"Unknown error for NETWORK_CONFIG_REQUEST (0x{errorCode:x2})."
        };

        return networkId is null
            ? $"{message}"
            : $"{message} (network: 0x{networkId:x16})";
    }

    private static async Task<(NodeId Source, IPEndPoint RemoteEndPoint, byte[] PacketBytes)?> ReceiveAndDecryptAsync(
        ZeroTierUdpTransport udp,
        NodeId expectedSource,
        byte[] key,
        CancellationToken cancellationToken)
    {
        var datagram = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);

        var packetBytes = datagram.Payload.ToArray();
        if (!ZeroTierPacketCodec.TryDecode(packetBytes, out var packet))
        {
            return null;
        }

        if (packet.Header.Source != expectedSource)
        {
            return null;
        }

        if (!ZeroTierPacketCrypto.Dearmor(packetBytes, key))
        {
            return null;
        }

        if ((packetBytes[IndexVerb] & ZeroTierPacketHeader.VerbFlagCompressed) != 0)
        {
            if (!ZeroTierPacketCompression.TryUncompress(packetBytes, out var uncompressed))
            {
                return null;
            }

            packetBytes = uncompressed;
        }

        return (packet.Header.Source, datagram.RemoteEndPoint, packetBytes);
    }

    private static ulong GeneratePacketId()
    {
        Span<byte> buffer = stackalloc byte[8];
        RandomNumberGenerator.Fill(buffer);
        return BinaryPrimitives.ReadUInt64BigEndian(buffer);
    }

    private static void WriteUInt40(Span<byte> destination, ulong value)
    {
        if (destination.Length < 5)
        {
            throw new ArgumentException("Destination must be at least 5 bytes.", nameof(destination));
        }

        destination[0] = (byte)((value >> 32) & 0xFF);
        destination[1] = (byte)((value >> 24) & 0xFF);
        destination[2] = (byte)((value >> 16) & 0xFF);
        destination[3] = (byte)((value >> 8) & 0xFF);
        destination[4] = (byte)(value & 0xFF);
    }
}
