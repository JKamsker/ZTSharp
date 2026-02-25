using System.Buffers.Binary;
using System.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierWhoisClient
{
    private const int IndexVerb = 27;
    private const int OkIndexInReVerb = ZeroTierPacketHeader.Length;
    private const int OkIndexInRePacketId = OkIndexInReVerb + 1;
    private const int OkIndexPayload = OkIndexInRePacketId + 8;

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

        var whoisPacketId = ZeroTierPacketIdGenerator.GeneratePacketId();
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
                received = await ZeroTierDecryptingPacketReceiver
                    .ReceiveAndDecryptAsync(udp, rootNodeId, rootKey, timeoutCts.Token)
                    .ConfigureAwait(false);
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

