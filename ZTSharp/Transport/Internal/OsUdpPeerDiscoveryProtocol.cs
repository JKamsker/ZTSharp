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
    private const int ChecksumOffset = NodeOffset + NodeLength;
    private const int ChecksumLength = sizeof(uint);

    internal const int PayloadLength = MagicLength + 1 + NodeLength + ChecksumLength;

    private static ReadOnlySpan<byte> Magic => "ZTC1"u8;

    internal static void WritePayload(FrameType frameType, ulong nodeId, ulong networkId, Span<byte> payload)
    {
        Magic.CopyTo(payload);
        payload[FrameTypeOffset] = (byte)frameType;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.Slice(NodeOffset), nodeId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.Slice(ChecksumOffset, ChecksumLength),
            ComputeChecksum(payload.Slice(0, ChecksumOffset), networkId));
    }

    internal static bool TryParsePayload(ReadOnlySpan<byte> payload, ulong networkId, out FrameType frameType, out ulong nodeId)
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

        var checksum = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(ChecksumOffset, ChecksumLength));
        if (checksum != ComputeChecksum(payload.Slice(0, ChecksumOffset), networkId))
        {
            return false;
        }

        return true;
    }

    private static uint ComputeChecksum(ReadOnlySpan<byte> payload, ulong networkId)
    {
        var hash = 2166136261u;
        foreach (var b in payload)
        {
            hash ^= b;
            hash *= 16777619u;
        }

        Span<byte> networkIdBuffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(networkIdBuffer, networkId);
        foreach (var b in networkIdBuffer)
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return hash;
    }
}
