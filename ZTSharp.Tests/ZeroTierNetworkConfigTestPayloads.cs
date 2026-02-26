using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.Tests;

internal static class ZeroTierNetworkConfigTestPayloads
{
    public static byte[] BuildHelloOkPayload(ulong helloPacketId, ulong helloTimestamp, IPEndPoint surface)
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

    public static byte[] BuildDictionaryWithStaticIp(IPAddress address, int bits)
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

    public static byte[] BuildSignedConfigChunkPayload(ulong networkId, byte[] dictionaryBytes, byte[] controllerPrivateKey)
        => BuildSignedConfigChunkPayload(
            networkId,
            dictionaryBytes,
            controllerPrivateKey,
            totalLength: (uint)dictionaryBytes.Length);

    public static byte[] BuildSignedConfigChunkPayload(
        ulong networkId,
        byte[] chunkBytes,
        byte[] controllerPrivateKey,
        uint totalLength,
        uint chunkIndex = 0,
        ulong updateId = 7,
        byte flags = 0)
    {
        var flagsLength = 1;
        var updateIdLength = 8;
        var totalLengthLength = 4;
        var chunkIndexLength = 4;

        if (chunkBytes.Length > ushort.MaxValue)
        {
            throw new ArgumentException("Chunk bytes must fit within a UInt16 length prefix.", nameof(chunkBytes));
        }

        var chunkLen = (ushort)chunkBytes.Length;
        var signatureMessageLength = 8 + 2 + chunkLen + flagsLength + updateIdLength + totalLengthLength + chunkIndexLength;
        var signatureMessage = new byte[signatureMessageLength];

        var p = 0;
        BinaryPrimitives.WriteUInt64BigEndian(signatureMessage.AsSpan(p, 8), networkId);
        p += 8;
        BinaryPrimitives.WriteUInt16BigEndian(signatureMessage.AsSpan(p, 2), chunkLen);
        p += 2;
        chunkBytes.CopyTo(signatureMessage.AsSpan(p));
        p += chunkBytes.Length;

        signatureMessage[p++] = flags;
        BinaryPrimitives.WriteUInt64BigEndian(signatureMessage.AsSpan(p, 8), updateId);
        p += 8;
        BinaryPrimitives.WriteUInt32BigEndian(signatureMessage.AsSpan(p, 4), totalLength);
        p += 4;
        BinaryPrimitives.WriteUInt32BigEndian(signatureMessage.AsSpan(p, 4), chunkIndex);
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
}
