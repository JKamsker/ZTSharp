using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using JKamsker.LibZt.ZeroTier.Protocol;
using JKamsker.LibZt.ZeroTier.Transport;

namespace JKamsker.LibZt.ZeroTier.Internal;

internal static class ZtZeroTierMulticastGatherClient
{
    private const int IndexVerb = 27;
    private const int IndexPayload = ZtZeroTierPacketHeader.Length;

    private const int OkIndexInReVerb = ZtZeroTierPacketHeader.Length;
    private const int OkIndexInRePacketId = OkIndexInReVerb + 1;
    private const int OkIndexPayload = OkIndexInRePacketId + 8;

    public static async Task<(uint TotalKnown, ZtNodeId[] Members)> GatherAsync(
        ZtZeroTierUdpTransport udp,
        ZtNodeId rootNodeId,
        IPEndPoint rootEndpoint,
        byte[] rootKey,
        ZtNodeId localNodeId,
        ulong networkId,
        ZtZeroTierMulticastGroup group,
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

    public static async Task<(uint TotalKnown, ZtNodeId[] Members)> GatherAsync(
        ZtZeroTierUdpTransport udp,
        ZtNodeId rootNodeId,
        IPEndPoint rootEndpoint,
        byte[] rootKey,
        ZtNodeId localNodeId,
        ulong networkId,
        ZtZeroTierMulticastGroup group,
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

        var payload = ZtZeroTierMulticastGatherCodec.EncodeRequestPayload(networkId, group, gatherLimit, inlineCom.Span);

        var packetId = GeneratePacketId();
        var header = new ZtZeroTierPacketHeader(
            PacketId: packetId,
            Destination: rootNodeId,
            Source: localNodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZtZeroTierVerb.MulticastGather);

        var packet = ZtZeroTierPacketCodec.Encode(header, payload);
        ZtZeroTierPacketCrypto.Armor(packet, rootKey, encryptPayload: true);
        await udp.SendAsync(rootEndpoint, packet, cancellationToken).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (true)
        {
            ZtZeroTierUdpDatagram datagram;
            try
            {
                datagram = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for MULTICAST_GATHER response after {timeout}.");
            }

            var packetBytes = datagram.Payload.ToArray();
            if (!ZtZeroTierPacketCodec.TryDecode(packetBytes, out var decoded))
            {
                continue;
            }

            if (decoded.Header.Source != rootNodeId)
            {
                continue;
            }

            if (!ZtZeroTierPacketCrypto.Dearmor(packetBytes, rootKey))
            {
                continue;
            }

            if ((packetBytes[IndexVerb] & ZtZeroTierPacketHeader.VerbFlagCompressed) != 0)
            {
                if (!ZtZeroTierPacketCompression.TryUncompress(packetBytes, out var uncompressed))
                {
                    continue;
                }

                packetBytes = uncompressed;
            }

            var verb = (ZtZeroTierVerb)(packetBytes[IndexVerb] & 0x1F);
            if (verb == ZtZeroTierVerb.Error)
            {
                if (packetBytes.Length < IndexPayload + 1 + 8 + 1)
                {
                    continue;
                }

                var errorInReVerb = (ZtZeroTierVerb)(packetBytes[IndexPayload] & 0x1F);
                if (errorInReVerb != ZtZeroTierVerb.MulticastGather)
                {
                    continue;
                }

                var errorInRePacketId = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(IndexPayload + 1, 8));
                if (errorInRePacketId != packetId)
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

            if (verb != ZtZeroTierVerb.Ok)
            {
                continue;
            }

            if (packetBytes.Length < OkIndexPayload)
            {
                continue;
            }

            var inReVerb = (ZtZeroTierVerb)(packetBytes[OkIndexInReVerb] & 0x1F);
            if (inReVerb != ZtZeroTierVerb.MulticastGather)
            {
                continue;
            }

            var inRePacketId = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(OkIndexInRePacketId, 8));
            if (inRePacketId != packetId)
            {
                continue;
            }

            if (!ZtZeroTierMulticastGatherCodec.TryParseOkPayload(
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
