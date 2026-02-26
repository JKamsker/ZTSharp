using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ZTSharp.Sockets;

internal static class OverlayTcpFrameCodec
{
    public const byte FrameVersion = 1;

    public enum FrameType : byte
    {
        Syn = 1,
        SynAck = 2,
        Data = 3,
        Fin = 4
    }

    public const int HeaderLength = 1 + 1 + 2 + 2 + sizeof(ulong) + sizeof(ulong);

    public static void BuildHeader(
        FrameType type,
        int sourcePort,
        int destinationPort,
        ulong destinationNodeId,
        ulong connectionId,
        Span<byte> destination)
    {
        destination[0] = FrameVersion;
        destination[1] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), (ushort)sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(4, 2), (ushort)destinationPort);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(6, sizeof(ulong)), destinationNodeId);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(6 + sizeof(ulong), sizeof(ulong)), connectionId);
    }

    public static bool TryParseHeader(
        ReadOnlySpan<byte> payload,
        out FrameType type,
        out int sourcePort,
        out int destinationPort,
        out ulong destinationNodeId,
        out ulong connectionId)
    {
        type = FrameType.Syn;
        sourcePort = 0;
        destinationPort = 0;
        destinationNodeId = 0;
        connectionId = 0;

        if (payload.Length < HeaderLength || payload[0] != FrameVersion)
        {
            return false;
        }

        type = (FrameType)payload[1];
        if (type is not (FrameType.Syn or FrameType.SynAck or FrameType.Data or FrameType.Fin))
        {
            return false;
        }

        sourcePort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2, 2));
        destinationPort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2));
        destinationNodeId = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(6, sizeof(ulong)));
        connectionId = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(6 + sizeof(ulong), sizeof(ulong)));
        return true;
    }

    public static ulong GenerateConnectionId()
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(buffer);
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }
}

