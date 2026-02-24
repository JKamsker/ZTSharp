using System.Buffers.Binary;
using System.Net;
using JKamsker.LibZt.ZeroTier.Protocol;

namespace JKamsker.LibZt.ZeroTier.Internal;

internal static class ZtZeroTierHelloOkPacketBuilder
{
    public static byte[] BuildPacket(
        ulong packetId,
        ZtNodeId destination,
        ZtNodeId source,
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
            : ZtZeroTierInetAddressCodec.GetSerializedLength(externalSurfaceAddress);

        var payload = new byte[1 + 8 + 8 + 1 + 1 + 1 + 2 + surfaceLength];
        payload[0] = (byte)ZtZeroTierVerb.Hello;
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(1, 8), inRePacketId);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(9, 8), helloTimestampEcho);
        payload[17] = ZtZeroTierHelloClient.AdvertisedProtocolVersion;
        payload[18] = ZtZeroTierHelloClient.AdvertisedMajorVersion;
        payload[19] = ZtZeroTierHelloClient.AdvertisedMinorVersion;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(20, 2), ZtZeroTierHelloClient.AdvertisedRevision);

        if (externalSurfaceAddress is not null)
        {
            _ = ZtZeroTierInetAddressCodec.Serialize(externalSurfaceAddress, payload.AsSpan(22));
        }

        var header = new ZtZeroTierPacketHeader(
            PacketId: packetId,
            Destination: destination,
            Source: source,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZtZeroTierVerb.Ok);

        var packet = ZtZeroTierPacketCodec.Encode(header, payload);
        ZtZeroTierPacketCrypto.Armor(packet, sharedKey, encryptPayload: true);
        return packet;
    }
}

