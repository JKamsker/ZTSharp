using System.Buffers.Binary;

namespace ZTSharp.Transport.Internal;

internal static class OsUdpPeerDiscoveryProtocol
{
    internal enum FrameType : byte
    {
        PeerHello = 1,
        PeerHelloResponse = 2
    }

    private const int MagicLength = 4;
    private const int FrameTypeOffset = MagicLength;
    private const int NodeOffset = MagicLength + 1;
    private const int NodeLength = sizeof(ulong);

    internal const int PayloadLength = MagicLength + 1 + NodeLength;

    private static ReadOnlySpan<byte> Magic => "ZTC1"u8;

    internal static void WritePayload(FrameType frameType, ulong nodeId, Span<byte> payload)
    {
        Magic.CopyTo(payload);
        payload[FrameTypeOffset] = (byte)frameType;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.Slice(NodeOffset), nodeId);
    }

    internal static bool TryParsePayload(ReadOnlySpan<byte> payload, out FrameType frameType, out ulong nodeId)
    {
        frameType = FrameType.PeerHello;
        nodeId = 0;

        if (payload.Length != PayloadLength)
        {
            return false;
        }

        if (!payload.Slice(0, MagicLength).SequenceEqual(Magic))
        {
            return false;
        }

        frameType = (FrameType)payload[FrameTypeOffset];
        if (frameType != FrameType.PeerHello && frameType != FrameType.PeerHelloResponse)
        {
            return false;
        }

        nodeId = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(NodeOffset, NodeLength));
        return true;
    }
}
