using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal sealed record ZeroTierNetworkConfigResult(
    ZeroTierHelloOk UpstreamRoot,
    byte[] UpstreamRootKey,
    ZeroTierIdentity ControllerIdentity,
    byte[] DictionaryBytes,
    IPAddress[] ManagedIps);

internal static class ZeroTierNetworkConfigClient
{
    private const int IndexVerb = 27;
    private const int IndexPayload = ZeroTierPacketHeader.Length;

    private const int OkIndexInReVerb = ZeroTierPacketHeader.Length;
    private const int OkIndexInRePacketId = OkIndexInReVerb + 1;
    private const int OkIndexPayload = OkIndexInRePacketId + 8;

    public static async Task<ZeroTierNetworkConfigResult> FetchAsync(
        ZeroTierIdentity localIdentity,
        ZeroTierWorld planet,
        ulong networkId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localIdentity);
        ArgumentNullException.ThrowIfNull(planet);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");
        }

        if (localIdentity.PrivateKey is null)
        {
            throw new InvalidOperationException("Local identity must contain a private key.");
        }

        var rootKeys = BuildRootKeys(localIdentity, planet);

        var deadline = DateTimeOffset.UtcNow + timeout;
        static TimeSpan GetRemainingTimeout(DateTimeOffset deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("Timed out while joining the network.");
            }

            return remaining;
        }

        var udp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: true);
        try
        {
            var helloOk = await ZeroTierHelloClient
                .HelloRootsAsync(udp, localIdentity, planet, GetRemainingTimeout(deadline), cancellationToken)
                .ConfigureAwait(false);

            if (!rootKeys.TryGetValue(helloOk.RootNodeId, out var upstreamRootKey))
            {
                throw new InvalidOperationException($"No root key available for {helloOk.RootNodeId}.");
            }

            var controllerNodeId = GetControllerNodeId(networkId);

            var controllerIdentity = await WhoisAsync(
                    udp,
                    rootNodeId: helloOk.RootNodeId,
                    rootEndpoint: helloOk.RootEndpoint,
                    rootKey: upstreamRootKey,
                    localNodeId: localIdentity.NodeId,
                    controllerNodeId,
                    GetRemainingTimeout(deadline),
                    cancellationToken)
                .ConfigureAwait(false);

            var controllerKey = new byte[48];
            ZeroTierC25519.Agree(localIdentity.PrivateKey, controllerIdentity.PublicKey, controllerKey);

            var keys = new Dictionary<NodeId, byte[]>(capacity: planet.Roots.Count + 1);
            foreach (var pair in rootKeys)
            {
                keys[pair.Key] = pair.Value;
            }

            keys[controllerNodeId] = controllerKey;

            await ZeroTierHelloClient.HelloAsync(
                    udp,
                    localIdentity,
                    planet,
                    destination: controllerNodeId,
                    physicalDestination: helloOk.RootEndpoint,
                    sharedKey: controllerKey,
                    timeout: GetRemainingTimeout(deadline),
                    cancellationToken)
                .ConfigureAwait(false);

            var (dictBytes, ips) = await RequestNetworkConfigAsync(
                    udp,
                    keys,
                    rootEndpoint: helloOk.RootEndpoint,
                    localNodeId: localIdentity.NodeId,
                    controllerIdentity,
                    networkId,
                    GetRemainingTimeout(deadline),
                    cancellationToken)
                .ConfigureAwait(false);

            return new ZeroTierNetworkConfigResult(helloOk, upstreamRootKey, controllerIdentity, dictBytes, ips);
        }
        finally
        {
            await udp.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static Dictionary<NodeId, byte[]> BuildRootKeys(ZeroTierIdentity localIdentity, ZeroTierWorld planet)
    {
        var keys = new Dictionary<NodeId, byte[]>(planet.Roots.Count);
        foreach (var root in planet.Roots)
        {
            var key = new byte[48];
            ZeroTierC25519.Agree(localIdentity.PrivateKey!, root.Identity.PublicKey, key);
            keys[root.Identity.NodeId] = key;
        }

        return keys;
    }

    private static async Task<ZeroTierIdentity> WhoisAsync(
        ZeroTierUdpTransport udp,
        NodeId rootNodeId,
        IPEndPoint rootEndpoint,
        byte[] rootKey,
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
        ZeroTierPacketCrypto.Armor(whoisPacket, rootKey, encryptPayload: true);

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

    private static async Task<(byte[] DictionaryBytes, IPAddress[] ManagedIps)> RequestNetworkConfigAsync(
        ZeroTierUdpTransport udp,
        Dictionary<NodeId, byte[]> keys,
        IPEndPoint rootEndpoint,
        NodeId localNodeId,
        ZeroTierIdentity controllerIdentity,
        ulong networkId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var controllerNodeId = controllerIdentity.NodeId;
        if (!keys.TryGetValue(controllerNodeId, out var controllerKey))
        {
            throw new InvalidOperationException("Missing controller key.");
        }

        var metaDataBytes = BuildRequestMetadataDictionary();
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
        ZeroTierPacketCrypto.Armor(reqPacket, controllerKey, encryptPayload: true);

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

            if (!TryParseConfigChunk(
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
                var managedIps = ParseManagedIps(dictionary);
                return (dictionary, managedIps);
            }
        }
    }

    private static IPAddress[] ParseManagedIps(byte[] dictionaryBytes)
    {
        var ips = new HashSet<IPAddress>();

        if (ZeroTierDictionary.TryGet(dictionaryBytes, "I", out var staticIpsBlob))
        {
            var data = staticIpsBlob.AsSpan();
            while (!data.IsEmpty)
            {
                if (!ZeroTierInetAddressCodec.TryDeserialize(data, out var endpoint, out var read) || read <= 0)
                {
                    break;
                }

                if (endpoint is not null)
                {
                    var bits = endpoint.Port;
                    var address = endpoint.Address;
                    if (!IsNetworkRoute(address, bits))
                    {
                        ips.Add(address);
                    }
                }

                data = data.Slice(read);
            }
        }
        else
        {
            TryAddLegacyIps(dictionaryBytes, "v4s", ips);
            TryAddLegacyIps(dictionaryBytes, "v6s", ips);
        }

        return ips
            .OrderBy(ip => ip.AddressFamily)
            .ThenBy(ip => ip.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    private static void TryAddLegacyIps(byte[] dictionaryBytes, string key, HashSet<IPAddress> destination)
    {
        if (!ZeroTierDictionary.TryGet(dictionaryBytes, key, out var valueBytes) || valueBytes.Length == 0)
        {
            return;
        }

        var value = Encoding.ASCII.GetString(valueBytes);
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var slash = part.IndexOf('/', StringComparison.Ordinal);
            var ipText = slash >= 0 ? part.Substring(0, slash) : part;
            var bitsText = slash >= 0 ? part.Substring(slash + 1) : string.Empty;
            if (!IPAddress.TryParse(ipText, out var ip))
            {
                continue;
            }

            _ = int.TryParse(bitsText, out var bits);
            if (IsNetworkRoute(ip, bits))
            {
                continue;
            }

            destination.Add(ip);
        }
    }

    private static bool IsNetworkRoute(IPAddress address, int bits)
    {
        if (bits <= 0)
        {
            return false;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            if (bits >= 32)
            {
                return false;
            }

            var bytes = address.GetAddressBytes();
            var ip =
                ((uint)bytes[0] << 24) |
                ((uint)bytes[1] << 16) |
                ((uint)bytes[2] << 8) |
                bytes[3];

            var hostMask = (bits == 0) ? 0xFFFFFFFFu : ((1u << (32 - bits)) - 1u);
            return (ip & hostMask) == 0;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (bits >= 128)
            {
                return false;
            }

            var bytes = address.GetAddressBytes();
            var fullBytes = bits / 8;
            var remainingBits = bits % 8;

            if (fullBytes < 16)
            {
                if (remainingBits == 0)
                {
                    for (var i = fullBytes; i < 16; i++)
                    {
                        if (bytes[i] != 0)
                        {
                            return false;
                        }
                    }

                    return true;
                }

                var mask = (byte)(0xFF >> remainingBits);
                if ((bytes[fullBytes] & mask) != 0)
                {
                    return false;
                }

                for (var i = fullBytes + 1; i < 16; i++)
                {
                    if (bytes[i] != 0)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        return false;
    }

    private static bool TryParseConfigChunk(
        byte[] packetBytes,
        int payloadStart,
        out ulong networkId,
        out ReadOnlySpan<byte> chunkData,
        out ulong configUpdateId,
        out uint totalLength,
        out uint chunkIndex,
        out byte[]? signature,
        out ReadOnlySpan<byte> signatureMessage)
    {
        networkId = 0;
        chunkData = default;
        configUpdateId = 0;
        totalLength = 0;
        chunkIndex = 0;
        signature = null;
        signatureMessage = default;

        if (payloadStart < 0 || payloadStart + 10 > packetBytes.Length)
        {
            return false;
        }

        var ptr = payloadStart;
        networkId = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(ptr, 8));
        ptr += 8;
        var chunkLen = BinaryPrimitives.ReadUInt16BigEndian(packetBytes.AsSpan(ptr, 2));
        ptr += 2;

        if (ptr + chunkLen > packetBytes.Length)
        {
            return false;
        }

        chunkData = packetBytes.AsSpan(ptr, chunkLen);
        ptr += chunkLen;

        if (ptr >= packetBytes.Length)
        {
            return true; // legacy unsigned single-chunk config
        }

        var signatureStart = ptr;

        ptr += 1; // flags
        if (ptr + 8 + 4 + 4 + 1 + 2 > packetBytes.Length)
        {
            return false;
        }

        configUpdateId = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(ptr, 8));
        ptr += 8;
        totalLength = BinaryPrimitives.ReadUInt32BigEndian(packetBytes.AsSpan(ptr, 4));
        ptr += 4;
        chunkIndex = BinaryPrimitives.ReadUInt32BigEndian(packetBytes.AsSpan(ptr, 4));
        ptr += 4;

        var sigCount = packetBytes[ptr++];
        var sigLen = BinaryPrimitives.ReadUInt16BigEndian(packetBytes.AsSpan(ptr, 2));
        ptr += 2;

        if (sigCount != 1 || sigLen != 96 || ptr + sigLen > packetBytes.Length)
        {
            return false;
        }

        signature = packetBytes.AsSpan(ptr, sigLen).ToArray();
        signatureMessage = packetBytes.AsSpan(payloadStart, signatureStart - payloadStart + 1 + 8 + 4 + 4);
        return true;
    }

    private static byte[] BuildRequestMetadataDictionary()
    {
        var sb = new StringBuilder();

        AppendHexKeyValue(sb, "v", 7); // ZT_NETWORKCONFIG_VERSION
        AppendHexKeyValue(sb, "vend", 1); // ZT_VENDOR_ZEROTIER
        AppendHexKeyValue(sb, "pv", 11); // Protocol (matches our HELLO advert)
        AppendHexKeyValue(sb, "majv", 1); // ZEROTIER_ONE_VERSION_MAJOR
        AppendHexKeyValue(sb, "minv", 12); // ZEROTIER_ONE_VERSION_MINOR
        AppendHexKeyValue(sb, "revv", 0); // ZEROTIER_ONE_VERSION_REVISION
        AppendHexKeyValue(sb, "mr", 1024); // ZT_MAX_NETWORK_RULES
        AppendHexKeyValue(sb, "mc", 128); // ZT_MAX_NETWORK_CAPABILITIES
        AppendHexKeyValue(sb, "mcr", 64); // ZT_MAX_CAPABILITY_RULES
        AppendHexKeyValue(sb, "mt", 128); // ZT_MAX_NETWORK_TAGS
        AppendHexKeyValue(sb, "f", 0); // Flags
        AppendHexKeyValue(sb, "revr", 1); // ZT_RULES_ENGINE_REVISION

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static void AppendHexKeyValue(StringBuilder sb, string key, ulong value)
    {
        if (sb.Length != 0)
        {
            sb.Append('\n');
        }

        sb.Append(key);
        sb.Append('=');
        sb.Append(value.ToString("x16", CultureInfo.InvariantCulture));
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

    private static NodeId GetControllerNodeId(ulong networkId) => new(networkId >> 24);

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
