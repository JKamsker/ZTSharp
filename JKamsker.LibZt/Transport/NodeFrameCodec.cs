using System.Buffers.Binary;

namespace JKamsker.LibZt.Transport;

internal static class NodeFrameCodec
{
    private const byte FrameVersion = 1;

    public static byte[] Encode(ulong networkId, ulong sourceNodeId, byte[] payload)
    {
        var data = new byte[1 + sizeof(ulong) * 2 + payload.Length];
        data[0] = FrameVersion;
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(1, sizeof(ulong)), networkId);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(1 + sizeof(ulong), sizeof(ulong)), sourceNodeId);
        payload.CopyTo(data.AsSpan(1 + sizeof(ulong) * 2));
        return data;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> frame,
        out ulong networkId,
        out ulong sourceNodeId,
        out ReadOnlyMemory<byte> payload)
    {
        if (frame.Length < 1 + sizeof(ulong) * 2 || frame[0] != FrameVersion)
        {
            networkId = 0;
            sourceNodeId = 0;
            payload = ReadOnlyMemory<byte>.Empty;
            return false;
        }

        networkId = BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(1, sizeof(ulong)));
        sourceNodeId = BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(1 + sizeof(ulong), sizeof(ulong)));
        payload = frame[(1 + sizeof(ulong) * 2)..].ToArray();
        return true;
    }
}
