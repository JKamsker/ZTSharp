using System.Buffers.Binary;
using System.Net;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierHelloOkPacketBuilder
{
    public static byte[] BuildPacket(
        ulong packetId,
        NodeId destination,
        NodeId source,
        ulong inRePacketId,
        ulong helloTimestampEcho,
        IPEndPoint? externalSurfaceAddress,
        ReadOnlySpan<byte> sharedKey)
    {
        if (sharedKey.Length < 32)
        {
            throw new ArgumentException("Shared key must be at least 32 bytes.", nameof(sharedKey));
        }

        var surfaceLength = externalSurfaceAddress is null
            ? 0
            : ZeroTierInetAddressCodec.GetSerializedLength(externalSurfaceAddress);

        var payload = new byte[1 + 8 + 8 + 1 + 1 + 1 + 2 + surfaceLength];
        payload[0] = (byte)ZeroTierVerb.Hello;
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(1, 8), inRePacketId);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(9, 8), helloTimestampEcho);
        payload[17] = ZeroTierHelloClient.AdvertisedProtocolVersion;
        payload[18] = ZeroTierHelloClient.AdvertisedMajorVersion;
        payload[19] = ZeroTierHelloClient.AdvertisedMinorVersion;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(20, 2), ZeroTierHelloClient.AdvertisedRevision);

        if (externalSurfaceAddress is not null)
        {
            _ = ZeroTierInetAddressCodec.Serialize(externalSurfaceAddress, payload.AsSpan(22));
        }

        var header = new ZeroTierPacketHeader(
            PacketId: packetId,
            Destination: destination,
            Source: source,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.Ok);

        var packet = ZeroTierPacketCodec.Encode(header, payload);
        ZeroTierPacketCrypto.Armor(packet, sharedKey, encryptPayload: true);
        return packet;
    }
}

