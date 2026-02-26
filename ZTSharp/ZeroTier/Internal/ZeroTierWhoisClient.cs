using System.Buffers.Binary;
using System.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierWhoisClient
{
    private const int OkIndexInReVerb = ZeroTierPacketHeader.IndexPayload;
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
        ZeroTierBinaryPrimitives.WriteUInt40BigEndian(whoisPayload, controllerNodeId.Value);

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

        return await ZeroTierTimeouts
            .RunWithTimeoutAsync(timeout, operation: "Waiting for OK(WHOIS) from root", WaitForOkWhoisAsync, cancellationToken)
            .ConfigureAwait(false);

        async ValueTask<ZeroTierIdentity> WaitForOkWhoisAsync(CancellationToken token)
        {
            while (true)
            {
                var received = await ZeroTierDecryptingPacketReceiver
                    .ReceiveAndDecryptAsync(udp, rootNodeId, rootKey, token)
                    .ConfigureAwait(false);

                if (received is null)
                {
                    continue;
                }

                var packetBytes = received.Value.PacketBytes;
                if ((ZeroTierVerb)(packetBytes[ZeroTierPacketHeader.IndexVerb] & 0x1F) != ZeroTierVerb.Ok)
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
                var identitiesParsed = 0;
                while (ptr < packetBytes.Length && identitiesParsed < 32)
                {
                    ZeroTierIdentity identity;
                    int bytesRead;
                    try
                    {
                        identity = ZeroTierIdentityCodec.Deserialize(packetBytes.AsSpan(ptr), out bytesRead);
                    }
                    catch (FormatException)
                    {
                        break;
                    }

                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    identitiesParsed++;
                    ptr += bytesRead;
                    if (identity.NodeId == controllerNodeId)
                    {
                        return identity;
                    }
                }
            }
        }
    }

}
