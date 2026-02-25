using System.Buffers.Binary;
using System.Net;
using ZTSharp.ZeroTier.Protocol;

namespace ZTSharp.ZeroTier.Internal;

internal readonly record struct ZeroTierHelloOkPayload(
    ulong InRePacketId,
    ulong TimestampEcho,
    byte RemoteProtocolVersion,
    byte RemoteMajorVersion,
    byte RemoteMinorVersion,
    ushort RemoteRevision,
    IPEndPoint? ExternalSurfaceAddress);

internal static class ZeroTierHelloOkParser
{
    private const int OkIndexInReVerb = ZeroTierPacketHeader.Length;
    private const int OkIndexInRePacketId = OkIndexInReVerb + 1;
    private const int OkIndexPayload = OkIndexInRePacketId + 8;

    private const int HelloOkIndexTimestamp = OkIndexPayload;
    private const int HelloOkIndexProtocolVersion = HelloOkIndexTimestamp + 8;
    private const int HelloOkIndexMajorVersion = HelloOkIndexProtocolVersion + 1;
    private const int HelloOkIndexMinorVersion = HelloOkIndexMajorVersion + 1;
    private const int HelloOkIndexRevision = HelloOkIndexMinorVersion + 1;

    public static bool TryParse(byte[] packetBytes, byte[] key, out ZeroTierHelloOkPayload payload)
    {
        ArgumentNullException.ThrowIfNull(packetBytes);
        ArgumentNullException.ThrowIfNull(key);

        payload = default;

        if (!ZeroTierPacketCrypto.Dearmor(packetBytes, key))
        {
            return false;
        }

        if ((packetBytes[27] & ZeroTierPacketHeader.VerbFlagCompressed) != 0)
        {
            if (!ZeroTierPacketCompression.TryUncompress(packetBytes, out var uncompressed))
            {
                return false;
            }

            packetBytes = uncompressed;
        }

        if (packetBytes.Length < HelloOkIndexRevision + 2)
        {
            return false;
        }

        var verb = (ZeroTierVerb)(packetBytes[27] & 0x1F);
        if (verb != ZeroTierVerb.Ok)
        {
            return false;
        }

        var inReVerb = (ZeroTierVerb)(packetBytes[OkIndexInReVerb] & 0x1F);
        if (inReVerb != ZeroTierVerb.Hello)
        {
            return false;
        }

        var inRePacketId = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(OkIndexInRePacketId, 8));
        var timestampEcho = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(HelloOkIndexTimestamp, 8));
        var remoteProto = packetBytes[HelloOkIndexProtocolVersion];
        var remoteMajor = packetBytes[HelloOkIndexMajorVersion];
        var remoteMinor = packetBytes[HelloOkIndexMinorVersion];
        var remoteRevision = BinaryPrimitives.ReadUInt16BigEndian(packetBytes.AsSpan(HelloOkIndexRevision, 2));

        var ptr = HelloOkIndexRevision + 2;
        IPEndPoint? surface = null;
        if (ptr < packetBytes.Length)
        {
            if (ZeroTierInetAddressCodec.TryDeserialize(packetBytes.AsSpan(ptr), out var parsed, out _))
            {
                surface = parsed;
            }
        }

        payload = new ZeroTierHelloOkPayload(
            InRePacketId: inRePacketId,
            TimestampEcho: timestampEcho,
            RemoteProtocolVersion: remoteProto,
            RemoteMajorVersion: remoteMajor,
            RemoteMinorVersion: remoteMinor,
            RemoteRevision: remoteRevision,
            ExternalSurfaceAddress: surface);
        return true;
    }
}

