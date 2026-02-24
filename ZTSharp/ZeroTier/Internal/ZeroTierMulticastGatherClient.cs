using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierMulticastGatherClient
{
    private const int IndexVerb = 27;
    private const int IndexPayload = ZeroTierPacketHeader.Length;

    private const int OkIndexInReVerb = ZeroTierPacketHeader.Length;
    private const int OkIndexInRePacketId = OkIndexInReVerb + 1;
    private const int OkIndexPayload = OkIndexInRePacketId + 8;

    public static async Task<(uint TotalKnown, NodeId[] Members)> GatherAsync(
        ZeroTierUdpTransport udp,
        NodeId rootNodeId,
        IPEndPoint rootEndpoint,
        byte[] rootKey,
        NodeId localNodeId,
        ulong networkId,
        ZeroTierMulticastGroup group,
        uint gatherLimit,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => await GatherAsync(
                udp,
                rootNodeId,
                rootEndpoint,
                rootKey,
                localNodeId,
                networkId,
                group,
                gatherLimit,
                inlineCom: default,
                timeout,
                cancellationToken)
            .ConfigureAwait(false);

    public static async Task<(uint TotalKnown, NodeId[] Members)> GatherAsync(
        ZeroTierUdpTransport udp,
        NodeId rootNodeId,
        IPEndPoint rootEndpoint,
        byte[] rootKey,
        NodeId localNodeId,
        ulong networkId,
        ZeroTierMulticastGroup group,
        uint gatherLimit,
        ReadOnlyMemory<byte> inlineCom,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(rootEndpoint);
        ArgumentNullException.ThrowIfNull(rootKey);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");
        }

        var payload = ZeroTierMulticastGatherCodec.EncodeRequestPayload(networkId, group, gatherLimit, inlineCom.Span);

        var packetId = GeneratePacketId();
        var header = new ZeroTierPacketHeader(
            PacketId: packetId,
            Destination: rootNodeId,
            Source: localNodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.MulticastGather);

        var packet = ZeroTierPacketCodec.Encode(header, payload);
        ZeroTierPacketCrypto.Armor(packet, rootKey, encryptPayload: true);
        var requestPacketId = BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(0, 8));
        await udp.SendAsync(rootEndpoint, packet, cancellationToken).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (true)
        {
            ZeroTierUdpDatagram datagram;
            try
            {
                datagram = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for MULTICAST_GATHER response after {timeout}.");
            }

            var packetBytes = datagram.Payload.ToArray();
            if (!ZeroTierPacketCodec.TryDecode(packetBytes, out var decoded))
            {
                continue;
            }

            if (decoded.Header.Source != rootNodeId)
            {
                continue;
            }

            if (!ZeroTierPacketCrypto.Dearmor(packetBytes, rootKey))
            {
                continue;
            }

            if ((packetBytes[IndexVerb] & ZeroTierPacketHeader.VerbFlagCompressed) != 0)
            {
                if (!ZeroTierPacketCompression.TryUncompress(packetBytes, out var uncompressed))
                {
                    continue;
                }

                packetBytes = uncompressed;
            }

            var verb = (ZeroTierVerb)(packetBytes[IndexVerb] & 0x1F);
            if (verb == ZeroTierVerb.Error)
            {
                if (packetBytes.Length < IndexPayload + 1 + 8 + 1)
                {
                    continue;
                }

                var errorInReVerb = (ZeroTierVerb)(packetBytes[IndexPayload] & 0x1F);
                if (errorInReVerb != ZeroTierVerb.MulticastGather)
                {
                    continue;
                }

                var errorInRePacketId = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(IndexPayload + 1, 8));
                if (errorInRePacketId != requestPacketId)
                {
                    continue;
                }

                var errorCode = packetBytes[IndexPayload + 1 + 8];
                ulong? errorNetworkId = null;
                if (packetBytes.Length >= IndexPayload + 1 + 8 + 1 + 8)
                {
                    errorNetworkId = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(IndexPayload + 1 + 8 + 1, 8));
                }

                throw new InvalidOperationException(FormatMulticastGatherError(errorCode, errorNetworkId));
            }

            if (verb != ZeroTierVerb.Ok)
            {
                continue;
            }

            if (packetBytes.Length < OkIndexPayload)
            {
                continue;
            }

            var inReVerb = (ZeroTierVerb)(packetBytes[OkIndexInReVerb] & 0x1F);
            if (inReVerb != ZeroTierVerb.MulticastGather)
            {
                continue;
            }

            var inRePacketId = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(OkIndexInRePacketId, 8));
            if (inRePacketId != requestPacketId)
            {
                continue;
            }

            if (!ZeroTierMulticastGatherCodec.TryParseOkPayload(
                    packetBytes.AsSpan(OkIndexPayload),
                    out var okNetworkId,
                    out _,
                    out var totalKnown,
                    out var members) ||
                okNetworkId != networkId)
            {
                continue;
            }

            return (totalKnown, members);
        }
    }

    private static string FormatMulticastGatherError(byte errorCode, ulong? networkId)
    {
        var message = errorCode switch
        {
            0x01 => "Invalid MULTICAST_GATHER request.",
            0x02 => "Bad/unsupported protocol version for MULTICAST_GATHER.",
            0x03 => "Object not found for MULTICAST_GATHER.",
            0x04 => "Identity collision reported by peer.",
            0x05 => "Peer does not support MULTICAST_GATHER.",
            0x06 => "Network membership certificate required (COM update needed).",
            0x07 => "Network access denied (not authorized).",
            0x08 => "Unwanted multicast.",
            0x09 => "Network authentication required (external/2FA).",
            _ => $"Unknown error for MULTICAST_GATHER (0x{errorCode:x2})."
        };

        return networkId is null
            ? $"{message}"
            : $"{message} (network: 0x{networkId:x16})";
    }

    private static ulong GeneratePacketId()
    {
        Span<byte> buffer = stackalloc byte[8];
        RandomNumberGenerator.Fill(buffer);
        return BinaryPrimitives.ReadUInt64BigEndian(buffer);
    }
}
