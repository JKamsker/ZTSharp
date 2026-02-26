using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal static class ZeroTierHelloPacketBuilder
{
    public static byte[] BuildPacket(
        ZeroTierIdentity localIdentity,
        NodeId destination,
        IPEndPoint physicalDestination,
        ZeroTierWorld planet,
        ulong timestamp,
        ReadOnlySpan<byte> sharedKey,
        byte advertisedProtocolVersion,
        byte advertisedMajorVersion,
        byte advertisedMinorVersion,
        ushort advertisedRevision,
        out ulong packetId)
    {
        var iv = new byte[8];
        RandomNumberGenerator.Fill(iv);
        packetId = BinaryPrimitives.ReadUInt64BigEndian(iv);

        var identityLength = ZeroTierIdentityCodec.GetSerializedLength(localIdentity, includePrivate: false);
        var inetLength = ZeroTierInetAddressCodec.GetSerializedLength(physicalDestination);

        var payloadFixedLength =
            1 + // protocol version
            1 + // major
            1 + // minor
            2 + // revision
            8 + // timestamp
            identityLength +
            inetLength +
            8 + // planet world id
            8; // planet timestamp

        var startCryptedPortionAtPayloadOffset = payloadFixedLength;

        var moonListLength = 2; // u16 count (0)
        var payload = new byte[payloadFixedLength + moonListLength];

        var p = 0;
        payload[p++] = advertisedProtocolVersion;
        payload[p++] = advertisedMajorVersion;
        payload[p++] = advertisedMinorVersion;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(p, 2), advertisedRevision);
        p += 2;
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(p, 8), timestamp);
        p += 8;

        p += ZeroTierIdentityCodec.Serialize(localIdentity, payload.AsSpan(p), includePrivate: false);
        p += ZeroTierInetAddressCodec.Serialize(physicalDestination, payload.AsSpan(p));

        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(p, 8), planet.Id);
        p += 8;
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(p, 8), planet.Timestamp);
        p += 8;

        // Encrypted portion: moon count (0) (no moons advertised).
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(p, 2), 0);
        p += 2;

        if (p != payload.Length)
        {
            throw new InvalidOperationException("HELLO payload size mismatch.");
        }

        var header = new ZeroTierPacketHeader(
            PacketId: packetId,
            Destination: destination,
            Source: localIdentity.NodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZeroTierVerb.Hello);

        var packet = ZeroTierPacketCodec.Encode(header, payload);

        // cryptField() encrypts the remainder of HELLO (moon list), using the raw key and IV with low 3 bits masked off.
        CryptHelloRemainder(packet, sharedKey, ZeroTierPacketHeader.IndexPayload + startCryptedPortionAtPayloadOffset);

        // HELLO is not fully encrypted, but must be MACed.
        ZeroTierPacketCrypto.Armor(packet, sharedKey, encryptPayload: false);

        return packet;
    }

    private static void CryptHelloRemainder(byte[] packet, ReadOnlySpan<byte> key, int start)
    {
        if (key.Length < 32)
        {
            throw new ArgumentException("Key must be at least 32 bytes.", nameof(key));
        }

        if ((uint)start > (uint)packet.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        var length = packet.Length - start;
        if (length == 0)
        {
            return;
        }

        Span<byte> iv = stackalloc byte[8];
        packet.AsSpan(0, 8).CopyTo(iv);
        iv[7] &= 0xF8;

        var keystream = new byte[length];
        ZeroTierSalsa20.GenerateKeyStream12(key.Slice(0, 32), iv, keystream);
        for (var i = 0; i < length; i++)
        {
            packet[start + i] ^= keystream[i];
        }
    }
}
